using OpenRender;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.InteropServices;

namespace SpyroGame.World;

/// <summary>
/// Manages GPU buffer allocation and initialization for Phase 3 (Visibility & Compaction).
/// Implements explicit initialization strategy to avoid garbage data issues.
/// Phase 5: Adds incremental updates and buffer reuse for streaming.
/// </summary>
public class Phase3BufferManager : IDisposable
{
    // Buffer handles
    private uint visMaskBuffer;
    private uint countBuffer;
    private uint offsetBuffer;
    private uint vertexBuffer;
    private uint indexBuffer;
    private uint atomicCounterBuffer;
    private uint indirectDrawBuffer;
    private uint perChunkEmitBuffer;
    private uint scanTotalsBuffer;
    private uint opaqueCountsBuffer;
    private uint waterEmitBuffer;

    // Buffer sizes
    private int maxChunks;
    private int maxVoxels;
    private int maxVertices;
    private int maxIndices;

    // Phase 5: Buffer reuse tracking for BOTH vertices and indices
    private struct BufferRegion
    {
        public uint Offset;
        public uint Size;
    }
    private List<BufferRegion> freeVertexRegions = [];
    private List<BufferRegion> freeIndexRegions = [];
    private readonly Queue<int> freeCommandSlots = new();
    private int nextCommandSlot = 0;
    private uint currentVertexBufferEnd = 0;
    private uint currentIndexBufferEnd = 0;
    private uint vertexBufferCapacity = 0;
    private uint indexBufferCapacity = 0;
    private uint commandSlotCapacity = 0;

    // Vertex stride: PackedPos+Data (4 bytes each) = 8 bytes
    private const int VERTEX_STRIDE = 8;

    public uint VisMaskBuffer => visMaskBuffer;
    public uint CountBuffer => countBuffer;
    public uint OffsetBuffer => offsetBuffer;
    public uint VertexBuffer => vertexBuffer;
    public uint IndexBuffer => indexBuffer;
    public uint AtomicCounterBuffer => atomicCounterBuffer;
    public uint IndirectDrawBuffer => indirectDrawBuffer;
    public uint PerChunkEmitBuffer => perChunkEmitBuffer;
    public uint ScanTotalsBuffer => scanTotalsBuffer;
    public uint OpaqueCountsBuffer => opaqueCountsBuffer;
    public uint WaterEmitBuffer => waterEmitBuffer;
    public uint CommandSlotBuffer => commandSlotBuffer;
    public uint ChunkInfoBuffer => chunkInfoBuffer;

    public int MaxChunks => maxChunks;
    public int MaxVertices => maxVertices;
    public int MaxIndices => maxIndices;

    // Public accessors for buffer reuse
    public uint VertexBufferCapacity => vertexBufferCapacity;
    public uint IndexBufferCapacity => indexBufferCapacity;
    public uint CommandSlotCapacity => commandSlotCapacity;
    public uint CurrentVertexBufferEnd => currentVertexBufferEnd;
    public uint CurrentIndexBufferEnd => currentIndexBufferEnd;
    public int FreeVertexRegionCount => freeVertexRegions.Count;
    public int FreeIndexRegionCount => freeIndexRegions.Count;
    public int FreeCommandSlotCount => freeCommandSlots.Count;

    private uint commandSlotBuffer;
    private uint chunkInfoBuffer;

    private const int UploadChannelCount = 4; // Increased from 2 for better async upload handling

    private struct MeshUploadChannel
    {
        public uint VertexBuffer;
        public uint IndexBuffer;
        public IntPtr VertexPtr;
        public IntPtr IndexPtr;
        public IntPtr Fence;
    }

    private readonly MeshUploadChannel[] uploadChannels = new MeshUploadChannel[UploadChannelCount];
    private int nextUploadChannel;
    private int maxChunkVertexBytes;
    private int maxChunkIndexBytes;

