using OpenRender;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Core coordinator for GPU-based streaming voxel terrain.
/// Manages chunk lifecycle, batch submission, and GPU<->CPU synchronization.
/// </summary>
public sealed class ChunkStreamingManager : IDisposable
{
    private readonly VoxelWorld world;

    public VoxelWorld World => world;
    public CollisionManager CollisionManager { get; private set; }

    private readonly GpuBufferAllocator bufferAllocator;
    private readonly Dictionary<int, ChunkDescriptor> activeChunks;
    private readonly Queue<int> highPriorityPending;
    private readonly Queue<int> lowPriorityPending;
    private readonly Queue<BatchSubmission> inFlightBatches;
    private readonly Queue<ScanningBatch> scanningBatches; // Phase 5.3: Batches waiting for Scan/Count completion
    private IntPtr lastCompactionFence = IntPtr.Zero;      // Phase 5.3: Fence to serialize Phase 3 access to shared buffers
    private int totalBatchesInPipeline = 0;

    private long currentFrame;
    private const int MAX_CHUNKS_PER_BATCH = VoxelHelper.DEFAULT_MAX_CHUNKS_PER_BATCH;
    private const int MAX_IN_FLIGHT_BATCHES = 2;
    private const int HIGH_PRIORITY_DISTANCE = 2; // Chunks within this radius are high priority

    // Configurable load distance (defaults to VoxelHelper.MaxDistanceInChunks)
    public int LoadDistance { get; set; } = VoxelHelper.MaxDistanceInChunks;

    // Phase 2: GPU generation shader
    private Shader? generationShader;
    // Phase 2.5: Column Spans
    private Shader? columnSpansShader;
    private readonly uint[] columnSpansBuffers = new uint[2];

    // Phase 2.6: Edits
    private Shader? applyEditsShader;
    private uint editBuffer;
    private uint worldEditsBuffer; // NEW: Buffer for neighbor edits
    private readonly Dictionary<int, Dictionary<int, BlockType>> chunkEdits = [];

    // Terrain Config & Params (M1)
    private TerrainConfig terrainConfig = new();
    private uint terrainParamsSSBO;
    private int heightSplineTexture;
    private int biomeLutTexture;
    private FileSystemWatcher? configWatcher;
    private volatile bool pendingConfigReload;
    private const string ConfigFileName = "terrain_config.json";

    private int generationSeed;
    private bool generationTestMode;
    private readonly uint[] chunkIndicesBuffers = new uint[2];
    private readonly uint[] voxelDataBuffers = new uint[2];
    private readonly uint[] columnHeightsBuffers = new uint[2];
    private readonly uint[] columnMetaBuffers = new uint[2];
    private int nextBufferIndex = 0; // Phase 5.3: Toggle for double buffering

    // Phase 3: dedicated chunk indices buffer to avoid races with Phase 2 uploads
    private uint compactionChunkIndicesBuffer;

    // Phase 3: Visibility & Compaction pipeline
    private Shader? visibilityShader;
    private Shader? countShader;
    private Shader? scanShader; // Cached scan shader
    private Shader? compactShader;
    private Shader? buildIndirectShader; // Cached build-indirect shader
    private Phase3BufferManager? phase3Buffers;

    // Phase 4: Rendering
    private VoxelTerrainRenderer? terrainRenderer;

    // Phase 4.5: Frustum Culling
    private Shader? frustumShader;
    private uint frustumUBO;
    private uint visibilityFlagsSSBO;
    private uint cullingChunkIndicesBuffer; // Dedicated buffer for culling to avoid conflicts with generation
    private uint cullingCommandSlotsBuffer; // Buffer for command slots corresponding to chunks being culled
    private int[]? lastVisibilityFlags;
    private int chunkIndicesBufferCapacity = 0;  // Track allocated capacity
    private int visibilityFlagsCapacity = 0;     // Track visibility buffer capacity
    public int VisibleChunkCount { get; private set; }
    public int CulledChunkCount { get; private set; }

    // Phase 5: Streaming & Unloading
    // CRITICAL: Unload distance must provide hysteresis but not accumulate too many chunks
    // Load distance: 16 chunks radius (1089 chunks in 33x33 grid)
    // Unload distance: 20 chunks radius (1681 chunks in 41x41 grid)
    // Hysteresis: 4 chunks (25% buffer) - enough to prevent thrashing
    private const int UNLOAD_DISTANCE_CHUNKS = VoxelHelper.MaxDistanceInChunks + 4; // Was +8, now +4
    private const int MAX_UNLOADS_PER_FRAME = 32; // Phase 5.2 FIX: Increased from 8 to handle unbounded growth
    private Vector3 lastCameraPosition;
    private int unloadCheckFrame = 0;
    private const int UNLOAD_CHECK_INTERVAL = 5; // Phase 5.2 FIX: Check every 5 frames instead of 30

    // Public access to the world for Player integration
    // public VoxelWorld World => world;

    public ChunkStreamingManager(VoxelWorld world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        CollisionManager = new CollisionManager();
        bufferAllocator = new GpuBufferAllocator();
        activeChunks = [];
        highPriorityPending = new Queue<int>();
        lowPriorityPending = new Queue<int>();
        inFlightBatches = new Queue<BatchSubmission>();
        scanningBatches = new Queue<ScanningBatch>();

        Log.Info("ChunkStreamingManager: Initialized");
    }

    /// <summary>
    /// Calculate the maximum number of chunks that can be active at once based on UNLOAD distance.
    /// This is used for pre-allocating buffers to eliminate progressive resizing.
    /// CRITICAL: Must account for hysteresis - chunks load at MaxDistanceInChunks but unload at UNLOAD_DISTANCE_CHUNKS!
    /// </summary>
    public static int CalculateMaxViewChunks()
    {
        // CRITICAL: Use UNLOAD distance, not LOAD distance!
        // Chunks are loaded within MaxDistanceInChunks (16) but kept until UNLOAD_DISTANCE_CHUNKS (20)
        // So max active chunks = (2 * unloadDistance + 1)^2
        const int UNLOAD_HYSTERESIS = 4; // Must match UNLOAD_DISTANCE_CHUNKS calculation
        var unloadDistance = VoxelHelper.MaxDistanceInChunks + UNLOAD_HYSTERESIS;

        // Calculate square area: (2 * unloadDistance + 1)^2
        // For unloadDistance=20: (2*20+1)^2 = 41^2 = 1,681 chunks
        var maxChunks = (2 * unloadDistance + 1) * (2 * unloadDistance + 1);

        Log.Info($"ChunkStreamingManager: Calculated max view chunks: {maxChunks} (unloadDistance={unloadDistance}, loadDistance={VoxelHelper.MaxDistanceInChunks})");

        return maxChunks;
    }

