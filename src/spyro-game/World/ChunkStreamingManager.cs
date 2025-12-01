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
/// Coordinates terrain streaming, chunk lifecycle, and CPU/GPU resource transitions.
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
    private const int HeightCacheWordsPerChunk = VoxelHelper.ChunkSideSizeSquare / 2;
    private const ushort HeightCacheHeightMask = 0x03FF;
    private const ushort HeightCacheWaterFlag = 1 << 10;
    private const ushort HeightCacheEditedFlag = 1 << 11;
    private const int HeightCacheLogIntervalFrames = 600;
    private static readonly bool HeightCacheEnabled = true;
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
    private readonly Dictionary<int, byte> lastUploadedPlaceholderMasks = [];
    private readonly Dictionary<int, Dictionary<int, BlockId>> chunkEdits = [];
    private readonly Dictionary<int, CpuChunkMesh> cpuMeshResults = [];
    private readonly Dictionary<int, long> chunkLastVisibleFrame = [];
    private readonly Queue<int> freeHeightCacheSlots = new();
    
    // Deferred remesh: chunks waiting for a neighbor to complete regeneration before remeshing
    // Key = chunk that needs to regenerate first, Value = list of neighbors waiting for remesh
    private readonly Dictionary<int, HashSet<int>> deferredRemeshWaiters = [];

    private Phase3BufferManager? phase3Buffers;
    private VoxelTerrainRenderer? terrainRenderer;

    private long currentFrame;
    private long lastRetentionGuardLogFrame = long.MinValue;
    private Vector3 lastCameraPosition;
    private int maxConcurrentCpuGenerations;
    private int inFlightCpuGenerations;

    private TerrainConfig terrainConfig;
    private uint terrainParamsSSBO;
    private int heightSplineTexture;
    private int biomeLutTexture;
    private int generationSeed = 1337;

    private uint heightCacheBuffer;
    private uint heightCacheSlotBuffer;
    private int heightCacheCapacity;
    private int[]? heightCacheSlotCpu;
    private long heightCacheUploads;
    private long heightCacheBytesUploaded;
    private int heightCacheSlotsInUse;
    private long heightCacheLastWarningFrame;
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
            UpdateVisibilityBudgetCapacity();
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
        UpdateVisibilityBudgetCapacity();
    }
    public uint TerrainParamsSSBO => terrainParamsSSBO;
    public int BiomeLutTexture => biomeLutTexture;
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

    public void InitializeGpuGeneration(int seed)
    {
        generationSeed = seed != 0 ? seed : generationSeed;
        terrainConfig.Seed = generationSeed;

        if (terrainParamsSSBO == 0)
        {
            GL.CreateBuffers(1, out terrainParamsSSBO);
            var size = System.Runtime.InteropServices.Marshal.SizeOf<TerrainConfig.TerrainGenerationParams>();
            GL.NamedBufferStorage(terrainParamsSSBO, size, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, terrainParamsSSBO, -1, "terrain_params_ssbo");
        }

        var maxChunks = CalculateMaxViewChunks();
        InitializeHeightCache(maxChunks);

        UploadTerrainConfig();
        cpuGenerationJobs.UpdateConfig(terrainConfig);
        LoadEdits();

        Log.Info($"ChunkStreamingManager: GPU generation assets initialized (seed={generationSeed}, maxChunks={maxChunks})");
    }

    private void LogHeightCacheStatsIfNeeded()
    {
        if (!HeightCacheEnabled || heightCacheCapacity == 0)
        {
            return;
        }

        if (HeightCacheLogIntervalFrames <= 0 || currentFrame % HeightCacheLogIntervalFrames != 0)
        {
            return;
        }

        var stats = GetHeightCacheStats();
        if (stats.Capacity == 0)
        {
            return;
        }

        var utilization = stats.Capacity > 0 ? (float)stats.SlotsInUse / stats.Capacity : 0f;
        var uploadedMb = stats.BytesUploaded / (1024f * 1024f);

        var message = $"HeightCache stats: {stats.SlotsInUse}/{stats.Capacity} slots ({utilization:P1}) used, uploads={stats.Uploads}, uploaded={uploadedMb:F1} MiB";

        Log.Info(message);
    }

    public readonly struct HeightCacheStats
    {
        public int Capacity { get; init; }
        public int SlotsInUse { get; init; }
        public int FreeSlots { get; init; }
        public long Uploads { get; init; }
        public long BytesUploaded { get; init; }
    }

    public HeightCacheStats GetHeightCacheStats()
    {
        if (!HeightCacheEnabled)
        {
            return default;
        }

        var capacity = heightCacheCapacity;
        var used = heightCacheSlotsInUse;
        return new HeightCacheStats
        {
            Capacity = capacity,
            SlotsInUse = used,
            FreeSlots = Math.Max(capacity - used, 0),
            Uploads = heightCacheUploads,
            BytesUploaded = heightCacheBytesUploaded
        };
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

    private void UpdateVisibilityBudgetCapacity()
    {
        if (!HeightCacheEnabled)
        {
            return;
        }

        var desiredCapacity = CalculateMaxViewChunks();

        if (heightCacheBuffer == 0)
        {
            // Not initialized yet; InitializeGpuGeneration will size buffers using the new value.
            return;
        }

        if (desiredCapacity <= heightCacheCapacity)
        {
            return;
        }

        Log.Info($"ChunkStreamingManager: expanding height cache to {desiredCapacity} slots (was {heightCacheCapacity}) due to visibility budget change");
        InitializeHeightCache(desiredCapacity);
        RestoreHeightCacheForActiveChunks();
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
        LogHeightCacheStatsIfNeeded();
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
            MarkChunkDirty(neighborIdx, $"neighbor {chunkIdx} became ready");
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
        var mask = GetPlaceholderMaskForChunk(chunkIdx);
        if (!chunkVoxelCache.TryGetVersion(chunkIdx, out var cacheVersion) || cacheVersion <= 0)
        {
            Log.Debug($"ScheduleCpuMeshing: chunk {chunkIdx} missing cache version; enqueueing with version=0");
        }

        cpuMeshingJobs.Enqueue(chunkIdx, mask, cacheVersion);
    }

    // Placeholder mask system removed - meshing now waits for all neighbors
    private byte GetPlaceholderMaskForChunk(int chunkIdx) => 0;

    private void DrainCompletedCpuMeshes()
    {
        var received = false;
        while (cpuMeshingJobs.TryDequeueResult(out var mesh))
        {
            cpuMeshResults[mesh.ChunkIndex] = mesh;
            received = true;
        }

        if ((received || cpuMeshResults.Count > 0) && phase3Buffers != null && terrainRenderer != null)
        {
            ProcessCpuMeshResults();
        }
    }

    private void ProcessCpuMeshResults()
    {
        if (phase3Buffers == null || terrainRenderer == null || cpuMeshResults.Count == 0)
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
        if (phase3Buffers == null || terrainRenderer == null)
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
            phase3Buffers.FreeVertexRegion((uint)descriptor.AtlasOffset, (uint)(descriptor.VisibleVoxelCount * 4));
            if (descriptor.IndexOffset >= 0)
            {
                phase3Buffers.FreeIndexRegion((uint)descriptor.IndexOffset, (uint)(descriptor.VisibleVoxelCount * 6));
            }
        }

        var vertexOffset = vertexCount > 0 ? (int)phase3Buffers.AllocateVertexRegion((uint)vertexCount) : -1;
        var indexOffset = indexCount > 0 ? (int)phase3Buffers.AllocateIndexRegion((uint)indexCount) : -1;

        UploadCpuMeshData(mesh, vertexOffset, vertexCount, indexOffset, indexCount);

        var slot = descriptor.CommandSlot >= 0 ? descriptor.CommandSlot : phase3Buffers.AllocateCommandSlot();
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
        lastUploadedPlaceholderMasks[mesh.ChunkIndex] = placeholderMask;

        if (pendingSeamRefresh.Contains(mesh.ChunkIndex))
        {
            RequestSeamRefresh(mesh.ChunkIndex, "Deferred seam dependency resolved", force: true);
        }

        return true;
    }

    private void UploadCpuMeshData(CpuChunkMesh mesh, int vertexOffset, int vertexCount, int indexOffset, int indexCount)
    {
        if (phase3Buffers == null)
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

        phase3Buffers.UploadMeshData(vertexSpan, vertexOffset, indexSpan, indexOffset);
    }

    private void WriteIndirectCommands(int slot, int chunkIndex, int vertexOffset, int indexOffset, uint opaqueFaces, uint waterFaces)
    {
        if (phase3Buffers == null || slot < 0)
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
                GL.NamedBufferSubData((int)phase3Buffers.IndirectDrawBuffer, (IntPtr)(slot * 40), 40, (IntPtr)cmdPtr);
            }
        }

        GL.NamedBufferSubData((int)phase3Buffers.ChunkInfoBuffer, (IntPtr)(slot * sizeof(int)), sizeof(int), ref chunkIndex);
    }

    private void RefreshRendererBuffers(string reason)
    {
        if (phase3Buffers == null || terrainRenderer == null)
        {
            return;
        }

        var readyChunks = activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready).ToList();
        var totalFaces = (uint)readyChunks.Sum(c => Math.Max(0, c.VisibleVoxelCount));
        var totalVerticesInBuffer = phase3Buffers.CurrentVertexBufferEnd;
        var totalIndicesInBuffer = phase3Buffers.CurrentIndexBufferEnd;

        terrainRenderer.SetupBuffers(phase3Buffers, totalVerticesInBuffer, totalFaces, readyChunks);
        Log.Debug($"ChunkStreamingManager: Renderer refreshed after {reason} (ready={readyChunks.Count}, faces={totalFaces}, vertices={totalVerticesInBuffer}, indices={totalIndicesInBuffer})");
    }

    private void InitializeHeightCache(int maxChunks)
    {
        if (!HeightCacheEnabled || maxChunks <= 0)
        {
            heightCacheCapacity = 0;
            heightCacheSlotCpu = null;
            freeHeightCacheSlots.Clear();
            return;
        }

        var wordsPerChunk = HeightCacheWordsPerChunk;
        var cacheBytes = maxChunks * wordsPerChunk * sizeof(uint);

        if (heightCacheBuffer != 0)
        {
            GL.DeleteBuffer(heightCacheBuffer);
        }
        GL.CreateBuffers(1, out heightCacheBuffer);
        GL.NamedBufferStorage(heightCacheBuffer, cacheBytes, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, heightCacheBuffer, -1, "height_cache_ssbo");

        if (heightCacheSlotBuffer != 0)
        {
            GL.DeleteBuffer(heightCacheSlotBuffer);
        }
        GL.CreateBuffers(1, out heightCacheSlotBuffer);
        var slotBytes = VoxelHelper.TotalChunks * sizeof(int);
        GL.NamedBufferStorage(heightCacheSlotBuffer, slotBytes, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, heightCacheSlotBuffer, -1, "height_cache_slots_ssbo");

        heightCacheCapacity = maxChunks;
        heightCacheSlotCpu = new int[VoxelHelper.TotalChunks];
        for (var i = 0; i < heightCacheSlotCpu.Length; i++) heightCacheSlotCpu[i] = -1;

        freeHeightCacheSlots.Clear();
        for (var i = 0; i < maxChunks; i++)
        {
            freeHeightCacheSlots.Enqueue(i);
        }

        var initSlots = new int[VoxelHelper.TotalChunks];
        for (var i = 0; i < initSlots.Length; i++) initSlots[i] = -1;
        GL.NamedBufferSubData(heightCacheSlotBuffer, IntPtr.Zero, slotBytes, initSlots);

        heightCacheBytesUploaded = 0;
        heightCacheUploads = 0;
        heightCacheSlotsInUse = 0;
        heightCacheLastWarningFrame = -HeightCacheLogIntervalFrames;
    }

    private void RestoreHeightCacheForActiveChunks()
    {
        if (!HeightCacheEnabled)
        {
            return;
        }

        var restored = 0;
        foreach (var chunkIdx in activeChunks.Keys)
        {
            var chunk = world[chunkIdx];
            if (chunk != null && chunk.TryGetSpanData(out var spanPairs, out var spanCounts, out var spanTypes))
            {
                UploadHeightCacheFromSpans(chunkIdx, spanPairs, spanCounts, spanTypes);
                restored++;
            }
        }

        if (restored > 0)
        {
            Log.Info($"ChunkStreamingManager: restored height cache data for {restored} active chunks after resizing");
        }
    }

    private void DisposeVoxelReadbackBuffers()
    {
        // Placeholder for parity with historical GPU pipeline; nothing to release at the moment.
    }

    private int EnsureHeightCacheSlot(int chunkIdx)
    {
        if (heightCacheSlotCpu == null || heightCacheBuffer == 0)
            return -1;

        var current = heightCacheSlotCpu[chunkIdx];
        if (current >= 0)
        {
            return current;
        }

        if (freeHeightCacheSlots.Count == 0)
        {
            if (currentFrame - heightCacheLastWarningFrame >= 300)
            {
                heightCacheLastWarningFrame = currentFrame;
                Log.Warn("HeightCache: exhausted slot pool; neighboring chunks will fall back to procedural sampling until slots free up.");
            }
            return -1;
        }

        var slot = freeHeightCacheSlots.Dequeue();
        heightCacheSlotCpu[chunkIdx] = slot;
        heightCacheSlotsInUse++;
        UpdateHeightCacheSlotValue(chunkIdx, slot);
        return slot;
    }

    private void ReleaseHeightCacheSlot(int chunkIdx)
    {
        if (heightCacheSlotCpu == null)
            return;

        var slot = heightCacheSlotCpu[chunkIdx];
        if (slot < 0)
            return;

        heightCacheSlotCpu[chunkIdx] = -1;
        if (heightCacheSlotsInUse > 0)
        {
            heightCacheSlotsInUse--;
        }
        freeHeightCacheSlots.Enqueue(slot);
        UpdateHeightCacheSlotValue(chunkIdx, -1);
    }

    private void UpdateHeightCacheSlotValue(int chunkIdx, int slot)
    {
        if (heightCacheSlotBuffer == 0)
            return;

        var value = new[] { slot };
        var offset = (IntPtr)(chunkIdx * sizeof(int));
        GL.NamedBufferSubData(heightCacheSlotBuffer, offset, sizeof(int), value);
    }

    private static ushort PackHeightEntry(int topHeightExclusive, bool hasWater, bool edited)
    {
        var height = (ushort)Math.Clamp(topHeightExclusive, 0, VoxelHelper.ChunkYSize);
        var packed = (ushort)(height & HeightCacheHeightMask);
        if (hasWater)
        {
            packed |= HeightCacheWaterFlag;
        }
        if (edited)
        {
            packed |= HeightCacheEditedFlag;
        }
        return packed;
    }

    private void UploadHeightCache(int chunkIdx, uint[] packedHeights)
    {
        if (heightCacheBuffer == 0 || packedHeights.Length != HeightCacheWordsPerChunk)
            return;

        var slot = EnsureHeightCacheSlot(chunkIdx);
        if (slot < 0)
            return;

        var byteOffset = slot * HeightCacheWordsPerChunk * sizeof(uint);
        GL.NamedBufferSubData(heightCacheBuffer, (IntPtr)byteOffset, HeightCacheWordsPerChunk * sizeof(uint), packedHeights);
        heightCacheUploads++;
        heightCacheBytesUploaded += HeightCacheWordsPerChunk * sizeof(uint);
    }

    private void InvalidateHeightCache(int chunkIdx)
    {
        ReleaseHeightCacheSlot(chunkIdx);
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
        descriptor.PlaceholderMask = GetPlaceholderMaskForChunk(chunkIdx);
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

        if (HeightCacheEnabled)
        {
            UploadHeightCacheFromSpans(chunkIdx, result.SpanPairs, result.SpanCounts, result.SpanTypes);
        }
    }

    private void UploadHeightCacheFromSpans(int chunkIdx, int[] spanPairs, byte[] spanCounts, BlockId[] spanTypes)
    {
        if (!HeightCacheEnabled)
        {
            return;
        }

        var packedHeights = new uint[HeightCacheWordsPerChunk];
        for (var col = 0; col < VoxelHelper.ChunkSideSizeSquare; col++)
        {
            var spanCount = spanCounts[col];
            var wordIndex = col >> 1;
            var shift = (col & 1) == 0 ? 0 : 16;
            ushort entry;
            if (spanCount == 0)
            {
                entry = 0;
            }
            else
            {
                var baseIdx = col * ChunkCollisionData.MaxSpansPerColumn * 2;
                var typeBaseIdx = col * ChunkCollisionData.MaxSpansPerColumn;
                var maxYExclusive = 0;
                var waterSpan = false;

                for (var s = 0; s < spanCount && s < ChunkCollisionData.MaxSpansPerColumn; s++)
                {
                    var y1 = spanPairs[baseIdx + s * 2 + 1];
                    if (y1 > maxYExclusive)
                    {
                        maxYExclusive = y1;
                    }

                    if (spanTypes[typeBaseIdx + s].IsWater())
                    {
                        waterSpan = true;
                    }
                }

                entry = PackHeightEntry(maxYExclusive, waterSpan, false);
            }

            var mask = 0xFFFFu << shift;
            var existing = packedHeights[wordIndex] & ~mask;
            packedHeights[wordIndex] = existing | ((uint)entry << shift);
        }

        UploadHeightCache(chunkIdx, packedHeights);
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
        if (desc.AtlasOffset >= 0 && desc.VisibleVoxelCount > 0 && phase3Buffers != null)
        {
            var verticesPerFace = 4;
            var indicesPerFace = 6;

            // Free vertex region
            phase3Buffers.FreeVertexRegion((uint)desc.AtlasOffset, (uint)(desc.VisibleVoxelCount * verticesPerFace));
            //Log.Debug($"Freed vertex region for chunk {chunkIndex}: offset={desc.AtlasOffset}, size={desc.VisibleVoxelCount * verticesPerFace}");

            // Free index region
            if (desc.IndexOffset >= 0)
            {
                phase3Buffers.FreeIndexRegion((uint)desc.IndexOffset, (uint)(desc.VisibleVoxelCount * indicesPerFace));
                //Log.Debug($"Freed index region for chunk {chunkIndex}: offset={desc.IndexOffset}, size={desc.VisibleVoxelCount * indicesPerFace}");
            }

            // Free command slot
            if (desc.CommandSlot >= 0)
            {
                phase3Buffers.FreeCommandSlot(desc.CommandSlot);
                //Log.Debug($"Freed command slot for chunk {chunkIndex}: slot={desc.CommandSlot}");
            }
        }

        // Remove from active chunks
        activeChunks.Remove(chunkIndex);
        pendingSeamRefresh.Remove(chunkIndex);
        lastSeamRefreshFrame.Remove(chunkIndex);
        lastUploadedPlaceholderMasks.Remove(chunkIndex);
        chunkLastVisibleFrame.Remove(chunkIndex);

        // Release cached height data slot
        InvalidateHeightCache(chunkIndex);

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
    /// Get memory usage statistics (Phase 5)
    /// </summary>
    public (long totalBytes, long voxelBytes, long visibilityBytes, long compactBytes) GetMemoryStats()
    {
        var voxelsPerChunk = VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize;
        var columnsPerChunk = VoxelHelper.ChunkSideSizeSquare;

        var chunkCapacity = phase3Buffers?.MaxChunks ?? CalculateMaxViewChunks();
        long voxelBytes = chunkCapacity * voxelsPerChunk * sizeof(uint);
        long columnBytes = chunkCapacity * columnsPerChunk * (sizeof(int) + sizeof(uint) * 4);
        var visibilityBytes = phase3Buffers?.GetAllocatedBytes() ?? 0;
        var compactBytes = terrainRenderer?.GetAllocatedBytes() ?? 0;

        var totalBytes = voxelBytes + columnBytes + visibilityBytes + compactBytes;

        return (totalBytes, voxelBytes, visibilityBytes, compactBytes);
    }

    // ========================================================================
    // Phase 5: Block Edit Operations (Player Interactions)
    // ========================================================================

    /// <summary>
    /// Apply a block edit (break or place) at world position (Phase 5)
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
        InvalidateHeightCache(chunkIdx);

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
    /// Mark a specific voxel as edited in the GPU edit mask buffer (Phase 5)
    /// This will be read by the generation shader to override generated terrain
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
    /// Mark a chunk as dirty (needs regeneration) (Phase 5)
    /// Dirty chunks will be regenerated on the next update cycle
    /// Phase 5.2: Uses incremental updates to preserve buffer regions
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
            // Phase 5.2: Keep existing buffer offset - we'll update in-place
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


    private void UploadTerrainConfig()
    {
        // Ensure seed is not 0 (which might cause issues or be uninitialized)
        if (generationSeed == 0) generationSeed = 1337;

        // Upload Terrain Params
        var terrainParams = terrainConfig.GetGenerationParams();
        terrainParams.Seed = (uint)generationSeed; // Keep the seed consistent with init

        Log.Info($"Uploading TerrainParams: Seed={terrainParams.Seed}, ContScale={terrainParams.ContinentalScale}, WarpScale={terrainParams.WarpScale}");

        GL.NamedBufferSubData(terrainParamsSSBO, IntPtr.Zero,
            System.Runtime.InteropServices.Marshal.SizeOf<TerrainConfig.TerrainGenerationParams>(), ref terrainParams);

        // Create and upload Height Spline Texture (1D)
        if (heightSplineTexture == 0)
        {
            GL.CreateTextures(TextureTarget.Texture1D, 1, out heightSplineTexture);
            GL.TextureParameter(heightSplineTexture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TextureParameter(heightSplineTexture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TextureParameter(heightSplineTexture, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            // Initial storage allocation
            GL.TextureStorage1D(heightSplineTexture, 1, SizedInternalFormat.R32f, 256);
        }
        var heightData = terrainConfig.BakeHeightSplineLut(256);
        GL.TextureSubImage1D(heightSplineTexture, 0, 0, 256, PixelFormat.Red, PixelType.Float, heightData);

        // Create and upload Biome LUT Texture (2D)
        if (biomeLutTexture == 0)
        {
            GL.CreateTextures(TextureTarget.Texture2D, 1, out biomeLutTexture);
            GL.TextureParameter(biomeLutTexture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(biomeLutTexture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TextureParameter(biomeLutTexture, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TextureParameter(biomeLutTexture, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            // Initial storage allocation
            GL.TextureStorage2D(biomeLutTexture, 1, SizedInternalFormat.R8ui, 256, 256);
        }
        var biomeData = terrainConfig.BuildBiomeIdLut(256);
        GL.TextureSubImage2D(biomeLutTexture, 0, 0, 0, 256, 256, PixelFormat.RedInteger, PixelType.UnsignedByte, biomeData);

        // M5: Initialize block textures (hot reload)
        terrainRenderer?.InitializeBlockTextures();
    }

    /// <summary>
    /// Initialize Phase 3 buffer resources used by the CPU meshing pipeline.
    /// </summary>
    public void InitializePhase3(int maxChunksPerBatch = 64)
    {
        phase3Buffers?.Dispose();

        if (activeChunks.Count > 0)
        {
            Log.Warn($"InitializePhase3: Clearing {activeChunks.Count} active chunks due to buffer reinitialization");
            activeChunks.Clear();
        }

        pendingSeamRefresh.Clear();
        chunkVoxelCache.Clear();
        ClearPendingCpuMeshingQueue();

        phase3Buffers = new Phase3BufferManager();
        phase3Buffers.AllocateBuffers(maxChunksPerBatch);

        var absoluteMaxRadius = Math.Clamp(VoxelHelper.MaxDistanceInChunks + PREFETCH_MARGIN_MAX, 1, VoxelHelper.WorldChunksXZ);
        var maxActiveChunks = CalculateMaxViewChunksForRadius(absoluteMaxRadius);
        phase3Buffers.ResizeIndirectDrawBuffer((uint)maxActiveChunks);

        Log.Info($"Phase 3 buffers initialized for CPU meshing (batch cap {maxChunksPerBatch}, indirect capacity {maxActiveChunks})");
    }


    /// <summary>
    /// Initialize Phase 4 GPU rendering
    /// </summary>
    public void InitializePhase4()
    {
        terrainRenderer = new VoxelTerrainRenderer();
        // M5: Initialize block-based textures
        terrainRenderer.InitializeBlockTextures();
        Log.Info("Phase 4 rendering initialized");
    }

    /// <summary>
    /// Initialize CPU frustum culling (Phase 4.5 replacement).
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

        const float margin = 32f; // Matches legacy GPU shader tolerance

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
        if (phase3Buffers == null || phase3Buffers.IndirectDrawBuffer == 0 || commandSlot < 0)
        {
            return;
        }

        var buffer = (int)phase3Buffers.IndirectDrawBuffer;
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
        // Cleanup Terrain Params (M1)
        if (terrainParamsSSBO != 0) GL.DeleteBuffer(terrainParamsSSBO);
        if (heightSplineTexture != 0) GL.DeleteTexture(heightSplineTexture);
        if (biomeLutTexture != 0) GL.DeleteTexture(biomeLutTexture);
        if (heightCacheBuffer != 0) GL.DeleteBuffer(heightCacheBuffer);
        if (heightCacheSlotBuffer != 0) GL.DeleteBuffer(heightCacheSlotBuffer);
        // terrainRenderer is now a SceneNode and will be cleaned up by the scene graph
        phase3Buffers?.Dispose();
        chunkVoxelCache.Dispose();
        ClearPendingCpuMeshingQueue();
        cpuGenerationJobs.Dispose();
        cpuMeshingJobs.Dispose();

        Log.Info("ChunkStreamingManager: Disposed");
    }

}
