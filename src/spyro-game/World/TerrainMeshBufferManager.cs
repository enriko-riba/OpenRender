using OpenRender;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.InteropServices;

namespace SpyroGame.World;

/// <summary>
/// Manages GPU buffer allocation and initialization for terrain meshes.
/// Implements explicit initialization strategy to avoid garbage data issues.
/// Adds incremental updates and buffer reuse for streaming.
/// </summary>
public sealed class TerrainMeshBufferManager : IDisposable
{
    // Buffer handles
    private uint vertexBuffer;
    private uint indexBuffer;
    private uint indirectDrawBuffer;
    private uint chunkInfoBuffer;

    // Buffer sizes
    private int maxChunks;
    private long maxVertices;
    private long maxIndices;

    // Buffer reuse tracking for BOTH vertices and indices
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

    public uint VertexBuffer => vertexBuffer;
    public uint IndexBuffer => indexBuffer;
    public uint IndirectDrawBuffer => indirectDrawBuffer;    
    public uint ChunkInfoBuffer => chunkInfoBuffer;

    public int MaxChunks => maxChunks;

    // Public accessors for buffer reuse
    public uint CommandSlotCapacity => commandSlotCapacity;
    public uint CurrentVertexBufferEnd => currentVertexBufferEnd;
    public uint CurrentIndexBufferEnd => currentIndexBufferEnd;

    // Increased to 40 to prevent main thread blocking during rapid movement.
    // We reduced the per-channel size to 4MB to keep total memory usage reasonable (~512MB).
    private const int UploadChannelCount = 40;

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
    /// Allocate all mesh buffers with explicit initialization.
    /// </summary>
    public void AllocateBuffers(int maxChunksInBatch)
    {
        maxChunks = maxChunksInBatch;
        
        // Estimate memory requirements based on a reasonable worst-case per chunk
        // instead of theoretical worst-case (which would be >18GB for 1000 chunks)
        // A complex chunk might have ~10k-20k visible faces.
        // We'll allocate for 4k faces per chunk on average based on observed usage.
        // If we run out, the buffers will automatically resize (but that's expensive).
        const int ESTIMATED_FACES_PER_CHUNK = 4_000;

        long totalFaces = (long)maxChunks * ESTIMATED_FACES_PER_CHUNK;
        
        maxVertices = totalFaces * 4;
        maxIndices = totalFaces * 6;
        
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

        Log.Info($"TerrainMeshBufferManager: Allocating buffers for {maxChunks} chunks");
        Log.Info($"  Est. faces/chunk: {ESTIMATED_FACES_PER_CHUNK:N0}");
        Log.Info($"  Max vertices: {maxVertices:N0} ({vertexMB:F2} MB)");
        Log.Info($"  Max indices: {maxIndices:N0} ({indexMB:F2} MB)");
        Log.Info($"  Total mesh memory: {totalMB:F2} MB");

        // Compacted vertices
        GL.CreateBuffers(1, out vertexBuffer);
        GL.NamedBufferStorage(vertexBuffer, (nint)maxVertices * VERTEX_STRIDE, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vertexBuffer, -1, "vertices_vbo");

        // Per-chunk index buffer (one contiguous buffer for all chunks)
        GL.CreateBuffers(1, out indexBuffer);
        GL.NamedBufferStorage(indexBuffer, (nint)maxIndices * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, indexBuffer, -1, "per_chunk_indices_ibo");

        // Multi-draw indirect command buffer
        // 2 commands per slot (Opaque + Transparent)
        GL.CreateBuffers(1, out indirectDrawBuffer);
        var indirectSize = (nint)maxChunks * 2 * 5 * sizeof(uint);
        GL.NamedBufferStorage(indirectDrawBuffer, indirectSize, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, indirectDrawBuffer, -1, "indirect_draw_commands");
        commandSlotCapacity = (uint)maxChunks;

        // Chunk Info Buffer (maps slot -> chunk index/info)
        GL.CreateBuffers(1, out chunkInfoBuffer);
        GL.NamedBufferStorage(chunkInfoBuffer, (nint)maxChunks * sizeof(int), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(chunkInfoBuffer, (int)((long)maxChunks * sizeof(int)), 0xFFFFFFFF);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, chunkInfoBuffer, -1, "chunk_info_ssbo");

        InitializeUploadChannels();

        Log.CheckGlError();
        Log.Info("TerrainMeshBufferManager: All buffers allocated");
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

    public void Dispose()
    {
        DisposeUploadChannels();

        if (vertexBuffer != 0) GL.DeleteBuffer(vertexBuffer);
        if (indexBuffer != 0) GL.DeleteBuffer(indexBuffer);
        if (indirectDrawBuffer != 0) GL.DeleteBuffer(indirectDrawBuffer);
        
        vertexBuffer = indexBuffer = indirectDrawBuffer = 0;

        if (chunkInfoBuffer != 0) GL.DeleteBuffer(chunkInfoBuffer);
        
        Log.Info("Phase3BufferManager: Disposed");
    }

    /// <summary>
    /// Get total GPU memory allocated for mesh buffers
    /// </summary>
    public long GetAllocatedBytes()
    {
        long total = 0;
        
        // Vertex buffer: worst-case vertices * stride
        total += (long)maxVertices * VERTEX_STRIDE;
        
        // Index buffer: worst-case indices * sizeof(uint)
        total += (long)maxIndices * sizeof(uint);
        
        // Indirect draw buffer: max chunks * 5 uints per command
        total += (long)maxChunks * 5 * sizeof(uint);

        // Chunk Info buffer: max chunks * sizeof(int)
        total += (long)maxChunks * sizeof(int);

        return total;
    }

    private void InitializeUploadChannels()
    {
        // Allocate 4MB per channel, which is enough for ~125k faces (500k vertices).
        // Theoretical max (checkerboard) is ~18MB, but typical terrain is <1MB.
        // 64 channels * 8MB (vertex+index) = 512MB total staging memory.
        maxChunkVertexBytes = 4 * 1024 * 1024;
        maxChunkIndexBytes = 4 * 1024 * 1024;

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

    private static void WaitForChannelFence(ref MeshUploadChannel channel)
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

    private static unsafe int CopyToMappedBuffer(IntPtr destination, ReadOnlySpan<uint> source, int capacityBytes, string label)
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
    /// Resize the index buffer to accommodate more indices
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
    /// Resize the indirect draw buffer to accommodate more commands
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
    /// Get the highest allocated command slot index + 1
    /// Used for MultiDrawElementsIndirect draw count
    /// </summary>
    public int GetCommandSlotHighWaterMark() => nextCommandSlot;

    /// <summary>
    /// Allocate a command slot in the indirect draw buffer
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
    /// Free a command slot and zero it out on GPU
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
}
