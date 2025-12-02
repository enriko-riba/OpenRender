using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRender;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Coordinates terrain streaming, chunk lifecycle, and CPU resource transitions.
/// </summary>
public sealed class ChunkStreamingManager : IDisposable
{
    private const int MAX_CHUNKS_PER_BATCH = 64;
    private const int HIGH_PRIORITY_DISTANCE = 2;
    private const int BASE_UNLOAD_PADDING_CHUNKS = 4;
    private const int MAX_UNLOADS_PER_FRAME = 32;
    private const int PREFETCH_MARGIN_DEFAULT = 0;
    private const int PREFETCH_MARGIN_MAX = 4;
    private const int RETENTION_PADDING_CHUNKS = 2;
    private const int RETENTION_FRAME_DELAY = 180;
    private const int SeamRefreshCooldownFrames = 240;
    private static readonly bool SeamRefreshEnabled = false;
    private const string ConfigFileName = "terrain_config.json";

    // MINECRAFT-STYLE MESHING:
    // 1. When voxel data completes, mesh immediately (treat missing neighbors as air)
    // 2. Notify neighbors - they re-mesh when their neighbor data becomes available
    // 3. No waiting, no guard bands - simple and robust

    private readonly VoxelWorld world;
    private readonly ChunkVoxelDataCache chunkVoxelCache;
    private readonly ChunkGenerationJobSystem cpuGenerationJobs;
    private readonly ChunkMeshingJobSystem cpuMeshingJobs;
    private readonly Dictionary<int, ChunkDescriptor> activeChunks = [];
    private readonly Queue<int> highPriorityPending = new();
    private readonly Queue<int> lowPriorityPending = new();
    private readonly HashSet<int> pendingSeamRefresh = [];
    private readonly Dictionary<int, long> lastSeamRefreshFrame = [];
    private readonly Dictionary<int, Dictionary<int, BlockId>> chunkEdits = [];
    private readonly Dictionary<int, CpuChunkMesh> cpuMeshResults = [];
    private readonly Dictionary<int, long> chunkLastVisibleFrame = [];
    
    // Deferred remesh: chunks waiting for a neighbor to complete regeneration before remeshing
    // Key = chunk that needs to regenerate first, Value = list of neighbors waiting for remesh
    private readonly Dictionary<int, HashSet<int>> deferredRemeshWaiters = [];

    private TerrainMeshBufferManager? meshBuffers;
    private VoxelTerrainRenderer? terrainRenderer;

    private long currentFrame;
    private long lastRetentionGuardLogFrame = long.MinValue;
    private Vector3 lastCameraPosition;
    private int maxConcurrentCpuGenerations;
    private int inFlightCpuGenerations;

    private TerrainConfig terrainConfig;
    private int generationSeed = 1337;

    private int visibilityFlagsCapacity;

    public ChunkStreamingManager(VoxelWorld world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        CollisionManager = new CollisionManager();
        terrainConfig = LoadTerrainConfig();

        chunkVoxelCache = new ChunkVoxelDataCache();

        // Use default worker counts (ProcessorCount/2 each) to avoid thread contention.
        // Generation and meshing share CPU resources, so total workers ≈ ProcessorCount.
        cpuGenerationJobs = new ChunkGenerationJobSystem(chunkVoxelCache, terrainConfig);
        maxConcurrentCpuGenerations = cpuGenerationJobs.WorkerCount;

        cpuMeshingJobs = new ChunkMeshingJobSystem(chunkVoxelCache);

        lastCameraPosition = Vector3.Zero;
    }

    public VoxelWorld World => world;
    public CollisionManager CollisionManager { get; }
    
    /// <summary>
    /// Query the biome at a world position using cached chunk biome data.
    /// </summary>
    public BiomeId GetBiomeAtWorldPos(int worldX, int worldZ) 
        => chunkVoxelCache.GetBiomeAtWorldPos(worldX, worldZ);

    /// <summary>
    /// Get climate data (C/T/H/E/PV) at a specific world position (interpolated).
    /// </summary>
    public (float C, float T, float H, float E, float PV)? GetClimateAtWorldPos(int worldX, int worldZ)
        => chunkVoxelCache.GetClimateAtWorldPos(worldX, worldZ);

    /// <summary>
    /// Get raw cell climate data (NOT interpolated) - this is what biome selection uses.
    /// </summary>
    public (float C, float T, float H, float E, float PV)? GetCellClimateAtWorldPos(int worldX, int worldZ)
        => chunkVoxelCache.GetCellClimateAtWorldPos(worldX, worldZ);
    
    /// <summary>
    /// Try to get full biome data for a chunk.
    /// </summary>
    public bool TryGetChunkBiomeData(int chunkIndex, out ChunkBiomeData? biomeData) 
        => chunkVoxelCache.TryGetBiomeData(chunkIndex, out biomeData);

    private int loadDistance = VoxelHelper.MaxDistanceInChunks;
    private int prefetchMarginChunks = PREFETCH_MARGIN_DEFAULT;
    public int LoadDistance
    {
        get => loadDistance;
        set
        {
            var clamped = Math.Clamp(value, 1, VoxelHelper.MaxDistanceInChunks);
            if (clamped == loadDistance)
            {
                return;
            }

            loadDistance = clamped;
        }
    }

    public void SetPrefetchMargin(int margin)
    {
        var clamped = Math.Clamp(margin, PREFETCH_MARGIN_DEFAULT, PREFETCH_MARGIN_MAX);
        if (clamped == prefetchMarginChunks)
        {
            return;
        }

        prefetchMarginChunks = clamped;
        Log.Info($"ChunkStreamingManager: Prefetch margin set to {prefetchMarginChunks} (LoadDistance={loadDistance})");
    }
    public int VisibleChunkCount { get; private set; }
    public int CulledChunkCount { get; private set; }
    public int StatVisibleChunks { get; private set; }
    public int StatFrustumCulledChunks { get; private set; }
    public int StatTotalIndices { get; private set; }
    public int StatVisibleIndices { get; private set; }

    public IEnumerable<ChunkDescriptor> GetReadyChunks()
        => activeChunks.Values.Where(c => c.State is TerrainChunkState.Ready or TerrainChunkState.Dirty);

    public (int total, int pending, int generating, int ready) GetStats()
    {
        var total = activeChunks.Count;
        var pending = activeChunks.Values.Count(c => c.State is TerrainChunkState.Pending);
        var generating = activeChunks.Values.Count(c => c.State is TerrainChunkState.Generating or TerrainChunkState.CountingVisibility);
        // Dirty chunks are renderable (have a mesh) - they just might need re-mesh later
        var ready = activeChunks.Values.Count(c => c.State is TerrainChunkState.Ready or TerrainChunkState.Dirty);
        return (total, pending, generating, ready);
    }

    public void FlushVoxelCache(string reason)
    {
        var cachedEntries = chunkVoxelCache.ActiveEntryCount;
        chunkVoxelCache.Clear();
        cpuMeshResults.Clear();
        pendingSeamRefresh.Clear();
        cpuMeshingJobs.DrainPendingWorkItems();
        ClearPendingCpuMeshingQueue();

        var dirtied = 0;
        foreach (var kvp in activeChunks.ToArray())
        {
            if (kvp.Value.State == TerrainChunkState.Ready)
            {
                dirtied++;
                MarkChunkDirty(kvp.Key);
            }
        }

        Log.Warn($"ChunkStreamingManager: voxel cache flushed ({reason}), cleared={cachedEntries} entries, dirtied={dirtied} chunks");
    }