    /// <summary>
    /// Update streaming system - called once per frame
    /// </summary>
    public void Update(Vector3 cameraPosition)
    {
        currentFrame++;

        // Handle hot-reload
        if (pendingConfigReload)
        {
            pendingConfigReload = false;
            // Add small delay/retry to avoid file lock issues
            try
            {
                var configPath = Path.Combine(Environment.CurrentDirectory, ConfigFileName);
                terrainConfig = TerrainConfig.Load(configPath);
                UploadTerrainConfig();
                Log.Info("TerrainConfig hot-reloaded");

                // Mark all chunks as dirty to force regeneration with new params
                foreach (var kvp in activeChunks)
                {
                    if (kvp.Value.State == TerrainChunkState.Ready)
                    {
                        MarkChunkDirty(kvp.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to hot-reload TerrainConfig: {ex.Message}");
            }
        }

        // 1. Poll completed batches
        PollCompletedBatches();

        // 2. Determine visible chunks
        var visibleChunks = DetermineVisibleChunks(cameraPosition);

        // 3. Queue new chunks for generation
        QueueNewChunks(visibleChunks);

        // 4. Submit batches if ready
        SubmitPendingBatches();

        // 5. Unload distant chunks (Phase 5)
        unloadCheckFrame++;
        if (unloadCheckFrame >= UNLOAD_CHECK_INTERVAL)
        {
            unloadCheckFrame = 0;
            UnloadDistantChunks(cameraPosition);
        }

        lastCameraPosition = cameraPosition;
    }

    /// <summary>
    /// Request chunks for generation (called by VoxelWorld)
    /// </summary>


    /// <summary>
    /// Get all ready chunks for rendering
    /// </summary>
    public IEnumerable<ChunkDescriptor> GetReadyChunks() => activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready);

    /// <summary>
    /// Get current statistics
    /// </summary>
    public (int total, int pending, int generating, int ready) GetStats()
    {
        var total = activeChunks.Count;
        var pending = activeChunks.Values.Count(c => c.State == TerrainChunkState.Pending);
        var generating = activeChunks.Values.Count(c => c.IsInFlight());
        var ready = activeChunks.Values.Count(c => c.State == TerrainChunkState.Ready);
        return (total, pending, generating, ready);
    }

    private HashSet<int> DetermineVisibleChunks(Vector3 cameraPosition)
    {
        var result = new HashSet<int>();
        var viewDistance = LoadDistance;
        var viewDistanceSq = viewDistance * viewDistance;

        // CRITICAL FIX: Clamp camera position to world bounds
        // Prevents invalid chunk indices when player flies outside world in ghost mode
        var worldSizeInBlocks = VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ;
        var clampedX = Math.Clamp(cameraPosition.X, 0, worldSizeInBlocks - 1);
        var clampedZ = Math.Clamp(cameraPosition.Z, 0, worldSizeInBlocks - 1);

        // Calculate chunk position directly from clamped coordinates
        var cameraChunkX = (int)(clampedX / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)(clampedZ / VoxelHelper.ChunkSideSize);

        // Additional safety clamp to chunk indices
        cameraChunkX = Math.Clamp(cameraChunkX, 0, VoxelHelper.WorldChunksXZ - 1);
        cameraChunkZ = Math.Clamp(cameraChunkZ, 0, VoxelHelper.WorldChunksXZ - 1);

        // Use circular distance check to match unload behavior
        for (var dz = -viewDistance; dz <= viewDistance; dz++)
        {
            for (var dx = -viewDistance; dx <= viewDistance; dx++)
            {
                // Only load chunks within circular distance
                var distanceSq = dx * dx + dz * dz;
                if (distanceSq > viewDistanceSq)
                    continue;

                var chunkX = cameraChunkX + dx;
                var chunkZ = cameraChunkZ + dz;

                if (chunkX >= 0 && chunkX < VoxelHelper.WorldChunksXZ &&
                    chunkZ >= 0 && chunkZ < VoxelHelper.WorldChunksXZ)
                {
                    var chunkIdx = chunkZ * VoxelHelper.WorldChunksXZ + chunkX;
                    result.Add(chunkIdx);
                }
            }
        }

        return result;
    }

    private void QueueNewChunks(HashSet<int> visibleChunks)
    {
        var newChunks = visibleChunks.Except(activeChunks.Keys).ToList();

        if (newChunks.Count > 0)
        {
            // Sort by priority (distance to camera)
            newChunks.Sort((a, b) => CalculatePriority(a).CompareTo(CalculatePriority(b)));

            foreach (var chunkIdx in newChunks)
            {
                // Ensure chunk exists in VoxelWorld so we can populate collision data later
                world.GetOrCreateChunkContainer(chunkIdx);

                var descriptor = new ChunkDescriptor
                {
                    ChunkIndex = chunkIdx,
                    State = TerrainChunkState.Pending,
                    CommandSlot = -1, // Initialize to -1 so we know it's not allocated
                };

                activeChunks[chunkIdx] = descriptor;

                // Determine priority based on distance
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
            //Log.Debug($"ChunkStreamingManager: Queued {newChunks.Count} new chunks ({highPriorityPending.Count} high priority)");
        }

        // Debug logging for stuck chunks
        if (currentFrame % 60 == 0)
        {
            foreach (var chunkIdx in visibleChunks)
            {
                if (activeChunks.TryGetValue(chunkIdx, out var desc))
                {
                    if (desc.State == TerrainChunkState.Generating)
                    {
                        // Only warn if stuck for > 10 seconds (600 frames)
                        if (currentFrame - desc.GenerationStartFrame > 600)
                        {
                            Log.Warn($"Chunk {chunkIdx} is stuck in Generating state! (started at {desc.GenerationStartFrame}, current {currentFrame})");
                        }
                    }
                }
            }
        }

        // Promote existing Pending chunks to High Priority if they are close
        foreach (var chunkIdx in visibleChunks)
        {
            if (activeChunks.TryGetValue(chunkIdx, out var desc) && desc.State == TerrainChunkState.Pending)
            {
                // Check distance
                var dist = CalculatePriority(chunkIdx); // Returns distance squared
                if (dist <= HIGH_PRIORITY_DISTANCE * HIGH_PRIORITY_DISTANCE)
                {
                    // Add to high priority queue (duplicates handled in SubmitPendingBatches)
                    highPriorityPending.Enqueue(chunkIdx);
                }
            }
        }
    }

    private void ReadbackColumnSpans(int[] chunkIndices, int bufferIndex)
    {
        if (chunkIndices.Length == 0) return;

        var sizeToRead = chunkIndices.Length * VoxelHelper.ChunkSideSizeSquare * 68;
        var bufferData = new byte[sizeToRead];

        // Read entire buffer to CPU memory in one go
        // This is much faster than reading from mapped VRAM byte-by-byte
        GL.GetNamedBufferSubData(columnSpansBuffers[bufferIndex], IntPtr.Zero, sizeToRead, bufferData);

        unsafe
        {
            fixed (byte* bytePtr = bufferData)
            {
                var chunksUpdated = 0;

                for (var i = 0; i < chunkIndices.Length; i++)
                {
                    var chunkIdx = chunkIndices[i];
                    var data = new ChunkCollisionData();

                    // Prepare arrays for Chunk.cs
                    var chunkSpansPairs = new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
                    var chunkSpanTypes = new byte[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
                    var chunkSpanCounts = new byte[VoxelHelper.ChunkSideSizeSquare];

                    for (var col = 0; col < 256; col++)
                    {
                        var offset = (i * 256 + col) * 68;

                        var count = *(uint*)(bytePtr + offset);
                        data.SpanCounts[col] = (byte)count;
                        chunkSpanCounts[col] = (byte)count;

                        for (var s = 0; s < count; s++)
                        {
                            var packed = *(uint*)(bytePtr + offset + 4 + s * 4);
                            var startY = (byte)(packed & 0xFF);
                            var endY = (byte)((packed >> 8) & 0xFF);
                            var blockType = (byte)((packed >> 16) & 0xFF);

                            data.Spans[col * 16 + s] = new ColumnSpan
                            {
                                StartY = startY,
                                EndY = endY,
                                BlockType = blockType
                            };

                            // Populate Chunk.cs arrays (exclusive upper bound)
                            chunkSpansPairs[(col * ChunkCollisionData.MaxSpansPerColumn + s) * 2 + 0] = startY;
                            chunkSpansPairs[(col * ChunkCollisionData.MaxSpansPerColumn + s) * 2 + 1] = endY + 1;
                            chunkSpanTypes[col * ChunkCollisionData.MaxSpansPerColumn + s] = blockType;
                        }
                    }

                    CollisionManager.UpdateChunkData(chunkIdx, data);

                    // Update Chunk object
                    var chunk = world[chunkIdx];
                    if (chunk != null)
                    {
                        chunk.ApplyColumnSpansForCollision(chunkSpansPairs, chunkSpanCounts, chunkSpanTypes);
                        chunksUpdated++;
                    }
                    else
                    {
                        Log.Warn($"ChunkStreamingManager: Chunk {chunkIdx} not found in VoxelWorld during readback!");
                    }
                }
                // Log.Debug($"ChunkStreamingManager: Readback complete for {chunksUpdated}/{chunkIndices.Length} chunks.");
            }
        }
    }

    private void SubmitPendingBatches()
    {
        // Don't submit if we have too many in-flight batches (pipeline full)
        // We have 2 buffers, so we can handle at most 2 batches in the entire pipeline
        if (totalBatchesInPipeline >= MAX_IN_FLIGHT_BATCHES)
            return;

        // Check if we have any pending chunks
        if (highPriorityPending.Count == 0 && lowPriorityPending.Count == 0)
            return;

        // Build batch (up to MAX_CHUNKS_PER_BATCH)
        // Prioritize high priority chunks
        var batchIndices = new List<int>();
        var processedIndices = new HashSet<int>(); // Track indices in this batch to avoid duplicates

        while (batchIndices.Count < MAX_CHUNKS_PER_BATCH && highPriorityPending.Count > 0)
        {
            var idx = highPriorityPending.Dequeue();

            // Skip if already processed in this batch (unlikely for high priority, but safe)
            if (processedIndices.Contains(idx)) continue;

            // Check state - only process Pending or Dirty chunks
            // This handles the case where a chunk is in both queues (promoted)
            // or was already processed in a previous batch but still in queue
            if (activeChunks.TryGetValue(idx, out var desc) &&
                (desc.State == TerrainChunkState.Pending || desc.State == TerrainChunkState.Dirty))
            {
                Log.Debug($"Adding high priority chunk {idx} to batch (State={desc.State})");
                batchIndices.Add(idx);
                processedIndices.Add(idx);
            }
        }

        while (batchIndices.Count < MAX_CHUNKS_PER_BATCH && lowPriorityPending.Count > 0)
        {
            var idx = lowPriorityPending.Dequeue();

            // Skip if already processed in this batch
            if (processedIndices.Contains(idx)) continue;

            // Check state - only process Pending or Dirty chunks
            if (activeChunks.TryGetValue(idx, out var desc) &&
                (desc.State == TerrainChunkState.Pending || desc.State == TerrainChunkState.Dirty))
            {
                batchIndices.Add(idx);
                processedIndices.Add(idx);
            }
        }

        if (batchIndices.Count == 0)
            return;

        // Mark chunks as generating (both new and dirty chunks)
        foreach (var idx in batchIndices)
        {
            if (activeChunks.TryGetValue(idx, out var desc))
            {
                // Phase 5.2: If chunk is Dirty, remember its old buffer offset
                // so we can free it after regeneration
                if (desc.State == TerrainChunkState.Dirty && desc.AtlasOffset >= 0)
                {
                    // Keep old offset - we'll free it in PollCompletedBatches
                    // after new mesh is ready
                    Log.Debug($"Chunk {idx} dirty with offset {desc.AtlasOffset}, will regenerate");
                }

                desc.State = TerrainChunkState.Generating;
                desc.GenerationStartFrame = currentFrame;
                activeChunks[idx] = desc;
            }
        }

        // CRITICAL FIX: Actually dispatch GPU generation!
        // Use double buffering for generation buffers
        var bufferIndex = nextBufferIndex;
        nextBufferIndex = (nextBufferIndex + 1) % 2;

        var fence = DispatchGenerationAsync([.. batchIndices], bufferIndex);

        // Create batch submission with fence
        var submission = new BatchSubmission
        {
            ChunkIndices = [.. batchIndices],
            Fence = fence,
            SubmitFrame = currentFrame,
            BufferIndex = bufferIndex
        };

        inFlightBatches.Enqueue(submission);
        totalBatchesInPipeline++;

        Log.Info($"ChunkStreamingManager: Submitted batch of {batchIndices.Count} chunks (frame {currentFrame})");
    }

    private void PollCompletedBatches()
    {
        // 0. Check compaction fence from previous batch (Phase 3 Part 2)
        // We must clean this up to release pipeline slots and allow next batch to proceed
        if (lastCompactionFence != IntPtr.Zero)
        {
            var compactStatus = GL.ClientWaitSync(lastCompactionFence, 0, 0);
            if (compactStatus is WaitSyncStatus.ConditionSatisfied or WaitSyncStatus.AlreadySignaled)
            {
                GL.DeleteSync(lastCompactionFence);
                lastCompactionFence = IntPtr.Zero;
                totalBatchesInPipeline--;
                //Log.Debug($"ChunkStreamingManager: Compaction complete. Pipeline batches: {totalBatchesInPipeline}");
            }
        }

        // 1. Check if we have a scanning batch in progress (Phase 3 Part 1 -> Part 2)
        // We only process one scanning batch at a time to avoid buffer conflicts in Phase 3 shared buffers
        if (scanningBatches.Count > 0)
        {
            var scanBatch = scanningBatches.Peek();
            var status = GL.ClientWaitSync(scanBatch.Fence, 0, 0);
            if (status is WaitSyncStatus.ConditionSatisfied or WaitSyncStatus.AlreadySignaled)
            {
                // Scan complete! Finish it (Part 2).
                GL.DeleteSync(scanBatch.Fence);
                scanningBatches.Dequeue();
                FinishBatch(scanBatch.ChunkIndices, scanBatch.BufferIndex);
            }
            // Else: still scanning, do nothing (don't block)
            return; // Can't start next batch yet because Phase 3 buffers are busy
        }

        // 2. If no scanning batch, check if we have a generated batch ready (Phase 2 -> Phase 3 Part 1)
        if (inFlightBatches.Count > 0)
        {
            // CRITICAL: Ensure previous batch's Compaction (Part 2) is fully complete before starting next batch's Visibility (Part 1)
            // This prevents race conditions on shared buffers (visMask, chunkIndices, etc.)
            if (lastCompactionFence != IntPtr.Zero)
            {
                return; // Previous batch still using shared buffers
            }

            var genBatch = inFlightBatches.Peek();
            // Check fence status without blocking (timeout=0)
            var status = GL.ClientWaitSync(genBatch.Fence, 0, 0);
            if (status is WaitSyncStatus.ConditionSatisfied or WaitSyncStatus.AlreadySignaled)
            {
                // Generation complete! Start Scan (Part 1).

                // Phase 2.5 Readback: Read column spans
                try
                {
                    ReadbackColumnSpans(genBatch.ChunkIndices, genBatch.BufferIndex);
                }
                catch (Exception ex)
                {
                    Log.Error($"ChunkStreamingManager: Failed to readback column spans: {ex.Message}");
                    // Continue to cleanup fence and dequeue, otherwise pipeline stalls
                }

                GL.DeleteSync(genBatch.Fence);
                inFlightBatches.Dequeue();

                Log.Info($"ChunkStreamingManager: Generation complete for {genBatch.ChunkIndices.Length} chunks (latency: {currentFrame - genBatch.SubmitFrame} frames). Starting Phase 3 Scan.");

                if (phase3Buffers != null && terrainRenderer != null && genBatch.ChunkIndices.Length > 0)
                {
                    // Start Phase 3 Part 1 (Vis/Count/Scan)
                    var scanFence = ExecutePhase3_Part1(genBatch.ChunkIndices, genBatch.BufferIndex);

                    scanningBatches.Enqueue(new ScanningBatch
                    {
                        ChunkIndices = genBatch.ChunkIndices,
                        Fence = scanFence,
                        SubmitFrame = genBatch.SubmitFrame,
                        BufferIndex = genBatch.BufferIndex
                    });
                }
                else
                {
                    // Skip empty or uninitialized batches
                    foreach (var chunkIdx in genBatch.ChunkIndices)
                    {
                        if (activeChunks.TryGetValue(chunkIdx, out var desc)) { desc.State = TerrainChunkState.Ready; desc.Fence = IntPtr.Zero; activeChunks[chunkIdx] = desc; }
                    }
                }
            }
        }
    }

    private void FinishBatch(int[] chunkIndices, int bufferIndex)
    {
        if (phase3Buffers != null && terrainRenderer != null && chunkIndices.Length > 0)
        {
            try
            {
                // Execute Phase 3 Part 2 (Read/Allocate/Compact/Build)
                // Returns a fence tracking completion of the GPU work
                var (allocatedVertexOffset, allocatedIndexOffset, counts, baseOffsets, commandSlots, compactFence) = ExecutePhase3_Part2(chunkIndices, bufferIndex);

                // Store fence to block next batch
                lastCompactionFence = compactFence;

                // If fence is invalid (error), release the pipeline slot immediately
                if (lastCompactionFence == IntPtr.Zero)
                {
                    totalBatchesInPipeline--;
                }

                if (counts.Length > 0)
                {
                    // Free old regions for dirty chunks
                    foreach (var chunkIdx in chunkIndices)
                    {
                        if (activeChunks.TryGetValue(chunkIdx, out var oldDesc) && oldDesc.State == TerrainChunkState.Dirty && oldDesc.VisibleVoxelCount > 0)
                        {
                            phase3Buffers.FreeVertexRegion((uint)oldDesc.AtlasOffset, (uint)(oldDesc.VisibleVoxelCount * 4));
                            phase3Buffers.FreeIndexRegion((uint)oldDesc.IndexOffset, (uint)(oldDesc.VisibleVoxelCount * 6));
                            // DO NOT free command slot here - we reused it in ExecutePhase3_Part2!
                            // If we freed it, we would lose the slot we just wrote to.
                        }
                    }
                    // Assign new offsets
                    for (var i = 0; i < chunkIndices.Length; i++)
                    {
                        var chunkIdx = chunkIndices[i];
                        if (!activeChunks.TryGetValue(chunkIdx, out var desc)) continue;
                        desc.AtlasOffset = (int)(allocatedVertexOffset + baseOffsets[i]);
                        uint indexPrefix = 0; // sum previous face counts *6
                        for (var j = 0; j < i; j++) indexPrefix += counts[j] * 6;
                        desc.IndexOffset = (int)(allocatedIndexOffset + indexPrefix);
                        desc.VisibleVoxelCount = (int)counts[i];
                        desc.CommandSlot = (int)commandSlots[i]; // Assign the allocated command slot
                        desc.State = TerrainChunkState.Ready;
                        desc.Fence = IntPtr.Zero;
                        activeChunks[chunkIdx] = desc;
                    }
                    Log.Info($"Phase 5.3: Updated {chunkIndices.Length} chunks (vertex region {allocatedVertexOffset}, index region {allocatedIndexOffset}) buffer ends V={phase3Buffers.CurrentVertexBufferEnd} I={phase3Buffers.CurrentIndexBufferEnd}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Phase 5.3: Failed batch processing: {ex.Message}");
                foreach (var chunkIdx in chunkIndices)
                {
                    if (activeChunks.TryGetValue(chunkIdx, out var desc))
                    {
                        desc.State = TerrainChunkState.Ready; desc.Fence = IntPtr.Zero; activeChunks[chunkIdx] = desc;
                    }
                }
            }
        }

        if (phase3Buffers != null && terrainRenderer != null)
        {
            var totalVerticesInBuffer = phase3Buffers.CurrentVertexBufferEnd;
            var totalIndicesInBuffer = phase3Buffers.CurrentIndexBufferEnd;
            var totalFaces = (uint)activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready && c.VisibleVoxelCount > 0).Sum(c => c.VisibleVoxelCount);
            var readyChunks = activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready);
            terrainRenderer.SetupBuffers(phase3Buffers, totalVerticesInBuffer, totalFaces, readyChunks);
            // drawCount equals last batch size for now; renderer logs 0 commands
            Log.Info($"Phase 5.3 DEBUG: Ready={activeChunks.Values.Count(c => c.State == TerrainChunkState.Ready)} faces={totalFaces} vertices={totalVerticesInBuffer} indices={totalIndicesInBuffer}");
        }
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
    private void UnloadDistantChunks(Vector3 cameraPosition)
    {
        if (activeChunks.Count == 0)
            return;

        var cameraChunkX = (int)(cameraPosition.X / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)(cameraPosition.Z / VoxelHelper.ChunkSideSize);

        var unloadDistanceSq = UNLOAD_DISTANCE_CHUNKS * UNLOAD_DISTANCE_CHUNKS;
        var chunksToUnload = new List<int>();

        // Find chunks beyond unload distance
        foreach (var kvp in activeChunks)
        {
            var chunkIdx = kvp.Key;
            var desc = kvp.Value;

            // Don't unload chunks that are still generating or pending
            if (desc.State is TerrainChunkState.Generating or
                TerrainChunkState.Pending)
                continue;

            var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
            var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

            var dx = chunkX - cameraChunkX;
            var dz = chunkZ - cameraChunkZ;
            var distanceSq = dx * dx + dz * dz;

            if (distanceSq > unloadDistanceSq)
            {
                chunksToUnload.Add(chunkIdx);
            }
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

        // Remove collision data
        CollisionManager.RemoveChunkData(chunkIndex);

        //Log.Debug($"Unloaded chunk {chunkIndex}");
    }

    /// <summary>
    /// Get memory usage statistics (Phase 5)
    /// </summary>
    public (long totalBytes, long voxelBytes, long visibilityBytes, long compactBytes) GetMemoryStats()
    {
        var voxelsPerChunk = VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize;
        var columnsPerChunk = VoxelHelper.ChunkSideSizeSquare;

        long voxelBytes = chunkIndicesBufferCapacity * voxelsPerChunk * sizeof(uint);
        long columnBytes = chunkIndicesBufferCapacity * columnsPerChunk * (sizeof(int) + sizeof(uint) * 4);
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
    public void ApplyBlockEdit(Vector3 worldPosition, BlockType blockType, bool isBreaking)
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
        MarkVoxelEdited(chunkIdx, voxelIdx, blockType, isBreaking);

        // Mark chunk as dirty (needs regeneration)
        MarkChunkDirty(chunkIdx);

        // If edit is on chunk boundary, mark neighbors as dirty too
        if (localX == 0 && chunkX > 0)
        {
            var neighborIdx = (chunkZ) * VoxelHelper.WorldChunksXZ + (chunkX - 1);
            Log.Info($"Block edit on -X boundary, marking neighbor {neighborIdx} dirty");
            MarkChunkDirty(neighborIdx);
        }
        if (localX == VoxelHelper.ChunkSideSize - 1 && chunkX < VoxelHelper.WorldChunksXZ - 1)
        {
            var neighborIdx = (chunkZ) * VoxelHelper.WorldChunksXZ + (chunkX + 1);
            Log.Info($"Block edit on +X boundary, marking neighbor {neighborIdx} dirty");
            MarkChunkDirty(neighborIdx);
        }
        if (localZ == 0 && chunkZ > 0)
        {
            var neighborIdx = (chunkZ - 1) * VoxelHelper.WorldChunksXZ + chunkX;
            Log.Info($"Block edit on -Z boundary, marking neighbor {neighborIdx} dirty");
            MarkChunkDirty(neighborIdx);
        }
        if (localZ == VoxelHelper.ChunkSideSize - 1 && chunkZ < VoxelHelper.WorldChunksXZ - 1)
        {
            var neighborIdx = (chunkZ + 1) * VoxelHelper.WorldChunksXZ + chunkX;
            Log.Info($"Block edit on +Z boundary, marking neighbor {neighborIdx} dirty");
            MarkChunkDirty(neighborIdx);
        }

        Log.Debug($"Block edit at world{worldPosition} → chunk{chunkIdx} local({localX},{localY},{localZ}) voxel{voxelIdx} type={blockType} breaking={isBreaking}");

        // Save edits immediately to prevent data loss
        SaveChunkEdits(chunkIdx);
    }

    /// <summary>
    /// Mark a specific voxel as edited in the GPU edit mask buffer (Phase 5)
    /// This will be read by the generation shader to override generated terrain
    /// </summary>
    private void MarkVoxelEdited(int chunkIdx, int voxelIdx, BlockType blockType, bool isBreaking)
    {
        if (!chunkEdits.TryGetValue(chunkIdx, out var value))
        {
            value = [];
            chunkEdits[chunkIdx] = value;
        }

        value[voxelIdx] = blockType;

        Log.Debug($"Voxel edit: chunk={chunkIdx} voxel={voxelIdx} type={blockType} breaking={isBreaking}");
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

    /// <summary>
    /// Initialize GPU resources for terrain generation.
    /// Pre-allocates buffers for maximum view distance to eliminate progressive resizing.
    /// </summary>
    public void InitializeGpuGeneration(int seed, bool testMode = false, int maxChunks = 0)
    {
        generationSeed = seed;
        generationTestMode = testMode;

        // Pre-allocate for max view distance if not specified
        if (maxChunks == 0)
        {
            maxChunks = CalculateMaxViewChunks();
        }

        // Create SSBOs FIRST (before loading shader)
        if (chunkIndicesBuffers[0] == 0)
        {
            GL.CreateBuffers(2, chunkIndicesBuffers);
            GL.CreateBuffers(2, voxelDataBuffers);
            GL.CreateBuffers(2, columnHeightsBuffers);
            GL.CreateBuffers(2, columnMetaBuffers);
            GL.CreateBuffers(2, columnSpansBuffers);
            GL.CreateBuffers(1, out editBuffer);
            GL.CreateBuffers(1, out worldEditsBuffer);
        }
        if (compactionChunkIndicesBuffer == 0)
        {
            GL.CreateBuffers(1, out compactionChunkIndicesBuffer);
        }
        if (cullingChunkIndicesBuffer == 0)
        {
            GL.CreateBuffers(1, out cullingChunkIndicesBuffer);
        }

        // Allocate storage for configurable max chunks (passed from caller)
        var voxelsPerChunk = VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize;

        // Reallocate buffers if capacity changed
        if (chunkIndicesBufferCapacity != maxChunks)
        {
            // Delete old buffers
            if (chunkIndicesBufferCapacity > 0)
            {
                GL.DeleteBuffers(2, chunkIndicesBuffers);
                GL.DeleteBuffers(2, voxelDataBuffers);
                GL.DeleteBuffers(2, columnHeightsBuffers);
                GL.DeleteBuffers(2, columnMetaBuffers);
                GL.DeleteBuffers(2, columnSpansBuffers);
                GL.DeleteBuffer(compactionChunkIndicesBuffer);
                GL.DeleteBuffer(cullingChunkIndicesBuffer);
                GL.DeleteBuffer(editBuffer);
                GL.DeleteBuffer(worldEditsBuffer);

                GL.CreateBuffers(2, chunkIndicesBuffers);
                GL.CreateBuffers(2, voxelDataBuffers);
                GL.CreateBuffers(2, columnHeightsBuffers);
                GL.CreateBuffers(2, columnMetaBuffers);
                GL.CreateBuffers(2, columnSpansBuffers);
                GL.CreateBuffers(1, out compactionChunkIndicesBuffer);
                GL.CreateBuffers(1, out cullingChunkIndicesBuffer);
                GL.CreateBuffers(1, out editBuffer);
                GL.CreateBuffers(1, out worldEditsBuffer);
            }

            // NOTE: Using int (not uint) to match C# int[] arrays used throughout the codebase
            for (var i = 0; i < 2; i++)
            {
                GL.NamedBufferStorage(chunkIndicesBuffers[i], maxChunks * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                GL.NamedBufferStorage(voxelDataBuffers[i], maxChunks * voxelsPerChunk * sizeof(uint), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                GL.NamedBufferStorage(columnHeightsBuffers[i], maxChunks * VoxelHelper.ChunkSideSizeSquare * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
                GL.NamedBufferStorage(columnMetaBuffers[i], maxChunks * VoxelHelper.ChunkSideSizeSquare * sizeof(uint) * 4, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                // 68 bytes per column (struct ColumnSpans { uint count; uint spans[16]; })
                GL.NamedBufferStorage(columnSpansBuffers[i], maxChunks * VoxelHelper.ChunkSideSizeSquare * 68, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.ClientStorageBit);
            }

            // Allocate edit buffer (max 64k edits per batch should be enough)
            GL.NamedBufferStorage(editBuffer, 65536 * 8, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);

            // Allocate world edits buffer (max 64k edits)
            // struct WorldEdit { int x; int y; int z; uint type; } = 16 bytes
            // + uint count (4 bytes) -> padded to 16 bytes alignment? No, std430.
            // Layout: count (4), padding (12), edits...
            GL.NamedBufferStorage(worldEditsBuffer, 16 + 65536 * 16, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);

            // Allocate culling buffer
            GL.NamedBufferStorage(cullingChunkIndicesBuffer, maxChunks * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);

            chunkIndicesBufferCapacity = maxChunks;
            Log.Info($"Reallocated generation buffers for {maxChunks} chunks (Double Buffered)");
        }

        // Initialize Terrain Params (M1)
        // Load config from disk or save default
        var configPath = Path.Combine(Environment.CurrentDirectory, ConfigFileName);
        if (File.Exists(configPath))
        {
            terrainConfig = TerrainConfig.Load(configPath);
            Log.Info($"Loaded TerrainConfig from {configPath}");
        }
        else
        {
            // terrainConfig.Save(configPath); // Disabled auto-save to prevent overwriting with defaults
            Log.Info($"Saved default TerrainConfig to {configPath}");
        }

        // Setup FileSystemWatcher
        if (configWatcher == null)
        {
            try
            {
                configWatcher = new FileSystemWatcher(Environment.CurrentDirectory, ConfigFileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite
                };
                configWatcher.Changed += (s, e) => pendingConfigReload = true;
                configWatcher.EnableRaisingEvents = true;
                Log.Info("TerrainConfig hot-reload enabled");
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to setup config watcher: {ex.Message}");
            }
        }

        if (terrainParamsSSBO == 0)
        {
            GL.CreateBuffers(1, out terrainParamsSSBO);
            var paramsSize = System.Runtime.InteropServices.Marshal.SizeOf<TerrainConfig.GpuParams>();
            GL.NamedBufferStorage(terrainParamsSSBO, paramsSize, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        }

        // Upload Terrain Params
        UploadTerrainConfig();

        Log.CheckGlError();

        // FORCE shader reload by clearing cache
        var shaderCacheField = typeof(Shader).GetField("shaderCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (shaderCacheField != null)
        {
            var cache = shaderCacheField.GetValue(null) as System.Collections.Generic.Dictionary<string, Shader>;
            cache?.Clear();
            Log.Info("Generation shader cache cleared - will reload from disk");
        }

        // Load compute shader AFTER buffers are created (will reload from disk after cache clear)
        try
        {
            generationShader = new Shader("Shaders/compute-generate.comp", ShaderType.ComputeShader);
            columnSpansShader = new Shader("Shaders/compute-column-spans.comp", ShaderType.ComputeShader);
            applyEditsShader = new Shader("Shaders/compute-apply-edits.comp", ShaderType.ComputeShader);
            Log.Info("Generation shaders compiled successfully");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to compile generation shaders: {ex.Message}");
            throw;
        }

        // Set uniforms using Shader class methods (they have proper error handling)
        generationShader.Use();

        // Check for OpenGL errors after shader use
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Log.Error($"OpenGL error after using shader: {error}");
            throw new InvalidOperationException($"Shader use failed: {error}");
        }

        // Use Shader class methods instead of direct GL calls - they handle type checking
        try
        {
            // generationShader.SetUInt("uSeed", (uint)seed); // Removed - using TerrainParams
            generationShader.SetUInt("uWorldChunksXZ", (uint)VoxelHelper.WorldChunksXZ);
            generationShader.SetInt("uTestMode", testMode ? 1 : 0);
            // Skip uElevOffset and uElevScale - they're not used in shader anymore (hardcoded in height01At)
            Log.Info("Shader uniforms set successfully");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to set shader uniforms: {ex.Message}");
            throw;
        }

        Log.CheckGlError();
        Log.Info($"GPU terrain generation initialized (testMode={testMode})");

        LoadEdits(); // Load edits after initialization
    }

    private void UploadTerrainConfig()
    {
        // Ensure seed is not 0 (which might cause issues or be uninitialized)
        if (generationSeed == 0) generationSeed = 1337;

        // Upload Terrain Params
        var gpuParams = terrainConfig.GetGpuParams();
        gpuParams.uSeed = (uint)generationSeed; // Keep the seed consistent with init

        Log.Info($"Uploading TerrainParams: Seed={gpuParams.uSeed}, ContScale={gpuParams.uContScale}, WarpScale={gpuParams.uWarpScale}");

        GL.NamedBufferSubData(terrainParamsSSBO, IntPtr.Zero, System.Runtime.InteropServices.Marshal.SizeOf<TerrainConfig.GpuParams>(), ref gpuParams);

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
    }

    /// <summary>
    /// Dispatch GPU generation for a batch of chunks (ASYNC with fence)
    /// Phase 2: Returns a fence that signals when generation completes
    /// </summary>
    public IntPtr DispatchGenerationAsync(int[] chunkIndices, int bufferIndex)
    {
        if (generationShader == null || chunkIndices.Length == 0)
            return IntPtr.Zero;

        // Upload chunk indices to the selected buffer
        GL.NamedBufferSubData(chunkIndicesBuffers[bufferIndex], IntPtr.Zero, chunkIndices.Length * sizeof(int), chunkIndices);

        // Bind SSBOs with correct bindings matching shader
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, chunkIndicesBuffers[bufferIndex]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, voxelDataBuffers[bufferIndex]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, columnHeightsBuffers[bufferIndex]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnMetaBuffers[bufferIndex]);

        // Bind Terrain Params (M1/M2) - Binding 10 as per terrain-common.glsl
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, terrainParamsSSBO);

        // Bind Textures
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture1D, heightSplineTexture);
        GL.ActiveTexture(TextureUnit.Texture1); // Changed to 1
        GL.BindTexture(TextureTarget.Texture2D, biomeLutTexture);

        // Set ALL uniforms every dispatch
        generationShader.Use();
        // generationShader.SetInt("uHeightSpline", 0); // Using layout(binding=0)
        // generationShader.SetInt("uBiomeLUT", 1);     // Using layout(binding=1)
        generationShader.SetUInt("uChunkCount", (uint)chunkIndices.Length);
        generationShader.SetUInt("uWorldChunksXZ", (uint)VoxelHelper.WorldChunksXZ);
        // generationShader.SetUInt("uSeed", (uint)generationSeed); // Removed
        generationShader.SetInt("uTestMode", generationTestMode ? 1 : 0);

        // Dispatch: one work-group per chunk, matching layout (16,1,16)
        GL.DispatchCompute(chunkIndices.Length, 1, 1);

        // Memory barrier to ensure writes complete before fence
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Phase 2.6: Apply Edits
        if (applyEditsShader != null && chunkEdits.Count > 0)
        {
            var editsToUpload = new List<uint>(); // voxelIndex, blockType
            var worldEditsToUpload = new List<int>(); // x, y, z, type (as int/uint mixed)
            var voxelsPerChunk = VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize;

            for (var i = 0; i < chunkIndices.Length; i++)
            {
                var chunkIdx = chunkIndices[i];
                if (chunkEdits.TryGetValue(chunkIdx, out var edits))
                {
                    Log.Debug($"Found {edits.Count} edits for chunk {chunkIdx} in batch");
                    foreach (var kvp in edits)
                    {
                        var batchVoxelIdx = (uint)(i * voxelsPerChunk + kvp.Key);
                        editsToUpload.Add(batchVoxelIdx);
                        editsToUpload.Add((uint)kvp.Value);
                        if (editsToUpload.Count <= 20) Log.Info($"Uploading edit: ChunkIdxInBatch={i}, LocalIdx={kvp.Key}, BatchIdx={batchVoxelIdx}, Type={kvp.Value}");
                    }
                }
            }

            // Collect ALL world edits (for visibility shader neighbor correction)
            foreach (var chunkKvp in chunkEdits)
            {
                var chunkIdx = chunkKvp.Key;
                var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);

                foreach (var voxelKvp in chunkKvp.Value)
                {
                    var voxelIdx = voxelKvp.Key;
                    var blockType = voxelKvp.Value;

                    var lx = voxelIdx % VoxelHelper.ChunkSideSize;
                    var lz = (voxelIdx / VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
                    var ly = voxelIdx / VoxelHelper.ChunkSideSizeSquare;

                    var wx = chunkPos.X + lx;
                    var wz = chunkPos.Z + lz;
                    var wy = ly; // Global Y is same as local Y

                    worldEditsToUpload.Add(wx);
                    worldEditsToUpload.Add(wy);
                    worldEditsToUpload.Add(wz);
                    worldEditsToUpload.Add((int)blockType);
                }
            }

            // Upload world edits
            if (worldEditsToUpload.Count > 0)
            {
                Log.Info($"Uploading {worldEditsToUpload.Count / 4} world edits for neighbor correction");
                // Layout: count (4 bytes), padding (12 bytes), edits...
                var header = new int[4] { worldEditsToUpload.Count / 4, 0, 0, 0 };
                GL.NamedBufferSubData(worldEditsBuffer, IntPtr.Zero, 16, header);
                GL.NamedBufferSubData(worldEditsBuffer, 16, worldEditsToUpload.Count * sizeof(int), worldEditsToUpload.ToArray());
            }
            else
            {
                var header = new int[4] { 0, 0, 0, 0 };
                GL.NamedBufferSubData(worldEditsBuffer, IntPtr.Zero, 16, header);
            }

            if (editsToUpload.Count > 0)
            {
                Log.Debug($"Uploading {editsToUpload.Count / 2} edits for batch of {chunkIndices.Length} chunks");
                GL.NamedBufferSubData(editBuffer, IntPtr.Zero, editsToUpload.Count * sizeof(uint), editsToUpload.ToArray());

                // Ensure edit buffer upload is visible
                GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);

                applyEditsShader.Use();
                applyEditsShader.SetUInt("uEditCount", (uint)(editsToUpload.Count / 2));

                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, voxelDataBuffers[bufferIndex]); // Voxel data (binding 1)
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, editBuffer); // Edits (binding 5)

                var groups = (editsToUpload.Count / 2 + 63) / 64;
                GL.DispatchCompute(groups, 1, 1);
                GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            }
        }
        else
        {
            // Clear world edits count if no edits
            var header = new int[4] { 0, 0, 0, 0 };
            GL.NamedBufferSubData(worldEditsBuffer, IntPtr.Zero, 16, header);
        }

        // Phase 2.5: Generate Column Spans
        if (columnSpansShader != null)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, voxelDataBuffers[bufferIndex]); // Input
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, columnSpansBuffers[bufferIndex]); // Output

            columnSpansShader.Use();
            columnSpansShader.SetUInt("uChunkCount", (uint)chunkIndices.Length);

            GL.DispatchCompute(chunkIndices.Length, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);
        }

        // Create fence to track completion (NON-BLOCKING)
        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);

        //Log.Debug($"Dispatched ASYNC GPU generation for {chunkIndices.Length} chunks (fence={fence}, buffer={bufferIndex})");

        return fence;
    }

    /// <summary>
    /// Dispatch GPU generation for a batch of chunks (SYNCHRONOUS - kept for compatibility)
    /// Phase 2: Basic implementation
    /// </summary>
    public void DispatchGeneration(int[] chunkIndices)
    {
        // Use buffer 0 for synchronous calls
        var fence = DispatchGenerationAsync(chunkIndices, 0);
        if (fence != IntPtr.Zero)
        {
            // Wait for completion (blocking)
            GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, ulong.MaxValue);
            GL.DeleteSync(fence);
        }
    }

