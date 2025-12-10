using OpenRender;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.World.Generation;
using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;

namespace SpyroGame.World;

/// <summary>
/// Coordinates terrain streaming, chunk lifecycle, and CPU resource transitions.
/// Simplified two-pass design:
/// - Pass 1: Generate terrain for new chunks
/// - Pass 2: Calculate lighting and mesh for all chunks needing processing
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
    private const string ConfigFileName = "terrain_config.json";
    
    // Background save system
    private const int AUTO_SAVE_INTERVAL_SECONDS = 30;
    private const int SAVE_FILE_VERSION = 2;  // v2 = compressed format
    
    /// <summary>
    /// All 8 neighbors (cardinal + diagonal) for marking reprocess
    /// </summary>
    private static readonly (int dx, int dz)[] AllNeighborOffsets =
    [
        (-1, 0), (1, 0), (0, -1), (0, 1),  // Cardinal
        (-1, -1), (-1, 1), (1, -1), (1, 1)  // Diagonal
    ];
    
    /// <summary>
    /// Cardinal neighbors only (for light propagation)
    /// </summary>
    private static readonly (int dx, int dz)[] CardinalNeighborOffsets =
    [
        (-1, 0), (1, 0), (0, -1), (0, 1)
    ];

    private readonly VoxelWorld world;
    private readonly ChunkVoxelDataCache chunkVoxelCache;
    private readonly ChunkGenerationJobSystem cpuGenerationJobs;
    private readonly ChunkMeshingJobSystem cpuMeshingJobs;
    private readonly Dictionary<int, ChunkDescriptor> activeChunks = [];
    private readonly Queue<int> highPriorityPending = new();
    private readonly Queue<int> lowPriorityPending = new();
    private readonly Dictionary<int, Dictionary<int, BlockId>> chunkEdits = [];
    private readonly Dictionary<int, CpuChunkMesh> cpuMeshResults = [];
    private readonly Dictionary<int, long> chunkLastVisibleFrame = [];
    
    // New batch tracking for simplified processing
    private readonly HashSet<int> currentStreamingBatch = [];
    private readonly HashSet<int> pendingStreamingBatch = [];  // Chunks waiting for next batch
    private readonly HashSet<int> chunksNeedingReprocess = [];
    private readonly HashSet<int> modifiedChunks = []; // Chunks that have changed since last save
    private readonly HashSet<int> chunksWithLightRecalculated = [];  // Chunks that already had light fully recalculated
    private readonly HashSet<int> chunksLoadedFromDisk = [];  // Chunks loaded from save file (have complete light data)
    private readonly HashSet<int> dirtyChunks = [];  // Chunks that need to be saved
    private bool batchProcessingInProgress;  // True when currentStreamingBatch is being processed
    
    // Background save system
    private readonly object saveLock = new();
    private readonly CancellationTokenSource saveTokenSource = new();
    private Task? backgroundSaveTask;
    private DateTime lastAutoSaveTime = DateTime.UtcNow;
    
    // Performance optimization: Reusable lists to avoid LINQ allocations
    private readonly List<int> reusableChunkList = new(256);
    private readonly List<int> reusableChunkList2 = new(256);  // Second list for nested operations

    private TerrainMeshBufferManager? meshBuffers;
    private VoxelTerrainRenderer? terrainRenderer;

    private long currentFrame;
    private long lastRetentionGuardLogFrame = long.MinValue;
    private Vector3 lastCameraPosition;
    private readonly int maxConcurrentCpuGenerations;
    private int inFlightCpuGenerations;

    private TerrainConfig terrainConfig;
    private int generationSeed = 1337;
    
    /// <summary>Processing metrics for performance monitoring.</summary>
    public ChunkProcessingMetrics Metrics { get; } = new();

    public ChunkStreamingManager(VoxelWorld world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        CollisionManager = new CollisionManager();
        terrainConfig = LoadTerrainConfig();

        chunkVoxelCache = new ChunkVoxelDataCache();

        // Use default worker counts (ProcessorCount/2 each) to avoid thread contention.
        cpuGenerationJobs = new ChunkGenerationJobSystem(chunkVoxelCache, terrainConfig, Metrics);
        maxConcurrentCpuGenerations = cpuGenerationJobs.WorkerCount;

        cpuMeshingJobs = new ChunkMeshingJobSystem(chunkVoxelCache, Metrics);

        lastCameraPosition = Vector3.Zero;
    }

    public VoxelWorld World => world;
    public TerrainConfig Config => terrainConfig;
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
        => activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready);

    public (int total, int pending, int generating, int ready) GetStats()
    {
        var total = activeChunks.Count;
        var pending = 0;
        var generating = 0;
        var ready = 0;
        
        foreach (var kvp in activeChunks)
        {
            var state = kvp.Value.State;
            if (state == TerrainChunkState.Pending)
                pending++;
            else if (state is TerrainChunkState.Generating or TerrainChunkState.HasTerrain or TerrainChunkState.Processing)
                generating++;
            else if (state == TerrainChunkState.Ready)
                ready++;
        }
        
        return (total, pending, generating, ready);
    }

    /// <summary>Gets detailed stats for loading screen progress tracking.</summary>
    public (int total, int pending, int generating, int hasTerrain, int processing, int ready) GetDetailedStats()
    {
        var total = activeChunks.Count;
        var pending = 0;
        var generating = 0;
        var hasTerrain = 0;
        var processing = 0;
        var ready = 0;
        
        foreach (var kvp in activeChunks)
        {
            switch (kvp.Value.State)
            {
                case TerrainChunkState.Pending:
                    pending++;
                    break;
                case TerrainChunkState.Generating:
                    generating++;
                    break;
                case TerrainChunkState.HasTerrain:
                    hasTerrain++;
                    break;
                case TerrainChunkState.Processing:
                    processing++;
                    break;
                case TerrainChunkState.Ready:
                    ready++;
                    break;
            }
        }
        
        return (total, pending, generating, hasTerrain, processing, ready);
    }

    /// <summary>Gets batch tracking stats for debugging.</summary>
    public (int currentBatch, int pendingBatch, bool processing) GetBatchStats()
        => (currentStreamingBatch.Count, pendingStreamingBatch.Count, batchProcessingInProgress);

    public void FlushVoxelCache(string reason)
    {
        var cachedEntries = chunkVoxelCache.ActiveEntryCount;
        chunkVoxelCache.Clear();
        cpuMeshResults.Clear();
        currentStreamingBatch.Clear();
        pendingStreamingBatch.Clear();
        batchProcessingInProgress = false;
        chunksNeedingReprocess.Clear();
        chunksWithLightRecalculated.Clear();
        chunksLoadedFromDisk.Clear();
        dirtyChunks.Clear();
        cpuMeshingJobs.DrainPendingWorkItems();
        ClearPendingCpuMeshingQueue();

        var dirtied = 0;
        foreach (var kvp in activeChunks.ToArray())
        {
            if (kvp.Value.State == TerrainChunkState.Ready)
            {
                dirtied++;
                MarkChunkForReprocess(kvp.Key, "cache flush");
            }
        }

        Log.Warn($"ChunkStreamingManager: voxel cache flushed ({reason}), cleared={cachedEntries} entries, marked={dirtied} chunks for reprocess");
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

        var worldSizeInBlocks = VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ;
        var clampedX = Math.Clamp(cameraPosition.X, 0, worldSizeInBlocks - 1);
        var clampedZ = Math.Clamp(cameraPosition.Z, 0, worldSizeInBlocks - 1);

        var cameraChunkX = Math.Clamp((int)(clampedX / VoxelHelper.ChunkSideSize), 0, VoxelHelper.WorldChunksXZ - 1);
        var cameraChunkZ = Math.Clamp((int)(clampedZ / VoxelHelper.ChunkSideSize), 0, VoxelHelper.WorldChunksXZ - 1);

        // Use square region (Chebyshev distance) to avoid diagonal cutoff artifacts
        // This loads slightly more chunks than a perfect circle but eliminates the checkerboard pattern
        for (var dz = -viewDistance; dz <= viewDistance; dz++)
        {
            for (var dx = -viewDistance; dx <= viewDistance; dx++)
            {
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

    private int CalculateMaxViewChunks() =>
        // Use retention distance + padding to ensure we have enough slots for all active chunks
        // Active chunks include: Visible + Retained + Pending Unload
        CalculateMaxViewChunksForRadius(GetUnloadDistanceChunks());

    private static int CalculateMaxViewChunksForRadius(int radius)
    {
        var clamped = Math.Clamp(radius, 1, VoxelHelper.WorldChunksXZ);
        // Use square chunk count since DetermineVisibleChunks uses Chebyshev distance (square region)
        return Math.Clamp(VoxelHelper.CalculateSquareChunkCount(clamped), 1, VoxelHelper.TotalChunks);
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
            if (descriptor.State is TerrainChunkState.Generating or TerrainChunkState.Processing)
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

    /// <summary>
    /// Queue new chunks for terrain generation and mark their Ready neighbors for reprocessing.
    /// </summary>
    private void QueueNewChunks(HashSet<int> visibleChunks)
    {
        // Performance optimization: Use reusable list instead of LINQ .Except().ToList()
        reusableChunkList.Clear();
        foreach (var chunkIdx in visibleChunks)
        {
            if (!activeChunks.ContainsKey(chunkIdx))
            {
                reusableChunkList.Add(chunkIdx);
            }
        }

        if (reusableChunkList.Count == 0)
            return;

        reusableChunkList.Sort((a, b) => CalculatePriority(a).CompareTo(CalculatePriority(b)));

        // Choose which batch to add to: current if not processing, pending otherwise
        var targetBatch = batchProcessingInProgress ? pendingStreamingBatch : currentStreamingBatch;

        var addedCount = 0;
        foreach (var chunkIdx in reusableChunkList)
        {
            if (!EnsureRetentionCapacity(visibleChunks))
                break;

            world.GetOrCreateChunkContainer(chunkIdx);

            var descriptor = new ChunkDescriptor
            {
                ChunkIndex = chunkIdx,
                State = TerrainChunkState.Pending,
                CommandSlot = -1,
            };

            activeChunks[chunkIdx] = descriptor;
            targetBatch.Add(chunkIdx);
            addedCount++;

            // Note: We do NOT mark neighbors for reprocess here.
            // Neighbors only need reprocess after terrain is generated AND
            // we know the boundary data affects them. This is handled in ProcessTerrainBatch.

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

        if (addedCount > 0)
        {
            Log.Info($"ChunkStreamingManager: Queued {addedCount} new chunks (of {reusableChunkList.Count} candidates)");
        }
    }

    /// <summary>
    /// Mark all Ready neighbors of a chunk for reprocessing (including diagonals).
    /// Called when a new chunk is queued for terrain generation.
    /// </summary>
    private void MarkNeighborsForReprocess(int chunkIdx)
    {
        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

        foreach (var (dx, dz) in AllNeighborOffsets)
        {
            var nx = chunkX + dx;
            var nz = chunkZ + dz;

            if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                continue;

            var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;

            // Skip if already tracked for processing
            if (currentStreamingBatch.Contains(neighborIdx) || chunksNeedingReprocess.Contains(neighborIdx))
                continue;

            if (activeChunks.TryGetValue(neighborIdx, out var neighborDesc) &&
                neighborDesc.State == TerrainChunkState.Ready)
            {
                // Mark for reprocess - will be processed in batch with new chunks
                neighborDesc.State = TerrainChunkState.HasTerrain;
                activeChunks[neighborIdx] = neighborDesc;
                chunksNeedingReprocess.Add(neighborIdx);
                Metrics.RecordReprocess();
            }
        }
    }

    /// <summary>
    /// Mark a single chunk for reprocessing.
    /// </summary>
    private void MarkChunkForReprocess(int chunkIdx, string reason)
    {
        // Skip if already tracked for processing
        if (currentStreamingBatch.Contains(chunkIdx) || chunksNeedingReprocess.Contains(chunkIdx))
            return;

        if (!activeChunks.TryGetValue(chunkIdx, out var desc))
            return;

        if (desc.State == TerrainChunkState.Ready)
        {
            desc.State = TerrainChunkState.HasTerrain;
            activeChunks[chunkIdx] = desc;
            chunksNeedingReprocess.Add(chunkIdx);
            Metrics.RecordReprocess();
            Log.Debug($"Marked chunk {chunkIdx} for reprocess: {reason}");
        }
    }

    public void Update(Vector3 cameraPosition)
    {
        currentFrame++;
        lastCameraPosition = cameraPosition;
        
        // Reset per-frame stats
        meshBuffers?.ResetFrameStats();
        
        // Update metrics timing
        Metrics.Update(currentFrame / 60.0); // Approximate seconds

        var visibleChunks = DetermineVisibleChunks(cameraPosition);
        QueueNewChunks(visibleChunks);
        TrackChunkVisibility(visibleChunks);

        SubmitPendingBatches();
        PollCompletedBatches();
        ProcessTerrainBatch(); // Process chunks when batch is ready
        DrainCompletedCpuMeshes();
        UnloadDistantChunks(cameraPosition, visibleChunks);
        
        // Periodic background save
        CheckPeriodicSave();
    }

    /// <summary>
    /// Process terrain batch - when all new chunks have terrain, propagate light and mesh.
    /// Block edit reprocessing is done immediately without waiting for streaming.
    /// </summary>
    private void ProcessTerrainBatch()
    {
        // First: Process any chunks needing reprocess IMMEDIATELY (block edits)
        // These should not wait for the streaming batch
        ProcessReprocessChunks();

        // Second: Check if streaming batch is ready (all new chunks have terrain)
        if (currentStreamingBatch.Count == 0)
        {
            // Current batch is empty, promote pending batch if any
            if (pendingStreamingBatch.Count > 0)
            {
                foreach (var idx in pendingStreamingBatch)
                    currentStreamingBatch.Add(idx);
                pendingStreamingBatch.Clear();
                batchProcessingInProgress = false;  // New batch, not yet processing
            }
            return;
        }

        // Check if all chunks in batch have terrain (or were unloaded - skip those)
        // A chunk "has terrain" if it exists in activeChunks and HasVoxelData()
        // A chunk that was unloaded is considered "done" for batch purposes
        var stillWaiting = false;
        foreach (var idx in currentStreamingBatch)
        {
            if (activeChunks.TryGetValue(idx, out var d))
            {
                // Chunk still exists - check if it has terrain
                if (!d.HasVoxelData())
                {
                    stillWaiting = true;
                    break;
                }
            }
            // If chunk doesn't exist in activeChunks, it was unloaded - that's fine
        }

        if (stillWaiting)
        {
            batchProcessingInProgress = true;  // Batch is generating, don't add new chunks
            return;
        }

        // All remaining chunks have terrain - process them
        // Performance optimization: Use reusable list instead of LINQ .Where().ToList()
        reusableChunkList2.Clear();
        foreach (var idx in currentStreamingBatch)
        {
            if (activeChunks.TryGetValue(idx, out var d) && d.State == TerrainChunkState.HasTerrain)
            {
                reusableChunkList2.Add(idx);
            }
        }

        if (reusableChunkList2.Count == 0)
        {
            // All chunks were either processed already or unloaded
            currentStreamingBatch.Clear();
            batchProcessingInProgress = false;
            
            // Promote pending batch if any
            if (pendingStreamingBatch.Count > 0)
            {
                foreach (var idx in pendingStreamingBatch)
                    currentStreamingBatch.Add(idx);
                pendingStreamingBatch.Clear();
                Log.Debug($"ChunkStreamingManager: Promoted {currentStreamingBatch.Count} pending chunks to current batch (after empty batch)");
            }
            return;
        }

        // Mark cardinal neighbors for reprocess now that we have terrain
        var neighborsToReprocess = new HashSet<int>();
        foreach (var chunkIdx in reusableChunkList2)
        {
            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

            // Only cardinal neighbors (not diagonals) need boundary face updates
            foreach (var (dx, dz) in CardinalNeighborOffsets)
            {
                var nx = chunkX + dx;
                var nz = chunkZ + dz;
                if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                    continue;

                var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;

                // Skip if neighbor is in our batch (will be processed together)
                if (currentStreamingBatch.Contains(neighborIdx))
                    continue;

                if (activeChunks.TryGetValue(neighborIdx, out var neighborDesc) &&
                    neighborDesc.State == TerrainChunkState.Ready)
                {
                    neighborsToReprocess.Add(neighborIdx);
                }
            }
        }

        // Add neighbors to process list
        foreach (var neighborIdx in neighborsToReprocess)
        {
            if (activeChunks.TryGetValue(neighborIdx, out var desc))
            {
                desc.State = TerrainChunkState.HasTerrain;
                activeChunks[neighborIdx] = desc;
                reusableChunkList2.Add(neighborIdx);
            }
        }

        // Queue all for meshing (light propagation happens on background thread)
        foreach (var chunkIdx in reusableChunkList2)
        {
            if (activeChunks.TryGetValue(chunkIdx, out var desc) && desc.State == TerrainChunkState.HasTerrain)
            {
                desc.State = TerrainChunkState.Processing;
                activeChunks[chunkIdx] = desc;
                
                // Skip light propagation for chunks loaded from disk - they already have complete light data
                var skipLightPropagation = chunksLoadedFromDisk.Remove(chunkIdx);
                ScheduleCpuMeshing(chunkIdx, propagateLight: !skipLightPropagation);
            }
        }

        Log.Info($"ChunkStreamingManager: Processing batch of {reusableChunkList2.Count} chunks (new={currentStreamingBatch.Count}, neighbors={neighborsToReprocess.Count})");

        // Clear batch tracking
        currentStreamingBatch.Clear();
        batchProcessingInProgress = false;

        // Promote pending batch if any
        if (pendingStreamingBatch.Count > 0)
        {
            foreach (var idx in pendingStreamingBatch)
                currentStreamingBatch.Add(idx);
            pendingStreamingBatch.Clear();
            Log.Debug($"ChunkStreamingManager: Promoted {currentStreamingBatch.Count} pending chunks to current batch");
        }
    }

    /// <summary>
    /// Process chunks needing reprocess (block edits) immediately.
    /// These are high priority and should not wait for streaming.
    /// </summary>
    private void ProcessReprocessChunks()
    {
        if (chunksNeedingReprocess.Count == 0)
            return;

        // Performance optimization: Use reusable list instead of LINQ .Where().ToList()
        reusableChunkList.Clear();
        foreach (var idx in chunksNeedingReprocess)
        {
            if (activeChunks.TryGetValue(idx, out var d))
            {
                // Process chunks that have terrain data and are not currently being meshed
                // HasTerrain: normal case - chunk was marked dirty and has voxel data
                // Ready: chunk was in Processing when marked, has now completed meshing and needs remesh
                if (d.State is TerrainChunkState.HasTerrain or TerrainChunkState.Ready)
                {
                    reusableChunkList.Add(idx);
                }
                // If Processing: leave in set, will be processed after mesh completes
            }
        }

        if (reusableChunkList.Count == 0)
            return;

        // Queue for meshing immediately
        foreach (var chunkIdx in reusableChunkList)
        {
            if (activeChunks.TryGetValue(chunkIdx, out var desc) && 
                (desc.State == TerrainChunkState.HasTerrain || desc.State == TerrainChunkState.Ready))
            {
                desc.State = TerrainChunkState.Processing;
                activeChunks[chunkIdx] = desc;
                
                // If light was already recalculated for this chunk (e.g., from RecalculateLightingAroundBlock),
                // don't re-propagate as that would undo the precise calculation
                var skipLightPropagation = chunksWithLightRecalculated.Remove(chunkIdx);
                ScheduleCpuMeshing(chunkIdx, propagateLight: !skipLightPropagation);
                
                chunksNeedingReprocess.Remove(chunkIdx);
            }
        }

        if (reusableChunkList.Count > 0)
        {
            Log.Info($"ChunkStreamingManager: Immediate reprocess of {reusableChunkList.Count} chunks (block edits)");
        }
    }

    /// <summary>
    /// Handle immediate light source change (torch placement/removal).
    /// Recalculates lighting and queues affected chunks for immediate reprocessing.
    /// </summary>
    public void HandleLightSourceChange(int chunkIdx, int blockX, int blockY, int blockZ, bool isPlacement, BlockId blockType)
    {
        // 1. Update voxel data in cache
        if (!chunkVoxelCache.TryGetChunkData(chunkIdx, out var chunkData) || chunkData == null)
        {
            Log.Warn($"HandleLightSourceChange: No voxel data for chunk {chunkIdx}");
            return;
        }

        var localX = blockX % VoxelHelper.ChunkSideSize;
        var localZ = blockZ % VoxelHelper.ChunkSideSize;
        chunkData.SetBlock(localX, blockY, localZ, blockType);

        // 2. Recalculate lighting for this chunk
        var sw = Stopwatch.StartNew();
        if (isPlacement)
        {
            // Adding light source - just propagate the new light
            LightingCalculator.CalculateLighting(chunkData);
        }
        else
        {
            // Removing light source - need full recalculation to clear stale light
            var removedLightLevel = BlockRegistry.GetLightValue(blockType);
            LightingCalculator.RemoveBlockLight(
                chunkIdx,
                localX, blockY, localZ,
                removedLightLevel,
                idx => chunkVoxelCache.TryGetChunkData(idx, out var data) ? data : null
            );
        }
        sw.Stop();
        Metrics.RecordLightCalculation(sw.Elapsed.TotalMilliseconds);

        // 3. Find all chunks within light radius
        var affectedChunks = GetChunksInLightRadius(blockX, blockY, blockZ, 14); // Torch light = 14

        // 4. Mark all for immediate reprocessing
        foreach (var idx in affectedChunks)
        {
            if (activeChunks.TryGetValue(idx, out var desc))
            {
                if (desc.State == TerrainChunkState.Ready)
                {
                    desc.State = TerrainChunkState.HasTerrain;
                    activeChunks[idx] = desc;
                    Metrics.RecordReprocess();
                }
            }
        }

        // 5. Propagate light across boundaries
        foreach (var idx in affectedChunks)
        {
            PropagateChunkBoundaryLight(idx);
        }

        // 6. Queue all for immediate meshing (high priority)
        foreach (var idx in affectedChunks)
        {
            if (activeChunks.TryGetValue(idx, out var desc) && desc.State == TerrainChunkState.HasTerrain)
            {
                desc.State = TerrainChunkState.Processing;
                activeChunks[idx] = desc;
                ScheduleCpuMeshing(idx);
            }
        }

        Log.Info($"HandleLightSourceChange: {(isPlacement ? "Placed" : "Removed")} {blockType} at ({blockX},{blockY},{blockZ}), affected {affectedChunks.Count} chunks");
    }

    /// <summary>
    /// Get all chunks that could be affected by a light source at the given position.
    /// </summary>
    private List<int> GetChunksInLightRadius(int worldX, int worldY, int worldZ, int radius)
    {
        var affected = new HashSet<int>();
        var centerChunkX = worldX / VoxelHelper.ChunkSideSize;
        var centerChunkZ = worldZ / VoxelHelper.ChunkSideSize;

        // Check a box of chunks that could be affected
        var chunkRadius = (radius / VoxelHelper.ChunkSideSize) + 1;
        for (var dx = -chunkRadius; dx <= chunkRadius; dx++)
        {
            for (var dz = -chunkRadius; dz <= chunkRadius; dz++)
            {
                var cx = centerChunkX + dx;
                var cz = centerChunkZ + dz;

                if (cx < 0 || cx >= VoxelHelper.WorldChunksXZ || cz < 0 || cz >= VoxelHelper.WorldChunksXZ)
                    continue;

                var chunkIdx = cz * VoxelHelper.WorldChunksXZ + cx;
                if (activeChunks.ContainsKey(chunkIdx))
                {
                    affected.Add(chunkIdx);
                }
            }
        }

        return [.. affected];
    }

    /// <summary>
    /// Propagate light across chunk boundaries for a single chunk.
    /// </summary>
    private void PropagateChunkBoundaryLight(int chunkIdx)
    {
        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

        if (!chunkVoxelCache.TryGetChunkData(chunkIdx, out var centerData) || centerData == null)
            return;

        foreach (var (dx, dz) in CardinalNeighborOffsets)
        {
            var nx = chunkX + dx;
            var nz = chunkZ + dz;

            if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                continue;

            var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;

            if (!chunkVoxelCache.TryGetChunkData(neighborIdx, out var neighborData) || neighborData == null)
                continue;

            // Bidirectional propagation
            LightingCalculator.PropagateNeighborLight(centerData, neighborData, dx, dz);
            LightingCalculator.PropagateNeighborLight(neighborData, centerData, -dx, -dz);
        }
    }

    /// <summary>
    /// Check if all cardinal neighbors have voxel data.
    /// </summary>
    private bool AllNeighborsHaveVoxelData(int chunkIdx)
    {
        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

        foreach (var (dx, dz) in CardinalNeighborOffsets)
        {
            var nx = chunkX + dx;
            var nz = chunkZ + dz;

            if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                continue;

            var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;

            // If neighbor is tracked but doesn't have voxel data yet, we must wait
            if (activeChunks.TryGetValue(neighborIdx, out var neighborDesc) && !neighborDesc.HasVoxelData())
                return false;
        }

        return true;
    }

    private void ClearPendingCpuMeshingQueue()
    {
        cpuMeshResults.Clear();

        while (cpuMeshingJobs.TryDequeueResult(out _))
        {
        }
    }

    private void ScheduleCpuMeshing(int chunkIdx, bool propagateLight = true)
    {
        if (!chunkVoxelCache.TryGetVersion(chunkIdx, out var cacheVersion) || cacheVersion <= 0)
        {
            Log.Debug($"ScheduleCpuMeshing: chunk {chunkIdx} missing cache version; enqueueing with version=0");
        }

        cpuMeshingJobs.Enqueue(chunkIdx, 0, cacheVersion, propagateLight);
    }

    private void DrainCompletedCpuMeshes()
    {
        var received = false;
        while (cpuMeshingJobs.TryDequeueResult(out var mesh))
        {
            cpuMeshResults[mesh.ChunkIndex] = mesh;
            received = true;
            // Mesh timing tracked elsewhere
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
        var deferred = 0;
        
        // Process meshes in order, stopping if we hit channel saturation
        foreach (var entry in cpuMeshResults.ToArray())
        {
            if (TryUploadCpuMesh(entry.Value))
            {
                cpuMeshResults.Remove(entry.Key);
                processed = true;
            }
            else
            {
                // Upload deferred - stop processing more this frame
                deferred++;
                break; // Don't try more uploads this frame if channels are saturated
            }
        }

        if (processed)
        {
            RefreshRendererBuffers("CPU meshing uploads");
        }
        
        if (deferred > 0 && cpuMeshResults.Count > 0)
        {
            Log.Debug($"ChunkStreamingManager: {cpuMeshResults.Count} mesh uploads deferred to next frame");
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
        var waterFaceCount = Math.Clamp(mesh.WaterFaceCount, 0, faceCount);
        var translucentFaceCount = Math.Clamp(mesh.TranslucentFaceCount, 0, faceCount - waterFaceCount);
        var opaqueFaceCount = Math.Max(0, faceCount - waterFaceCount - translucentFaceCount);
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

        // Try to upload - if channels are busy, defer to next frame
        var vertexSpan = ReadOnlySpan<uint>.Empty;
        if (vertexCount > 0 && mesh.VertexData.Length > 0)
        {
            var safeLength = Math.Min(expectedVertexEntries, mesh.VertexData.Length);
            vertexSpan = mesh.VertexData.AsSpan(0, safeLength);
        }

        var indexSpan = ReadOnlySpan<uint>.Empty;
        if (indexCount > 0 && mesh.IndexData.Length > 0)
        {
            var safeLength = Math.Min(indexCount, mesh.IndexData.Length);
            indexSpan = mesh.IndexData.AsSpan(0, safeLength);
        }

        // Allocate buffer regions BEFORE upload so we know offsets
        // Only allocate if we have data to upload
        int vertexOffset = -1, indexOffset = -1;
        
        if (vertexCount > 0)
        {
            // Free old region if exists
            if (descriptor.VisibleVoxelCount > 0 && descriptor.AtlasOffset >= 0)
            {
                meshBuffers.FreeVertexRegion((uint)descriptor.AtlasOffset, (uint)(descriptor.VisibleVoxelCount * 4));
            }
            vertexOffset = (int)meshBuffers.AllocateVertexRegion((uint)vertexCount);
        }
        
        if (indexCount > 0)
        {
            // Free old region if exists
            if (descriptor.VisibleVoxelCount > 0 && descriptor.IndexOffset >= 0)
            {
                meshBuffers.FreeIndexRegion((uint)descriptor.IndexOffset, (uint)(descriptor.VisibleVoxelCount * 6));
            }
            indexOffset = (int)meshBuffers.AllocateIndexRegion((uint)indexCount);
        }

        // Try non-blocking upload
        if (!meshBuffers.UploadMeshData(vertexSpan, vertexOffset, indexSpan, indexOffset))
        {
            // Upload channels busy - free the regions we just allocated and defer
            if (vertexOffset >= 0)
                meshBuffers.FreeVertexRegion((uint)vertexOffset, (uint)vertexCount);
            if (indexOffset >= 0)
                meshBuffers.FreeIndexRegion((uint)indexOffset, (uint)indexCount);
            return false; // Keep in cpuMeshResults for retry
        }

        var slot = descriptor.CommandSlot >= 0 ? descriptor.CommandSlot : meshBuffers.AllocateCommandSlot();
        WriteIndirectCommands(slot, mesh.ChunkIndex, vertexOffset, indexOffset, (uint)opaqueFaceCount, (uint)waterFaceCount, (uint)translucentFaceCount);

        Log.Debug($"Chunk {mesh.ChunkIndex} mesh upload faces={faceCount} water={waterFaceCount} translucent={translucentFaceCount} cacheVer={mesh.CacheVersion} enqueue={mesh.EnqueueId} build={mesh.BuildId}");

        var refreshedDescriptor = new ChunkDescriptor
        {
            ChunkIndex = mesh.ChunkIndex,
            AtlasOffset = vertexOffset >= 0 ? vertexOffset : 0,
            IndexOffset = indexOffset >= 0 ? indexOffset : 0,
            CommandSlot = slot,
            VisibleVoxelCount = faceCount,
            State = TerrainChunkState.Ready,
            GenerationStartFrame = currentFrame,
            MaxSurfaceHeight = mesh.MaxSurfaceHeight,
        };
        activeChunks[mesh.ChunkIndex] = refreshedDescriptor;

        return true;
    }

    private void WriteIndirectCommands(int slot, int chunkIndex, int vertexOffset, int indexOffset, uint opaqueFaces, uint waterFaces, uint translucentFaces)
    {
        if (meshBuffers == null || slot < 0)
        {
            return;
        }

        var baseVertex = vertexOffset >= 0 ? (uint)vertexOffset : 0u;
        var firstIndex = indexOffset >= 0 ? (uint)indexOffset : 0u;

        // 3 commands per slot: Opaque, Water, Translucent
        // Each command is 5 uints (20 bytes)
        // Total slot size = 60 bytes
        Span<uint> command =
        [
            // Command 1: Opaque (Offset 0)
            opaqueFaces * 6u,
            1u,
            firstIndex,
            baseVertex,
            0u,
            // Command 2: Water (Offset 20)
            waterFaces * 6u,
            1u,
            firstIndex + opaqueFaces * 6u,
            baseVertex,
            0u,
            // Command 3: Translucent (Offset 40)
            translucentFaces * 6u,
            1u,
            firstIndex + (opaqueFaces + waterFaces) * 6u,
            baseVertex,
            0u
        ];
        unsafe
        {
            fixed (uint* cmdPtr = command)
            {
                GL.NamedBufferSubData((int)meshBuffers.IndirectDrawBuffer, (IntPtr)(slot * 60), 60, (IntPtr)cmdPtr);
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

        // Performance optimization: Count ready chunks and sum faces without LINQ
        var readyCount = 0;
        var totalFaces = 0u;
        foreach (var kvp in activeChunks)
        {
            if (kvp.Value.State == TerrainChunkState.Ready)
            {
                readyCount++;
                totalFaces += (uint)Math.Max(0, kvp.Value.VisibleVoxelCount);
            }
        }
        
        var totalVerticesInBuffer = meshBuffers.CurrentVertexBufferEnd;
        var totalIndicesInBuffer = meshBuffers.CurrentIndexBufferEnd;

        terrainRenderer.SetupBuffers(meshBuffers, totalVerticesInBuffer, totalFaces);
        Log.Debug($"ChunkStreamingManager: Renderer refreshed after {reason} (ready={readyCount}, faces={totalFaces}, vertices={totalVerticesInBuffer}, indices={totalIndicesInBuffer})");
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
                (desc.State == TerrainChunkState.Pending || desc.State == TerrainChunkState.HasTerrain))
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
                (desc.State == TerrainChunkState.Pending || desc.State == TerrainChunkState.HasTerrain))
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
            // Try to load full state from disk first
            if (TryLoadChunkState(idx))
            {
                // Loaded successfully!
                // Rebuild collision data (needed for physics)
                if (chunkVoxelCache.TryGetChunkData(idx, out var data) && data != null)
                {
                    var chunk = world[idx];
                    if (chunk != null)
                    {
                        chunk.RebuildAllCollisionSpans(data);
                        // Also update the global CollisionManager for picking
                        CollisionManager.UpdateChunkData(idx, chunk.ToChunkCollisionData());
                    }
                }
                
                // Mark as HasTerrain so it proceeds to meshing
                if (activeChunks.TryGetValue(idx, out var desc))
                {
                    desc.State = TerrainChunkState.HasTerrain;
                    desc.GenerationStartFrame = currentFrame;
                    activeChunks[idx] = desc;
                }
                
                // Skip generation job
                continue;
            }

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

        if (descriptor.State is TerrainChunkState.Generating or TerrainChunkState.Processing)
        {
            return false;
        }

        if (inFlightCpuGenerations >= maxConcurrentCpuGenerations)
        {
            return false;
        }

        descriptor.State = TerrainChunkState.Generating;
        descriptor.GenerationStartFrame = currentFrame;
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

        if (descriptor.State is not (TerrainChunkState.Pending or TerrainChunkState.HasTerrain))
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

            // 1. Base Terrain Generated -> Schedule Decoration
            if (result.Type == GenerationJobType.BaseTerrain)
            {
                if (descriptor.State != TerrainChunkState.Generating)
                {
                    chunkVoxelCache.TryRelease(chunkIdx);
                    Log.Debug($"ChunkStreamingManager: Dropping BaseTerrain result for chunk {chunkIdx} (state={descriptor.State})");
                    continue;
                }

                // Transition to Decorating
                descriptor.State = TerrainChunkState.Decorating;
                descriptor.GenerationStartFrame = currentFrame;
                activeChunks[chunkIdx] = descriptor;

                // Enqueue Decoration Job
                // Note: Edits were already applied during BaseTerrain generation
                cpuGenerationJobs.Enqueue(chunkIdx, null, GenerationJobType.Decoration);
                inFlightCpuGenerations++; // Increment because we started a new job
                continue;
            }

            // 2. Decoration Completed -> Ready for Meshing
            if (result.Type == GenerationJobType.Decoration)
            {
                if (descriptor.State != TerrainChunkState.Decorating)
                {
                    chunkVoxelCache.TryRelease(chunkIdx);
                    Log.Debug($"ChunkStreamingManager: Dropping Decoration result for chunk {chunkIdx} (state={descriptor.State})");
                    continue;
                }

                ApplyCollisionResults(chunkIdx, result.Generation);

                // Mark as HasTerrain - ready for lighting/meshing
                descriptor.State = TerrainChunkState.HasTerrain;
                descriptor.GenerationStartFrame = currentFrame;
                activeChunks[chunkIdx] = descriptor;
                
                processed++;
            }
        }

        if (processed > 0)
        {
            Log.Debug($"ChunkStreamingManager: {processed} chunks completed terrain generation (frame {currentFrame})");
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
    /// Uses Chebyshev distance (square region) to match visibility calculations
    /// </summary>
    private void UnloadDistantChunks(Vector3 cameraPosition, HashSet<int> visibleChunks)
    {
        if (activeChunks.Count == 0)
            return;

        var cameraChunkX = (int)(cameraPosition.X / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)(cameraPosition.Z / VoxelHelper.ChunkSideSize);

        var unloadRadius = GetUnloadDistanceChunks();
        var retentionRadius = GetRetentionDistanceChunks();

        var chunksToUnload = new List<int>();

        // Find chunks beyond unload distance
        foreach (var kvp in activeChunks)
        {
            var chunkIdx = kvp.Key;
            var desc = kvp.Value;

            // Don't unload chunks that are still in-flight (actively being processed)
            // Decorating is included to prevent race condition where base terrain is unloaded
            // before decoration job reads it from voxel cache
            if (desc.State is TerrainChunkState.Generating or
                TerrainChunkState.Decorating or
                TerrainChunkState.HasBaseTerrain or
                TerrainChunkState.Pending or
                TerrainChunkState.Processing)
                continue;

            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

            var dx = chunkX - cameraChunkX;
            var dz = chunkZ - cameraChunkZ;
            // Use Chebyshev distance (square region) to match visibility
            var chebyshevDist = Math.Max(Math.Abs(dx), Math.Abs(dz));

            if (visibleChunks.Contains(chunkIdx))
                continue;

            var lastSeen = chunkLastVisibleFrame.TryGetValue(chunkIdx, out var frame) ? frame : long.MinValue;
            var framesSinceVisible = lastSeen == long.MinValue ? long.MaxValue : currentFrame - frame;
            var outsideUnload = chebyshevDist > unloadRadius;
            var outsideRetention = chebyshevDist > retentionRadius;

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

            var aDist = Math.Max(Math.Abs(aX - cameraChunkX), Math.Abs(aZ - cameraChunkZ));
            var bDist = Math.Max(Math.Abs(bX - cameraChunkX), Math.Abs(bZ - cameraChunkZ));

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

        // Save dirty chunk before unloading
        bool needsSave;
        lock (saveLock)
        {
            needsSave = dirtyChunks.Remove(chunkIndex);
        }
        if (needsSave)
        {
            SaveChunkState(chunkIndex);
        }

        RemoveChunkFromPendingQueues(chunkIndex);
        
        // CRITICAL: Remove from batch tracking to prevent stale batch checks
        currentStreamingBatch.Remove(chunkIndex);
        pendingStreamingBatch.Remove(chunkIndex);
        chunksNeedingReprocess.Remove(chunkIndex);
        chunksLoadedFromDisk.Remove(chunkIndex);

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
        var (_, pending, generating, ready) = GetStats();

        // Calculate target based on current (prefetch-aware) load distance using square region
        var targetChunks = VoxelHelper.CalculateSquareChunkCount(GetActiveLoadDistance());

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
    /// <summary>
    /// Helper to set a block and update the column's surface height cache.
    /// This ensures the mesher knows how high to scan for geometry.
    /// </summary>
    private static void SetBlockWithHeightUpdate(ChunkData chunkData, int x, int y, int z, BlockId block)
    {
        chunkData.SetBlock(x, y, z, block);
        
        // Update surface height (highest non-air block)
        // This ensures meshing and lighting include non-opaque blocks like torches, glass, leaves
        var idx = z * VoxelHelper.ChunkSideSize + x;
        var currentHeight = chunkData.SurfaceHeights[idx];
        var isAir = block.IsAir();

        if (!isAir)
        {
            if (y > currentHeight)
            {
                chunkData.SurfaceHeights[idx] = y;
            }
        }
        else if (y == currentHeight)
        {
            // Removed the highest block, scan down to find new highest
            var newHeight = -1; // Default for empty column
            // Scan down from just below the removed block
            for (var scanY = y - 1; scanY >= 0; scanY--)
            {
                if (!chunkData.GetBlock(x, scanY, z).IsAir())
                {
                    newHeight = scanY;
                    break;
                }
            }
            chunkData.SurfaceHeights[idx] = newHeight;
        }
    }

    /// <summary>
    /// Check if any cardinal neighbor (including above/below) of the given block is Water.
    /// Used to determine if a broken block should be replaced with Water (simulating water flow).
    /// </summary>
    /// <param name="chunkIdx">The chunk index containing the block.</param>
    /// <param name="localX">Local X coordinate within the chunk.</param>
    /// <param name="localY">Local Y coordinate within the chunk.</param>
    /// <param name="localZ">Local Z coordinate within the chunk.</param>
    /// <param name="chunkData">The chunk's voxel data.</param>
    /// <returns>True if any adjacent block is Water, false otherwise.</returns>
    private bool HasAdjacentWaterBlock(int chunkIdx, int localX, int localY, int localZ, ChunkData chunkData)
    {
        // Check Y neighbors (above and below) - these are always within the same chunk
        if (localY > 0 && chunkData.GetBlock(localX, localY - 1, localZ) == BlockId.Water)
            return true;
        if (localY < VoxelHelper.ChunkYSize - 1 && chunkData.GetBlock(localX, localY + 1, localZ) == BlockId.Water)
            return true;

        // Check X neighbors
        if (localX > 0)
        {
            if (chunkData.GetBlock(localX - 1, localY, localZ) == BlockId.Water)
                return true;
        }
        else
        {
            // Check neighbor chunk -X
            var neighborIdx = GetNeighborChunkIndex(chunkIdx, -1, 0);
            if (neighborIdx >= 0 && chunkVoxelCache.TryGetChunkData(neighborIdx, out var neighborData) && neighborData != null)
            {
                if (neighborData.GetBlock(VoxelHelper.ChunkSideSize - 1, localY, localZ) == BlockId.Water)
                    return true;
            }
        }

        if (localX < VoxelHelper.ChunkSideSize - 1)
        {
            if (chunkData.GetBlock(localX + 1, localY, localZ) == BlockId.Water)
                return true;
        }
        else
        {
            // Check neighbor chunk +X
            var neighborIdx = GetNeighborChunkIndex(chunkIdx, 1, 0);
            if (neighborIdx >= 0 && chunkVoxelCache.TryGetChunkData(neighborIdx, out var neighborData) && neighborData != null)
            {
                if (neighborData.GetBlock(0, localY, localZ) == BlockId.Water)
                    return true;
            }
        }

        // Check Z neighbors
        if (localZ > 0)
        {
            if (chunkData.GetBlock(localX, localY, localZ - 1) == BlockId.Water)
                return true;
        }
        else
        {
            // Check neighbor chunk -Z
            var neighborIdx = GetNeighborChunkIndex(chunkIdx, 0, -1);
            if (neighborIdx >= 0 && chunkVoxelCache.TryGetChunkData(neighborIdx, out var neighborData) && neighborData != null)
            {
                if (neighborData.GetBlock(localX, localY, VoxelHelper.ChunkSideSize - 1) == BlockId.Water)
                    return true;
            }
        }

        if (localZ < VoxelHelper.ChunkSideSize - 1)
        {
            if (chunkData.GetBlock(localX, localY, localZ + 1) == BlockId.Water)
                return true;
        }
        else
        {
            // Check neighbor chunk +Z
            var neighborIdx = GetNeighborChunkIndex(chunkIdx, 0, 1);
            if (neighborIdx >= 0 && chunkVoxelCache.TryGetChunkData(neighborIdx, out var neighborData) && neighborData != null)
            {
                if (neighborData.GetBlock(localX, localY, 0) == BlockId.Water)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get the chunk index of a neighbor chunk given a delta offset.
    /// Returns -1 if the neighbor is out of world bounds.
    /// </summary>
    private static int GetNeighborChunkIndex(int chunkIdx, int dx, int dz)
    {
        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
        var nx = chunkX + dx;
        var nz = chunkZ + dz;
        if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
            return -1;
        return nz * VoxelHelper.WorldChunksXZ + nx;
    }

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

        // Water replacement logic for breaking blocks:
        // Water should fill in when breaking a block ONLY if at least one cardinal neighbor 
        // (including above/below) is a Water block. This simulates water flowing into the void.
        // This allows:
        // - Breaking blocks underwater in ocean -> water fills in
        // - Breaking blocks on land/caves -> air (no water magically appears)
        if (isBreaking && blockId == BlockId.Air)
        {
            if (chunkVoxelCache.TryGetChunkData(chunkIdx, out var checkData) && checkData != null)
            {
                if (HasAdjacentWaterBlock(chunkIdx, localX, localY, localZ, checkData))
                {
                    blockId = BlockId.Water;
                }
            }
        }

        // Check if this edit involves a light-emitting block (for proper light propagation to neighbors)
        var oldBlock = BlockId.Air;
        var oldLightValue = 0;
        var newLightValue = BlockRegistry.GetLightValue(blockId);
        var oldSkyLight = 0;
        var oldBlockLight = 0;
        
        if (chunkVoxelCache.TryGetChunkData(chunkIdx, out var chunkData) && chunkData != null)
        {
            oldBlock = chunkData.GetBlock(localX, localY, localZ);
            oldLightValue = BlockRegistry.GetLightValue(oldBlock);
            // Get current light values at this position BEFORE placing the block
            // Index formula: y * ChunkSideSizeSquare + z * ChunkSideSize + x
            // Light data format: low nibble = sky light, high nibble = block light
            var lightIndex = localY * VoxelHelper.ChunkSideSizeSquare + localZ * VoxelHelper.ChunkSideSize + localX;
            oldSkyLight = chunkData.LightData[lightIndex] & 0xF;              // Low nibble = sky light
            oldBlockLight = (chunkData.LightData[lightIndex] >> 4) & 0xF;     // High nibble = block light
        }
        
        var isRemovingLightSource = oldLightValue > 0 && newLightValue == 0;
        var isPlacingLightSource = newLightValue > 0;
        // Light is affected if: removing/placing light source OR changing opacity (opaque <-> transparent)
        var oldIsOpaque = oldBlock.IsOpaque();
        var newIsOpaque = blockId.IsOpaque();
        var affectsLight = oldLightValue > 0 || newLightValue > 0 || (oldIsOpaque != newIsOpaque);
        
        // Placing an opaque block where there was light (sky or block) - need full recalculation
        var isBlockingLight = !oldIsOpaque && newIsOpaque && (oldSkyLight > 0 || oldBlockLight > 0);

        // If removing a light source, use the cross-chunk light removal algorithm
        HashSet<int>? lightAffectedChunks = null;
        if (isRemovingLightSource && chunkData != null)
        {
            // First update the block in the cache so light removal sees the new state
            SetBlockWithHeightUpdate(chunkData, localX, localY, localZ, blockId);
            
            // Remove light using BFS that crosses chunk boundaries
            lightAffectedChunks = LightingCalculator.RemoveBlockLight(
                chunkIdx,
                localX, localY, localZ,
                oldLightValue,
                idx => chunkVoxelCache.TryGetChunkData(idx, out var data) ? data : null
            );
            
            Log.Info($"Block light removal affected {lightAffectedChunks.Count} chunks: {string.Join(", ", lightAffectedChunks)}");
        }
        else if (isBlockingLight && chunkData != null)
        {
            // Placing an opaque block that blocks existing light
            // Use comprehensive recalculation: find all light sources in radius 15, clear, and re-propagate
            SetBlockWithHeightUpdate(chunkData, localX, localY, localZ, blockId);
            
            lightAffectedChunks = LightingCalculator.RecalculateLightingAroundBlock(
                chunkIdx,
                localX, localY, localZ,
                idx => chunkVoxelCache.TryGetChunkData(idx, out var data) ? data : null
            );
            
            // Mark these chunks as having light already recalculated - don't re-propagate during meshing
            foreach (var affectedIdx in lightAffectedChunks)
            {
                chunksWithLightRecalculated.Add(affectedIdx);
            }
            
            Log.Info($"Light recalculation around placed block affected {lightAffectedChunks.Count} chunks");
        }
        else if (chunkData != null)
        {
            // For all other block changes (including placing light sources), update the voxel cache directly
            SetBlockWithHeightUpdate(chunkData, localX, localY, localZ, blockId);
            
            // Recalculate lighting for this chunk if:
            // - Placing a light source (use cross-chunk propagation)
            // - Breaking an opaque block (light can now flow through)
            if (isPlacingLightSource)
            {
                // Log available neighbor chunks for debugging
                var chunkXCoord = chunkIdx % VoxelHelper.WorldChunksXZ;
                var chunkZCoord = chunkIdx / VoxelHelper.WorldChunksXZ;
                var neighbors = new List<int>();
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var nx = chunkXCoord + dx;
                    var nz = chunkZCoord + dz;
                    if (nx >= 0 && nx < VoxelHelper.WorldChunksXZ && nz >= 0 && nz < VoxelHelper.WorldChunksXZ)
                    {
                        var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;
                        if (chunkVoxelCache.TryGetChunkData(neighborIdx, out _))
                            neighbors.Add(neighborIdx);
                    }
                }
                Log.Info($"Placing light source at ({localX},{localY},{localZ}) in chunk {chunkIdx}, {neighbors.Count}/8 neighbors in cache: [{string.Join(",", neighbors)}]");
                
                // Use cross-chunk light propagation for placed light sources
                lightAffectedChunks = LightingCalculator.AddBlockLight(
                    chunkIdx,
                    localX, localY, localZ,
                    newLightValue,
                    idx => chunkVoxelCache.TryGetChunkData(idx, out var data) ? data : null
                );
                
                // Mark these chunks as having light already calculated - don't re-propagate during meshing
                foreach (var affectedIdx in lightAffectedChunks)
                {
                    chunksWithLightRecalculated.Add(affectedIdx);
                }
                
                Log.Info($"Block light addition affected {lightAffectedChunks.Count} chunks: {string.Join(", ", lightAffectedChunks)}");
            }
            else if (!newIsOpaque && oldIsOpaque)
            {
                // Breaking opaque block - use cross-chunk recalculation to preserve neighbor light
                // This finds all light sources (block + sky) within radius 15 and re-propagates
                lightAffectedChunks = LightingCalculator.RecalculateLightingAroundBlock(
                    chunkIdx,
                    localX, localY, localZ,
                    idx => chunkVoxelCache.TryGetChunkData(idx, out var data) ? data : null
                );
                
                // Mark these chunks as having light already recalculated
                foreach (var affectedIdx in lightAffectedChunks)
                {
                    chunksWithLightRecalculated.Add(affectedIdx);
                }
                
                Log.Info($"Breaking opaque block - light recalculation affected {lightAffectedChunks.Count} chunks");
            }
        }

        // Update collision data immediately so player collision and block picking work correctly
        // This rebuilds the collision spans for the affected column from voxel data
        if (chunkData != null)
        {
            // Update CollisionManager (used for raycasting/picking)
            CollisionManager.RebuildColumnFromVoxelData(chunkIdx, localX, localZ, chunkData);
            
            // Update Chunk spans (used for player collision via VoxelWorld)
            var chunk = world[chunkIdx];
            chunk?.RebuildColumnSpans(localX, localZ, chunkData);
        }

        MarkVoxelEdited(chunkIdx, voxelIdx, blockId, isBreaking);

        // Mark chunk as dirty (needs regeneration)
        MarkChunkDirty(chunkIdx);

        // Mark all light-affected chunks for reprocessing
        if (lightAffectedChunks != null)
        {
            foreach (var affectedIdx in lightAffectedChunks)
            {
                if (affectedIdx != chunkIdx) // Source chunk already marked dirty
                {
                    MarkChunkForReprocess(affectedIdx);
                }
            }
        }

        // If edit affects light or is on chunk boundary, mark neighbors for reprocess
        if (affectsLight)
        {
            // Light sources affect all neighbors
            MarkNeighborsForReprocess(chunkIdx);
            
            // Also mark neighbors as modified because their light levels might change
            // This ensures we save the propagated light changes
            foreach (var (dx, dz) in CardinalNeighborOffsets)
            {
                var nx = chunkX + dx;
                var nz = chunkZ + dz;
                if (nx >= 0 && nx < VoxelHelper.WorldChunksXZ && nz >= 0 && nz < VoxelHelper.WorldChunksXZ)
                {
                    var nIdx = nz * VoxelHelper.WorldChunksXZ + nx;
                    if (activeChunks.ContainsKey(nIdx))
                    {
                        modifiedChunks.Add(nIdx);
                    }
                }
            }
        }
        else
        {
            // Non-light edits: only mark boundary-touching neighbors
            if (localX == 0 && chunkX > 0)
                MarkChunkForReprocess((chunkZ) * VoxelHelper.WorldChunksXZ + (chunkX - 1));
            if (localX == VoxelHelper.ChunkSideSize - 1 && chunkX < VoxelHelper.WorldChunksXZ - 1)
                MarkChunkForReprocess((chunkZ) * VoxelHelper.WorldChunksXZ + (chunkX + 1));
            if (localZ == 0 && chunkZ > 0)
                MarkChunkForReprocess((chunkZ - 1) * VoxelHelper.WorldChunksXZ + chunkX);
            if (localZ == VoxelHelper.ChunkSideSize - 1 && chunkZ < VoxelHelper.WorldChunksXZ - 1)
                MarkChunkForReprocess((chunkZ + 1) * VoxelHelper.WorldChunksXZ + chunkX);
        }

        Log.Debug($"Block edit at world{worldPosition} → chunk{chunkIdx} local({localX},{localY},{localZ}) voxel{voxelIdx} block={blockId} breaking={isBreaking} affectsLight={affectsLight}");

        // Mark as modified so it gets saved
        modifiedChunks.Add(chunkIdx);
        
        // Mark chunk as dirty for background save (no immediate save for performance)
        lock (saveLock)
        {
            dirtyChunks.Add(chunkIdx);
        }
    }

    /// <summary>
    /// Mark a specific chunk for reprocessing (light recalc + remesh).
    /// Unlike highPriorityPending (which goes to terrain generation), chunksNeedingReprocess 
    /// only triggers meshing - the voxel data is preserved.
    /// </summary>
    private void MarkChunkForReprocess(int chunkIdx)
    {
        // Skip if already tracked for processing
        if (currentStreamingBatch.Contains(chunkIdx) || chunksNeedingReprocess.Contains(chunkIdx))
            return;

        if (!activeChunks.TryGetValue(chunkIdx, out var desc))
            return;

        // Accept chunks in Ready state (normal case) or Processing state (chunk is being meshed
        // but needs to be remeshed again with updated data)
        if (desc.State == TerrainChunkState.Ready || desc.State == TerrainChunkState.Processing)
        {
            // Only change state from Ready to HasTerrain; leave Processing as-is
            // Processing chunks will finish meshing, become Ready, then get reprocessed
            if (desc.State == TerrainChunkState.Ready)
            {
                desc.State = TerrainChunkState.HasTerrain;
                activeChunks[chunkIdx] = desc;
            }
            chunksNeedingReprocess.Add(chunkIdx);
            // NOTE: Do NOT add to highPriorityPending - that triggers terrain regeneration
            // which would destroy our voxel data including light values. Chunks in
            // chunksNeedingReprocess are processed by ProcessReprocessChunks which only remeshes.
            Metrics.RecordReprocess();
        }
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
            desc.State = TerrainChunkState.HasTerrain;
            activeChunks[chunkIdx] = desc;
            chunksNeedingReprocess.Add(chunkIdx);
            // NOTE: Do NOT add to highPriorityPending - that triggers terrain regeneration
            // which would destroy our voxel data including light values. Chunks in
            // chunksNeedingReprocess are processed by ProcessReprocessChunks which only remeshes.

            Log.Debug($"Marked chunk {chunkIdx} for reprocessing (was Ready, now HasTerrain)");
        }
        else if (desc.State == TerrainChunkState.HasTerrain)
        {
            // Already needs reprocessing, ensure it's in the set
            chunksNeedingReprocess.Add(chunkIdx);
            Log.Debug($"Chunk {chunkIdx} already marked for reprocessing");
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

    private bool IsChunkVisible(int chunkIndex, Vector4[] frustumPlanes)
    {
        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;

        // Get actual chunk height from descriptor if available, otherwise use full height
        var chunkHeight = VoxelHelper.ChunkYSize;
        if (activeChunks.TryGetValue(chunkIndex, out var descriptor) && descriptor.MaxSurfaceHeight > 0)
        {
            // Add a small margin above the surface for safety
            chunkHeight = Math.Min(descriptor.MaxSurfaceHeight + 8, VoxelHelper.ChunkYSize);
        }

        var min = new Vector3(chunkX * VoxelHelper.ChunkSideSize, 0f, chunkZ * VoxelHelper.ChunkSideSize);
        var max = min + new Vector3(VoxelHelper.ChunkSideSize, chunkHeight, VoxelHelper.ChunkSideSize);

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
        // 3 commands per slot * 5 uints per command = 15 uints
        var slotBase = commandSlot * 15 * sizeof(uint); 
        
        // Instance count is the 2nd uint in the command struct (offset 4 bytes)
        var opaqueInstanceOffset = slotBase + sizeof(uint);
        var waterInstanceOffset = slotBase + 5 * sizeof(uint) + sizeof(uint);
        var translucentInstanceOffset = slotBase + 10 * sizeof(uint) + sizeof(uint);
        
        var instanceValue = isVisible ? 1u : 0u;

        unsafe
        {
            GL.NamedBufferSubData(buffer, (IntPtr)opaqueInstanceOffset, sizeof(uint), (IntPtr)(&instanceValue));
            GL.NamedBufferSubData(buffer, (IntPtr)waterInstanceOffset, sizeof(uint), (IntPtr)(&instanceValue));
            GL.NamedBufferSubData(buffer, (IntPtr)translucentInstanceOffset, sizeof(uint), (IntPtr)(&instanceValue));
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

    /// <summary>
    /// Save a chunk's state to disk with GZip compression.
    /// Thread-safe: can be called from background save thread.
    /// </summary>
    public void SaveChunkState(int chunkIdx)
    {
        // Get data under cache lock
        if (!chunkVoxelCache.TryGetChunkData(chunkIdx, out var data) || data == null) return;
        chunkVoxelCache.TryGetBiomeData(chunkIdx, out var biomeData);

        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);
        var worldName = terrainConfig.WorldName;
        var seed = generationSeed;

        var folderName = $"{worldName}_{seed}";
        var fileName = $"chunk_{chunkPos.X}_{chunkPos.Z}.dat";
        var path = Path.Combine(Environment.CurrentDirectory, "save", folderName, fileName);

        var dirName = Path.GetDirectoryName(path);
        if (dirName is not null) Directory.CreateDirectory(dirName);

        // Write to temp file first, then rename for atomic save
        var tempPath = path + ".tmp";
        try
        {
            using (var fileStream = File.Create(tempPath))
            {
                // Write uncompressed header (magic + version) so we can detect format
                using var headerWriter = new BinaryWriter(fileStream, System.Text.Encoding.UTF8, leaveOpen: true);
                headerWriter.Write("CHNK");
                headerWriter.Write(SAVE_FILE_VERSION); // v2 = compressed
                
                // Compress the rest with GZip
                using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal, leaveOpen: true);
                using var writer = new BinaryWriter(gzipStream, System.Text.Encoding.UTF8, leaveOpen: true);
                
                data.Serialize(writer);
                
                // Write biome data presence flag
                writer.Write(biomeData != null);
                if (biomeData != null)
                {
                    biomeData.Serialize(writer);
                }
            }
            
            // Atomic rename
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save chunk {chunkIdx}: {ex.Message}");
            // Clean up temp file if it exists
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Try to load a chunk's state from disk.
    /// Supports both v1 (uncompressed) and v2 (GZip compressed) formats.
    /// </summary>
    public bool TryLoadChunkState(int chunkIdx)
    {
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);
        var worldName = terrainConfig.WorldName;
        var seed = generationSeed;

        var folderName = $"{worldName}_{seed}";
        var fileName = $"chunk_{chunkPos.X}_{chunkPos.Z}.dat";
        var path = Path.Combine(Environment.CurrentDirectory, "save", folderName, fileName);

        if (!File.Exists(path)) return false;

        try
        {
            using var fileStream = File.OpenRead(path);
            
            // Read header (uncompressed)
            using var headerReader = new BinaryReader(fileStream, System.Text.Encoding.UTF8, leaveOpen: true);
            var magic = headerReader.ReadString();
            if (magic != "CHNK") return false;
            var version = headerReader.ReadInt32();

            ChunkData data;
            ChunkBiomeData? biomeData = null;

            if (version >= 2)
            {
                // v2+: GZip compressed
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzipStream);
                
                data = ChunkData.Deserialize(reader);
                
                if (reader.ReadBoolean())
                {
                    biomeData = ChunkBiomeData.Deserialize(reader);
                }
            }
            else
            {
                // v1: Uncompressed (legacy format)
                data = ChunkData.Deserialize(headerReader);
                
                if (headerReader.ReadBoolean())
                {
                    biomeData = ChunkBiomeData.Deserialize(headerReader);
                }
            }

            // Store in cache
            chunkVoxelCache.Store(data);
            if (biomeData != null)
            {
                chunkVoxelCache.StoreBiomeData(chunkIdx, biomeData);
            }
            
            // Mark as loaded from disk - light data is already complete, skip recalculation
            chunksLoadedFromDisk.Add(chunkIdx);
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load chunk state for {chunkIdx}: {ex.Message}");
            return false;
        }
    }

    public void SaveEdits()
    {
        foreach (var chunkIdx in chunkEdits.Keys)
        {
            SaveChunkEdits(chunkIdx);
        }
        // Only save full state for chunks that have been modified
        foreach (var chunkIdx in modifiedChunks)
        {
            if (activeChunks.ContainsKey(chunkIdx))
            {
                SaveChunkState(chunkIdx);
            }
        }
        Log.Info($"Saved edits for {chunkEdits.Count} chunks and state for {modifiedChunks.Count} chunks");
    }
    
    /// <summary>
    /// Check if periodic save is needed and start background save if so.
    /// </summary>
    private void CheckPeriodicSave()
    {
        var now = DateTime.UtcNow;
        if ((now - lastAutoSaveTime).TotalSeconds < AUTO_SAVE_INTERVAL_SECONDS)
            return;
            
        // Check if there are dirty chunks
        int dirtyCount;
        lock (saveLock)
        {
            dirtyCount = dirtyChunks.Count;
        }
        
        if (dirtyCount == 0)
            return;
            
        // Check if background save is already running
        if (backgroundSaveTask != null && !backgroundSaveTask.IsCompleted)
            return;
            
        lastAutoSaveTime = now;
        StartBackgroundSave();
    }

    /// <summary>
    /// Start a background task to save all dirty chunks.
    /// </summary>
    private void StartBackgroundSave() => backgroundSaveTask = Task.Run(SaveDirtyChunksBackground, saveTokenSource.Token);

    /// <summary>
    /// Background worker method to save dirty chunks.
    /// </summary>
    private void SaveDirtyChunksBackground()
    {
        var chunksToSave = new List<int>();
        
        lock (saveLock)
        {
            chunksToSave.AddRange(dirtyChunks);
        }
        
        if (chunksToSave.Count == 0)
            return;
            
        var saved = 0;
        foreach (var chunkIdx in chunksToSave)
        {
            if (saveTokenSource.Token.IsCancellationRequested)
                break;
                
            try
            {
                SaveChunkState(chunkIdx);
                
                // Remove from dirty set after successful save
                lock (saveLock)
                {
                    dirtyChunks.Remove(chunkIdx);
                }
                saved++;
            }
            catch (Exception ex)
            {
                Log.Error($"Background save failed for chunk {chunkIdx}: {ex.Message}");
            }
        }
        
        if (saved > 0)
        {
            Log.Info($"Background save completed: {saved} chunks saved");
        }
    }
    
    /// <summary>
    /// Save all dirty chunks synchronously. Used during shutdown.
    /// </summary>
    private void SaveAllDirtyChunks()
    {
        List<int> chunksToSave;
        lock (saveLock)
        {
            chunksToSave = [.. dirtyChunks];
            dirtyChunks.Clear();
        }
        
        if (chunksToSave.Count == 0)
            return;
            
        foreach (var chunkIdx in chunksToSave)
        {
            try
            {
                SaveChunkState(chunkIdx);
            }
            catch (Exception ex)
            {
                Log.Error($"Final save failed for chunk {chunkIdx}: {ex.Message}");
            }
        }
        
        Log.Info($"Final save completed: {chunksToSave.Count} dirty chunks saved");
    }
    
    /// <summary>
    /// Explicitly save all pending world data and prepare for shutdown.
    /// Call this method before exiting the game to ensure all block edits are persisted.
    /// This is the recommended way to save - do not rely on Dispose() for critical saves.
    /// </summary>
    public void Shutdown()
    {
        Log.Info("ChunkStreamingManager: Shutdown initiated - saving all pending data...");
        
        // Cancel background save and wait for completion
        saveTokenSource.Cancel();
        try
        {
            backgroundSaveTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException) { }
        catch (TaskCanceledException) { }
        
        // Save all dirty chunks synchronously
        SaveAllDirtyChunks();
        
        // Save edit data
        SaveEdits();
        
        Log.Info("ChunkStreamingManager: Shutdown save complete");
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
        // Note: Shutdown() should be called explicitly before Dispose() to save pending data.
        // Dispose() only releases resources - it does NOT save data.
        
        // Cancel background save task if still running
        saveTokenSource.Cancel();
        try
        {
            backgroundSaveTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException) { }
        catch (TaskCanceledException) { }

        // Cleanup all fences
        foreach (var kvp in activeChunks)
        {
            if (kvp.Value.Fence != IntPtr.Zero)
            {
                try { GL.DeleteSync(kvp.Value.Fence); } catch { }
            }
        }

        // Cleanup resources
        meshBuffers?.Dispose();
        chunkVoxelCache.Dispose();
        ClearPendingCpuMeshingQueue();
        cpuGenerationJobs.Dispose();
        cpuMeshingJobs.Dispose();
        saveTokenSource.Dispose();

        Log.Info("ChunkStreamingManager: Disposed");
    }

}