    private static TerrainConfig LoadTerrainConfig()
    {
        var path = Path.Combine(Environment.CurrentDirectory, ConfigFileName);
        if (File.Exists(path))
        {
            try
            {
                return TerrainConfig.Load(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to load TerrainConfig '{path}': {ex.Message}");
            }
        }

        return new TerrainConfig();
    }

    public void InitializeCpuGeneration(int seed)
    {
        generationSeed = seed != 0 ? seed : generationSeed;
        terrainConfig.Seed = generationSeed;

        var maxChunks = CalculateMaxViewChunks();

        cpuGenerationJobs.UpdateConfig(terrainConfig);
        LoadEdits();

        Log.Info($"ChunkStreamingManager: CPU generation assets initialized (seed={generationSeed}, maxChunks={maxChunks})");
    }

    private int GetActiveLoadDistance()
    {
        var maxRadius = Math.Clamp(VoxelHelper.MaxDistanceInChunks + PREFETCH_MARGIN_MAX, 1, VoxelHelper.WorldChunksXZ);
        return Math.Clamp(loadDistance + prefetchMarginChunks, 1, maxRadius);
    }

    private int GetRetentionDistanceChunks()
    {
        var maxRadius = Math.Clamp(VoxelHelper.MaxDistanceInChunks + PREFETCH_MARGIN_MAX + RETENTION_PADDING_CHUNKS, 1, VoxelHelper.WorldChunksXZ);
        return Math.Clamp(loadDistance + prefetchMarginChunks + RETENTION_PADDING_CHUNKS, 1, maxRadius);
    }

    private int GetUnloadDistanceChunks()
    {
        var retention = GetRetentionDistanceChunks();
        var maxRadius = Math.Clamp(VoxelHelper.MaxDistanceInChunks + PREFETCH_MARGIN_MAX + RETENTION_PADDING_CHUNKS + BASE_UNLOAD_PADDING_CHUNKS, 1, VoxelHelper.WorldChunksXZ);
        return Math.Clamp(retention + BASE_UNLOAD_PADDING_CHUNKS, 1, maxRadius);
    }

    private HashSet<int> DetermineVisibleChunks(Vector3 cameraPosition)
    {
        var result = new HashSet<int>();
        var viewDistance = GetActiveLoadDistance();
        var viewDistanceSq = viewDistance * viewDistance;

        var worldSizeInBlocks = VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ;
        var clampedX = Math.Clamp(cameraPosition.X, 0, worldSizeInBlocks - 1);
        var clampedZ = Math.Clamp(cameraPosition.Z, 0, worldSizeInBlocks - 1);

        var cameraChunkX = Math.Clamp((int)(clampedX / VoxelHelper.ChunkSideSize), 0, VoxelHelper.WorldChunksXZ - 1);
        var cameraChunkZ = Math.Clamp((int)(clampedZ / VoxelHelper.ChunkSideSize), 0, VoxelHelper.WorldChunksXZ - 1);

        for (var dz = -viewDistance; dz <= viewDistance; dz++)
        {
            for (var dx = -viewDistance; dx <= viewDistance; dx++)
            {
                if (dx * dx + dz * dz > viewDistanceSq)
                {
                    continue;
                }

                var chunkX = cameraChunkX + dx;
                var chunkZ = cameraChunkZ + dz;
                if (chunkX < 0 || chunkX >= VoxelHelper.WorldChunksXZ || chunkZ < 0 || chunkZ >= VoxelHelper.WorldChunksXZ)
                {
                    continue;
                }

                var chunkIdx = chunkZ * VoxelHelper.WorldChunksXZ + chunkX;
                result.Add(chunkIdx);
            }
        }

        return result;
    }

    private int CalculateMaxViewChunks()
    {
        // Use retention distance + padding to ensure we have enough slots for all active chunks
        // Active chunks include: Visible + Retained + Pending Unload
        return CalculateMaxViewChunksForRadius(GetUnloadDistanceChunks());
    }

    private int CalculateMaxViewChunksForRadius(int radius)
    {
        var clamped = Math.Clamp(radius, 1, VoxelHelper.WorldChunksXZ);
        return Math.Clamp(VoxelHelper.CalculateCircularChunkCount(clamped), 1, VoxelHelper.TotalChunks);
    }

    private int GetRetentionCapacity() => CalculateMaxViewChunksForRadius(GetRetentionDistanceChunks());

    private void TrackChunkVisibility(HashSet<int> visibleChunks)
    {
        foreach (var chunkIdx in visibleChunks)
        {
            if (activeChunks.ContainsKey(chunkIdx))
            {
                chunkLastVisibleFrame[chunkIdx] = currentFrame;
            }
        }
    }

    private bool EnsureRetentionCapacity(HashSet<int> visibleChunks)
    {
        var retentionCapacity = GetRetentionCapacity();
        if (activeChunks.Count < retentionCapacity)
        {
            return true;
        }

        if (TryEvictRetainedChunk(visibleChunks))
        {
            return true;
        }

        if (currentFrame - lastRetentionGuardLogFrame >= 120)
        {
            lastRetentionGuardLogFrame = currentFrame;
            Log.Warn($"ChunkStreamingManager: Retention budget reached (active={activeChunks.Count}, capacity={retentionCapacity}); deferring new chunk submissions");
        }

        return false;
    }

    private bool TryEvictRetainedChunk(HashSet<int> visibleChunks)
    {
        if (activeChunks.Count == 0)
        {
            return false;
        }

        var cameraChunkX = Math.Clamp((int)(lastCameraPosition.X / VoxelHelper.ChunkSideSize), 0, VoxelHelper.WorldChunksXZ - 1);
        var cameraChunkZ = Math.Clamp((int)(lastCameraPosition.Z / VoxelHelper.ChunkSideSize), 0, VoxelHelper.WorldChunksXZ - 1);
        var retentionDistance = GetRetentionDistanceChunks();
        var retentionDistanceSq = retentionDistance * retentionDistance;
        var unloadDistance = GetUnloadDistanceChunks();
        var unloadDistanceSq = unloadDistance * unloadDistance;

        var candidate = -1;
        var oldestFrame = long.MaxValue;

        foreach (var entry in activeChunks.ToArray())
        {
            var chunkIdx = entry.Key;
            if (visibleChunks.Contains(chunkIdx))
            {
                continue;
            }

            var descriptor = entry.Value;
            if (descriptor.State is TerrainChunkState.Generating or TerrainChunkState.CountingVisibility)
            {
                continue;
            }

            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
            var dx = chunkX - cameraChunkX;
            var dz = chunkZ - cameraChunkZ;
            var distanceSq = dx * dx + dz * dz;

            var lastSeen = chunkLastVisibleFrame.TryGetValue(chunkIdx, out var frame)
                ? frame
                : long.MinValue;
            var framesSinceVisible = lastSeen == long.MinValue ? long.MaxValue : currentFrame - lastSeen;

            if (distanceSq > unloadDistanceSq || (distanceSq > retentionDistanceSq && framesSinceVisible >= RETENTION_FRAME_DELAY))
            {
                candidate = chunkIdx;
                break;
            }

            if (framesSinceVisible >= RETENTION_FRAME_DELAY)
            {
                candidate = chunkIdx;
                break;
            }

            if (lastSeen < oldestFrame)
            {
                oldestFrame = lastSeen;
                candidate = chunkIdx;
            }
        }

        if (candidate < 0)
        {
            return false;
        }

        UnloadChunk(candidate);
        chunkLastVisibleFrame.Remove(candidate);
        return true;
    }

    private void RemoveChunkFromPendingQueues(int chunkIndex)
    {
        RemoveFromQueue(highPriorityPending, chunkIndex);
        RemoveFromQueue(lowPriorityPending, chunkIndex);
    }

    private static void RemoveFromQueue(Queue<int> queue, int chunkIndex)
    {
        if (queue.Count == 0)
        {
            return;
        }

        var items = queue.Count;
        for (var i = 0; i < items; i++)
        {
            var value = queue.Dequeue();
            if (value != chunkIndex)
            {
                queue.Enqueue(value);
            }
        }
    }

    private void QueueNewChunks(HashSet<int> visibleChunks)
    {
        var newChunks = visibleChunks.Except(activeChunks.Keys).ToList();

        if (newChunks.Count > 0)
        {
            newChunks.Sort((a, b) => CalculatePriority(a).CompareTo(CalculatePriority(b)));

            foreach (var chunkIdx in newChunks)
            {
                if (!EnsureRetentionCapacity(visibleChunks))
                {
                    break;
                }

                world.GetOrCreateChunkContainer(chunkIdx);

                var descriptor = new ChunkDescriptor
                {
                    ChunkIndex = chunkIdx,
                    State = TerrainChunkState.Pending,
                    CommandSlot = -1,
                };

                activeChunks[chunkIdx] = descriptor;

                var dist = CalculatePriority(chunkIdx);
                if (dist <= HIGH_PRIORITY_DISTANCE * HIGH_PRIORITY_DISTANCE)
                {
                    highPriorityPending.Enqueue(chunkIdx);
                }
                else
                {
                    lowPriorityPending.Enqueue(chunkIdx);
                }
            }
        }

        foreach (var chunkIdx in visibleChunks)
        {
            if (activeChunks.TryGetValue(chunkIdx, out var desc) && desc.State == TerrainChunkState.Pending)
            {
                var dist = CalculatePriority(chunkIdx);
                if (dist <= HIGH_PRIORITY_DISTANCE * HIGH_PRIORITY_DISTANCE)
                {
                    highPriorityPending.Enqueue(chunkIdx);
                }
            }
        }
    }

    public void Update(Vector3 cameraPosition)
    {
        currentFrame++;
        lastCameraPosition = cameraPosition;

        ProcessPendingSeamRefreshes();

        var visibleChunks = DetermineVisibleChunks(cameraPosition);
        QueueNewChunks(visibleChunks);
        TrackChunkVisibility(visibleChunks);

        SubmitPendingBatches();
        PollCompletedBatches();
        ProcessDirtyChunks(); // Re-mesh dirty chunks (e.g., neighbors that loaded)
        DrainCompletedCpuMeshes();
        UnloadDistantChunks(cameraPosition, visibleChunks);
    }

    // Re-meshing is deferred for chunks at the edge of visible distance.
    // Only chunks within (loadDistance - RemeshProximityMargin) are re-meshed immediately.
    // This avoids wasted work on far chunks that may be unloaded when player turns.
    private const int RemeshProximityMargin = 2;
    
    /// <summary>
    /// Process chunks marked Dirty - re-mesh them to pick up neighbor data.
    /// Only re-meshes chunks that are close enough to the camera.
    /// Far chunks stay Dirty until they get closer or are unloaded.
    /// </summary>
    private void ProcessDirtyChunks()
    {
        // Calculate the proximity threshold - only re-mesh chunks within this distance
        var loadDistance = GetActiveLoadDistance();
        var remeshDistanceSq = (loadDistance - RemeshProximityMargin) * (loadDistance - RemeshProximityMargin);
        
        // Get camera chunk position
        var worldSizeInBlocks = VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ;
        var clampedX = Math.Clamp(lastCameraPosition.X, 0, worldSizeInBlocks - 1);
        var clampedZ = Math.Clamp(lastCameraPosition.Z, 0, worldSizeInBlocks - 1);
        var cameraChunkX = (int)(clampedX / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)(clampedZ / VoxelHelper.ChunkSideSize);
        
        var dirtyChunks = activeChunks.Values
            .Where(c => c.State == TerrainChunkState.Dirty)
            .Where(c =>
            {
                // Only re-mesh chunks that are close enough to camera
                var chunkX = c.ChunkIndex % VoxelHelper.WorldChunksXZ;
                var chunkZ = c.ChunkIndex / VoxelHelper.WorldChunksXZ;
                var dx = chunkX - cameraChunkX;
                var dz = chunkZ - cameraChunkZ;
                return dx * dx + dz * dz <= remeshDistanceSq;
            })
            .OrderBy(c => CalculatePriority(c.ChunkIndex))
            .Take(4) // Reduced from 8 to further limit per-frame work
            .ToList();

        foreach (var desc in dirtyChunks)
        {
            var chunkIdx = desc.ChunkIndex;
            
            // Update state before scheduling to prevent re-processing
            var updatedDesc = desc;
            updatedDesc.State = TerrainChunkState.CountingVisibility;
            activeChunks[chunkIdx] = updatedDesc;
            
            ScheduleCpuMeshing(chunkIdx);
            Log.Debug($"Re-meshing dirty chunk {chunkIdx} (close to camera)");
        }
    }

    /// <summary>
    /// Mark a chunk as dirty to trigger re-mesh (e.g., when neighbor loads).
    /// Only marks Ready chunks - others are already being processed.
    /// </summary>
    private void MarkChunkDirty(int chunkIdx, string reason)
    {
        if (!activeChunks.TryGetValue(chunkIdx, out var desc))
            return;
            
        if (desc.State != TerrainChunkState.Ready)
            return;
            
        desc.State = TerrainChunkState.Dirty;
        activeChunks[chunkIdx] = desc;
        Log.Debug($"Marked chunk {chunkIdx} dirty: {reason}");
    }

    /// <summary>
    /// Notify neighbors that this chunk's voxel data is now available.
    /// Neighbors that are Ready will be marked Dirty to re-mesh with correct boundary data.
    /// </summary>
    private void NotifyNeighborsChunkReady(int chunkIdx)
    {
        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

        void NotifyNeighbor(int dx, int dz)
        {
            var nx = chunkX + dx;
            var nz = chunkZ + dz;
            if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                return;
            var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;
            
            // If neighbor is ready, propagate light between them
            if (activeChunks.TryGetValue(neighborIdx, out var neighborDesc) && neighborDesc.State == TerrainChunkState.Ready)
            {
                // We need mutable access to ChunkData. 
                // ChunkVoxelDataCache stores ChunkData, but TryGetReadOnly returns a view.
                // However, we know the cache stores the actual ChunkData object.
                // We can use a new method on cache or just rely on the fact that we are on the main thread
                // and we can get the data if we expose it.
                // For now, let's assume we can get it via a new method on ChunkVoxelDataCache.
                
                if (chunkVoxelCache.TryGetChunkData(chunkIdx, out var centerData) && centerData != null &&
                    chunkVoxelCache.TryGetChunkData(neighborIdx, out var neighborData) && neighborData != null)
                {
                    LightingCalculator.PropagateNeighborLight(centerData, neighborData, dx, dz);
                    // Mark center dirty too, as it might have received light from neighbor
                    MarkChunkDirty(chunkIdx, $"received light from neighbor {neighborIdx}");
                }
                
                MarkChunkDirty(neighborIdx, $"neighbor {chunkIdx} became ready");
            }
        }

        NotifyNeighbor(-1, 0);
        NotifyNeighbor(1, 0);
        NotifyNeighbor(0, -1);
        NotifyNeighbor(0, 1);
    }
    
    /// <summary>
    /// Process chunks that were waiting for this chunk to regenerate before remeshing.
    /// Used when a block edit at chunk boundary requires neighbor to see updated voxels.
    /// </summary>
    private void ProcessDeferredRemeshWaiters(int chunkIdx)
    {
        if (!deferredRemeshWaiters.TryGetValue(chunkIdx, out var waiters) || waiters.Count == 0)
            return;
            
        foreach (var waiterIdx in waiters)
        {
            if (!activeChunks.TryGetValue(waiterIdx, out var waiterDesc))
                continue;
                
            if (waiterDesc.State == TerrainChunkState.Ready)
            {
                // Waiter is ready - trigger remesh (not regeneration)
                waiterDesc.State = TerrainChunkState.CountingVisibility;
                activeChunks[waiterIdx] = waiterDesc;
                ScheduleCpuMeshing(waiterIdx);
                Log.Info($"Deferred remesh triggered for chunk {waiterIdx} (waited for {chunkIdx})");
            }
            else
            {
                Log.Debug($"Deferred remesh skipped for chunk {waiterIdx} (state={waiterDesc.State})");
            }
        }
        
        deferredRemeshWaiters.Remove(chunkIdx);
    }

    /// <summary>
    private void ProcessPendingSeamRefreshes()
    {
        if (!SeamRefreshEnabled)
        {
            pendingSeamRefresh.Clear();
            return;
        }

        if (pendingSeamRefresh.Count == 0)
        {
            return;
        }

        foreach (var chunkIdx in pendingSeamRefresh.ToArray())
        {
            if (!activeChunks.TryGetValue(chunkIdx, out var desc))
            {
                pendingSeamRefresh.Remove(chunkIdx);
                continue;
            }

            if (desc.State != TerrainChunkState.Ready)
            {
                continue;
            }

            var lastFrame = lastSeamRefreshFrame.TryGetValue(chunkIdx, out var frame)
                ? frame
                : long.MinValue;

            if (currentFrame - lastFrame < SeamRefreshCooldownFrames)
            {
                continue;
            }

            pendingSeamRefresh.Remove(chunkIdx);
            RequestSeamRefresh(chunkIdx, "Pending seam refresh");
        }
    }

    private void RequestSeamRefresh(int chunkIdx, string reason, bool force = false)
    {
        if (!SeamRefreshEnabled)
        {
            return;
        }

        pendingSeamRefresh.Remove(chunkIdx);

        if (!activeChunks.TryGetValue(chunkIdx, out var desc))
        {
            return;
        }

        if (desc.State == TerrainChunkState.Ready)
        {
            if (!force &&
                lastSeamRefreshFrame.TryGetValue(chunkIdx, out var lastFrame) &&
                currentFrame - lastFrame < SeamRefreshCooldownFrames)
            {
                if (pendingSeamRefresh.Add(chunkIdx))
                {
                    Log.Debug($"Seam refresh cooldown for chunk {chunkIdx} ({reason})");
                }
                return;
            }

            desc.State = TerrainChunkState.Dirty;
            activeChunks[chunkIdx] = desc;
            highPriorityPending.Enqueue(chunkIdx);
            lastSeamRefreshFrame[chunkIdx] = currentFrame;
            Log.Debug($"Queued seam refresh for chunk {chunkIdx}: {reason}");
        }
        else
        {
            if (pendingSeamRefresh.Add(chunkIdx))
            {
                Log.Debug($"Seam refresh deferred for chunk {chunkIdx} (state={desc.State}) reason={reason}");
            }
        }
    }


    private void ClearPendingCpuMeshingQueue()
    {
        cpuMeshResults.Clear();

        while (cpuMeshingJobs.TryDequeueResult(out _))
        {
        }
    }

    private void ScheduleCpuMeshing(int chunkIdx)
    {
        if (!chunkVoxelCache.TryGetVersion(chunkIdx, out var cacheVersion) || cacheVersion <= 0)
        {
            Log.Debug($"ScheduleCpuMeshing: chunk {chunkIdx} missing cache version; enqueueing with version=0");
        }

        cpuMeshingJobs.Enqueue(chunkIdx, 0, cacheVersion);
    }

    private void DrainCompletedCpuMeshes()
    {
        var received = false;
        while (cpuMeshingJobs.TryDequeueResult(out var mesh))
        {
            cpuMeshResults[mesh.ChunkIndex] = mesh;
            received = true;
        }

        if ((received || cpuMeshResults.Count > 0) && meshBuffers != null && terrainRenderer != null)
        {
            ProcessCpuMeshResults();
        }
    }

    private void ProcessCpuMeshResults()
    {
        if (meshBuffers == null || terrainRenderer == null || cpuMeshResults.Count == 0)
        {
            return;
        }

        var processed = false;
        foreach (var entry in cpuMeshResults.ToArray())
        {
            if (TryUploadCpuMesh(entry.Value))
            {
                cpuMeshResults.Remove(entry.Key);
                processed = true;
            }
        }

        if (processed)
        {
            RefreshRendererBuffers("CPU meshing uploads");
        }
    }

    private bool TryUploadCpuMesh(CpuChunkMesh mesh)
    {
        if (meshBuffers == null || terrainRenderer == null)
        {
            return false;
        }

        if (!activeChunks.TryGetValue(mesh.ChunkIndex, out var descriptor))
        {
            chunkVoxelCache.TryRelease(mesh.ChunkIndex);
            return true;
        }

        var faceCount = Math.Max(0, mesh.VisibleFaceCount);
        var translucentFaceCount = Math.Clamp(mesh.TranslucentFaceCount, 0, faceCount);
        var opaqueFaceCount = Math.Max(0, faceCount - translucentFaceCount);
        var vertexCount = faceCount * 4;
        var indexCount = faceCount * 6;

        if (mesh.IndexData.Length != indexCount)
        {
            Log.Warn($"Chunk {mesh.ChunkIndex} index count mismatch: expected {indexCount}, got {mesh.IndexData.Length}");
            indexCount = mesh.IndexData.Length;
            faceCount = indexCount / 6;
            vertexCount = faceCount * 4;
        }

        var expectedVertexEntries = vertexCount * 2; // packed position + attributes per vertex
        if (mesh.VertexData.Length != expectedVertexEntries)
        {
            Log.Warn($"Chunk {mesh.ChunkIndex} vertex data mismatch: expected {expectedVertexEntries}, got {mesh.VertexData.Length}");
        }

        if (descriptor.VisibleVoxelCount > 0)
        {
            meshBuffers.FreeVertexRegion((uint)descriptor.AtlasOffset, (uint)(descriptor.VisibleVoxelCount * 4));
            if (descriptor.IndexOffset >= 0)
            {
                meshBuffers.FreeIndexRegion((uint)descriptor.IndexOffset, (uint)(descriptor.VisibleVoxelCount * 6));
            }
        }

        var vertexOffset = vertexCount > 0 ? (int)meshBuffers.AllocateVertexRegion((uint)vertexCount) : -1;
        var indexOffset = indexCount > 0 ? (int)meshBuffers.AllocateIndexRegion((uint)indexCount) : -1;

        UploadCpuMeshData(mesh, vertexOffset, vertexCount, indexOffset, indexCount);

        var slot = descriptor.CommandSlot >= 0 ? descriptor.CommandSlot : meshBuffers.AllocateCommandSlot();
        WriteIndirectCommands(slot, mesh.ChunkIndex, vertexOffset, indexOffset, (uint)opaqueFaceCount, (uint)translucentFaceCount);

        // Placeholder mask is always 0 now - we wait for all neighbors before meshing
        byte placeholderMask = 0;

        Log.Debug($"Chunk {mesh.ChunkIndex} mesh upload faces={faceCount} translucent={translucentFaceCount} mask=0x{placeholderMask:X2} cacheVer={mesh.CacheVersion} enqueue={mesh.EnqueueId} build={mesh.BuildId}");

        var refreshedDescriptor = new ChunkDescriptor
        {
            ChunkIndex = mesh.ChunkIndex,
            AtlasOffset = vertexOffset >= 0 ? vertexOffset : 0,
            IndexOffset = indexOffset >= 0 ? indexOffset : 0,
            CommandSlot = slot,
            VisibleVoxelCount = faceCount,
            State = TerrainChunkState.Ready,
            Fence = IntPtr.Zero,
            GenerationStartFrame = currentFrame,
            PlaceholderMask = placeholderMask,
        };
        activeChunks[mesh.ChunkIndex] = refreshedDescriptor;

        if (pendingSeamRefresh.Contains(mesh.ChunkIndex))
        {
            RequestSeamRefresh(mesh.ChunkIndex, "Deferred seam dependency resolved", force: true);
        }

        return true;
    }

    private void UploadCpuMeshData(CpuChunkMesh mesh, int vertexOffset, int vertexCount, int indexOffset, int indexCount)
    {
        if (meshBuffers == null)
        {
            return;
        }

        var vertexSpan = ReadOnlySpan<uint>.Empty;
        if (vertexCount > 0 && vertexOffset >= 0 && mesh.VertexData.Length > 0)
        {
            var expectedEntries = vertexCount * 2;
            var safeLength = Math.Min(expectedEntries, mesh.VertexData.Length);
            vertexSpan = mesh.VertexData.AsSpan(0, safeLength);
        }

        var indexSpan = ReadOnlySpan<uint>.Empty;
        if (indexCount > 0 && indexOffset >= 0 && mesh.IndexData.Length > 0)
        {
            var safeLength = Math.Min(indexCount, mesh.IndexData.Length);
            indexSpan = mesh.IndexData.AsSpan(0, safeLength);
        }

        meshBuffers.UploadMeshData(vertexSpan, vertexOffset, indexSpan, indexOffset);
    }

    private void WriteIndirectCommands(int slot, int chunkIndex, int vertexOffset, int indexOffset, uint opaqueFaces, uint waterFaces)
    {
        if (meshBuffers == null || slot < 0)
        {
            return;
        }

        var baseVertex = vertexOffset >= 0 ? (uint)vertexOffset : 0u;
        var firstIndex = indexOffset >= 0 ? (uint)indexOffset : 0u;

        Span<uint> command =
        [
            opaqueFaces * 6u,
            1u,
            firstIndex,
            baseVertex,
            0u,
            waterFaces * 6u,
            1u,
            firstIndex + opaqueFaces * 6u,
            baseVertex,
            0u,
        ];
        unsafe
        {
            fixed (uint* cmdPtr = command)
            {
                GL.NamedBufferSubData((int)meshBuffers.IndirectDrawBuffer, (IntPtr)(slot * 40), 40, (IntPtr)cmdPtr);
            }
        }

        GL.NamedBufferSubData((int)meshBuffers.ChunkInfoBuffer, (IntPtr)(slot * sizeof(int)), sizeof(int), ref chunkIndex);
    }

    private void RefreshRendererBuffers(string reason)
    {
        if (meshBuffers == null || terrainRenderer == null)
        {
            return;
        }

        var readyChunks = activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready).ToList();
        var totalFaces = (uint)readyChunks.Sum(c => Math.Max(0, c.VisibleVoxelCount));
        var totalVerticesInBuffer = meshBuffers.CurrentVertexBufferEnd;
        var totalIndicesInBuffer = meshBuffers.CurrentIndexBufferEnd;

        terrainRenderer.SetupBuffers(meshBuffers, totalVerticesInBuffer, totalFaces);
        Log.Debug($"ChunkStreamingManager: Renderer refreshed after {reason} (ready={readyChunks.Count}, faces={totalFaces}, vertices={totalVerticesInBuffer}, indices={totalIndicesInBuffer})");
    }