    /// <summary>
    /// Initialize Phase 3 GPU resources (Visibility & Compaction)
    /// </summary>
    public void InitializePhase3(int maxChunksPerBatch = 64)
    {
        // Dispose old buffers if reinitializing
        phase3Buffers?.Dispose();

        // CRITICAL: Clear active chunks when reinitializing buffers
        // Existing chunks point to offsets in the disposed buffers and are now invalid
        if (activeChunks.Count > 0)
        {
            Log.Warn($"InitializePhase3: Clearing {activeChunks.Count} active chunks due to buffer reinitialization");
            activeChunks.Clear();
        }

        // FORCE shader reload by clearing cache
        // This ensures we get the latest shader code after edits
        var shaderCacheField = typeof(Shader).GetField("shaderCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (shaderCacheField != null)
        {
            var cache = shaderCacheField.GetValue(null) as System.Collections.Generic.Dictionary<string, Shader>;
            cache?.Clear();
            Log.Info("Shader cache cleared - will reload shaders");
        }

        // Load compute shaders (will reload from disk after cache clear)
        visibilityShader = new Shader("Shaders/compute-visibility.comp", ShaderType.ComputeShader);
        countShader = new Shader("Shaders/compute-count.comp", ShaderType.ComputeShader);
        scanShader = new Shader("Shaders/compute-scan.comp", ShaderType.ComputeShader);
        compactShader = new Shader("Shaders/compute-compact.comp", ShaderType.ComputeShader);
        buildIndirectShader = new Shader("Shaders/compute-build-indirect.comp", ShaderType.ComputeShader);

        // Allocate Phase 3 buffers
        phase3Buffers = new Phase3BufferManager();
        phase3Buffers.AllocateBuffers(maxChunksPerBatch);

        // Preallocate indirect draw buffer to max active chunks (load distance + hysteresis)
        var maxActiveChunks = CalculateMaxViewChunks();
        phase3Buffers.ResizeIndirectDrawBuffer((uint)maxActiveChunks);

        Log.Info($"Phase 3 GPU pipeline initialized (max {maxChunksPerBatch} chunks, indirect capacity {maxActiveChunks})");
    }

    /// <summary>
    /// Execute Phase 3 Part 1: Visibility → Count → Prefix Sum
    /// Returns a fence that signals when the scan is complete.
    /// </summary>
    public IntPtr ExecutePhase3_Part1(int[] chunkIndices, int bufferIndex)
    {
        if (phase3Buffers == null || visibilityShader == null || countShader == null)
        {
            Log.Error("Phase 3 not initialized!");
            return IntPtr.Zero;
        }

        var chunkCount = (uint)chunkIndices.Length;

        // Stage 3.1 Visibility
        // Bind the correct voxel data buffer for this batch
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.VOXEL_DATA, voxelDataBuffers[bufferIndex]);
        // Bind chunk indices buffer for neighbor lookup (Phase 5.4 FIX)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.CHUNK_INDICES, chunkIndicesBuffers[bufferIndex]);
        // Bind world edits buffer for neighbor correction (Phase 5.5 FIX)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, worldEditsBuffer);

        // Bind Terrain Params (M1/M2) - Binding 10
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, terrainParamsSSBO);