    /// <summary>
    /// Allocate all Phase 3 buffers with explicit initialization.
    /// </summary>
    public void AllocateBuffers(int maxChunksInBatch)
    {
        maxChunks = maxChunksInBatch;
        maxVoxels = maxChunks * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize;

        // Worst-case: 6 visible faces per voxel (isolated voxel)
        var worstCaseFaces = maxVoxels * 6;
        maxVertices = worstCaseFaces * 4;
        maxIndices = worstCaseFaces * 6;
        
        // Initialize buffer tracking
        vertexBufferCapacity = (uint)maxVertices;
        indexBufferCapacity = (uint)maxIndices;
        currentVertexBufferEnd = 0;
        currentIndexBufferEnd = 0;
        freeVertexRegions.Clear();
        freeIndexRegions.Clear();
        freeCommandSlots.Clear();

        // Calculate memory usage
        var vertexMB = (long)maxVertices * VERTEX_STRIDE / 1024f / 1024f;
        var indexMB = (long)maxIndices * sizeof(uint) / 1024f / 1024f;
        var totalMB = vertexMB + indexMB;

        Log.Info($"Phase3BufferManager: Allocating buffers for {maxChunks} chunks");
        Log.Info($"  Max voxels: {maxVoxels:N0}");
        Log.Info($"  Worst-case faces: {worstCaseFaces:N0}");
        Log.Info($"  Max vertices: {maxVertices:N0} ({vertexMB:F2} MB)");
        Log.Info($"  Max indices: {maxIndices:N0} ({indexMB:F2} MB)");
        Log.Info($"  Total mesh memory: {totalMB:F2} MB");

        // Visibility mask (1 uint per 4 voxels, 8 bits per voxel)
        // We pack 4 voxels into 1 uint (8 bits each) to save 4x memory bandwidth.
        GL.CreateBuffers(1, out visMaskBuffer);
        GL.NamedBufferStorage(visMaskBuffer, maxChunks * (VoxelHelper.PackedChunkVoxelCount / 4) * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(visMaskBuffer, maxChunks * (VoxelHelper.PackedChunkVoxelCount / 4) * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, visMaskBuffer, -1, "vis_mask_ssbo");

        // Visible counts (1 uint per chunk)
        GL.CreateBuffers(1, out countBuffer);
        GL.NamedBufferStorage(countBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.ClientStorageBit);
        ClearBufferUInt(countBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, countBuffer, -1, "count_visible_ssbo");

        // Per-chunk face emission counters (reset each compaction)
        GL.CreateBuffers(1, out perChunkEmitBuffer);
        GL.NamedBufferStorage(perChunkEmitBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(perChunkEmitBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, perChunkEmitBuffer, -1, "per_chunk_face_emit_ssbo");

        // Base offsets (1 uint per chunk)
        GL.CreateBuffers(1, out offsetBuffer);
        GL.NamedBufferStorage(offsetBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.ClientStorageBit);
        ClearBufferUInt(offsetBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, offsetBuffer, -1, "offset_base_ssbo");

        // Compacted vertices
        GL.CreateBuffers(1, out vertexBuffer);
        GL.NamedBufferStorage(vertexBuffer, (nint)maxVertices * VERTEX_STRIDE, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vertexBuffer, -1, "compact_vertices_vbo");

        // Per-chunk index buffer (one contiguous buffer for all chunks)
        GL.CreateBuffers(1, out indexBuffer);
        GL.NamedBufferStorage(indexBuffer, (nint)maxIndices * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, indexBuffer, -1, "per_chunk_indices_ibo");

        // Multi-draw indirect command buffer
        // 2 commands per slot (Opaque + Transparent)
        GL.CreateBuffers(1, out indirectDrawBuffer);
        var indirectSize = maxChunks * 2 * 5 * sizeof(uint);
        GL.NamedBufferStorage(indirectDrawBuffer, indirectSize, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, indirectDrawBuffer, -1, "indirect_draw_commands");
        commandSlotCapacity = (uint)maxChunks;

        // Command Slot Buffer (to pass slots to shader)
        GL.CreateBuffers(1, out commandSlotBuffer);
        if (commandSlotBuffer == 0) Log.Error("Phase3BufferManager: Failed to create commandSlotBuffer!");
        GL.NamedBufferStorage(commandSlotBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, commandSlotBuffer, -1, "command_slot_ssbo");

        // Chunk Info Buffer (maps slot -> chunk index/info)
        GL.CreateBuffers(1, out chunkInfoBuffer);
        GL.NamedBufferStorage(chunkInfoBuffer, maxChunks * sizeof(int), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(chunkInfoBuffer, maxChunks * sizeof(int), 0xFFFFFFFF);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, chunkInfoBuffer, -1, "chunk_info_ssbo");

        // Atomic counters (3 uints: vertex counter, index counter, face counter)
        GL.CreateBuffers(1, out atomicCounterBuffer);
        GL.NamedBufferStorage(atomicCounterBuffer, 3 * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(atomicCounterBuffer, 3 * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, atomicCounterBuffer, -1, "atomic_counters_ssbo");

        // Totals buffer (2 uints)
        GL.CreateBuffers(1, out scanTotalsBuffer);
        GL.NamedBufferStorage(scanTotalsBuffer, 2 * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.ClientStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, scanTotalsBuffer, -1, "scan_totals_ssbo");

        // Opaque Counts (1 uint per chunk)
        GL.CreateBuffers(1, out opaqueCountsBuffer);
        GL.NamedBufferStorage(opaqueCountsBuffer, maxChunks * sizeof(uint), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(opaqueCountsBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, opaqueCountsBuffer, -1, "opaque_counts_ssbo");

        // Water Emit Counter (1 uint per chunk)
        GL.CreateBuffers(1, out waterEmitBuffer);
        GL.NamedBufferStorage(waterEmitBuffer, maxChunks * sizeof(uint), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(waterEmitBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, waterEmitBuffer, -1, "water_emit_ssbo");

        InitializeUploadChannels();

        Log.CheckGlError();
        Log.Info("Phase3BufferManager: All buffers allocated");
    }

    /// <summary>
    /// Clear buffer of uints to known value.
    /// </summary>
    private static void ClearBufferUInt(uint buffer, int sizeBytes, uint value)
    {
        var count = sizeBytes / sizeof(uint);
        var data = new uint[count];
        Array.Fill(data, value);
        GL.NamedBufferSubData(buffer, IntPtr.Zero, sizeBytes, data);
    }

    /// <summary>
    /// Reset atomic counters to zero before compaction pass.
    /// </summary>
    public void ResetAtomicCounters()
    {
        var zeros = new uint[3] { 0, 0, 0 };
        GL.NamedBufferSubData(atomicCounterBuffer, IntPtr.Zero, 3 * sizeof(uint), zeros);
    }

    /// <summary>
    /// Read back actual vertex/index/face counts from atomic counters.
    /// </summary>
    public (uint vertexCount, uint indexCount, uint faceCount) ReadAtomicCounters()
    {
        var counts = new uint[3];
        GL.GetNamedBufferSubData(atomicCounterBuffer, IntPtr.Zero, 3 * sizeof(uint), counts);
        return (counts[0], counts[1], counts[2]);
    }

    public void Dispose()
    {
        DisposeUploadChannels();

        if (visMaskBuffer != 0) GL.DeleteBuffer(visMaskBuffer);
        if (countBuffer != 0) GL.DeleteBuffer(countBuffer);
        if (offsetBuffer != 0) GL.DeleteBuffer(offsetBuffer);
        if (vertexBuffer != 0) GL.DeleteBuffer(vertexBuffer);
        if (indexBuffer != 0) GL.DeleteBuffer(indexBuffer);
        if (indirectDrawBuffer != 0) GL.DeleteBuffer(indirectDrawBuffer);
        if (atomicCounterBuffer != 0) GL.DeleteBuffer(atomicCounterBuffer);
        if (perChunkEmitBuffer != 0) GL.DeleteBuffer(perChunkEmitBuffer);
        if (scanTotalsBuffer != 0) GL.DeleteBuffer(scanTotalsBuffer);
        
        visMaskBuffer = countBuffer = offsetBuffer = 0;
        vertexBuffer = indexBuffer = indirectDrawBuffer = atomicCounterBuffer = 0;
        perChunkEmitBuffer = 0;
        scanTotalsBuffer = 0;

        if (chunkInfoBuffer != 0) GL.DeleteBuffer(chunkInfoBuffer);
        
        Log.Info("Phase3BufferManager: Disposed");
    }

    /// <summary>
    /// Get total GPU memory allocated for Phase 3 buffers
    /// </summary>
    public long GetAllocatedBytes()
    {
        long total = 0;
        
        // Visibility mask: 1 uint per 4 voxels
        total += maxChunks * (VoxelHelper.PackedChunkVoxelCount / 4) * sizeof(uint);
        
        // Count buffer: 1 uint per chunk
        total += maxChunks * sizeof(uint);
        
        // Offset buffer: 1 uint per chunk
        total += maxChunks * sizeof(uint);
        
        // Vertex buffer: worst-case vertices * stride
        total += (long)maxVertices * VERTEX_STRIDE;
        
        // Index buffer: worst-case indices * sizeof(uint)
        total += (long)maxIndices * sizeof(uint);
        
        // Indirect draw buffer: max chunks * 5 uints per command
        total += (long)maxChunks * 5 * sizeof(uint);
        
        // Atomic counter buffer: 3 uints
        total += 3 * sizeof(uint);
        
        // Per-chunk emit buffer: 1 uint per chunk
        total += maxChunks * sizeof(uint);
        
        return total;
    }

    private void InitializeUploadChannels()
    {
        maxChunkVertexBytes = Math.Max(VoxelHelper.ChunkVoxelCount * 6 * 4 * VERTEX_STRIDE, 256);
        maxChunkIndexBytes = Math.Max(VoxelHelper.ChunkVoxelCount * 6 * 6 * sizeof(uint), 256);

        for (var i = 0; i < uploadChannels.Length; i++)
        {
            ref var channel = ref uploadChannels[i];

            if (channel.VertexBuffer != 0)
            {
                continue;
            }

            GL.CreateBuffers(1, out channel.VertexBuffer);
            GL.NamedBufferStorage(channel.VertexBuffer, maxChunkVertexBytes, IntPtr.Zero,
                BufferStorageFlags.MapWriteBit |
                BufferStorageFlags.MapPersistentBit |
                BufferStorageFlags.MapCoherentBit |
                BufferStorageFlags.DynamicStorageBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, channel.VertexBuffer, -1, $"mesh_upload_vertex_staging_{i}");

            GL.CreateBuffers(1, out channel.IndexBuffer);
            GL.NamedBufferStorage(channel.IndexBuffer, maxChunkIndexBytes, IntPtr.Zero,
                BufferStorageFlags.MapWriteBit |
                BufferStorageFlags.MapPersistentBit |
                BufferStorageFlags.MapCoherentBit |
                BufferStorageFlags.DynamicStorageBit);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, channel.IndexBuffer, -1, $"mesh_upload_index_staging_{i}");

            channel.VertexPtr = GL.MapNamedBufferRange(channel.VertexBuffer, IntPtr.Zero, maxChunkVertexBytes,
                BufferAccessMask.MapWriteBit |
                BufferAccessMask.MapPersistentBit |
                BufferAccessMask.MapCoherentBit);
            channel.IndexPtr = GL.MapNamedBufferRange(channel.IndexBuffer, IntPtr.Zero, maxChunkIndexBytes,
                BufferAccessMask.MapWriteBit |
                BufferAccessMask.MapPersistentBit |
                BufferAccessMask.MapCoherentBit);

            if (channel.VertexPtr == IntPtr.Zero || channel.IndexPtr == IntPtr.Zero)
            {
                Log.Warn($"Phase3BufferManager: failed to map staging buffer {i}");
            }

            channel.Fence = IntPtr.Zero;
        }

        nextUploadChannel = 0;
    }

    private void DisposeUploadChannels()
    {
        for (var i = 0; i < uploadChannels.Length; i++)
        {
            ref var channel = ref uploadChannels[i];

            if (channel.Fence != IntPtr.Zero)
            {
                GL.DeleteSync(channel.Fence);
                channel.Fence = IntPtr.Zero;
            }

            if (channel.VertexPtr != IntPtr.Zero && channel.VertexBuffer != 0)
            {
                GL.UnmapNamedBuffer(channel.VertexBuffer);
                channel.VertexPtr = IntPtr.Zero;
            }

            if (channel.IndexPtr != IntPtr.Zero && channel.IndexBuffer != 0)
            {
                GL.UnmapNamedBuffer(channel.IndexBuffer);
                channel.IndexPtr = IntPtr.Zero;
            }

            if (channel.VertexBuffer != 0)
            {
                GL.DeleteBuffer(channel.VertexBuffer);
                channel.VertexBuffer = 0;
            }

            if (channel.IndexBuffer != 0)
            {
                GL.DeleteBuffer(channel.IndexBuffer);
                channel.IndexBuffer = 0;
            }
        }
    }

    private int AcquireUploadChannel()
    {
        var channelIndex = nextUploadChannel;
        ref var channel = ref uploadChannels[channelIndex];

        WaitForChannelFence(ref channel);

        nextUploadChannel = (channelIndex + 1) % uploadChannels.Length;
        return channelIndex;
    }

    private void WaitForChannelFence(ref MeshUploadChannel channel)
    {
        if (channel.Fence == IntPtr.Zero)
        {
            return;
        }

        var waitResult = GL.ClientWaitSync(channel.Fence, ClientWaitSyncFlags.SyncFlushCommandsBit, 0);
        while (waitResult == WaitSyncStatus.TimeoutExpired)
        {
            waitResult = GL.ClientWaitSync(channel.Fence, ClientWaitSyncFlags.None, 1_000_000);
        }

        GL.DeleteSync(channel.Fence);
        channel.Fence = IntPtr.Zero;
    }

    private unsafe int CopyToMappedBuffer(IntPtr destination, ReadOnlySpan<uint> source, int capacityBytes, string label)
    {
        if (destination == IntPtr.Zero || source.Length == 0)
        {
            return 0;
        }

        var srcBytes = MemoryMarshal.AsBytes(source);
        var bytesToCopy = Math.Min(srcBytes.Length, capacityBytes);
        if (srcBytes.Length > capacityBytes)
        {
            Log.Warn($"Phase3BufferManager: {label} upload truncated ({srcBytes.Length}B > {capacityBytes}B)");
        }

        var destSpan = new Span<byte>((void*)destination, bytesToCopy);
        srcBytes[..bytesToCopy].CopyTo(destSpan);
        return bytesToCopy;
    }

    public void UploadMeshData(ReadOnlySpan<uint> vertexData, int vertexOffset, ReadOnlySpan<uint> indexData, int indexOffset)
    {
        if ((vertexData.Length == 0 || vertexOffset < 0) && (indexData.Length == 0 || indexOffset < 0))
        {
            return;
        }

        var channelIndex = AcquireUploadChannel();
        ref var channel = ref uploadChannels[channelIndex];
        var issuedCopy = false;

        if (vertexData.Length > 0 && vertexOffset >= 0)
        {
            var vertexByteOffset = (IntPtr)(vertexOffset * VoxelHelper.VERTEX_STRIDE_BYTES);
            var vertexBytes = CopyToMappedBuffer(channel.VertexPtr, vertexData, maxChunkVertexBytes, "vertex");
            if (vertexBytes > 0)
            {
                GL.MemoryBarrier(MemoryBarrierFlags.ClientMappedBufferBarrierBit);
                GL.CopyNamedBufferSubData(channel.VertexBuffer, vertexBuffer, IntPtr.Zero, vertexByteOffset, (nint)vertexBytes);
                issuedCopy = true;
            }
        }

        if (indexData.Length > 0 && indexOffset >= 0)
        {
            var indexByteOffset = (IntPtr)(indexOffset * sizeof(uint));
            var indexBytes = CopyToMappedBuffer(channel.IndexPtr, indexData, maxChunkIndexBytes, "index");
            if (indexBytes > 0)
            {
                GL.MemoryBarrier(MemoryBarrierFlags.ClientMappedBufferBarrierBit);
                GL.CopyNamedBufferSubData(channel.IndexBuffer, indexBuffer, IntPtr.Zero, indexByteOffset, (nint)indexBytes);
                issuedCopy = true;
            }
        }

        if (issuedCopy)
        {
            channel.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        }
    }

    // ========================================================================
    // Incremental Updates & Buffer Reuse
    // ========================================================================

    /// <summary>
    /// Allocate a vertex buffer region, reusing freed space if available
    /// Returns the offset into the vertex buffer where the region starts
    /// </summary>
    public uint AllocateVertexRegion(uint requestedSize)
    {
        if (requestedSize == 0)
            return 0;

        // Try to find a free region that fits
        for (var i = 0; i < freeVertexRegions.Count; i++)
        {
            var region = freeVertexRegions[i];
            if (region.Size >= requestedSize)
            {
                // Use this region
                var offset = region.Offset;

                // Update free region (shrink or remove)
                if (region.Size == requestedSize)
                {
                    freeVertexRegions.RemoveAt(i);
                }
                else
                {
                    freeVertexRegions[i] = new BufferRegion
                    {
                        Offset = region.Offset + requestedSize,
                        Size = region.Size - requestedSize
                    };
                }

                //Log.Debug($"Reused vertex region: offset={offset}, size={requestedSize}");
                return offset;
            }
        }

        // No free region found - allocate at end
        var newOffset = currentVertexBufferEnd;
        currentVertexBufferEnd += requestedSize;

        // Check if we need to resize
        if (currentVertexBufferEnd > vertexBufferCapacity)
        {
            ResizeVertexBuffer(currentVertexBufferEnd * 2);
        }

        Log.Debug($"Allocated new vertex region: offset={newOffset}, size={requestedSize}, end={currentVertexBufferEnd}");
        return newOffset;
    }

    /// <summary>
    /// Allocate an index buffer region, reusing freed space if available
    /// Returns the offset into the index buffer where the region starts
    /// </summary>
    public uint AllocateIndexRegion(uint requestedSize)
    {
        if (requestedSize == 0)
            return 0;

        // Try to find a free region that fits
        for (var i = 0; i < freeIndexRegions.Count; i++)
        {
            var region = freeIndexRegions[i];
            if (region.Size >= requestedSize)
            {
                // Use this region
                var offset = region.Offset;

                // Update free region (shrink or remove)
                if (region.Size == requestedSize)
                {
                    freeIndexRegions.RemoveAt(i);
                }
                else
                {
                    freeIndexRegions[i] = new BufferRegion
                    {
                        Offset = region.Offset + requestedSize,
                        Size = region.Size - requestedSize
                    };
                }

                //Log.Debug($"Reused index region: offset={offset}, size={requestedSize}");
                return offset;
            }
        }

        // No free region found - allocate at end
        var newOffset = currentIndexBufferEnd;
        currentIndexBufferEnd += requestedSize;

        // Check if we need to resize
        if (currentIndexBufferEnd > indexBufferCapacity)
        {
            ResizeIndexBuffer(currentIndexBufferEnd * 2);
        }

        Log.Debug($"Allocated new index region: offset={newOffset}, size={requestedSize}, end={currentIndexBufferEnd}");
        return newOffset;
    }

    /// <summary>
    /// Mark a vertex buffer region as free for reuse
    /// </summary>
    public void FreeVertexRegion(uint baseOffset, uint size)
    {
        if (size == 0)
            return;

        // Add to free list
        freeVertexRegions.Add(new BufferRegion { Offset = baseOffset, Size = size });

        // Sort by offset to enable merging
        freeVertexRegions = [.. freeVertexRegions.OrderBy(r => r.Offset)];

        // Try to merge adjacent free regions
        MergeFreeRegions(ref freeVertexRegions);

        //Log.Debug($"Freed vertex region: offset={baseOffset}, size={size}, freeRegions={freeVertexRegions.Count}");
    }

    /// <summary>
    /// Mark an index buffer region as free for reuse
    /// </summary>
    public void FreeIndexRegion(uint baseOffset, uint size)
    {
        if (size == 0)
            return;

        // Add to free list
        freeIndexRegions.Add(new BufferRegion { Offset = baseOffset, Size = size });

        // Sort by offset to enable merging
        freeIndexRegions = [.. freeIndexRegions.OrderBy(r => r.Offset)];

        // Try to merge adjacent free regions
        MergeFreeRegions(ref freeIndexRegions);

        //Log.Debug($"Freed index region: offset={baseOffset}, size={size}, freeRegions={freeIndexRegions.Count}");
    }

    /// <summary>
    /// Merge adjacent free regions to reduce fragmentation
    /// </summary>
    private static void MergeFreeRegions(ref List<BufferRegion> regions)
    {
        if (regions.Count < 2)
            return;

        var merged = new List<BufferRegion>();
        var current = regions[0];

        for (var i = 1; i < regions.Count; i++)
        {
            var next = regions[i];

            // Check if current and next are adjacent
            if (current.Offset + current.Size == next.Offset)
            {
                // Merge them
                current = new BufferRegion
                {
                    Offset = current.Offset,
                    Size = current.Size + next.Size
                };
            }
            else
            {
                // Not adjacent, keep current and move to next
                merged.Add(current);
                current = next;
            }
        }

        // Add the last region
        merged.Add(current);

        regions = merged;
    }

    /// <summary>
    /// Resize the vertex buffer to accommodate more vertices
    /// This is expensive and should be avoided by pre-allocating enough space
    /// </summary>
    private void ResizeVertexBuffer(uint newCapacity)
    {
        Log.Warn($"Resizing vertex buffer from {vertexBufferCapacity} to {newCapacity} vertices (expensive!)");

        // Create new buffer
        GL.CreateBuffers(1, out uint newBuffer);
        GL.NamedBufferStorage(newBuffer, (nint)newCapacity * VERTEX_STRIDE, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, newBuffer, -1, "compact_vertices_vbo_resized");

        // Copy old data
        GL.CopyNamedBufferSubData(vertexBuffer, newBuffer, IntPtr.Zero, IntPtr.Zero,
            (nint)vertexBufferCapacity * VERTEX_STRIDE);

        // Delete old buffer and replace
        GL.DeleteBuffer(vertexBuffer);
        vertexBuffer = newBuffer;
        vertexBufferCapacity = newCapacity;

        Log.CheckGlError();
    }

    /// <summary>
    /// Resize the index buffer to accommodate more indices (Phase 5.3)
    /// This is expensive and should be avoided by pre-allocating enough space
    /// </summary>
    private void ResizeIndexBuffer(uint newCapacity)
    {
        Log.Warn($"Resizing index buffer from {indexBufferCapacity} to {newCapacity} indices (expensive!)");

        // Create new buffer
        GL.CreateBuffers(1, out uint newBuffer);
        GL.NamedBufferStorage(newBuffer, (nint)newCapacity * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, newBuffer, -1, "per_chunk_indices_ibo_resized");

        // Copy old data
        GL.CopyNamedBufferSubData(indexBuffer, newBuffer, IntPtr.Zero, IntPtr.Zero,
            (nint)indexBufferCapacity * sizeof(uint));

        // Delete old buffer and replace
        GL.DeleteBuffer(indexBuffer);
        indexBuffer = newBuffer;
        indexBufferCapacity = newCapacity;

        Log.CheckGlError();
    }

    /// <summary>
    /// Resize the indirect draw buffer to accommodate more commands (Phase 5.3)
    /// This is called when the number of active chunks exceeds the current capacity
    /// </summary>
    public void ResizeIndirectDrawBuffer(uint newCommandCount)
    {
        // Each slot now holds 2 commands (Opaque + Transparent)
        var newSize = (int)(newCommandCount * 2 * 5 * sizeof(uint)); // 5 uints per command * 2
        var oldSize = maxChunks * 2 * 5 * sizeof(uint);

        if (newSize <= oldSize)
            return; // Already big enough

        Log.Info($"Resizing indirect draw buffer from {maxChunks} to {newCommandCount} slots (x2 commands)");

        // Create new buffer
        GL.CreateBuffers(1, out uint newBuffer);
        GL.NamedBufferStorage(newBuffer, newSize, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, newBuffer, -1, "indirect_draw_commands_resized");

        // Copy old data if any
        if (oldSize > 0)
        {
            GL.CopyNamedBufferSubData(indirectDrawBuffer, newBuffer, IntPtr.Zero, IntPtr.Zero, oldSize);
        }

        // Delete old buffer and replace
        GL.DeleteBuffer(indirectDrawBuffer);
        indirectDrawBuffer = newBuffer;

        // Also resize the command slot buffer and chunk info buffer to match
        // This ensures they stay in sync with the indirect buffer capacity
        ResizeCommandSlotBuffer(newCommandCount, (uint)maxChunks);

        maxChunks = (int)newCommandCount;
        commandSlotCapacity = newCommandCount;

        Log.CheckGlError();
    }

    /// <summary>
    /// Update a single chunk's mesh data in the vertex buffer (Phase 5)
    /// This allows incremental updates without full regeneration
    /// </summary>
    public void UpdateChunkMesh(int chunkIndex, uint baseOffset, uint vertexCount, ReadOnlySpan<byte> vertexData)
    {
        if (vertexCount == 0)
            return;

        // Validate offset and count
        if (baseOffset + vertexCount > vertexBufferCapacity)
        {
            throw new ArgumentException($"Buffer overflow: offset={baseOffset}, count={vertexCount}, capacity={vertexBufferCapacity}");
        }

        // Upload new vertex data to GPU
        var byteOffset = (nint)(baseOffset * VERTEX_STRIDE);
        var byteSize = (nint)(vertexCount * VERTEX_STRIDE);

        unsafe
        {
            fixed (byte* ptr = vertexData)
            {
                GL.NamedBufferSubData(vertexBuffer, byteOffset, byteSize, (IntPtr)ptr);
            }
        }

        Log.Debug($"Updated chunk {chunkIndex} mesh: {vertexCount} vertices at offset {baseOffset}");
    }

    /// <summary>
    /// Get the highest allocated command slot index + 1
    /// Used for MultiDrawElementsIndirect draw count
    /// </summary>
    public int GetCommandSlotHighWaterMark() => nextCommandSlot;

    /// <summary>
    /// Allocate a command slot in the indirect draw buffer (Phase 5.3)
    /// Returns the slot index (0..MaxChunks-1)
    /// </summary>
    public int AllocateCommandSlot()
    {
        if (freeCommandSlots.Count > 0)
        {
            return freeCommandSlots.Dequeue();
        }

        var slot = nextCommandSlot++;
        
        // Check if we need to resize indirect buffer
        if (slot >= commandSlotCapacity)
        {
            var newCap = (uint)(commandSlotCapacity * 1.5);
            if (newCap == 0) newCap = 128;
            ResizeIndirectDrawBuffer(newCap);
            // commandSlotCapacity is updated in ResizeIndirectDrawBuffer
        }
        
        return slot;
    }

    /// <summary>
    /// Free a command slot and zero it out on GPU (Phase 5.3)
    /// </summary>
    public void FreeCommandSlot(int slot)
    {
        if (slot < 0) return;

        freeCommandSlots.Enqueue(slot);

        // Zero out the command in the buffer so it doesn't draw anything
        // Command is 5 uints = 20 bytes. We have 2 commands per slot = 40 bytes.
        var zeros = new uint[10]; // all zero
        GL.NamedBufferSubData(indirectDrawBuffer, (IntPtr)(slot * 40), 40, zeros);

        // Mark chunk info as invalid (-1)
        var invalid = -1;
        GL.NamedBufferSubData(chunkInfoBuffer, (IntPtr)(slot * sizeof(int)), sizeof(int), ref invalid);
    }

    private void ResizeCommandSlotBuffer(uint newCapacity, uint oldCapacity)
    {
        GL.CreateBuffers(1, out uint newBuffer);
        if (newBuffer == 0) Log.Error("Phase3BufferManager: Failed to create resized commandSlotBuffer!");
        GL.NamedBufferStorage((int)newBuffer, (nint)(newCapacity * sizeof(uint)), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        // No need to copy old data as this buffer is write-only for the shader
        GL.DeleteBuffer(commandSlotBuffer);
        commandSlotBuffer = newBuffer;

        // Also resize ChunkInfoBuffer
        GL.CreateBuffers(1, out uint newInfoBuffer);
        GL.NamedBufferStorage((int)newInfoBuffer, (nint)(newCapacity * sizeof(int)), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        
        // Copy old data as it persists across frames
        if (oldCapacity > 0)
        {
            GL.CopyNamedBufferSubData(chunkInfoBuffer, newInfoBuffer, IntPtr.Zero, IntPtr.Zero, (nint)(oldCapacity * sizeof(int)));
        }
        
        GL.DeleteBuffer(chunkInfoBuffer);
        chunkInfoBuffer = newInfoBuffer;
    }

    /// <summary>
    /// Read vertex data from the buffer (Phase 5)
    /// Used for incremental updates when chunks change
    /// </summary>
    public byte[] ReadVertices(uint baseOffset, uint vertexCount)
    {
        if (vertexCount == 0)
            return [];

        var byteOffset = (nint)(baseOffset * VERTEX_STRIDE);
        var byteSize = (nint)(vertexCount * VERTEX_STRIDE);
        var data = new byte[byteSize];

        GL.GetNamedBufferSubData(vertexBuffer, byteOffset, byteSize, data);

        return data;
    }
}