    private void SubmitPendingBatches()
    {
        if (highPriorityPending.Count == 0 && lowPriorityPending.Count == 0)
            return;

        var availableSlots = Math.Clamp(maxConcurrentCpuGenerations - inFlightCpuGenerations, 0, MAX_CHUNKS_PER_BATCH);
        if (availableSlots == 0)
            return;

        var batchIndices = new List<int>();
        var processedIndices = new HashSet<int>();

        while (batchIndices.Count < availableSlots && highPriorityPending.Count > 0)
        {
            var idx = highPriorityPending.Dequeue();
            if (processedIndices.Contains(idx))
                continue;

            if (activeChunks.TryGetValue(idx, out var desc) &&
                (desc.State == TerrainChunkState.Pending || desc.State == TerrainChunkState.Dirty))
            {
                batchIndices.Add(idx);
                processedIndices.Add(idx);
            }
        }

        while (batchIndices.Count < availableSlots && lowPriorityPending.Count > 0)
        {
            var idx = lowPriorityPending.Dequeue();
            if (processedIndices.Contains(idx))
                continue;

            if (activeChunks.TryGetValue(idx, out var desc) &&
                (desc.State == TerrainChunkState.Pending || desc.State == TerrainChunkState.Dirty))
            {
                batchIndices.Add(idx);
                processedIndices.Add(idx);
            }
        }

        if (batchIndices.Count == 0)
            return;

        // Sort by distance to camera for priority (closest first)
        batchIndices.Sort((a, b) => CalculatePriority(a).CompareTo(CalculatePriority(b)));

        var submitted = 0;
        foreach (var idx in batchIndices)
        {
            if (!TryBeginChunkGeneration(idx))
            {
                RequeueChunk(idx);
                continue;
            }

            var blockIdEdits = BuildBlockIdEdits(idx);
            cpuGenerationJobs.Enqueue(idx, blockIdEdits);
            submitted++;
        }

        if (submitted > 0)
        {
            Log.Debug($"ChunkStreamingManager: Queued {submitted} chunks for CPU generation (frame {currentFrame}, batchSize={batchIndices.Count})");
        }
    }

