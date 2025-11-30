using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
    private void ClearPendingCpuMeshingQueue()
    {
        currentFrame++;

        // Handle hot-reload
        if (pendingConfigReload)
        {
            pendingConfigReload = false;
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

    public void FlushVoxelCache(string reason)
    {
        var cachedEntries = chunkVoxelCache.ActiveEntryCount;
        chunkVoxelCache.Clear();
        cpuMeshResults.Clear();
        placeholderMasksInFlight.Clear();
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

    private void LogHeightCacheStatsIfNeeded()
    {
        if (!HeightCacheEnabled || heightCacheCapacity == 0)
            return;

        if (HeightCacheLogIntervalFrames <= 0)
            return;

        if (currentFrame % HeightCacheLogIntervalFrames != 0)
            return;

        var stats = GetHeightCacheStats();
        if (stats.Capacity == 0)
            return;

        var utilization = (float)stats.SlotsInUse / stats.Capacity;
        var uploadedMb = stats.BytesUploaded / (1024f * 1024f);
        var totalSamples = stats.ShaderHits + stats.ShaderMisses;
        var totalHitRate = totalSamples > 0 ? (float)stats.ShaderHits / totalSamples : 0f;

        var deltaHits = heightCacheShaderHitsTotal - heightCacheShaderHitsAtLastLog;
        var deltaMisses = heightCacheShaderMissesTotal - heightCacheShaderMissesAtLastLog;
        heightCacheShaderHitsAtLastLog = heightCacheShaderHitsTotal;
        heightCacheShaderMissesAtLastLog = heightCacheShaderMissesTotal;
        var deltaSamples = deltaHits + deltaMisses;
        var deltaHitRate = deltaSamples > 0 ? (float)deltaHits / deltaSamples : 0f;

        var message = $"HeightCache stats: {stats.SlotsInUse}/{stats.Capacity} slots ({utilization:P1}) used, uploads={stats.Uploads}, uploaded={uploadedMb:F1} MiB";
        if (totalSamples > 0)
        {
            message += $" | cache samples={totalSamples:N0} hits={stats.ShaderHits:N0} misses={stats.ShaderMisses:N0} (hit {totalHitRate:P1})";
        }
        if (deltaSamples > 0)
        {
            message += $" | Δ hits={deltaHits:N0} misses={deltaMisses:N0} (hit {deltaHitRate:P1})";
        }

        Log.Info(message);
    }

    public readonly struct HeightCacheStats
    {
        public int Capacity { get; init; }
        public int SlotsInUse { get; init; }
        public int FreeSlots { get; init; }
        public long Uploads { get; init; }
        public long BytesUploaded { get; init; }
        public long ShaderHits { get; init; }
        public long ShaderMisses { get; init; }
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
            BytesUploaded = heightCacheBytesUploaded,
            ShaderHits = heightCacheShaderHitsTotal,
            ShaderMisses = heightCacheShaderMissesTotal
        };
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

    private byte ComputePlaceholderMask(int chunkIdx, HashSet<int> batchSet)
    {
        byte mask = 0;
        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

        void CheckNeighbor(int dx, int dz, byte bit)
        {
            var nx = chunkX + dx;
            var nz = chunkZ + dz;
            if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                return;

            var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;

            if (batchSet.Contains(neighborIdx))
                return; // Will be generated alongside

            if (activeChunks.TryGetValue(neighborIdx, out var neighborDesc))
            {
                // Only treat neighbors with finalized meshes as "real" data sources.
                // Pending or in-flight chunks still leave seams, so flag them as placeholders.
                if (neighborDesc.State is TerrainChunkState.Ready or TerrainChunkState.Dirty)
                    return;
            }

            mask |= bit;
        }

        CheckNeighbor(1, 0, PLACEHOLDER_POS_X);
        CheckNeighbor(-1, 0, PLACEHOLDER_NEG_X);
        CheckNeighbor(0, 1, PLACEHOLDER_POS_Z);
        CheckNeighbor(0, -1, PLACEHOLDER_NEG_Z);

        return mask;
    }

    private void UploadPlaceholderMasksForBatch(IReadOnlyList<int> chunkIndices, int bufferIndex)
    {
        if (chunkIndices.Count == 0)
            return;

        if (placeholderMaskBuffers[bufferIndex] == 0)
            return;

        var masks = ArrayPool<uint>.Shared.Rent(chunkIndices.Count);
        try
        {
            for (var i = 0; i < chunkIndices.Count; i++)
            {
                var chunkIdx = chunkIndices[i];
                if (placeholderMasksInFlight.TryGetValue(chunkIdx, out var inflightMask))
                {
                    masks[i] = inflightMask;
                }
                else
                {
                    masks[i] = activeChunks.TryGetValue(chunkIdx, out var desc) ? desc.PlaceholderMask : 0u;
                }
            }

            GL.NamedBufferSubData(placeholderMaskBuffers[bufferIndex], IntPtr.Zero, chunkIndices.Count * sizeof(uint), masks);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(masks);
        }
    }

    private void ResolvePlaceholderDependencies(int chunkIdx)
    {
        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

        void ClearNeighborBit(int dx, int dz, byte oppositeBit)
        {
            var nx = chunkX + dx;
            var nz = chunkZ + dz;
            if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                return;

            var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;
            if (!activeChunks.TryGetValue(neighborIdx, out var neighborDesc))
                return;

            if ((neighborDesc.PlaceholderMask & oppositeBit) == 0)
                return;

            neighborDesc.PlaceholderMask &= (byte)~oppositeBit;
            activeChunks[neighborIdx] = neighborDesc;

            RequestSeamRefresh(neighborIdx, $"Neighbor {chunkIdx} resolved seam edge");
        }

        ClearNeighborBit(-1, 0, PLACEHOLDER_POS_X); // Our -X neighbor had +X placeholder
        ClearNeighborBit(1, 0, PLACEHOLDER_NEG_X);
        ClearNeighborBit(0, -1, PLACEHOLDER_POS_Z);
        ClearNeighborBit(0, 1, PLACEHOLDER_NEG_Z);
    }

    private void ProcessPendingSeamRefreshes()
    {
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

    private void RequestSeamRefresh(int chunkIdx, string reason)
    {
        pendingSeamRefresh.Remove(chunkIdx);

        if (!activeChunks.TryGetValue(chunkIdx, out var desc))
        {
            return;
        }

        if (desc.State == TerrainChunkState.Ready)
        {
            if (lastSeamRefreshFrame.TryGetValue(chunkIdx, out var lastFrame) &&
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
                            var startY = (short)(packed & 0x1FF);
                            var endY = (short)((packed >> 9) & 0x1FF);
                            var blockType = (byte)((packed >> 18) & 0xFF);

                            data.Spans[col * 16 + s] = new ColumnSpan
                            {
                                StartY = startY,
                                EndY = endY,
                                BlockDescriptor = blockType // This is BlockDescriptor from GPU
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
                        if (HeightCacheEnabled)
                        {
                            var packedHeights = new uint[HeightCacheWordsPerChunk];
                            for (var col = 0; col < VoxelHelper.ChunkSideSizeSquare; col++)
                            {
                                var spanCount = chunkSpanCounts[col];
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
                                    var maxYExclusive = 0;
                                    var waterSpan = false;
                                    for (var s = 0; s < spanCount && s < ChunkCollisionData.MaxSpansPerColumn; s++)
                                    {
                                        var y1 = chunkSpansPairs[baseIdx + s * 2 + 1];
                                        if (y1 > maxYExclusive) maxYExclusive = y1;
                                        var descriptor = chunkSpanTypes[col * ChunkCollisionData.MaxSpansPerColumn + s];
                                        if (descriptor == (byte)BlockDescriptor.Water)
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

    private byte GetPlaceholderMaskForChunk(int chunkIdx)
        => placeholderMasksInFlight.TryGetValue(chunkIdx, out var mask) ? mask : (byte)0;

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
        WriteIndirectCommands(slot, mesh.ChunkIndex, vertexOffset, indexOffset, (uint)faceCount, 0);

        var placeholderMask = mesh.PlaceholderMask;
        if (placeholderMasksInFlight.TryGetValue(mesh.ChunkIndex, out var inflightMask))
        {
            placeholderMask = inflightMask;
            placeholderMasksInFlight.Remove(mesh.ChunkIndex);
        }

        Log.Info($"Chunk {mesh.ChunkIndex} mesh upload faces={faceCount} mask=0x{placeholderMask:X2} cacheVer={mesh.CacheVersion} enqueue={mesh.EnqueueId} build={mesh.BuildId}");

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

        ResolvePlaceholderDependencies(mesh.ChunkIndex);

        if (pendingSeamRefresh.Contains(mesh.ChunkIndex))
        {
            RequestSeamRefresh(mesh.ChunkIndex, "Deferred seam dependency resolved");
        }

        return true;
    }

    private void UploadCpuMeshData(CpuChunkMesh mesh, int vertexOffset, int vertexCount, int indexOffset, int indexCount)
    {
        if (phase3Buffers == null)
        {
            return;
        }

        if (vertexCount > 0 && vertexOffset >= 0 && mesh.VertexData.Length > 0)
        {
            var vertexByteOffset = (nint)(vertexOffset * VoxelHelper.VERTEX_STRIDE_BYTES);
            var vertexByteCount = mesh.VertexData.Length * sizeof(uint);
            GL.NamedBufferSubData((int)phase3Buffers.VertexBuffer, (IntPtr)vertexByteOffset, vertexByteCount, mesh.VertexData);
        }

        if (indexCount > 0 && indexOffset >= 0 && mesh.IndexData.Length > 0)
        {
            var indexByteOffset = (nint)(indexOffset * sizeof(uint));
            var indexByteCount = mesh.IndexData.Length * sizeof(uint);
            GL.NamedBufferSubData((int)phase3Buffers.IndexBuffer, (IntPtr)indexByteOffset, indexByteCount, mesh.IndexData);
        }
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
        Log.Info($"ChunkStreamingManager: Renderer refreshed after {reason} (ready={readyChunks.Count}, faces={totalFaces}, vertices={totalVerticesInBuffer}, indices={totalIndicesInBuffer})");
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

        DisposeHeightCacheStatsReadbackBuffer();

        if (heightCacheStatsBuffer != 0)
        {
            GL.DeleteBuffer(heightCacheStatsBuffer);
        }
        GL.CreateBuffers(1, out heightCacheStatsBuffer);
        GL.NamedBufferStorage(heightCacheStatsBuffer, HeightCacheStatsByteSize, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, heightCacheStatsBuffer, -1, "height_cache_stats_ssbo");

        GL.CreateBuffers(1, out heightCacheStatsReadbackBuffer);
        GL.NamedBufferStorage(heightCacheStatsReadbackBuffer, HeightCacheStatsByteSize, IntPtr.Zero,
            BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, heightCacheStatsReadbackBuffer, -1, "height_cache_stats_readback_ssbo");

        heightCacheStatsReadbackPtr = GL.MapNamedBufferRange(heightCacheStatsReadbackBuffer, IntPtr.Zero, HeightCacheStatsByteSize,
            BufferAccessMask.MapReadBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit);
        if (heightCacheStatsReadbackPtr == IntPtr.Zero)
        {
            Log.Warn("ChunkStreamingManager: Failed to map height cache stats readback buffer; warnings may persist.");
        }

        ResetHeightCacheShaderCountersCpu();
        ResetHeightCacheStatsBuffer();
    }

    private void ResetHeightCacheShaderCountersCpu()
    {
        heightCacheShaderHitsTotal = 0;
        heightCacheShaderMissesTotal = 0;
        heightCacheShaderHitsAtLastLog = 0;
        heightCacheShaderMissesAtLastLog = 0;
    }

    private void DisposeHeightCacheStatsReadbackBuffer()
    {
        if (heightCacheStatsReadbackPtr != IntPtr.Zero && heightCacheStatsReadbackBuffer != 0)
        {
            GL.UnmapNamedBuffer(heightCacheStatsReadbackBuffer);
            heightCacheStatsReadbackPtr = IntPtr.Zero;
        }

        if (heightCacheStatsReadbackBuffer != 0)
        {
            GL.DeleteBuffer(heightCacheStatsReadbackBuffer);
            heightCacheStatsReadbackBuffer = 0;
        }
    }

    private void ResetHeightCacheStatsBuffer()
    {
        if (heightCacheStatsBuffer == 0)
            return;

        var zeros = new uint[HeightCacheStatsWordCount];
        GL.NamedBufferSubData(heightCacheStatsBuffer, IntPtr.Zero, HeightCacheStatsByteSize, zeros);

        if (heightCacheStatsReadbackPtr != IntPtr.Zero)
        {
            unsafe
            {
                var ptr = (uint*)heightCacheStatsReadbackPtr;
                for (var i = 0; i < HeightCacheStatsWordCount; i++)
                {
                    ptr[i] = 0u;
                }
            }
        }
    }

    private void AccumulateHeightCacheShaderCounters()
    {
        if (heightCacheStatsBuffer == 0 || heightCacheStatsReadbackBuffer == 0 || heightCacheStatsReadbackPtr == IntPtr.Zero)
            return;

        GL.CopyNamedBufferSubData((int)heightCacheStatsBuffer, (int)heightCacheStatsReadbackBuffer, IntPtr.Zero, IntPtr.Zero, HeightCacheStatsByteSize);
        GL.MemoryBarrier(MemoryBarrierFlags.ClientMappedBufferBarrierBit);

        unsafe
        {
            var counters = (uint*)heightCacheStatsReadbackPtr;
            heightCacheShaderHitsTotal += counters[0];
            heightCacheShaderMissesTotal += counters[1];
        }
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
        // Limit to one CPU batch per frame to avoid long stalls
        if (totalBatchesInPipeline >= 1)
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

        var batchSet = batchIndices.ToHashSet();
        var batchPlaceholderMasks = new byte[batchIndices.Count];
        for (var i = 0; i < batchIndices.Count; i++)
        {
            var idx = batchIndices[i];
            var mask = ComputePlaceholderMask(idx, batchSet);
            batchPlaceholderMasks[i] = mask;

            if (mask != 0)
            {
                placeholderMasksInFlight[idx] = mask;
            }
            else
            {
                placeholderMasksInFlight.Remove(idx);
            }
        }

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

        totalBatchesInPipeline = 1;
        try
        {
            for (var i = 0; i < batchIndices.Count; i++)
            {
                GenerateChunkCpu(batchIndices[i]);
            }

            Log.Info($"ChunkStreamingManager: Generated batch of {batchIndices.Count} chunks on CPU (frame {currentFrame})");
        }
        finally
        {
            totalBatchesInPipeline = 0;
        }
    }

    private void PollCompletedBatches()
    {
        // GPU pipeline removed; CPU generation happens synchronously within SubmitPendingBatches.
    }

    private void GenerateChunkCpu(int chunkIdx)
    {
        ChunkVoxelDataCache.ChunkVoxelBuffer? writable = null;
        try
        {
            writable = chunkVoxelCache.RentWritable(chunkIdx);
            var descriptorEdits = BuildDescriptorEdits(chunkIdx);
            var result = cpuTerrainGenerator.GenerateChunk(chunkIdx, writable.Span, descriptorEdits);
            chunkVoxelCache.Store(writable);
            writable = null;

            ApplyCollisionResults(chunkIdx, result);
            ScheduleCpuMeshing(chunkIdx);
        }
        catch (Exception ex)
        {
            writable?.Dispose();
            Log.Error($"ChunkStreamingManager: CPU terrain generation failed for chunk {chunkIdx}: {ex.Message}");
        }
    }

    private IReadOnlyDictionary<int, BlockDescriptor>? BuildDescriptorEdits(int chunkIdx)
    {
        if (!chunkEdits.TryGetValue(chunkIdx, out var edits) || edits.Count == 0)
        {
            return null;
        }

        var descriptorMap = new Dictionary<int, BlockDescriptor>(edits.Count);
        foreach (var entry in edits)
        {
            descriptorMap[entry.Key] = ConvertBlockType(entry.Value);
        }

        return descriptorMap;
    }

    private static BlockDescriptor ConvertBlockType(BlockType blockType) => blockType switch
    {
        BlockType.None => BlockDescriptor.Air,
        BlockType.WaterLevel => BlockDescriptor.Water,
        BlockType.Sand => BlockDescriptor.ShoreLine,
        BlockType.Grass => BlockDescriptor.Surface,
        BlockType.GrassDirt => BlockDescriptor.Surface,
        BlockType.Dirt => BlockDescriptor.Subsurface,
        BlockType.Gravel => BlockDescriptor.Subsurface,
        BlockType.Rock => BlockDescriptor.DeepSubsurface,
        BlockType.BedRock => BlockDescriptor.DeepSubsurface,
        BlockType.Snow => BlockDescriptor.Surface,
        _ => BlockDescriptor.DeepSubsurface
    };

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

    private void UploadHeightCacheFromSpans(int chunkIdx, int[] spanPairs, byte[] spanCounts, byte[] spanTypes)
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

                    if (spanTypes[typeBaseIdx + s] == (byte)BlockDescriptor.Water)
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
        pendingSeamRefresh.Remove(chunkIndex);
        lastSeamRefreshFrame.Remove(chunkIndex);
        lastUploadedPlaceholderMasks.Remove(chunkIndex);

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
        
        // Calculate target based on current load distance
        var targetChunks = (2 * LoadDistance + 1) * (2 * LoadDistance + 1);
        
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
            blockType = BlockType.WaterLevel;
        }

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
    public void InitializeGpuGeneration(int seed, int maxChunks = 0)
    {
        generationSeed = seed;
        // generationTestMode = testMode; // Removed

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
            GL.CreateBuffers(2, placeholderMaskBuffers);
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
                GL.DeleteBuffers(2, placeholderMaskBuffers);
                GL.DeleteBuffer(compactionChunkIndicesBuffer);
                GL.DeleteBuffer(cullingChunkIndicesBuffer);
                GL.DeleteBuffer(editBuffer);
                GL.DeleteBuffer(worldEditsBuffer);

                GL.CreateBuffers(2, chunkIndicesBuffers);
                GL.CreateBuffers(2, voxelDataBuffers);
                GL.CreateBuffers(2, columnHeightsBuffers);
                GL.CreateBuffers(2, columnMetaBuffers);
                GL.CreateBuffers(2, columnSpansBuffers);
                GL.CreateBuffers(2, placeholderMaskBuffers);
                GL.CreateBuffers(1, out compactionChunkIndicesBuffer);
                GL.CreateBuffers(1, out cullingChunkIndicesBuffer);
                GL.CreateBuffers(1, out editBuffer);
                GL.CreateBuffers(1, out worldEditsBuffer);
            }

            // NOTE: Using int (not uint) to match C# int[] arrays used throughout the codebase
            for (var i = 0; i < 2; i++)
            {
                GL.NamedBufferStorage(chunkIndicesBuffers[i], maxChunks * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                // UNPACKED Voxel Data: 1 voxel per uint (32 bits per voxel)
                // Was: maxChunks * VoxelHelper.PackedChunkVoxelCount * sizeof(uint)
                // Now: maxChunks * VoxelHelper.ChunkVoxelCount * sizeof(uint)
                // Note: VoxelHelper.PackedChunkVoxelCount is now equal to ChunkVoxelCount in VoxelHelper.cs
                GL.NamedBufferStorage(voxelDataBuffers[i], maxChunks * VoxelHelper.ChunkVoxelCount * sizeof(uint), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                GL.NamedBufferStorage(columnHeightsBuffers[i], maxChunks * VoxelHelper.ChunkSideSizeSquare * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
                GL.NamedBufferStorage(columnMetaBuffers[i], maxChunks * VoxelHelper.ChunkSideSizeSquare * sizeof(uint) * 4, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                // 68 bytes per column (struct ColumnSpans { uint count; uint spans[16]; })
                GL.NamedBufferStorage(columnSpansBuffers[i], maxChunks * VoxelHelper.ChunkSideSizeSquare * 68, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.ClientStorageBit);
                GL.NamedBufferStorage(placeholderMaskBuffers[i], maxChunks * sizeof(uint), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
                GL.ObjectLabel(ObjectLabelIdentifier.Buffer, placeholderMaskBuffers[i], -1, "placeholder_masks_ssbo");
            }

            InitializeVoxelReadbackBuffers(maxChunks);

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

            InitializeHeightCache(maxChunks);
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
            terrainConfig.Save(configPath);
            Log.Info($"Saved default TerrainConfig to {configPath}");
        }

        terrainConfig.Seed = seed;
        cpuTerrainGenerator.UpdateConfig(terrainConfig);

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
            lightShader = new Shader("Shaders/compute-light.comp", ShaderType.ComputeShader);
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
            // generationShader.SetUInt("uSeed", (uint)seed); // Removed
            generationShader.SetUInt("uWorldChunksXZ", (uint)VoxelHelper.WorldChunksXZ);
            // generationShader.SetInt("uTestMode", testMode ? 1 : 0); // Removed
            // Skip uElevOffset and uElevScale - they're not used in shader anymore (hardcoded in height01At)
            Log.Info("Shader uniforms set successfully");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to set shader uniforms: {ex.Message}");
            throw;
        }

        Log.CheckGlError();
        Log.Info($"GPU terrain generation initialized");

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

        // M5: Reload biome textures if renderer is active (hot reload)
        terrainRenderer?.LoadBiomeTextures(terrainConfig);
    }

    /// <summary>
    /// Dispatch GPU generation for a batch of chunks (ASYNC with fence)
    /// Phase 2: Returns a fence that signals when generation completes
    /// </summary>
    public IntPtr DispatchGenerationAsync(int[] chunkIndices, int bufferIndex, uint[] placeholderMasks)
    {
        if (generationShader == null || chunkIndices.Length == 0)
            return IntPtr.Zero;

        // Upload chunk indices to the selected buffer
        GL.NamedBufferSubData(chunkIndicesBuffers[bufferIndex], IntPtr.Zero, chunkIndices.Length * sizeof(int), chunkIndices);
        if (placeholderMaskBuffers[bufferIndex] != 0 && placeholderMasks != null && placeholderMasks.Length > 0)
        {
            GL.NamedBufferSubData(placeholderMaskBuffers[bufferIndex], IntPtr.Zero, placeholderMasks.Length * sizeof(uint), placeholderMasks);
        }

        // Bind SSBOs with correct bindings matching shader
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 9, chunkIndicesBuffers[bufferIndex]);
        if (placeholderMaskBuffers[bufferIndex] != 0)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.PLACEHOLDER_MASKS, placeholderMaskBuffers[bufferIndex]);
        }
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, voxelDataBuffers[bufferIndex]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, columnHeightsBuffers[bufferIndex]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnMetaBuffers[bufferIndex]);

        // Bind Terrain Params (M1/M2) - Binding 10 as per terrain-common.glsl
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, terrainParamsSSBO);

        // Bind Textures
        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture1D, heightSplineTexture);
        GL.ActiveTexture(TextureUnit.Texture7);
        GL.BindTexture(TextureTarget.Texture2D, biomeLutTexture);

        // Set ALL uniforms every dispatch
        generationShader.Use();
        // generationShader.SetInt("uHeightSpline", 6); // Using layout(binding=6)
        // generationShader.SetInt("uBiomeLUT", 7);     // Using layout(binding=7)
        generationShader.SetUInt("uChunkCount", (uint)chunkIndices.Length);
        generationShader.SetUInt("uWorldChunksXZ", (uint)VoxelHelper.WorldChunksXZ);
        // generationShader.SetUInt("uSeed", (uint)generationSeed); // Removed
        // generationShader.SetInt("uTestMode", generationTestMode ? 1 : 0); // Removed

        // Dispatch: one work-group per chunk Y-slice
        // Phase 5.6 FIX: compute-generate now loops over Y via workgroups (parallel execution)
        // Dispatch (chunkCount, 384, 1)
        GL.DispatchCompute(chunkIndices.Length, VoxelHelper.ChunkYSize, 1);

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

        // Phase 2.1: Calculate Lighting
        if (lightShader != null)
        {
            lightShader.Use();
            lightShader.SetUInt("uChunkCount", (uint)chunkIndices.Length);
            lightShader.SetUInt("uWorldChunksXZ", (uint)VoxelHelper.WorldChunksXZ);

            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 9, chunkIndicesBuffers[bufferIndex]);
            if (placeholderMaskBuffers[bufferIndex] != 0)
            {
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.PLACEHOLDER_MASKS, placeholderMaskBuffers[bufferIndex]);
            }
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, voxelDataBuffers[bufferIndex]);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, terrainParamsSSBO);

            GL.ActiveTexture(TextureUnit.Texture6);
            GL.BindTexture(TextureTarget.Texture1D, heightSplineTexture);
            GL.ActiveTexture(TextureUnit.Texture7);
            GL.BindTexture(TextureTarget.Texture2D, biomeLutTexture);

            // Dispatch: one work-group per chunk (16x1x16 threads)
            // The shader handles the Y-loop internally for column initialization
            GL.DispatchCompute(chunkIndices.Length, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
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
        var zeroMasks = new uint[chunkIndices.Length];
        var fence = DispatchGenerationAsync(chunkIndices, 0, zeroMasks);
        if (fence != IntPtr.Zero)
        {
            // Wait for completion (blocking)
            GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, ulong.MaxValue);
            GL.DeleteSync(fence);
        }
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

        placeholderMasksInFlight.Clear();
        pendingSeamRefresh.Clear();
        chunkVoxelCache.Clear();
        ClearPendingCpuMeshingQueue();

        phase3Buffers = new Phase3BufferManager();
        phase3Buffers.AllocateBuffers(maxChunksPerBatch);

        var maxActiveChunks = CalculateMaxViewChunks();
        phase3Buffers.ResizeIndirectDrawBuffer((uint)maxActiveChunks);

        Log.Info($"Phase 3 buffers initialized for CPU meshing (batch cap {maxChunksPerBatch}, indirect capacity {maxActiveChunks})");
    }


    /// <summary>
    /// Initialize Phase 4 GPU rendering
    /// </summary>
    public void InitializePhase4()
    {
        terrainRenderer = new VoxelTerrainRenderer();
        // M5: Load biome textures
        terrainRenderer.LoadBiomeTextures(terrainConfig);
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

        // Create dedicated chunk indices buffer for culling
        if (cullingChunkIndicesBuffer == 0 || chunkIndicesBufferCapacity < maxChunks)
        {
            if (cullingChunkIndicesBuffer != 0) GL.DeleteBuffer(cullingChunkIndicesBuffer);
            
            GL.CreateBuffers(1, out cullingChunkIndicesBuffer);
            GL.NamedBufferStorage(cullingChunkIndicesBuffer, maxChunks * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, cullingChunkIndicesBuffer, -1, "culling_chunk_indices_ssbo");
            
            chunkIndicesBufferCapacity = maxChunks;
        }

        // Create stats buffer (fixed size)
        if (cullingStatsBuffer == 0)
        {
            GL.CreateBuffers(1, out cullingStatsBuffer);
            GL.NamedBufferStorage(cullingStatsBuffer, 5 * sizeof(uint), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, cullingStatsBuffer, -1, "culling_stats_ssbo");
        }

        // Create readback buffer
        if (cullingReadbackBuffer == 0)
        {
            GL.CreateBuffers(1, out cullingReadbackBuffer);
            GL.NamedBufferStorage(cullingReadbackBuffer, 5 * sizeof(uint), IntPtr.Zero,
                BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.ClientStorageBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, cullingReadbackBuffer, -1, "culling_readback_ssbo");
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

        // 1. Check if previous readback is ready (Async Readback)
        if (cullingFence != IntPtr.Zero)
        {
            var status = GL.ClientWaitSync(cullingFence, 0, 0);
            if (status is WaitSyncStatus.ConditionSatisfied or WaitSyncStatus.AlreadySignaled)
            {
                // Read back stats from readback buffer (non-blocking now)
                var stats = new uint[5];
                GL.GetNamedBufferSubData(cullingReadbackBuffer, IntPtr.Zero, stats.Length * sizeof(uint), stats);
                
                StatTotalIndices = (int)stats[0];
                StatVisibleIndices = (int)stats[1];
                StatVisibleChunks = (int)stats[2];
                StatFrustumCulledChunks = (int)stats[3];
                // StatOccludedChunks = (int)stats[4]; // Removed
                
                GL.DeleteSync(cullingFence);
                cullingFence = IntPtr.Zero;
            }
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
        
        // Bind Stats Buffer (Binding 5)
        // Clear stats first
        GL.ClearNamedBufferData(cullingStatsBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt, IntPtr.Zero);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, cullingStatsBuffer);

        // Set uniforms
        frustumShader.Use();
        GL.Uniform1(frustumShader.GetUniformLocation("chunkCount"), chunkIndices.Length);
        GL.Uniform1(frustumShader.GetUniformLocation("chunkSideSize"), VoxelHelper.ChunkSideSize);
        GL.Uniform1(frustumShader.GetUniformLocation("chunkYSize"), VoxelHelper.ChunkYSize);
        GL.Uniform1(frustumShader.GetUniformLocation("worldChunksXZ"), VoxelHelper.WorldChunksXZ);
        
        // Dispatch: 64 threads per workgroup, ceil(chunkCount / 64) workgroups
        var workGroups = (chunkIndices.Length + 63) / 64;
        GL.DispatchCompute(workGroups, 1, 1);

        // Wait for completion of compute (for the copy)
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);

        // Copy stats to readback buffer for next frame
        GL.CopyNamedBufferSubData(cullingStatsBuffer, cullingReadbackBuffer, IntPtr.Zero, IntPtr.Zero, 5 * sizeof(uint));
        
        // Create fence for next frame
        if (cullingFence != IntPtr.Zero) GL.DeleteSync(cullingFence);
        cullingFence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);

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
                        var edits = new Dictionary<int, BlockType>(count);
                        for (var k = 0; k < count; k++)
                        {
                            var voxelIdx = reader.ReadInt32();
                            var type = (BlockType)reader.ReadByte();
                            edits[voxelIdx] = type;
                        }
                        chunkEdits[chunkIdx] = edits;
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
        if (cullingStatsBuffer != 0) GL.DeleteBuffer(cullingStatsBuffer);
        if (cullingReadbackBuffer != 0) GL.DeleteBuffer(cullingReadbackBuffer);
        if (cullingFence != IntPtr.Zero) GL.DeleteSync(cullingFence);
        if (cullingChunkIndicesBuffer != 0) GL.DeleteBuffer(cullingChunkIndicesBuffer);

        // Cleanup Terrain Params (M1)
        if (terrainParamsSSBO != 0) GL.DeleteBuffer(terrainParamsSSBO);
        if (heightSplineTexture != 0) GL.DeleteTexture(heightSplineTexture);
        if (biomeLutTexture != 0) GL.DeleteTexture(biomeLutTexture);
        if (heightCacheBuffer != 0) GL.DeleteBuffer(heightCacheBuffer);
        if (heightCacheSlotBuffer != 0) GL.DeleteBuffer(heightCacheSlotBuffer);
        DisposeHeightCacheStatsReadbackBuffer();
        if (heightCacheStatsBuffer != 0) GL.DeleteBuffer(heightCacheStatsBuffer);
        if (placeholderMaskBuffers[0] != 0 || placeholderMaskBuffers[1] != 0)
            GL.DeleteBuffers(2, placeholderMaskBuffers);

        // terrainRenderer is now a SceneNode and will be cleaned up by the scene graph
        phase3Buffers?.Dispose();
        bufferAllocator?.Dispose();
        chunkVoxelCache.Dispose();
        ClearPendingCpuMeshingQueue();
        cpuMeshingJobs.Dispose();
        DisposeVoxelReadbackBuffers();

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

}
