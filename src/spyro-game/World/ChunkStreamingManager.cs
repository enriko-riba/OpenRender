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
    private readonly GpuBufferAllocator bufferAllocator;
    private readonly Dictionary<int, ChunkDescriptor> activeChunks;
    private readonly Queue<int> pendingGeneration;
    private readonly Queue<BatchSubmission> inFlightBatches;

    private long currentFrame;
    private const int MAX_CHUNKS_PER_BATCH = VoxelHelper.DEFAULT_MAX_CHUNKS_PER_BATCH;
    private const int MAX_IN_FLIGHT_BATCHES = 4;

    // Phase 2: GPU generation shader
    private Shader? generationShader;
    private uint chunkIndicesBuffer;
    private uint voxelDataBuffer;
    private uint columnHeightsBuffer;
    private uint columnMetaBuffer;

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
    public VoxelWorld World => world;

    public ChunkStreamingManager(VoxelWorld world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        this.bufferAllocator = new GpuBufferAllocator();
        this.activeChunks = new Dictionary<int, ChunkDescriptor>();
        this.pendingGeneration = new Queue<int>();
        this.inFlightBatches = new Queue<BatchSubmission>();

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

    /// <summary>
    /// Get all ready chunks for rendering
    /// </summary>
    public IEnumerable<ChunkDescriptor> GetReadyChunks()
    {
        return activeChunks.Values.Where(c => c.State == TerrainChunkState.Ready);
    }

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
        var viewDistance = VoxelHelper.MaxDistanceInChunks;
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
        // Don't submit if we have too many in-flight batches
        if (inFlightBatches.Count >= MAX_IN_FLIGHT_BATCHES)
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
        var fence = DispatchGenerationAsync(batchIndices.ToArray());

        // Create batch submission with fence
        var submission = new BatchSubmission
        {
            ChunkIndices = batchIndices.ToArray(),
            Fence = fence,
            SubmitFrame = currentFrame
        };

        inFlightBatches.Enqueue(submission);

        Log.Info($"ChunkStreamingManager: Submitted batch of {batchIndices.Count} chunks (frame {currentFrame})");
    }

    private void PollCompletedBatches()
    {
        while (inFlightBatches.Count > 0)
        {
            var batch = inFlightBatches.Peek();

            // Check if fence is signaled
            if (batch.Fence != IntPtr.Zero)
            {
                var status = GL.ClientWaitSync(batch.Fence, 0, 0);
                if (status == WaitSyncStatus.TimeoutExpired)
                    break; // Not ready yet

                // Cleanup fence
                try { GL.DeleteSync(batch.Fence); } catch { }
            }

            // Batch complete - dequeue
            inFlightBatches.Dequeue();
            
            Log.Info($"ChunkStreamingManager: Completed batch of {batch.ChunkIndices.Length} chunks (latency: {currentFrame - batch.SubmitFrame} frames)");

            // ========================================================================
            // Phase 5.2: INCREMENTAL BUFFER UPDATES
            // ========================================================================
            // Instead of marking chunks as ready without updating renderer,
            // we now:
            // 1. Execute Phase 3 pipeline on completed chunks
            // 2. Allocate buffer regions (reusing freed space)
            // 3. Update chunk descriptors with offsets
            // 4. Register chunks as ready
            
            if (phase3Buffers != null && terrainRenderer != null && batch.ChunkIndices.Length > 0)
            {
                try
                {
                    // Execute Phase 3: Visibility → Count → Prefix Sum → Compaction
                    var (vertexCount, faceCount) = ExecutePhase3(batch.ChunkIndices);
                    
                    if (vertexCount > 0)
                    {
                        // Get current vertex buffer state
                        var currentBufferEnd = phase3Buffers.CurrentBufferEnd;
                        
                        // Allocate buffer region for these chunks (reuses freed space!)
                        var allocatedOffset = phase3Buffers.AllocateRegion(vertexCount);
                        
                        // Update chunk descriptors with buffer offsets
                        var verticesPerFace = 4;
                        var vertexOffset = 0u;
                        
                        // Read face counts from Phase 3 count buffer
                        var counts = new uint[batch.ChunkIndices.Length];
                        GL.GetNamedBufferSubData(phase3Buffers.CountBuffer, IntPtr.Zero,
                            batch.ChunkIndices.Length * sizeof(uint), counts);
                        
                        // Phase 5.2: Free old buffer regions for dirty chunks before allocating new
                        foreach (var chunkIdx in batch.ChunkIndices)
                        {
                            if (activeChunks.TryGetValue(chunkIdx, out var desc))
                            {
                                // If chunk had an old buffer region, free it now
                                if (desc.AtlasOffset >= 0 && desc.VisibleVoxelCount > 0)
                                {
                                    phase3Buffers.FreeRegion((uint)desc.AtlasOffset, (uint)(desc.VisibleVoxelCount * verticesPerFace));
                                    Log.Debug($"Freed old buffer region for dirty chunk {chunkIdx}: offset={desc.AtlasOffset}, size={desc.VisibleVoxelCount * verticesPerFace}");
                                }
                            }
                        }
                        
                        for (int i = 0; i < batch.ChunkIndices.Length; i++)
                        {
                            var chunkIdx = batch.ChunkIndices[i];
                            if (activeChunks.TryGetValue(chunkIdx, out var desc))
                            {
                                desc.AtlasOffset = (int)(allocatedOffset + vertexOffset);
                                desc.VisibleVoxelCount = (int)counts[i];  // Face count
                                desc.State = TerrainChunkState.Ready;
                                desc.Fence = IntPtr.Zero;
                                activeChunks[chunkIdx] = desc;
                                
                                vertexOffset += counts[i] * (uint)verticesPerFace;
                            }
                        }
                        
                        // NOTE: Vertices are already in Phase3BufferManager's vertex buffer
                        // from ExecutePhase3's compaction stage. No need to copy again!
                        
                        // Phase 5.2 FIX: Update renderer with CUMULATIVE buffer state, not per-batch
                        // The renderer needs to know the TOTAL vertices/faces in the buffer
                        var totalVerticesInBuffer = phase3Buffers.CurrentBufferEnd;
                        var totalFacesInBuffer = activeChunks.Values.Count(c => c.State == TerrainChunkState.Ready);
                        
                        terrainRenderer.SetupBuffers(phase3Buffers, totalVerticesInBuffer, (uint)totalFacesInBuffer);
                        
                        Log.Info($"Phase 5.2: Updated {batch.ChunkIndices.Length} chunks, allocated {vertexCount} vertices at offset {allocatedOffset}, buffer end={totalVerticesInBuffer}, {phase3Buffers.FreeRegionCount} free regions");
                    }
                    else
                    {
                        // No visible faces - mark chunks as ready anyway
                        foreach (var chunkIdx in batch.ChunkIndices)
                        {
                            if (activeChunks.TryGetValue(chunkIdx, out var desc))
                            {
                                desc.State = TerrainChunkState.Ready;
                                desc.Fence = IntPtr.Zero;
                                activeChunks[chunkIdx] = desc;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Phase 5.2: Failed to update batch: {ex.Message}");
                    
                    // Fallback: mark chunks as ready without buffer update
                    foreach (var chunkIdx in batch.ChunkIndices)
                    {
                        if (activeChunks.TryGetValue(chunkIdx, out var desc))
                        {
                            desc.State = TerrainChunkState.Ready;
                            desc.Fence = IntPtr.Zero;
                            activeChunks[chunkIdx] = desc;
                        }
                    }
                }
            }
            else
            {
                // Phase 3/4 not initialized - fallback behavior
                foreach (var chunkIdx in batch.ChunkIndices)
                {
                    if (activeChunks.TryGetValue(chunkIdx, out var desc))
                    {
                        desc.State = TerrainChunkState.Ready;
                        desc.Fence = IntPtr.Zero;
                        activeChunks[chunkIdx] = desc;
                    }
                }
                
                if (phase3Buffers == null)
                    Log.Warn("Phase 3 not initialized - skipping buffer updates");
                if (terrainRenderer == null)
                    Log.Warn("Phase 4 not initialized - skipping renderer updates");
            }
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
    /// Phase 5.2: Frees buffer regions for reuse
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

        // Phase 5.2: Free buffer region for reuse
        if (desc.AtlasOffset >= 0 && desc.VisibleVoxelCount > 0 && phase3Buffers != null)
        {
            var verticesPerFace = 4;
            phase3Buffers.FreeRegion((uint)desc.AtlasOffset, (uint)(desc.VisibleVoxelCount * verticesPerFace));
            Log.Debug($"Freed buffer region for chunk {chunkIndex}: offset={desc.AtlasOffset}, size={desc.VisibleVoxelCount * verticesPerFace}");
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
        // Pre-allocate for max view distance if not specified
        if (maxChunks == 0)
        {
            maxChunks = CalculateMaxViewChunks();
        }
        
        // Validate shader constants match VoxelHelper
        VoxelHelper.ValidateShaderConstants();
        
        // Create SSBOs FIRST (before loading shader)
        if (chunkIndicesBuffer == 0)
        {
            GL.CreateBuffers(1, out chunkIndicesBuffer);
        }
        if (voxelDataBuffer == 0)
        {
            GL.CreateBuffers(1, out voxelDataBuffer);
        }
        if (columnHeightsBuffer == 0)
        {
            GL.CreateBuffers(1, out columnHeightsBuffer);
        }
        if (columnMetaBuffer == 0)
        {
            GL.CreateBuffers(1, out columnMetaBuffer);
        }

        // Allocate storage for configurable max chunks (passed from caller)
        var voxelsPerChunk = VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize;

        // Reallocate buffers if capacity changed
        if (chunkIndicesBufferCapacity != maxChunks)
        {
            // Delete old buffers
            if (chunkIndicesBufferCapacity > 0)
            {
                GL.DeleteBuffer(chunkIndicesBuffer);
                GL.DeleteBuffer(voxelDataBuffer);
                GL.DeleteBuffer(columnHeightsBuffer);
                GL.DeleteBuffer(columnMetaBuffer);
                GL.CreateBuffers(1, out chunkIndicesBuffer);
                GL.CreateBuffers(1, out voxelDataBuffer);
                GL.CreateBuffers(1, out columnHeightsBuffer);
                GL.CreateBuffers(1, out columnMetaBuffer);
            }

            // NOTE: Using int (not uint) to match C# int[] arrays used throughout the codebase
            GL.NamedBufferStorage(chunkIndicesBuffer, maxChunks * sizeof(int), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit);
            GL.NamedBufferStorage(voxelDataBuffer, maxChunks * voxelsPerChunk * sizeof(uint), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit);
            GL.NamedBufferStorage(columnHeightsBuffer, maxChunks * VoxelHelper.ChunkSideSizeSquare * sizeof(int), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
            GL.NamedBufferStorage(columnMetaBuffer, maxChunks * VoxelHelper.ChunkSideSizeSquare * sizeof(uint) * 4, IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit);

            chunkIndicesBufferCapacity = maxChunks;
            Log.Info($"Reallocated generation buffers for {maxChunks} chunks");
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
    public IntPtr DispatchGenerationAsync(int[] chunkIndices)
    {
        if (generationShader == null || chunkIndices.Length == 0)
            return IntPtr.Zero;

        var voxelsPerChunk = VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize;
        var totalVoxels = chunkIndices.Length * voxelsPerChunk;
        GL.ClearNamedBufferData(voxelDataBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt, IntPtr.Zero);
        GL.ClearNamedBufferData(columnHeightsBuffer, PixelInternalFormat.R32i, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero);
        GL.ClearNamedBufferData(columnMetaBuffer, PixelInternalFormat.Rgba32ui, PixelFormat.RgbaInteger, PixelType.UnsignedInt, IntPtr.Zero);
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);

        // Upload chunk indices
        // NOTE: chunkIndices is int[], so use sizeof(int) not sizeof(uint)
        GL.NamedBufferSubData(chunkIndicesBuffer, IntPtr.Zero, chunkIndices.Length * sizeof(int), chunkIndices);

        // Bind SSBOs with correct bindings matching shader
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, chunkIndicesBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, voxelDataBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, columnHeightsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnMetaBuffer);

        // Set ALL uniforms every dispatch
        generationShader.Use();
        generationShader.SetUInt("uChunkCount", (uint)chunkIndices.Length);
        generationShader.SetUInt("uWorldChunksXZ", (uint)VoxelHelper.WorldChunksXZ);

        // Dispatch: one work-group per chunk, matching layout (16,1,16)
        GL.DispatchCompute(chunkIndices.Length, 1, 1);

        // Memory barrier to ensure writes complete before fence
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Create fence to track completion (NON-BLOCKING)
        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);

        Log.Debug($"Dispatched ASYNC GPU generation for {chunkIndices.Length} chunks (fence={fence})");

        return fence;
    }

    /// <summary>
    /// Dispatch GPU generation for a batch of chunks (SYNCHRONOUS - kept for compatibility)
    /// Phase 2: Basic implementation
    /// </summary>
    public void DispatchGeneration(int[] chunkIndices)
    {
        var fence = DispatchGenerationAsync(chunkIndices);
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

        Log.Info($"Phase 3 GPU pipeline initialized (max {maxChunksPerBatch} chunks)");
    }

    /// <summary>
    /// Execute Phase 3 pipeline: Visibility → Count → Prefix Sum → Compaction
    /// Phase 5.1: Returns face count instead of index count (indices are shared).
    /// Follows explicit synchronization strategy with barriers at every stage.
    /// </summary>
    public (uint vertexCount, uint faceCount) ExecutePhase3(int[] chunkIndices)
    {
        if (phase3Buffers == null || visibilityShader == null || countShader == null)
        {
            Log.Error("Phase 3 not initialized!");
            return (0, 0);
        }

        if (chunkIndices.Length == 0)
            return (0, 0);

        var chunkCount = (uint)chunkIndices.Length;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ========================================================================
        // Stage 3.1: Visibility Determination
        // ========================================================================

        // Bind buffers: voxelData (input), visibilityMask (output)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VOXEL_DATA, voxelDataBuffer);
        phase3Buffers.BindBuffersForVisibility();

        // Set uniforms and dispatch (uChunkCount is uint in shader)
        visibilityShader.Use();
        var visUniformLocation = visibilityShader.GetUniformLocation("uChunkCount");
        GL.Uniform1(visUniformLocation, chunkCount);

        // Dispatch: chunkCount work groups in X, 128 work groups in Y (one per Y-slice), 1 in Z
        // Each work group is (16, 1, 16) threads matching generation shader layout
        GL.DispatchCompute((int)chunkCount, 128, 1);

        // BARRIER: Ensure visibility writes complete before count reads
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        var visibilityMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();

        // ========================================================================
        // Stage 3.2: Count Visible Faces
        // ========================================================================

        phase3Buffers.BindBuffersForCount();

        countShader.Use();
        var countUniformLocation = countShader.GetUniformLocation("uChunkCount");
        GL.Uniform1(countUniformLocation, chunkCount);

        // Dispatch: ceil(chunkCount / 256) work groups
        var countWorkGroups = (chunkCount + 255) / 256;
        GL.DispatchCompute((int)countWorkGroups, 1, 1);

        // BARRIER: Ensure counts written before CPU reads
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        var countMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();

        // ========================================================================
        // Stage 3.3: CPU Prefix Sum
        // ========================================================================

        // Download counts from GPU
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
        var counts = new uint[chunkCount];
        GL.GetNamedBufferSubData(phase3Buffers.CountBuffer, IntPtr.Zero,
            (int)(chunkCount * sizeof(uint)), counts);

        // Compute prefix sum (base offsets for each chunk)
        var (baseOffsets, totalVertices) = ComputePrefixSum(counts);

        // Upload offsets back to GPU
        GL.NamedBufferSubData(phase3Buffers.OffsetBuffer, IntPtr.Zero,
            (int)(chunkCount * sizeof(uint)), baseOffsets);

        // BARRIER: Ensure offsets uploaded before compaction reads
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);

        var prefixSumMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();

        // ========================================================================
        // Stage 3.4: Mesh Compaction
        // ========================================================================

        // Reset atomic counters
        phase3Buffers.ResetAtomicCounters();

        // CRITICAL: Upload chunk indices for world position calculation
        GL.NamedBufferSubData(chunkIndicesBuffer, IntPtr.Zero, chunkIndices.Length * sizeof(int), chunkIndices);
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);

        // Bind buffers for compaction (including chunk indices for world positioning)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            0, chunkIndicesBuffer);  // Chunk indices at binding 0
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            1, voxelDataBuffer);  // Voxel data at binding 1
        phase3Buffers.BindBuffersForCompaction();

        // Set uniforms and dispatch (uChunkCount is uint in shader)
        compactShader!.Use();
        var compactUniformLocation = compactShader.GetUniformLocation("uChunkCount");
        GL.Uniform1(compactUniformLocation, chunkCount);

        // Set world size uniform for chunk positioning
        var worldSizeLocation = compactShader.GetUniformLocation("uWorldChunksXZ");
        GL.Uniform1(worldSizeLocation, (uint)VoxelHelper.WorldChunksXZ);

        // Dispatch: 1 work group per chunk, 128 Y-slices (matching generation shader layout)
        GL.DispatchCompute((int)chunkCount, 128, 1);

        // BARRIER: Ensure vertex writes complete before rendering
        GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit);

        // Read back actual counts from atomic counters
        // Phase 5.1: Second counter now tracks face count instead of index count
        var (actualVertexCount, actualFaceCount) = phase3Buffers.ReadAtomicCounters();

        var compactionMs = sw.Elapsed.TotalMilliseconds;
        var totalMs = visibilityMs + countMs + prefixSumMs + compactionMs;

        Log.Info($"Phase 3 complete: {chunkCount} chunks, {actualVertexCount} vertices, {actualFaceCount} faces (Phase 5.1: shared IBO)");
        Log.Info($"  Timing: Vis={visibilityMs:F2}ms Count={countMs:F2}ms PrefixSum={prefixSumMs:F2}ms Compact={compactionMs:F2}ms Total={totalMs:F2}ms");

        return (actualVertexCount, actualFaceCount);
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
        GL.NamedBufferSubData(chunkIndicesBuffer, IntPtr.Zero,
            chunkIndices.Length * sizeof(int), chunkIndices);

        // Bind buffers
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 0, frustumUBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, chunkIndicesBuffer);
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

        // Phase 2: Generate voxel data
        DispatchGeneration(chunkIndices);

        // Phase 3: Visibility and compaction
        var (vertexCount, faceCount) = ExecutePhase3(chunkIndices);

        // Phase 4: Setup rendering buffers
        if (terrainRenderer != null && phase3Buffers != null && vertexCount > 0)
        {
            // Phase 5.1: Pass face count to renderer (will use shared IBO)
            terrainRenderer.SetupBuffers(phase3Buffers, vertexCount, faceCount);
            
            // Register chunks as ready in the streaming manager
            RegisterChunksAsReady(chunkIndices);
            
            Log.Info($"Complete pipeline executed: {chunkIndices.Length} chunks, {faceCount} faces ready for rendering");
        }
    }

    /// <summary>
    /// Register chunks as ready in the streaming manager after generation completes.
    /// This allows GetReadyChunks() and GetStats() to return correct values.
    /// </summary>
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
    }
}