    private bool TryBeginChunkGeneration(int chunkIdx)
    {
        if (!activeChunks.TryGetValue(chunkIdx, out var descriptor))
        {
            return false;
        }

        if (descriptor.State is TerrainChunkState.Generating or TerrainChunkState.CountingVisibility)
        {
            return false;
        }

        if (inFlightCpuGenerations >= maxConcurrentCpuGenerations)
        {
            return false;
        }

        descriptor.State = TerrainChunkState.Generating;
        descriptor.GenerationStartFrame = currentFrame;
        descriptor.PlaceholderMask = 0;
        activeChunks[chunkIdx] = descriptor;
        inFlightCpuGenerations++;
        return true;
    }

    private void TrackCpuGenerationCompletion(int chunkIdx)
    {
        if (inFlightCpuGenerations > 0)
        {
            inFlightCpuGenerations--;
        }

        if (!activeChunks.TryGetValue(chunkIdx, out var descriptor))
        {
            return;
        }

        descriptor.GenerationStartFrame = currentFrame;
        activeChunks[chunkIdx] = descriptor;
    }

    private void RequeueChunk(int chunkIdx)
    {
        if (!activeChunks.TryGetValue(chunkIdx, out var descriptor))
        {
            return;
        }

        if (descriptor.State is not (TerrainChunkState.Pending or TerrainChunkState.Dirty))
        {
            descriptor.State = TerrainChunkState.Pending;
            activeChunks[chunkIdx] = descriptor;
        }

        var dist = CalculatePriority(chunkIdx);
        if (dist <= HIGH_PRIORITY_DISTANCE * HIGH_PRIORITY_DISTANCE)
        {
            highPriorityPending.Enqueue(chunkIdx);
        }
        else
        {
            lowPriorityPending.Enqueue(chunkIdx);
        }
    }