        // Bind Textures
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture1D, heightSplineTexture);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, biomeLutTexture);

        phase3Buffers.BindBuffersForVisibility();
        visibilityShader.Use();
        // GL.Uniform1(visibilityShader.GetUniformLocation("uHeightSpline"), 0); // Using layout(binding=0)
        // GL.Uniform1(visibilityShader.GetUniformLocation("uBiomeLUT"), 1);     // Using layout(binding=1)
        GL.Uniform1(visibilityShader.GetUniformLocation("uChunkCount"), chunkCount);
        // GL.Uniform1(visibilityShader.GetUniformLocation("uSeed"), (uint)generationSeed); // Removed
        GL.Uniform1(visibilityShader.GetUniformLocation("uWorldChunksXZ"), (uint)VoxelHelper.WorldChunksXZ);
        GL.Uniform1(visibilityShader.GetUniformLocation("uTestMode"), generationTestMode ? 1 : 0);

        GL.DispatchCompute((int)chunkCount, 128, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Stage 3.2 Count
        phase3Buffers.BindBuffersForCount();
        // Bind OpaqueCounts buffer (binding 12)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12, (int)phase3Buffers.OpaqueCountsBuffer);

        countShader.Use();
        GL.Uniform1(countShader.GetUniformLocation("uChunkCount"), chunkCount);
        var countWorkGroups = (chunkCount + 255) / 256;
        GL.DispatchCompute((int)countWorkGroups, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Stage 3.3 Prefix Sum (GPU)
        phase3Buffers.BindBuffersForScan();
        scanShader ??= new Shader("Shaders/compute-scan.comp", ShaderType.ComputeShader);
        scanShader.Use();
        GL.Uniform1(scanShader.GetUniformLocation("uChunkCount"), chunkCount);
        var scanGroups = (chunkCount + 255) / 256;
        GL.DispatchCompute((int)scanGroups, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);

        // Create fence to track completion of Scan
        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        return fence;
    }

    /// <summary>
    /// Execute Phase 3 Part 2: Readback → Allocation → Compaction → Build Indirect
    /// Must be called after Part 1 fence is signaled.
    /// </summary>
    public (uint allocatedVertexOffset, uint allocatedIndexOffset, uint[] counts, uint[] baseOffsets, uint[] commandSlots, IntPtr fence) ExecutePhase3_Part2(int[] chunkIndices, int bufferIndex)
    {
        if (phase3Buffers == null || compactShader == null)
        {
            Log.Error("Phase 3 not initialized!");
            return (0, 0, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), IntPtr.Zero);
        }

        var chunkCount = (uint)chunkIndices.Length;

        // Fetch face counts for each chunk (needed to set VisibleVoxelCount for indirect commands)
        var counts = new uint[chunkCount];
        GL.GetNamedBufferSubData(phase3Buffers.CountBuffer, IntPtr.Zero, (int)(chunkCount * sizeof(uint)), counts);

        // OPTIMIZATION: Calculate offsets and totals on CPU to avoid 2 extra readbacks
        // We only need to read 'counts'. Base offsets and totals are derived from it.
        var baseOffsets = new uint[chunkCount];
        uint totalVertices = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            baseOffsets[i] = totalVertices;
            totalVertices += counts[i] * 4u; // 4 vertices per face
        }
        var totalIndices = (totalVertices / 4u) * 6u; // 6 indices per face

        var allocatedVertexOffset = phase3Buffers.AllocateVertexRegion(totalVertices);
        var allocatedIndexOffset = phase3Buffers.AllocateIndexRegion(totalIndices);

        // Stage 3.4 Compaction
        phase3Buffers.ResetAtomicCounters();

        // Clear counters with SubData instead of ClearNamedBufferData to avoid format errors
        // We only need to clear the first chunkCount entries as the shader uses gl_WorkGroupID.x
        var zeros = new uint[chunkCount];
        GL.NamedBufferSubData(phase3Buffers.PerChunkEmitBuffer, IntPtr.Zero, (int)(chunkCount * sizeof(uint)), zeros);
        GL.NamedBufferSubData(phase3Buffers.WaterEmitBuffer, IntPtr.Zero, (int)(chunkCount * sizeof(uint)), zeros);

        GL.NamedBufferData(compactionChunkIndicesBuffer, chunkIndices.Length * sizeof(int), chunkIndices, BufferUsageHint.DynamicDraw);
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.CHUNK_INDICES, compactionChunkIndicesBuffer);
        // Bind the correct voxel data buffer for compaction
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.VOXEL_DATA, voxelDataBuffers[bufferIndex]);
        // Bind world edits buffer for neighbor lookup
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 13, worldEditsBuffer);

        // Bind Terrain Params (M1/M2) - Binding 10
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, terrainParamsSSBO);

        // Bind Textures
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture1D, heightSplineTexture);
        // uBiomeLUT is unused in M2 compact shader, skipping to avoid warnings
        // GL.ActiveTexture(TextureUnit.Texture1);
        // GL.BindTexture(TextureTarget.Texture2D, biomeLutTexture);

        phase3Buffers.BindBuffersForCompaction();
        // Bind OpaqueCounts buffer (binding 12)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12, (int)phase3Buffers.OpaqueCountsBuffer);
        // Bind WaterEmit buffer (binding 14)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 14, (int)phase3Buffers.WaterEmitBuffer);

        compactShader.Use();
        // GL.Uniform1(compactShader.GetUniformLocation("uHeightSpline"), 0); // Using layout(binding=0)
        // GL.Uniform1(compactShader.GetUniformLocation("uBiomeLUT"), 1);     // Using layout(binding=1)
        GL.Uniform1(compactShader.GetUniformLocation("uChunkCount"), chunkCount);
        GL.Uniform1(compactShader.GetUniformLocation("uWorldChunksXZ"), (uint)VoxelHelper.WorldChunksXZ);
        // GL.Uniform1(compactShader.GetUniformLocation("uSeed"), (uint)generationSeed); // Removed
        GL.Uniform1(compactShader.GetUniformLocation("uTestMode"), generationTestMode ? 1 : 0);
        GL.Uniform1(compactShader.GetUniformLocation("uVertexRegionOffset"), allocatedVertexOffset);
        GL.Uniform1(compactShader.GetUniformLocation("uIndexRegionOffset"), allocatedIndexOffset);
        GL.DispatchCompute((int)chunkCount, 128, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit | MemoryBarrierFlags.ElementArrayBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Stage 3.5 Build indirect commands on GPU
        // PHASE 5.3: Use command slots to write to correct location in indirect buffer
        // Allocate slots for this batch (REUSE existing slots if available to prevent duplicates)
        var commandSlots = new uint[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            var chunkIdx = chunkIndices[i];
            // Check if chunk already has a slot allocated
            commandSlots[i] = activeChunks.TryGetValue(chunkIdx, out var desc) && desc.CommandSlot >= 0
                ? (uint)desc.CommandSlot
                : (uint)phase3Buffers.AllocateCommandSlot();
        }

        // Upload slots to GPU
        if (phase3Buffers.CommandSlotBuffer == 0)
        {
            Log.Error("ExecutePhase3_Part2: CommandSlotBuffer is 0!");
            return (0, 0, [], [], [], IntPtr.Zero);
        }
        GL.NamedBufferSubData((int)phase3Buffers.CommandSlotBuffer, IntPtr.Zero, (int)(chunkCount * sizeof(uint)), commandSlots);

        phase3Buffers.BindBuffersForBuildIndirect();
        // Bind the new command slot buffer
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.COMMAND_SLOTS, (int)phase3Buffers.CommandSlotBuffer);
        // Bind chunk indices (reusing compaction buffer)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.CHUNK_INDICES, compactionChunkIndicesBuffer);
        // Bind OpaqueCounts buffer (binding 12)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12, (int)phase3Buffers.OpaqueCountsBuffer);

        buildIndirectShader ??= new Shader("Shaders/compute-build-indirect.comp", ShaderType.ComputeShader);
        buildIndirectShader.Use();
        GL.Uniform1(buildIndirectShader.GetUniformLocation("uChunkCount"), chunkCount);
        GL.Uniform1(buildIndirectShader.GetUniformLocation("uVertexRegionOffset"), allocatedVertexOffset);
        GL.Uniform1(buildIndirectShader.GetUniformLocation("uIndexRegionOffset"), allocatedIndexOffset);
        var groups = (chunkCount + 255) / 256;
        GL.DispatchCompute((int)groups, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Create fence to track completion of Compaction/Build
        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);

        Log.Info($"Phase 3 complete: {chunkCount} chunks (faces total approx={counts.Aggregate(0u, (a, c) => a + c)}) region offsets V={allocatedVertexOffset} I={allocatedIndexOffset}");

        // Return slots so we can update descriptors
        return (allocatedVertexOffset, allocatedIndexOffset, counts, baseOffsets, commandSlots, fence);
    }

    /// <summary>
    /// Initialize Phase 4 GPU rendering
    /// </summary>
    public void InitializePhase4()
    {
        terrainRenderer = new VoxelTerrainRenderer();
        Log.Info("Phase 4 rendering initialized");
    }

    /// <summary>
    /// Initialize frustum culling system (Phase 4.5).
    /// Pre-allocates buffers for maximum view distance to eliminate progressive resizing.
    /// </summary>
    public void InitializeFrustumCulling(int maxChunks = 0)
    {
        // Pre-allocate for max view distance if not specified
        if (maxChunks == 0)
        {
            maxChunks = CalculateMaxViewChunks();
        }

        // Load frustum culling shader
        frustumShader ??= new Shader("Shaders/compute-frustum.comp", ShaderType.ComputeShader);

        // Create or recreate frustum UBO (6 planes * vec4 = 96 bytes)
        if (frustumUBO == 0)
        {
            GL.CreateBuffers(1, out frustumUBO);
            GL.NamedBufferStorage(frustumUBO, 6 * 4 * sizeof(float), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, frustumUBO, -1, "frustum_ubo");
        }

        // Reallocate visibility flags SSBO if capacity changed
        if (visibilityFlagsCapacity != maxChunks)
        {
            if (visibilityFlagsSSBO != 0)
            {
                GL.DeleteBuffer(visibilityFlagsSSBO);
                GL.DeleteBuffer(cullingCommandSlotsBuffer);
            }

            GL.CreateBuffers(1, out visibilityFlagsSSBO);
            GL.NamedBufferStorage(visibilityFlagsSSBO, maxChunks * sizeof(int), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, visibilityFlagsSSBO, -1, "visibility_flags_ssbo");

            GL.CreateBuffers(1, out cullingCommandSlotsBuffer);
            GL.NamedBufferStorage(cullingCommandSlotsBuffer, maxChunks * sizeof(int), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, cullingCommandSlotsBuffer, -1, "culling_command_slots_ssbo");

            lastVisibilityFlags = new int[maxChunks];
            visibilityFlagsCapacity = maxChunks;

            Log.Info($"Reallocated frustum culling buffers for {maxChunks} chunks");
        }

        Log.CheckGlError();
        Log.Info($"Frustum culling initialized (capacity: {maxChunks} chunks)");
    }

    /// <summary>
    /// Execute GPU frustum culling for given chunks
    /// Returns array of visibility flags (1 = visible, 0 = culled)
    /// 
    /// PERFORMANCE FIX: The visibility flags are now consumed directly on GPU during rendering.
    /// This method no longer performs CPU readback to avoid pipeline stalls.
    /// The visibility_flags_ssbo buffer remains available on GPU for the renderer to use.
    /// </summary>
    public int[] ExecuteFrustumCulling(OpenRender.Core.Rendering.ICamera camera, int[] chunkIndices)
    {
        if (frustumShader == null || chunkIndices.Length == 0 || phase3Buffers == null)
        {
            // Return all visible if culling not initialized
            return [.. Enumerable.Repeat(1, chunkIndices.Length)];
        }

        // Buffers are pre-allocated to max view distance, so this should never happen
        if (chunkIndices.Length > chunkIndicesBufferCapacity)
        {
            Log.Error($"Frustum culling buffer overflow! Requested {chunkIndices.Length} chunks, capacity {chunkIndicesBufferCapacity}.");
            return [.. Enumerable.Repeat(1, chunkIndices.Length)];
        }

        if (chunkIndices.Length > visibilityFlagsCapacity)
        {
            Log.Error($"Visibility flags buffer overflow! Requested {chunkIndices.Length} chunks, capacity {visibilityFlagsCapacity}.");
            return [.. Enumerable.Repeat(1, chunkIndices.Length)];
        }

        // Update frustum planes UBO
        UpdateFrustumUBO(camera);

        // Prepare command slots
        var commandSlots = new int[chunkIndices.Length];
        for (var i = 0; i < chunkIndices.Length; i++)
        {
            commandSlots[i] = activeChunks.TryGetValue(chunkIndices[i], out var desc) ? desc.CommandSlot : -1;
        }

        // Upload chunk indices and command slots
        GL.NamedBufferSubData(cullingChunkIndicesBuffer, IntPtr.Zero,
            chunkIndices.Length * sizeof(int), chunkIndices);
        GL.NamedBufferSubData(cullingCommandSlotsBuffer, IntPtr.Zero,
            commandSlots.Length * sizeof(int), commandSlots);

        // Bind buffers
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 0, frustumUBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, cullingChunkIndicesBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, visibilityFlagsSSBO); // Still bound for debug/readback if needed
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, cullingCommandSlotsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, (int)phase3Buffers.IndirectDrawBuffer);

        // Set uniforms
        frustumShader.Use();
        GL.Uniform1(frustumShader.GetUniformLocation("chunkCount"), chunkIndices.Length);
        GL.Uniform1(frustumShader.GetUniformLocation("chunkSideSize"), VoxelHelper.ChunkSideSize);
        GL.Uniform1(frustumShader.GetUniformLocation("chunkYSize"), VoxelHelper.ChunkYSize);
        GL.Uniform1(frustumShader.GetUniformLocation("worldChunksXZ"), VoxelHelper.WorldChunksXZ);

        // Dispatch: 64 threads per workgroup, ceil(chunkCount / 64) workgroups
        var workGroups = (chunkIndices.Length + 63) / 64;
        GL.DispatchCompute(workGroups, 1, 1);

        // Wait for completion
        GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

        // PERFORMANCE FIX: Skip CPU readback - visibility flags are now consumed on GPU
        // The visibility_flags_ssbo remains bound and available for the renderer to use
        // during the draw call (e.g., in the vertex shader via gl_InstanceID lookup)

        // For now, return all visible to maintain API compatibility
        // The actual culling happens on GPU during rendering
        VisibleChunkCount = chunkIndices.Length; // Conservative estimate
        CulledChunkCount = 0;

        return [.. Enumerable.Repeat(1, chunkIndices.Length)];
    }

    /// <summary>
    /// Update frustum planes UBO from camera
    /// </summary>
    private void UpdateFrustumUBO(OpenRender.Core.Rendering.ICamera camera)
    {
        // Extract frustum planes from camera
        var frustum = camera.Frustum;
        var planes = frustum.Planes;

        // Upload to UBO (6 planes * vec4)
        var planeData = new float[24]; // 6 planes * 4 floats
        for (var i = 0; i < 6; i++)
        {
            planeData[i * 4 + 0] = planes[i].X;
            planeData[i * 4 + 1] = planes[i].Y;
            planeData[i * 4 + 2] = planes[i].Z;
            planeData[i * 4 + 3] = planes[i].W;
        }

        GL.NamedBufferSubData(frustumUBO, IntPtr.Zero, planeData.Length * sizeof(float), planeData);
    }

    /// <summary>
    /// Get the terrain renderer for rendering integration.
    /// Returns null if Phase 4 not initialized.
    /// </summary>
    public VoxelTerrainRenderer? GetTerrainRenderer() => terrainRenderer;

    /// <summary>
    /// Execute complete pipeline: Generation → Visibility → Compaction → Setup Rendering
    /// Phase 5.1: Uses face count instead of index count.
    /// This is the end-to-end Phase 2-4 integration.
    /// Also registers chunks as ready in the streaming manager.
    /// </summary>
    public void ExecuteCompletePipeline(int[] chunkIndices)
    {
        if (chunkIndices.Length == 0) return;

        // CRITICAL FIX: Process in batches to avoid overflowing Phase 3 buffers
        // Phase 3 buffers are allocated with size MAX_CHUNKS_PER_BATCH (default 64)
        // Initial load might request hundreds of chunks, causing buffer overflow and corruption
        // Reduced batch size to 32 to be safe and avoid TDR
        var batchSize = VoxelHelper.INITIAL_LOAD_BATCH_SIZE;
        Log.Info($"ExecuteCompletePipeline: Processing {chunkIndices.Length} chunks in batches of {batchSize}");

        for (var i = 0; i < chunkIndices.Length; i += batchSize)
        {
            var count = Math.Min(batchSize, chunkIndices.Length - i);
            var batchIndices = new int[count];
            Array.Copy(chunkIndices, i, batchIndices, 0, count);

            Log.Debug($"  Batch {i / batchSize}: {count} chunks");

            DispatchGeneration(batchIndices);

            // Synchronous execution for initial load (blocking is acceptable here)
            // Use buffer 0 for synchronous execution
            var fence = ExecutePhase3_Part1(batchIndices, 0);
            var waitResult = GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, 1000000000); // 1s timeout
            if (waitResult == WaitSyncStatus.TimeoutExpired) Log.Warn("ExecutePhase3_Part1 timeout");
            GL.DeleteSync(fence);

            var (allocatedVtx, allocatedIdx, counts, baseOffsets, commandSlots, compactFence) = ExecutePhase3_Part2(batchIndices, 0);

            // Wait for compaction to finish (since this is synchronous pipeline)
            waitResult = GL.ClientWaitSync(compactFence, ClientWaitSyncFlags.SyncFlushCommandsBit, 1000000000); // 1s timeout
            if (waitResult == WaitSyncStatus.TimeoutExpired) Log.Warn("ExecutePhase3_Part2 timeout");
            GL.DeleteSync(compactFence);



            // Assign descriptor offsets similar to PollCompletedBatches
            if (phase3Buffers != null)
            {
                for (var j = 0; j < batchIndices.Length; j++)
                {
                    var chunkIdx = batchIndices[j];
                    if (activeChunks.TryGetValue(chunkIdx, out var desc))
                    {
                        desc.AtlasOffset = (int)(allocatedVtx + baseOffsets[j]);
                        uint indexPrefix = 0; for (var k = 0; k < j; k++) indexPrefix += counts[k] * 6;
                        desc.IndexOffset = (int)(allocatedIdx + indexPrefix);
                        desc.VisibleVoxelCount = (int)counts[j];
                        desc.CommandSlot = (int)commandSlots[j];
                        desc.State = TerrainChunkState.Ready;
                        activeChunks[chunkIdx] = desc;
                    }
                    else
                    {
                        activeChunks[chunkIdx] = new ChunkDescriptor
                        {
                            ChunkIndex = chunkIdx,
                            AtlasOffset = (int)(allocatedVtx + baseOffsets[j]),
                            IndexOffset = (int)(allocatedIdx + counts.Take(j).Aggregate(0u, (a, c) => a + c * 6)),
                            VisibleVoxelCount = (int)counts[j],
                            CommandSlot = (int)commandSlots[j],
                            State = TerrainChunkState.Ready
                        };
                    }
                }
            }
        }

        var totalFaces = activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready).Sum(c => (long)c.VisibleVoxelCount);
        var totalVertices = phase3Buffers?.CurrentVertexBufferEnd ?? 0;

        if (terrainRenderer != null && phase3Buffers != null && totalVertices > 0)
        {
            terrainRenderer.SetupBuffers(phase3Buffers, totalVertices, (uint)totalFaces, activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready));
            Log.Info($"Complete pipeline executed: {chunkIndices.Length} chunks processed in batches");
        }
    }

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
            writer.Write((byte)kvp.Value);
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
        foreach (var file in files)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out var x) && int.TryParse(parts[2], out var z))
                {
                    if (x >= 0 && x < VoxelHelper.WorldChunksXZ && z >= 0 && z < VoxelHelper.WorldChunksXZ)
                    {
                        var chunkIdx = z * VoxelHelper.WorldChunksXZ + x;

                        using var stream = File.OpenRead(file);
                        using var reader = new BinaryReader(stream);

                        var count = reader.ReadInt32();
                        var edits = new Dictionary<int, BlockType>(count);
                        for (var k = 0; k < count; k++)
                        {
                            var voxelIdx = reader.ReadInt32();
                            var type = (BlockType)reader.ReadByte();
                            edits[voxelIdx] = type;
                        }
                        chunkEdits[chunkIdx] = edits;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load edits from {file}: {ex.Message}");
            }
        }
        Log.Info($"Loaded edits for {chunkEdits.Count} chunks from {dirPath}");
    }

    public void Dispose()
    {
        SaveEdits(); // Save on dispose

        // Cleanup all fences
        foreach (var batch in inFlightBatches)
        {
            if (batch.Fence != IntPtr.Zero)
            {
                try { GL.DeleteSync(batch.Fence); } catch { }
            }
        }

        foreach (var kvp in activeChunks)
        {
            if (kvp.Value.Fence != IntPtr.Zero)
            {
                try { GL.DeleteSync(kvp.Value.Fence); } catch { }
            }
        }

        // Cleanup frustum culling resources
        if (frustumUBO != 0) GL.DeleteBuffer(frustumUBO);
        if (visibilityFlagsSSBO != 0) GL.DeleteBuffer(visibilityFlagsSSBO);
        if (cullingCommandSlotsBuffer != 0) GL.DeleteBuffer(cullingCommandSlotsBuffer);

        // Cleanup Terrain Params (M1)
        if (terrainParamsSSBO != 0) GL.DeleteBuffer(terrainParamsSSBO);
        if (heightSplineTexture != 0) GL.DeleteTexture(heightSplineTexture);
        if (biomeLutTexture != 0) GL.DeleteTexture(biomeLutTexture);

        // terrainRenderer is now a SceneNode and will be cleaned up by the scene graph
        phase3Buffers?.Dispose();
        bufferAllocator?.Dispose();

        Log.Info("ChunkStreamingManager: Disposed");
    }

    /// <summary>
    /// Batch submission tracking
    /// </summary>
    private struct BatchSubmission
    {
        public int[] ChunkIndices;
        public IntPtr Fence;
        public long SubmitFrame;
        public int BufferIndex; // Phase 5.3: Which double-buffer set is used
    }

    /// <summary>
    /// Scanning batch tracking (Phase 3 Part 1)
    /// </summary>
    private struct ScanningBatch
    {
        public int[] ChunkIndices;
        public IntPtr Fence;
        public long SubmitFrame;
        public int BufferIndex; // Phase 5.3: Which double-buffer set is used
    }
}
