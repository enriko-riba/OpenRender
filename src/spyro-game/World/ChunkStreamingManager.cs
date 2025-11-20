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

    private readonly GpuBufferAllocator bufferAllocator;
    private readonly Dictionary<int, ChunkDescriptor> activeChunks;
    private readonly Queue<int> pendingGeneration;
    private readonly Queue<BatchSubmission> inFlightBatches;
    private readonly Queue<ScanningBatch> scanningBatches; // Phase 5.3: Batches waiting for Scan/Count completion
    private IntPtr lastCompactionFence = IntPtr.Zero;      // Phase 5.3: Fence to serialize Phase 3 access to shared buffers
    private int totalBatchesInPipeline = 0;

    private long currentFrame;
    private const int MAX_CHUNKS_PER_BATCH = VoxelHelper.DEFAULT_MAX_CHUNKS_PER_BATCH;
    private const int MAX_IN_FLIGHT_BATCHES = 2;

    // Configurable load distance (defaults to VoxelHelper.MaxDistanceInChunks)
    public int LoadDistance { get; set; } = VoxelHelper.MaxDistanceInChunks;

    // Phase 2: GPU generation shader
    private Shader? generationShader;
    private int generationSeed;
    private bool generationTestMode;
    private uint[] chunkIndicesBuffers = new uint[2];
    private uint[] voxelDataBuffers = new uint[2];
    private uint[] columnHeightsBuffers = new uint[2];
    private uint[] columnMetaBuffers = new uint[2];
    private IntPtr[] bufferFences = new IntPtr[2]; // Fences to track when buffers are free (Phase 3 complete)
    private int nextBufferIndex = 0; // Phase 5.3: Toggle for double buffering

    // Phase 3: dedicated chunk indices buffer to avoid races with Phase 2 uploads
    private uint compactionChunkIndicesBuffer;

    // Phase 3: Visibility & Compaction pipeline
    private Shader? visibilityShader;
    private Shader? countShader;
    private Shader? compactShader;
    private Phase3BufferManager? phase3Buffers;

    // Phase 4: Rendering
    private VoxelTerrainRenderer? terrainRenderer;

    // Phase 4.5: Frustum Culling
    private Shader? frustumShader;
    private uint frustumUBO;
    private uint visibilityFlagsSSBO;
    private uint cullingChunkIndicesBuffer; // Dedicated buffer for culling to avoid conflicts with generation
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
        bufferAllocator = new GpuBufferAllocator();
        activeChunks = new Dictionary<int, ChunkDescriptor>();
        pendingGeneration = new Queue<int>();
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
    /*
    public void RequestChunks(int[] chunkIndices)
    {
        if (chunkIndices == null || chunkIndices.Length == 0)
            return;

        foreach (var chunkIdx in chunkIndices)
        {
            if (!activeChunks.ContainsKey(chunkIdx))
            {
                // Create descriptor for new chunk
                var descriptor = new ChunkDescriptor
                {
                    ChunkIndex = chunkIdx,
                    State = TerrainChunkState.Pending,
                    LastAccessFrame = currentFrame,
                    Priority = CalculatePriority(chunkIdx)
                };

                activeChunks[chunkIdx] = descriptor;
                pendingGeneration.Enqueue(chunkIdx);
            }
            else
            {
                // Update access time for existing chunk
                var desc = activeChunks[chunkIdx];
                desc.LastAccessFrame = currentFrame;
                activeChunks[chunkIdx] = desc;
            }
        }

        Log.Debug($"ChunkStreamingManager: Requested {chunkIndices.Length} chunks, {pendingGeneration.Count} pending");
    }

    /// <summary>
    /// Get chunk descriptor by world index
    /// </summary>
    public bool TryGetChunk(int chunkIndex, out ChunkDescriptor descriptor)
    {
        return activeChunks.TryGetValue(chunkIndex, out descriptor);
    }
    */

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
        if (newChunks.Count == 0)
            return;

        // Sort by priority (distance to camera)
        newChunks.Sort((a, b) => CalculatePriority(a).CompareTo(CalculatePriority(b)));

        foreach (var chunkIdx in newChunks)
        {
            var descriptor = new ChunkDescriptor
            {
                ChunkIndex = chunkIdx,
                State = TerrainChunkState.Pending,
                LastAccessFrame = currentFrame,
                Priority = CalculatePriority(chunkIdx)
            };

            activeChunks[chunkIdx] = descriptor;
            pendingGeneration.Enqueue(chunkIdx);
        }

        Log.Debug($"ChunkStreamingManager: Queued {newChunks.Count} new chunks");
    }

    private void SubmitPendingBatches()
    {
        // Don't submit if we have too many in-flight batches (pipeline full)
        // We have 2 buffers, so we can handle at most 2 batches in the entire pipeline
        if (totalBatchesInPipeline >= MAX_IN_FLIGHT_BATCHES)
            return;

        // CRITICAL FIX: Submit batches even if small, don't wait for MAX_CHUNKS_PER_BATCH
        // This fixes the bug where chunks < 64 never get generated
        if (pendingGeneration.Count == 0)
            return;

        // Build batch (up to MAX_CHUNKS_PER_BATCH, but submit even if smaller)
        var batchIndices = new List<int>();
        var batchSize = Math.Min(MAX_CHUNKS_PER_BATCH, pendingGeneration.Count);
        
        for (int i = 0; i < batchSize; i++)
        {
            if (pendingGeneration.Count > 0)
            {
                batchIndices.Add(pendingGeneration.Dequeue());
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
                activeChunks[idx] = desc;
            }
        }

        // CRITICAL FIX: Actually dispatch GPU generation!
        // Use double buffering for generation buffers
        int bufferIndex = nextBufferIndex;
        nextBufferIndex = (nextBufferIndex + 1) % 2;
        
        var fence = DispatchGenerationAsync(batchIndices.ToArray(), bufferIndex);

        // Create batch submission with fence
        var submission = new BatchSubmission
        {
            ChunkIndices = batchIndices.ToArray(),
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
                FinishBatch(scanBatch.ChunkIndices, scanBatch.SubmitFrame, scanBatch.BufferIndex);
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
                var compactStatus = GL.ClientWaitSync(lastCompactionFence, 0, 0);
                if (compactStatus == WaitSyncStatus.TimeoutExpired)
                {
                    return; // Previous batch still using shared buffers
                }
                // Previous batch done
                GL.DeleteSync(lastCompactionFence);
                lastCompactionFence = IntPtr.Zero;
                totalBatchesInPipeline--;
            }

            var genBatch = inFlightBatches.Peek();
            // Check fence status without blocking (timeout=0)
            var status = GL.ClientWaitSync(genBatch.Fence, 0, 0);
            if (status is WaitSyncStatus.ConditionSatisfied or WaitSyncStatus.AlreadySignaled)
            {
                // Generation complete! Start Scan (Part 1).
                GL.DeleteSync(genBatch.Fence);
                inFlightBatches.Dequeue();
                
                Log.Info($"ChunkStreamingManager: Generation complete for {genBatch.ChunkIndices.Length} chunks (latency: {currentFrame - genBatch.SubmitFrame} frames). Starting Phase 3 Scan.");

                if (phase3Buffers != null && terrainRenderer != null && genBatch.ChunkIndices.Length > 0)
                {
                    // Start Phase 3 Part 1 (Vis/Count/Scan)
                    var scanFence = ExecutePhase3_Part1(genBatch.ChunkIndices, genBatch.BufferIndex);
                    
                    scanningBatches.Enqueue(new ScanningBatch { 
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

    private void FinishBatch(int[] chunkIndices, long submitFrame, int bufferIndex)
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
                            // Also free old command slot if it exists
                            if (oldDesc.CommandSlot >= 0)
                            {
                                phase3Buffers.FreeCommandSlot(oldDesc.CommandSlot);
                            }
                        }
                    }
                    // Assign new offsets
                    for (int i = 0; i < chunkIndices.Length; i++)
                    {
                        var chunkIdx = chunkIndices[i];
                        if (!activeChunks.TryGetValue(chunkIdx, out var desc)) continue;
                        desc.AtlasOffset = (int)(allocatedVertexOffset + baseOffsets[i]);
                        uint indexPrefix = 0; // sum previous face counts *6
                        for (int j = 0; j < i; j++) indexPrefix += counts[j] * 6;
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
            var totalFaces = (uint)activeChunks.Values.Where(c=>c.State==TerrainChunkState.Ready && c.VisibleVoxelCount>0).Sum(c=>c.VisibleVoxelCount);
            var readyChunks = activeChunks.Values.Where(c=>c.State==TerrainChunkState.Ready);
            terrainRenderer.SetupBuffers(phase3Buffers, totalVerticesInBuffer, totalFaces, readyChunks);
            // drawCount equals last batch size for now; renderer logs 0 commands
            Log.Info($"Phase 5.3 DEBUG: Ready={activeChunks.Values.Count(c=>c.State==TerrainChunkState.Ready)} faces={totalFaces} vertices={totalVerticesInBuffer} indices={totalIndicesInBuffer}");
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
            if (desc.State == TerrainChunkState.Generating || 
                desc.State == TerrainChunkState.Pending)
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
        
        Log.Debug($"Unloaded chunk {chunkIndex}");
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
        long visibilityBytes = phase3Buffers?.GetAllocatedBytes() ?? 0;
        long compactBytes = terrainRenderer?.GetAllocatedBytes() ?? 0;
        
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

        // Calculate voxel index within chunk
        var voxelIdx = localZ * VoxelHelper.ChunkSideSizeSquare + 
                       localY * VoxelHelper.ChunkSideSize + 
                       localX;
        
        // Mark voxel as edited in edit mask
        MarkVoxelEdited(chunkIdx, voxelIdx, blockType, isBreaking);
        
        // Mark chunk as dirty (needs regeneration)
        MarkChunkDirty(chunkIdx);
        
        // If edit is on chunk boundary, mark neighbors as dirty too
        if (localX == 0 && chunkX > 0)
            MarkChunkDirty((chunkZ) * VoxelHelper.WorldChunksXZ + (chunkX - 1));
        if (localX == VoxelHelper.ChunkSideSize - 1 && chunkX < VoxelHelper.WorldChunksXZ - 1)
            MarkChunkDirty((chunkZ) * VoxelHelper.WorldChunksXZ + (chunkX + 1));
        if (localZ == 0 && chunkZ > 0)
            MarkChunkDirty((chunkZ - 1) * VoxelHelper.WorldChunksXZ + chunkX);
        if (localZ == VoxelHelper.ChunkSideSize - 1 && chunkZ < VoxelHelper.WorldChunksXZ - 1)
            MarkChunkDirty((chunkZ + 1) * VoxelHelper.WorldChunksXZ + chunkX);
        
        Log.Debug($"Block edit at world{worldPosition} → chunk{chunkIdx} local({localX},{localY},{localZ}) voxel{voxelIdx} type={blockType} breaking={isBreaking}");
    }

    /// <summary>
    /// Mark a specific voxel as edited in the GPU edit mask buffer (Phase 5)
    /// This will be read by the generation shader to override generated terrain
    /// </summary>
    private void MarkVoxelEdited(int chunkIdx, int voxelIdx, BlockType blockType, bool isBreaking)
    {
        // TODO Phase 5.2: Upload edit data to GPU editMask3D buffer
        // For now, we just mark the chunk as dirty and rely on full regeneration
        // Future: Update editMask buffer directly and use incremental updates
        
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
            pendingGeneration.Enqueue(chunkIdx);
            
            Log.Debug($"Marked chunk {chunkIdx} as dirty (was Ready, now Dirty for incremental update)");
        }
        else if (desc.State == TerrainChunkState.Dirty)
        {
            // Already dirty, no need to re-queue
            Log.Debug($"Chunk {chunkIdx} already marked as dirty");
        }
    }

    /// <summary>
    /// Get active chunk count for UI/debugging (Phase 5)
    /// </summary>
    public int GetActiveChunkCount() => activeChunks.Count;

    /// <summary>
    /// Get pending generation count for UI/debugging (Phase 5)
    /// </summary>
    public int GetPendingGenerationCount() => pendingGeneration.Count;

    /// <summary>
    /// Initialize GPU resources for terrain generation.
    /// Pre-allocates buffers for maximum view distance to eliminate progressive resizing.
    /// </summary>
    public void InitializeGpuGeneration(int seed, float elevOffset, float elevScale, bool testMode = false, int maxChunks = 0)
    {
        this.generationSeed = seed;
        this.generationTestMode = testMode;

        // Pre-allocate for max view distance if not specified
        if (maxChunks == 0)
        {
            maxChunks = CalculateMaxViewChunks();
        }
        
        // Validate shader constants match VoxelHelper
        VoxelHelper.ValidateShaderConstants();
        
        // Create SSBOs FIRST (before loading shader)
        if (chunkIndicesBuffers[0] == 0)
        {
            GL.CreateBuffers(2, chunkIndicesBuffers);
            GL.CreateBuffers(2, voxelDataBuffers);
            GL.CreateBuffers(2, columnHeightsBuffers);
            GL.CreateBuffers(2, columnMetaBuffers);
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
                GL.DeleteBuffer(compactionChunkIndicesBuffer);
                GL.DeleteBuffer(cullingChunkIndicesBuffer);
                
                GL.CreateBuffers(2, chunkIndicesBuffers);
                GL.CreateBuffers(2, voxelDataBuffers);
                GL.CreateBuffers(2, columnHeightsBuffers);
                GL.CreateBuffers(2, columnMetaBuffers);
                GL.CreateBuffers(1, out compactionChunkIndicesBuffer);
                GL.CreateBuffers(1, out cullingChunkIndicesBuffer);
            }

            // NOTE: Using int (not uint) to match C# int[] arrays used throughout the codebase
            for(int i=0; i<2; i++)
            {
                GL.NamedBufferStorage(chunkIndicesBuffers[i], maxChunks * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                GL.NamedBufferStorage(voxelDataBuffers[i], maxChunks * voxelsPerChunk * sizeof(uint), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                GL.NamedBufferStorage(columnHeightsBuffers[i], maxChunks * VoxelHelper.ChunkSideSizeSquare * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
                GL.NamedBufferStorage(columnMetaBuffers[i], maxChunks * VoxelHelper.ChunkSideSizeSquare * sizeof(uint) * 4, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            }
            
            // Allocate culling buffer
            GL.NamedBufferStorage(cullingChunkIndicesBuffer, maxChunks * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);

            chunkIndicesBufferCapacity = maxChunks;
            Log.Info($"Reallocated generation buffers for {maxChunks} chunks (Double Buffered)");
        }

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
            Log.Info("Generation shader compiled successfully");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to compile generation shader: {ex.Message}");
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
            generationShader.SetUInt("uSeed", (uint)seed);
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
    }

    /// <summary>
    /// Dispatch GPU generation for a batch of chunks (ASYNC with fence)
    /// Phase 2: Returns a fence that signals when generation completes
    /// </summary>
    public IntPtr DispatchGenerationAsync(int[] chunkIndices, int bufferIndex)
    {
        if (generationShader == null || chunkIndices.Length == 0)
            return IntPtr.Zero;

        var voxelsPerChunk = VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize;
        var totalVoxels = chunkIndices.Length * voxelsPerChunk;

        // Upload chunk indices to the selected buffer
        GL.NamedBufferSubData(chunkIndicesBuffers[bufferIndex], IntPtr.Zero, chunkIndices.Length * sizeof(int), chunkIndices);

        // Bind SSBOs with correct bindings matching shader
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, chunkIndicesBuffers[bufferIndex]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, voxelDataBuffers[bufferIndex]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, columnHeightsBuffers[bufferIndex]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnMetaBuffers[bufferIndex]);

        // Set ALL uniforms every dispatch
        generationShader.Use();
        generationShader.SetUInt("uChunkCount", (uint)chunkIndices.Length);
        generationShader.SetUInt("uWorldChunksXZ", (uint)VoxelHelper.WorldChunksXZ);
        generationShader.SetUInt("uSeed", (uint)generationSeed);
        generationShader.SetInt("uTestMode", generationTestMode ? 1 : 0);

        // Dispatch: one work-group per chunk, matching layout (16,1,16)
        GL.DispatchCompute(chunkIndices.Length, 1, 1);

        // Memory barrier to ensure writes complete before fence
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Create fence to track completion (NON-BLOCKING)
        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);

        Log.Debug($"Dispatched ASYNC GPU generation for {chunkIndices.Length} chunks (fence={fence}, buffer={bufferIndex})");

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
        compactShader = new Shader("Shaders/compute-compact.comp", ShaderType.ComputeShader);

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
        phase3Buffers.BindBuffersForVisibility();
        visibilityShader.Use();
        GL.Uniform1(visibilityShader.GetUniformLocation("uChunkCount"), chunkCount);
        GL.DispatchCompute((int)chunkCount, 128, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Stage 3.2 Count
        phase3Buffers.BindBuffersForCount();
        countShader.Use();
        GL.Uniform1(countShader.GetUniformLocation("uChunkCount"), chunkCount);
        var countWorkGroups = (chunkCount + 255) / 256;
        GL.DispatchCompute((int)countWorkGroups, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Stage 3.3 Prefix Sum (GPU)
        phase3Buffers.BindBuffersForScan();
        var scanShader = new Shader("Shaders/compute-scan.comp", ShaderType.ComputeShader);
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
            return (0,0,Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), IntPtr.Zero);
        }
        
        var chunkCount = (uint)chunkIndices.Length;

        // Fetch baseOffsets (needed to set per-chunk baseVertex) and totals for allocations
        // This readback should be fast now because we waited for the fence
        var baseOffsets = new uint[chunkCount];
        GL.GetNamedBufferSubData(phase3Buffers.OffsetBuffer, IntPtr.Zero, (int)(chunkCount * sizeof(uint)), baseOffsets);
        
        // Fetch face counts for each chunk (needed to set VisibleVoxelCount for indirect commands)
        var counts = new uint[chunkCount];
        GL.GetNamedBufferSubData(phase3Buffers.CountBuffer, IntPtr.Zero, (int)(chunkCount * sizeof(uint)), counts);
        
        uint[] totals = new uint[2];
        GL.GetNamedBufferSubData(phase3Buffers.ScanTotalsBuffer, IntPtr.Zero, 2 * sizeof(uint), totals);
        
        var totalVertices = totals[0];
        var totalIndices = totals[1];
        uint allocatedVertexOffset = phase3Buffers.AllocateVertexRegion(totalVertices);
        uint allocatedIndexOffset = phase3Buffers.AllocateIndexRegion(totalIndices);

        // Stage 3.4 Compaction
        phase3Buffers.ResetAtomicCounters();
        GL.ClearNamedBufferData(phase3Buffers.PerChunkEmitBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt, IntPtr.Zero);
        GL.NamedBufferData(compactionChunkIndicesBuffer, chunkIndices.Length * sizeof(int), chunkIndices, BufferUsageHint.DynamicDraw);
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.CHUNK_INDICES, compactionChunkIndicesBuffer);
        // Bind the correct voxel data buffer for compaction
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.VOXEL_DATA, voxelDataBuffers[bufferIndex]);
        phase3Buffers.BindBuffersForCompaction();
        compactShader.Use();
        GL.Uniform1(compactShader.GetUniformLocation("uChunkCount"), chunkCount);
        GL.Uniform1(compactShader.GetUniformLocation("uWorldChunksXZ"), (uint)VoxelHelper.WorldChunksXZ);
        GL.Uniform1(compactShader.GetUniformLocation("uVertexRegionOffset"), allocatedVertexOffset);
        GL.Uniform1(compactShader.GetUniformLocation("uIndexRegionOffset"), allocatedIndexOffset);
        GL.DispatchCompute((int)chunkCount, 128, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit | MemoryBarrierFlags.ElementArrayBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Stage 3.5 Build indirect commands on GPU
        // PHASE 5.3: Use command slots to write to correct location in indirect buffer
        // Allocate slots for this batch
        var commandSlots = new uint[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            commandSlots[i] = (uint)phase3Buffers.AllocateCommandSlot();
        }
        
        // Upload slots to GPU
        GL.NamedBufferSubData((int)phase3Buffers.CommandSlotBuffer, IntPtr.Zero, (int)(chunkCount * sizeof(uint)), commandSlots);
        
        phase3Buffers.BindBuffersForBuildIndirect();
        // Bind the new command slot buffer
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.COMMAND_SLOTS, (int)phase3Buffers.CommandSlotBuffer);
        
        var buildIndirect = new Shader("Shaders/compute-build-indirect.comp", ShaderType.ComputeShader);
        buildIndirect.Use();
        GL.Uniform1(buildIndirect.GetUniformLocation("uChunkCount"), chunkCount);
        GL.Uniform1(buildIndirect.GetUniformLocation("uVertexRegionOffset"), allocatedVertexOffset);
        GL.Uniform1(buildIndirect.GetUniformLocation("uIndexRegionOffset"), allocatedIndexOffset);
        var groups = (chunkCount + 255) / 256;
        GL.DispatchCompute((int)groups, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Create fence to track completion of Compaction/Build
        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);

        Log.Info($"Phase 3 complete: {chunkCount} chunks (faces total approx={counts.Aggregate(0u,(a,c)=>a+c)}) region offsets V={allocatedVertexOffset} I={allocatedIndexOffset}");
        
        // Return slots so we can update descriptors
        return (allocatedVertexOffset, allocatedIndexOffset, counts, baseOffsets, commandSlots, fence);
    }

    /// <summary>
    /// Compute prefix sum for vertex buffer offsets.
    /// Stage 3.3: CPU Prefix Sum
    /// </summary>
    private (uint[] baseOffsets, uint totalVertices) ComputePrefixSum(uint[] visibleCounts)
    {
        var baseOffsets = new uint[visibleCounts.Length];
        uint runningTotal = 0;

        for (int i = 0; i < visibleCounts.Length; i++)
        {
            baseOffsets[i] = runningTotal;
            runningTotal += visibleCounts[i] * 4;  // 4 vertices per face
        }

        Log.Debug($"Prefix sum: {visibleCounts.Length} chunks, {runningTotal} total vertices");

        return (baseOffsets, runningTotal);
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
        if (frustumShader == null)
        {
            frustumShader = new Shader("Shaders/compute-frustum.comp", ShaderType.ComputeShader);
        }

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
            }
            
            GL.CreateBuffers(1, out visibilityFlagsSSBO);
            GL.NamedBufferStorage(visibilityFlagsSSBO, maxChunks * sizeof(int), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, visibilityFlagsSSBO, -1, "visibility_flags_ssbo");

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
        if (frustumShader == null || chunkIndices.Length == 0)
        {
            // Return all visible if culling not initialized
            return Enumerable.Repeat(1, chunkIndices.Length).ToArray();
        }

        // Buffers are pre-allocated to max view distance, so this should never happen
        if (chunkIndices.Length > chunkIndicesBufferCapacity)
        {
            Log.Error($"Frustum culling buffer overflow! Requested {chunkIndices.Length} chunks, capacity {chunkIndicesBufferCapacity}.");
            return Enumerable.Repeat(1, chunkIndices.Length).ToArray();
        }
        
        if (chunkIndices.Length > visibilityFlagsCapacity)
        {
            Log.Error($"Visibility flags buffer overflow! Requested {chunkIndices.Length} chunks, capacity {visibilityFlagsCapacity}.");
            return Enumerable.Repeat(1, chunkIndices.Length).ToArray();
        }

        // Update frustum planes UBO
        UpdateFrustumUBO(camera);

        // Upload chunk indices
        GL.NamedBufferSubData(cullingChunkIndicesBuffer, IntPtr.Zero,
            chunkIndices.Length * sizeof(int), chunkIndices);

        // Bind buffers
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 0, frustumUBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, cullingChunkIndicesBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, visibilityFlagsSSBO);

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
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // PERFORMANCE FIX: Skip CPU readback - visibility flags are now consumed on GPU
        // The visibility_flags_ssbo remains bound and available for the renderer to use
        // during the draw call (e.g., in the vertex shader via gl_InstanceID lookup)
        
        // For now, return all visible to maintain API compatibility
        // The actual culling happens on GPU during rendering
        VisibleChunkCount = chunkIndices.Length; // Conservative estimate
        CulledChunkCount = 0;
        
        return Enumerable.Repeat(1, chunkIndices.Length).ToArray();
        
        /* REMOVED: Expensive CPU readback that causes VIDEO→HOST memory copies
        var visibilityFlags = new int[chunkIndices.Length];
        GL.GetNamedBufferSubData(visibilityFlagsSSBO, IntPtr.Zero,
            chunkIndices.Length * sizeof(int), visibilityFlags);
        
        VisibleChunkCount = visibilityFlags.Count(f => f == 1);
        CulledChunkCount = chunkIndices.Length - VisibleChunkCount;
        
        return visibilityFlags;
        */
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
        int batchSize = 32; 
        Log.Info($"ExecuteCompletePipeline: Processing {chunkIndices.Length} chunks in batches of {batchSize}");
        
        for (int i = 0; i < chunkIndices.Length; i += batchSize)
        {
            int count = Math.Min(batchSize, chunkIndices.Length - i);
            var batchIndices = new int[count];
            Array.Copy(chunkIndices, i, batchIndices, 0, count);

            Log.Debug($"  Batch {i/batchSize}: {count} chunks");

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
            
            // Force finish to ensure all GPU work is done before next batch (debugging)
            GL.Finish();
            
            // Assign descriptor offsets similar to PollCompletedBatches
            if (phase3Buffers != null)
            {
                for(int j=0; j<batchIndices.Length; j++)
                {
                    var chunkIdx = batchIndices[j];
                    if (activeChunks.TryGetValue(chunkIdx, out var desc))
                    {
                        desc.AtlasOffset = (int)(allocatedVtx + baseOffsets[j]);
                        uint indexPrefix = 0; for(int k=0; k<j; k++) indexPrefix += counts[k]*6;
                        desc.IndexOffset = (int)(allocatedIdx + indexPrefix);
                        desc.VisibleVoxelCount = (int)counts[j];
                        desc.CommandSlot = (int)commandSlots[j];
                        desc.State = TerrainChunkState.Ready;
                        activeChunks[chunkIdx] = desc;
                    }
                    else
                    {
                        activeChunks[chunkIdx] = new ChunkDescriptor{
                            ChunkIndex=chunkIdx, 
                            AtlasOffset=(int)(allocatedVtx+baseOffsets[j]), 
                            IndexOffset=(int)(allocatedIdx + counts.Take(j).Aggregate(0u,(a,c)=> a + c*6)), 
                            VisibleVoxelCount=(int)counts[j], 
                            CommandSlot=(int)commandSlots[j], 
                            State=TerrainChunkState.Ready, 
                            LastAccessFrame=currentFrame, 
                            Priority=CalculatePriority(chunkIdx)
                        };
                    }
                }
            }
        }

        var totalFaces = activeChunks.Values.Where(c=>c.State==TerrainChunkState.Ready).Sum(c=>(long)c.VisibleVoxelCount);
        var totalVertices = phase3Buffers?.CurrentVertexBufferEnd ?? 0;
        
        if (terrainRenderer != null && phase3Buffers != null && totalVertices > 0)
        {
            terrainRenderer.SetupBuffers(phase3Buffers, totalVertices, (uint)totalFaces, activeChunks.Values.Where(c=>c.State==TerrainChunkState.Ready));
            Log.Info($"Complete pipeline executed: {chunkIndices.Length} chunks processed in batches");
        }
    }

    public void RegisterChunksAsReady(int[] chunkIndices)
    {
        foreach (var chunkIdx in chunkIndices)
        {
            if (activeChunks.ContainsKey(chunkIdx))
            {
                // Update existing chunk
                var desc = activeChunks[chunkIdx];
                desc.State = TerrainChunkState.Ready;
                activeChunks[chunkIdx] = desc;
            }
            else
            {
                // Create new descriptor for chunk not tracked yet
                var descriptor = new ChunkDescriptor
                {
                    ChunkIndex = chunkIdx,
                    State = TerrainChunkState.Ready,
                    LastAccessFrame = currentFrame,
                    Priority = CalculatePriority(chunkIdx)
                };
                activeChunks[chunkIdx] = descriptor;
            }
        }
        
        Log.Info($"Registered {chunkIndices.Length} chunks as ready");
    }

    public void Dispose()
    {
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