    private void PollCompletedBatches()
    {
        var processed = 0;
        while (cpuGenerationJobs.TryDequeueResult(out var result))
        {
            var chunkIdx = result.ChunkIndex;
            TrackCpuGenerationCompletion(chunkIdx);

            if (!activeChunks.TryGetValue(chunkIdx, out var descriptor))
            {
                chunkVoxelCache.TryRelease(chunkIdx);
                continue;
            }

            if (descriptor.State != TerrainChunkState.Generating)
            {
                chunkVoxelCache.TryRelease(chunkIdx);
                Log.Debug($"ChunkStreamingManager: Dropping CPU generation result for chunk {chunkIdx} (state={descriptor.State})");
                continue;
            }

            ApplyCollisionResults(chunkIdx, result.Generation);
            
            // MINECRAFT-STYLE: Mesh immediately, treat missing neighbors as air
            // Then notify neighbors so they can re-mesh with correct boundary data
            descriptor.State = TerrainChunkState.CountingVisibility;
            descriptor.GenerationStartFrame = currentFrame;
            activeChunks[chunkIdx] = descriptor;
            
            ScheduleCpuMeshing(chunkIdx);
            
            // Notify neighbors - they will re-mesh to pick up this chunk's data
            NotifyNeighborsChunkReady(chunkIdx);
            
            // Process deferred remesh waiters (neighbors waiting for this chunk's edit to complete)
            ProcessDeferredRemeshWaiters(chunkIdx);
            
            processed++;
        }

        if (processed > 0)
        {
            Log.Debug($"ChunkStreamingManager: Applied CPU generation results for {processed} chunks (frame {currentFrame})");
        }
    }

    private IReadOnlyDictionary<int, BlockId>? BuildBlockIdEdits(int chunkIdx)
    {
        if (!chunkEdits.TryGetValue(chunkIdx, out var edits) || edits.Count == 0)
        {
            return null;
        }

        // edits are already BlockId, no conversion needed
        return edits;
    }

    private void ApplyCollisionResults(int chunkIdx, CpuTerrainGenerator.ChunkGenerationResult result)
    {
        CollisionManager.UpdateChunkData(chunkIdx, result.Collision);

        var chunk = world[chunkIdx];
        if (chunk == null)
        {
            Log.Warn($"ChunkStreamingManager: Chunk {chunkIdx} container missing during collision upload");
            return;
        }

        chunk.ApplyColumnSpansForCollision(result.SpanPairs, result.SpanCounts, result.SpanTypes);
    }

    private int CalculatePriority(int chunkIndex)
    {
        // Calculate actual distance to camera
        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;

        var chunkWorldX = chunkX * VoxelHelper.ChunkSideSize + VoxelHelper.ChunkSideSize / 2.0f;
        var chunkWorldZ = chunkZ * VoxelHelper.ChunkSideSize + VoxelHelper.ChunkSideSize / 2.0f;

        var dx = chunkWorldX - lastCameraPosition.X;
        var dz = chunkWorldZ - lastCameraPosition.Z;
        var distanceSq = dx * dx + dz * dz;

        // Return distance squared (lower = higher priority)
        return (int)distanceSq;
    }

    /// <summary>
    /// Unload chunks that are too far from camera (Phase 5)
    /// Uses hysteresis to avoid thrashing (load/unload cycles)
    /// </summary>
    private void UnloadDistantChunks(Vector3 cameraPosition, HashSet<int> visibleChunks)
    {
        if (activeChunks.Count == 0)
            return;

        var cameraChunkX = (int)(cameraPosition.X / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)(cameraPosition.Z / VoxelHelper.ChunkSideSize);

        var unloadRadius = GetUnloadDistanceChunks();
        var unloadDistanceSq = unloadRadius * unloadRadius;
        var retentionRadius = GetRetentionDistanceChunks();
        var retentionDistanceSq = retentionRadius * retentionRadius;
        
        var chunksToUnload = new List<int>();

        // Find chunks beyond unload distance
        foreach (var kvp in activeChunks)
        {
            var chunkIdx = kvp.Key;
            var desc = kvp.Value;

            // Don't unload chunks that are still in-flight (actively being processed)
            if (desc.State is TerrainChunkState.Generating or
                TerrainChunkState.Pending or
                TerrainChunkState.CountingVisibility)
                continue;
            
            // Dirty chunks CAN be unloaded if they're far away - they'll re-generate when needed

            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

            var dx = chunkX - cameraChunkX;
            var dz = chunkZ - cameraChunkZ;
            var distanceSq = dx * dx + dz * dz;

            if (visibleChunks.Contains(chunkIdx))
                continue;

            var lastSeen = chunkLastVisibleFrame.TryGetValue(chunkIdx, out var frame) ? frame : long.MinValue;
            var framesSinceVisible = lastSeen == long.MinValue ? long.MaxValue : currentFrame - frame;
            var outsideUnload = distanceSq > unloadDistanceSq;
            var outsideRetention = distanceSq > retentionDistanceSq;

            if (!outsideUnload && !outsideRetention && framesSinceVisible < RETENTION_FRAME_DELAY)
                continue;

            if (!outsideUnload && framesSinceVisible < RETENTION_FRAME_DELAY)
                continue;

            chunksToUnload.Add(chunkIdx);
        }

        if (chunksToUnload.Count == 0)
            return;

        // Sort by distance (furthest first) and limit unloads per frame
        chunksToUnload.Sort((a, b) =>
        {
            var aX = a % VoxelHelper.WorldChunksXZ;
            var aZ = a / VoxelHelper.WorldChunksXZ;
            var bX = b % VoxelHelper.WorldChunksXZ;
            var bZ = b / VoxelHelper.WorldChunksXZ;

            var aDist = (aX - cameraChunkX) * (aX - cameraChunkX) + (aZ - cameraChunkZ) * (aZ - cameraChunkZ);
            var bDist = (bX - cameraChunkX) * (bX - cameraChunkX) + (bZ - cameraChunkZ) * (bZ - cameraChunkZ);

            return bDist.CompareTo(aDist); // Furthest first
        });

        var unloadCount = Math.Min(chunksToUnload.Count, MAX_UNLOADS_PER_FRAME);

        for (var i = 0; i < unloadCount; i++)
        {
            var chunkIdx = chunksToUnload[i];
            UnloadChunk(chunkIdx);
        }

        if (unloadCount > 0)
        {
            Log.Info($"Unloaded {unloadCount} chunks (camera: ({cameraChunkX}, {cameraChunkZ}), active: {activeChunks.Count})");
        }
    }

    /// <summary>
    /// Unload a single chunk and clean up its resources (Phase 5)
    /// Phase 5.3: Frees BOTH vertex and index buffer regions for reuse
    /// </summary>
    private void UnloadChunk(int chunkIndex)
    {
        if (!activeChunks.TryGetValue(chunkIndex, out var desc))
            return;

        RemoveChunkFromPendingQueues(chunkIndex);

        // Clean up fence if present
        if (desc.Fence != IntPtr.Zero)
        {
            try { GL.DeleteSync(desc.Fence); } catch { }
        }

        // Phase 5.3: Free BOTH vertex and index buffer regions for reuse
        if (desc.AtlasOffset >= 0 && desc.VisibleVoxelCount > 0 && meshBuffers != null)
        {
            var verticesPerFace = 4;
            var indicesPerFace = 6;

            // Free vertex region
            meshBuffers.FreeVertexRegion((uint)desc.AtlasOffset, (uint)(desc.VisibleVoxelCount * verticesPerFace));
            //Log.Debug($"Freed vertex region for chunk {chunkIndex}: offset={desc.AtlasOffset}, size={desc.VisibleVoxelCount * verticesPerFace}");

            // Free index region
            if (desc.IndexOffset >= 0)
            {
                meshBuffers.FreeIndexRegion((uint)desc.IndexOffset, (uint)(desc.VisibleVoxelCount * indicesPerFace));
                //Log.Debug($"Freed index region for chunk {chunkIndex}: offset={desc.IndexOffset}, size={desc.VisibleVoxelCount * indicesPerFace}");
            }

            // Free command slot
            if (desc.CommandSlot >= 0)
            {
                meshBuffers.FreeCommandSlot(desc.CommandSlot);
                //Log.Debug($"Freed command slot for chunk {chunkIndex}: slot={desc.CommandSlot}");
            }
        }

        // Remove from active chunks
        activeChunks.Remove(chunkIndex);
        pendingSeamRefresh.Remove(chunkIndex);
        lastSeamRefreshFrame.Remove(chunkIndex);
        chunkLastVisibleFrame.Remove(chunkIndex);

        // Remove collision data
        CollisionManager.RemoveChunkData(chunkIndex);

        // Release CPU-side voxel copy
        chunkVoxelCache.TryRelease(chunkIndex);

        //Log.Debug($"Unloaded chunk {chunkIndex}");
    }

    /// <summary>
    /// Get detailed streaming progress information for loading screens.
    /// </summary>
    public (int targetChunks, int loadedChunks, int pendingChunks, int generatingChunks, float progressPercent) GetStreamingProgress()
    {
        var (total, pending, generating, ready) = GetStats();

        // Calculate target based on current (prefetch-aware) load distance
        var targetChunks = VoxelHelper.CalculateCircularChunkCount(GetActiveLoadDistance());

        // Progress is based on ready chunks vs target
        var progressPercent = ready / (float)Math.Max(1, targetChunks) * 100f;
        progressPercent = Math.Clamp(progressPercent, 0f, 100f);

        return (targetChunks, ready, pending, generating, progressPercent);
    }

    /// <summary>
    /// Get memory usage statistics
    /// </summary>
    public (long totalBytes, long voxelBytes, long visibilityBytes, long compactBytes) GetMemoryStats()
    {
        // Voxel data is now CPU-only, so GPU voxel bytes is 0
        long voxelBytes = 0;
        long columnBytes = 0;
        
        // Mesh buffers contain the actual mesh data (VBO, IBO, Indirect)
        var visibilityBytes = meshBuffers?.GetAllocatedBytes() ?? 0;
        
        // Compact bytes was used for renderer-specific data, but now buffers are shared.
        // We can use this slot for texture memory estimate if needed, or just 0.
        var compactBytes = 0L;

        var totalBytes = voxelBytes + columnBytes + visibilityBytes + compactBytes;

        return (totalBytes, voxelBytes, visibilityBytes, compactBytes);
    }

    // ========================================================================
    // Block Edit Operations (Player Interactions)
    // ========================================================================

    /// <summary>
    /// Apply a block edit (break or place) at world position
    /// Marks affected chunk(s) as dirty and queues for regeneration
    /// </summary>
    public void ApplyBlockEdit(Vector3 worldPosition, BlockId blockId, bool isBreaking)
    {
        // Convert world position to chunk coordinates
        var chunkX = (int)(worldPosition.X / VoxelHelper.ChunkSideSize);
        var chunkZ = (int)(worldPosition.Z / VoxelHelper.ChunkSideSize);

        // Bounds check
        if (chunkX < 0 || chunkX >= VoxelHelper.WorldChunksXZ ||
            chunkZ < 0 || chunkZ >= VoxelHelper.WorldChunksXZ)
        {
            Log.Warn($"Block edit out of world bounds: {worldPosition}");
            return;
        }

        var chunkIdx = chunkZ * VoxelHelper.WorldChunksXZ + chunkX;

        // Cached column heights are stale after edits until regeneration completes

        // Convert to local voxel coordinates within chunk
        var localX = (int)(worldPosition.X % VoxelHelper.ChunkSideSize);
        var localY = (int)worldPosition.Y;
        var localZ = (int)(worldPosition.Z % VoxelHelper.ChunkSideSize);

        // Bounds check
        if (localX < 0 || localX >= VoxelHelper.ChunkSideSize ||
            localY < 0 || localY >= VoxelHelper.ChunkYSize ||
            localZ < 0 || localZ >= VoxelHelper.ChunkSideSize)
        {
            Log.Warn($"Block edit out of chunk bounds: local({localX},{localY},{localZ})");
            return;
        }

        // Calculate voxel index within chunk (Y-major layout to match compute shader)
        var voxelIdx = localY * VoxelHelper.ChunkSideSizeSquare +
                       localZ * VoxelHelper.ChunkSideSize +
                       localX;

        // DEBUG: Verify index calculation
        // Log.Debug($"ApplyBlockEdit: local({localX},{localY},{localZ}) -> voxelIdx {voxelIdx} (Y*256 + Z*16 + X)");

        // Mark voxel as edited in edit mask
        // FIX: If breaking a block underwater, replace with Water instead of Air
        if (isBreaking && (int)worldPosition.Y <= VoxelHelper.WaterLevel)
        {
            blockId = BlockId.Water;
        }

        MarkVoxelEdited(chunkIdx, voxelIdx, blockId, isBreaking);

        // Mark chunk as dirty (needs regeneration)
        MarkChunkDirty(chunkIdx);

        // If edit is on chunk boundary, defer neighbor remesh until edited chunk regenerates
        // This ensures the neighbor sees the updated voxel cache when it remeshes
        void DeferNeighborRemesh(int neighborIdx)
        {
            if (!activeChunks.TryGetValue(neighborIdx, out var neighborDesc))
                return;
            if (neighborDesc.State != TerrainChunkState.Ready)
                return; // Neighbor is already processing
                
            // Add neighbor to deferred remesh list - will be triggered when chunkIdx completes
            if (!deferredRemeshWaiters.TryGetValue(chunkIdx, out var waiters))
            {
                waiters = [];
                deferredRemeshWaiters[chunkIdx] = waiters;
            }
            waiters.Add(neighborIdx);
            Log.Info($"Block edit: neighbor {neighborIdx} deferred remesh until chunk {chunkIdx} regenerates");
        }
        
        if (localX == 0 && chunkX > 0)
        {
            var neighborIdx = (chunkZ) * VoxelHelper.WorldChunksXZ + (chunkX - 1);
            DeferNeighborRemesh(neighborIdx);
        }
        if (localX == VoxelHelper.ChunkSideSize - 1 && chunkX < VoxelHelper.WorldChunksXZ - 1)
        {
            var neighborIdx = (chunkZ) * VoxelHelper.WorldChunksXZ + (chunkX + 1);
            DeferNeighborRemesh(neighborIdx);
        }
        if (localZ == 0 && chunkZ > 0)
        {
            var neighborIdx = (chunkZ - 1) * VoxelHelper.WorldChunksXZ + chunkX;
            DeferNeighborRemesh(neighborIdx);
        }
        if (localZ == VoxelHelper.ChunkSideSize - 1 && chunkZ < VoxelHelper.WorldChunksXZ - 1)
        {
            var neighborIdx = (chunkZ + 1) * VoxelHelper.WorldChunksXZ + chunkX;
            DeferNeighborRemesh(neighborIdx);
        }

        Log.Debug($"Block edit at world{worldPosition} → chunk{chunkIdx} local({localX},{localY},{localZ}) voxel{voxelIdx} block={blockId} breaking={isBreaking}");

        // Save edits immediately to prevent data loss
        SaveChunkEdits(chunkIdx);
    }

    /// <summary>
    /// Mark a specific voxel as edited in the edit dictionary
    /// This will be read by the CPU generator to override generated terrain
    /// </summary>
    private void MarkVoxelEdited(int chunkIdx, int voxelIdx, BlockId blockId, bool isBreaking)
    {
        if (!chunkEdits.TryGetValue(chunkIdx, out var value))
        {
            value = [];
            chunkEdits[chunkIdx] = value;
        }

        value[voxelIdx] = blockId;

        Log.Debug($"Voxel edit: chunk={chunkIdx} voxel={voxelIdx} block={blockId} breaking={isBreaking}");
    }


    /// <summary>
    /// Mark a chunk as dirty (needs regeneration)
    /// Dirty chunks will be regenerated on the next update cycle
    /// Uses incremental updates to preserve buffer regions
    /// </summary>
    private void MarkChunkDirty(int chunkIdx)
    {
        if (!activeChunks.TryGetValue(chunkIdx, out var desc))
        {
            // Chunk not loaded yet, will be generated fresh when loaded
            return;
        }

        // Mark as pending regeneration
        if (desc.State == TerrainChunkState.Ready)
        {
            // Keep existing buffer offset - we'll update in-place
            // Free the old buffer region only if we can't reuse it
            // (size might change after edit, but usually it's similar)
            desc.State = TerrainChunkState.Dirty;
            activeChunks[chunkIdx] = desc;
            highPriorityPending.Enqueue(chunkIdx);

            Log.Debug($"Marked chunk {chunkIdx} as dirty (was Ready, now Dirty for incremental update)");
        }
        else if (desc.State == TerrainChunkState.Dirty)
        {
            // Already dirty, no need to re-queue
            Log.Debug($"Chunk {chunkIdx} already marked as dirty");
        }
    }

    /// <summary>
    /// Initialize mesh buffer resources used by the CPU meshing pipeline.
    /// </summary>
    public void InitializeMeshBuffers(int maxChunksPerBatch = 64)
    {
        meshBuffers?.Dispose();

        if (activeChunks.Count > 0)
        {
            Log.Warn($"InitializeMeshBuffers: Clearing {activeChunks.Count} active chunks due to buffer reinitialization");
            activeChunks.Clear();
        }

        pendingSeamRefresh.Clear();
        chunkVoxelCache.Clear();
        ClearPendingCpuMeshingQueue();

        var absoluteMaxRadius = Math.Clamp(VoxelHelper.MaxDistanceInChunks + PREFETCH_MARGIN_MAX, 1, VoxelHelper.WorldChunksXZ);
        var maxActiveChunks = CalculateMaxViewChunksForRadius(absoluteMaxRadius);

        meshBuffers = new TerrainMeshBufferManager();
        // Allocate enough buffers for ALL potential active chunks, not just a batch
        meshBuffers.AllocateBuffers(maxActiveChunks);

        Log.Info($"Mesh buffers initialized for CPU meshing (capacity {maxActiveChunks} chunks)");
    }


    /// <summary>
    /// Initialize GPU rendering
    /// </summary>
    public void InitializeRendering()
    {
        terrainRenderer = new VoxelTerrainRenderer();
        // M5: Initialize block-based textures
        terrainRenderer.InitializeBlockTextures();
        Log.Info("Rendering initialized");
    }

    /// <summary>
    /// Initialize CPU frustum culling.
    /// No GPU resources are required anymore because chunk visibility is computed on the CPU.
    /// </summary>
    public void InitializeFrustumCulling(int maxChunks = 0)
    {
        if (maxChunks == 0)
        {
            var absoluteMaxRadius = Math.Clamp(VoxelHelper.MaxDistanceInChunks + PREFETCH_MARGIN_MAX, 1, VoxelHelper.WorldChunksXZ);
            maxChunks = CalculateMaxViewChunksForRadius(absoluteMaxRadius);
        }

        visibilityFlagsCapacity = maxChunks;
        Log.Info($"Frustum culling initialized for CPU pipeline (capacity: {maxChunks} chunks)");
    }

    /// <summary>
    /// Execute CPU-based frustum culling for the provided chunk indices.
    /// Returns an array of visibility flags (1 = visible, 0 = culled) and updates indirect draw commands in-place.
    /// </summary>
    public int[] ExecuteFrustumCulling(ICamera camera, int[] chunkIndices)
    {
        if (chunkIndices.Length == 0)
        {
            return [];
        }

        var planes = (Vector4[])camera.Frustum.Planes.Clone();
        var flags = new int[chunkIndices.Length];

        var visibleChunks = 0;
        var culledChunks = 0;
        var totalIndices = 0;
        var visibleIndices = 0;

        for (var i = 0; i < chunkIndices.Length; i++)
        {
            var chunkIdx = chunkIndices[i];
            var isVisible = IsChunkVisible(chunkIdx, planes);
            flags[i] = isVisible ? 1 : 0;

            if (activeChunks.TryGetValue(chunkIdx, out var descriptor) && descriptor.CommandSlot >= 0)
            {
                UpdateCommandVisibility(descriptor.CommandSlot, isVisible);

                var chunkIndexCount = Math.Max(0, descriptor.VisibleVoxelCount) * 6;
                totalIndices += chunkIndexCount;
                if (isVisible)
                {
                    visibleIndices += chunkIndexCount;
                }
            }

            if (isVisible)
            {
                visibleChunks++;
            }
            else
            {
                culledChunks++;
            }
        }

        VisibleChunkCount = visibleChunks;
        CulledChunkCount = culledChunks;
        StatTotalIndices = totalIndices;
        StatVisibleIndices = visibleIndices;
        StatVisibleChunks = visibleChunks;
        StatFrustumCulledChunks = culledChunks;

        return flags;
    }

    private static bool IsChunkVisible(int chunkIndex, Vector4[] frustumPlanes)
    {
        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;

        var min = new Vector3(chunkX * VoxelHelper.ChunkSideSize, 0f, chunkZ * VoxelHelper.ChunkSideSize);
        var max = min + new Vector3(VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, VoxelHelper.ChunkSideSize);

        const float margin = 32f; // Matches shader tolerance

        for (var i = 0; i < 6; i++)
        {
            var plane = frustumPlanes[i];
            var normal = new Vector3(plane.X, plane.Y, plane.Z);

            var positiveVertex = new Vector3(
                normal.X >= 0f ? max.X : min.X,
                normal.Y >= 0f ? max.Y : min.Y,
                normal.Z >= 0f ? max.Z : min.Z);

            var distance = Vector3.Dot(normal, positiveVertex) + plane.W;
            if (distance < -margin)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateCommandVisibility(int commandSlot, bool isVisible)
    {
        if (meshBuffers == null || meshBuffers.IndirectDrawBuffer == 0 || commandSlot < 0)
        {
            return;
        }

        var buffer = (int)meshBuffers.IndirectDrawBuffer;
        var slotBase = commandSlot * 10 * sizeof(uint); // 2 commands * 5 uints each
        var opaqueInstanceOffset = slotBase + sizeof(uint);
        var transparentInstanceOffset = slotBase + 5 * sizeof(uint) + sizeof(uint);
        var instanceValue = isVisible ? 1u : 0u;

        unsafe
        {
            GL.NamedBufferSubData(buffer, (IntPtr)opaqueInstanceOffset, sizeof(uint), (IntPtr)(&instanceValue));
            GL.NamedBufferSubData(buffer, (IntPtr)transparentInstanceOffset, sizeof(uint), (IntPtr)(&instanceValue));
        }
    }

    /// <summary>
    /// Get the terrain renderer for rendering integration.
    /// Returns null if Phase 4 not initialized.
    /// </summary>
    public VoxelTerrainRenderer? GetTerrainRenderer() => terrainRenderer;

    public void SaveChunkEdits(int chunkIdx)
    {
        if (!chunkEdits.TryGetValue(chunkIdx, out var edits) || edits.Count == 0) return;

        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);
        var worldName = terrainConfig.WorldName;
        var seed = generationSeed;

        var folderName = $"{worldName}_{seed}";
        var fileName = $"chunk_{chunkPos.X}_{chunkPos.Z}.bin";
        var path = Path.Combine(Environment.CurrentDirectory, "save", folderName, fileName);

        var dirName = Path.GetDirectoryName(path);
        if (dirName is not null) Directory.CreateDirectory(dirName);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(edits.Count);
        foreach (var kvp in edits)
        {
            writer.Write(kvp.Key);
            writer.Write((ushort)kvp.Value);
        }
    }

    public void SaveEdits()
    {
        foreach (var chunkIdx in chunkEdits.Keys)
        {
            SaveChunkEdits(chunkIdx);
        }
        Log.Info($"Saved edits for {chunkEdits.Count} chunks");
    }

    public void LoadEdits()
    {
        var worldName = terrainConfig.WorldName;
        var seed = generationSeed;
        var folderName = $"{worldName}_{seed}";
        var dirPath = Path.Combine(Environment.CurrentDirectory, "save", folderName);

        if (!Directory.Exists(dirPath)) return;

        var files = Directory.GetFiles(dirPath, "chunk_*.bin");
        var loadedCount = 0;
        var deletedCount = 0;
        
        foreach (var file in files)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out var worldX) && int.TryParse(parts[2], out var worldZ))
                {
                    // CRITICAL FIX: Convert world coordinates to chunk indices
                    // SaveChunkEdits saves as world block coordinates (chunkPos.X, chunkPos.Z)
                    // We must divide by ChunkSideSize to get chunk indices
                    var chunkX = worldX / VoxelHelper.ChunkSideSize;
                    var chunkZ = worldZ / VoxelHelper.ChunkSideSize;

                    if (chunkX >= 0 && chunkX < VoxelHelper.WorldChunksXZ && chunkZ >= 0 && chunkZ < VoxelHelper.WorldChunksXZ)
                    {
                        var chunkIdx = chunkZ * VoxelHelper.WorldChunksXZ + chunkX;

                        using var stream = File.OpenRead(file);
                        using var reader = new BinaryReader(stream);

                        var count = reader.ReadInt32();
                        
                        // Check if file has enough data for ushort format (4 + count * 6 bytes)
                        // Old format was byte (4 + count * 5 bytes)
                        var expectedNewSize = 4 + count * (sizeof(int) + sizeof(ushort));
                        var expectedOldSize = 4 + count * (sizeof(int) + sizeof(byte));
                        var fileSize = stream.Length;
                        
                        if (fileSize == expectedOldSize)
                        {
                            // Old format - delete the file as it's incompatible
                            stream.Close();
                            File.Delete(file);
                            deletedCount++;
                            continue;
                        }
                        
                        if (fileSize != expectedNewSize)
                        {
                            Log.Warn($"Skipping edit file {name}: unexpected file size {fileSize} (expected {expectedNewSize})");
                            continue;
                        }
                        
                        var edits = new Dictionary<int, BlockId>(count);
                        for (var k = 0; k < count; k++)
                        {
                            var voxelIdx = reader.ReadInt32();
                            var type = (BlockId)reader.ReadUInt16();
                            edits[voxelIdx] = type;
                        }
                        chunkEdits[chunkIdx] = edits;
                        loadedCount++;
                        Log.Debug($"Loaded {count} edits for chunk {chunkIdx} (world coords {worldX},{worldZ} → chunk indices {chunkX},{chunkZ})");
                    }
                    else
                    {
                        Log.Warn($"Skipping edit file {name}: chunk indices ({chunkX},{chunkZ}) out of bounds");
                    }
                }
            }
            catch (Exception ex)
            {
                // Delete corrupted/incompatible files
                try
                {
                    File.Delete(file);
                    deletedCount++;
                    Log.Debug($"Deleted incompatible edit file: {file}");
                }
                catch
                {
                    Log.Error($"Failed to load or delete edits from {file}: {ex.Message}");
                }
            }
        }
        
        if (deletedCount > 0)
        {
            Log.Info($"Deleted {deletedCount} incompatible old-format edit files");
        }
        Log.Info($"Loaded edits for {loadedCount} chunks from {dirPath}");
    }

    public void Dispose()
    {
        SaveEdits(); // Save on dispose

        // Cleanup all fences
        foreach (var kvp in activeChunks)
        {
            if (kvp.Value.Fence != IntPtr.Zero)
            {
                try { GL.DeleteSync(kvp.Value.Fence); } catch { }
            }
        }

        // Cleanup frustum culling resources
        // terrainRenderer is now a SceneNode and will be cleaned up by the scene graph
        meshBuffers?.Dispose();
        chunkVoxelCache.Dispose();
        ClearPendingCpuMeshingQueue();
        cpuGenerationJobs.Dispose();
        cpuMeshingJobs.Dispose();

        Log.Info("ChunkStreamingManager: Disposed");
    }

}
