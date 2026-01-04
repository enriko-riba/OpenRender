using DarkVox.Shared.World;
using OpenRender;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.InteropServices;

namespace DarkVox.World;

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
    
    // Upload throttling stats
    private int uploadsThisFrame;
    private int uploadsDeferredThisFrame;
    
    /// <summary>Number of mesh uploads completed this frame.</summary>
    public int UploadsThisFrame => uploadsThisFrame;
    
    /// <summary>Number of mesh uploads deferred due to busy channels.</summary>
    public int UploadsDeferredThisFrame => uploadsDeferredThisFrame;
    
    /// <summary>Reset per-frame upload counters. Call at start of each frame.</summary>
    public void ResetFrameStats()
    {
        uploadsThisFrame = 0;
        uploadsDeferredThisFrame = 0;
    }

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
        // 4 commands per slot (Opaque + AlphaTest + Water + Transparent)
        GL.CreateBuffers(1, out indirectDrawBuffer);
        var indirectSize = (nint)maxChunks * 4 * 5 * sizeof(uint);
        GL.NamedBufferStorage(indirectDrawBuffer, indirectSize, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, indirectDrawBuffer, -1, "indirect_draw_commands");
        // IMPORTANT: Explicitly zero the command buffer.
        // GL buffer storage is uninitialized; leaving garbage commands can cause stray draws
        // (e.g. chunks appearing at incorrect offsets/heights) until the slot is overwritten.
        ClearBufferUIntRange(indirectDrawBuffer, IntPtr.Zero, (int)indirectSize, 0u);
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

    /// <summary>
    /// Clear a range within a uint buffer to a known value.
    /// Uses chunked uploads to avoid allocating very large arrays.
    /// </summary>
    private static void ClearBufferUIntRange(uint buffer, IntPtr byteOffset, int sizeBytes, uint value)
    {
        if (sizeBytes <= 0)
        {
            return;
        }

        const int ChunkUints = 4096;
        var chunk = new uint[ChunkUints];
        Array.Fill(chunk, value);

        var remainingBytes = sizeBytes;
        var offset = byteOffset;
        var chunkBytes = ChunkUints * sizeof(uint);

        while (remainingBytes > 0)
        {
            var writeBytes = Math.Min(remainingBytes, chunkBytes);
            GL.NamedBufferSubData(buffer, offset, writeBytes, chunk);
            offset += writeBytes;
            remainingBytes -= writeBytes;
        }
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
        
        // Indirect draw buffer: max chunks * 4 commands * 5 uints per command
        total += (long)maxChunks * 4 * 5 * sizeof(uint);

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

    /// <summary>
    /// Try to acquire an upload channel without blocking.
    /// Scans all channels starting from nextUploadChannel to find one that's ready.
    /// Returns -1 if all channels are busy (caller should defer to next frame).
    /// </summary>
    private int TryAcquireUploadChannel()
    {
        // Scan all channels starting from the next expected one
        for (var i = 0; i < uploadChannels.Length; i++)
        {
            var channelIndex = (nextUploadChannel + i) % uploadChannels.Length;
            ref var channel = ref uploadChannels[channelIndex];
            
            // Check if this channel's fence has completed (non-blocking)
            if (TryCompleteFence(ref channel))
            {
                // Channel is ready - advance for next time and return
                nextUploadChannel = (channelIndex + 1) % uploadChannels.Length;
                return channelIndex;
            }
        }
        
        // All channels are busy - defer to next frame
        uploadsDeferredThisFrame++;
        return -1;
    }
    
    /// <summary>
    /// Non-blocking fence check. Returns true if fence is complete or not set.
    /// </summary>
    private static bool TryCompleteFence(ref MeshUploadChannel channel)
    {
        if (channel.Fence == IntPtr.Zero)
        {
            return true; // No fence = ready
        }
        
        // Non-blocking check with timeout 0
        var waitResult = GL.ClientWaitSync(channel.Fence, ClientWaitSyncFlags.SyncFlushCommandsBit, 0);
        
        if (waitResult is WaitSyncStatus.AlreadySignaled or WaitSyncStatus.ConditionSatisfied)
        {
            // Fence complete - clean up
            GL.DeleteSync(channel.Fence);
            channel.Fence = IntPtr.Zero;
            return true;
        }
        
        // Still pending (TimeoutExpired) or error (WaitFailed)
        return false;
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

    /// <summary>
    /// Upload mesh data to GPU buffers using non-blocking channel acquisition.
    /// Returns false if all upload channels are busy (caller should retry next frame).
    /// </summary>
    public bool UploadMeshData(ReadOnlySpan<uint> vertexData, int vertexOffset, ReadOnlySpan<uint> indexData, int indexOffset)
    {
        if ((vertexData.Length == 0 || vertexOffset < 0) && (indexData.Length == 0 || indexOffset < 0))
        {
            return true; // Nothing to upload = success
        }

        // If the payload doesn't fit our staging buffers, do a direct upload rather than truncating.
        // Truncation leaves tail data from old chunks in GPU buffers, which can manifest as
        // raised columns/spikes and missing chunk sections.
        var needsDirect = false;
        var vertexBytesNeeded = 0;
        var indexBytesNeeded = 0;

        if (vertexData.Length > 0 && vertexOffset >= 0)
        {
            vertexBytesNeeded = MemoryMarshal.AsBytes(vertexData).Length;
            if (vertexBytesNeeded > maxChunkVertexBytes)
            {
                needsDirect = true;
            }
        }

        if (indexData.Length > 0 && indexOffset >= 0)
        {
            indexBytesNeeded = MemoryMarshal.AsBytes(indexData).Length;
            if (indexBytesNeeded > maxChunkIndexBytes)
            {
                needsDirect = true;
            }
        }

        if (needsDirect)
        {
            unsafe
            {
                if (vertexData.Length > 0 && vertexOffset >= 0 && vertexBytesNeeded > 0)
                {
                    var vertexByteOffset = (IntPtr)(vertexOffset * VoxelHelper.VERTEX_STRIDE_BYTES);
                    fixed (uint* vPtr = vertexData)
                    {
                        GL.NamedBufferSubData(vertexBuffer, vertexByteOffset, vertexBytesNeeded, (IntPtr)vPtr);
                    }
                }

                if (indexData.Length > 0 && indexOffset >= 0 && indexBytesNeeded > 0)
                {
                    var indexByteOffset = (IntPtr)(indexOffset * sizeof(uint));
                    fixed (uint* iPtr = indexData)
                    {
                        GL.NamedBufferSubData(indexBuffer, indexByteOffset, indexBytesNeeded, (IntPtr)iPtr);
                    }
                }
            }

            uploadsThisFrame++;
            return true;
        }

        var channelIndex = TryAcquireUploadChannel();
        if (channelIndex < 0)
        {
            return false; // All channels busy - defer to next frame
        }
        
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
            uploadsThisFrame++;
        }
        
        return true;
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

        // Sort in-place by offset to enable merging (avoids LINQ allocation)
        freeVertexRegions.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // Try to merge adjacent free regions
        MergeFreeRegions(freeVertexRegions);

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

        // Sort in-place by offset to enable merging (avoids LINQ allocation)
        freeIndexRegions.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // Try to merge adjacent free regions
        MergeFreeRegions(freeIndexRegions);

        //Log.Debug($"Freed index region: offset={baseOffset}, size={size}, freeRegions={freeIndexRegions.Count}");
    }

    /// <summary>
    /// Merge adjacent free regions to reduce fragmentation.
    /// Modifies the list in-place to avoid allocations.
    /// </summary>
    private static void MergeFreeRegions(List<BufferRegion> regions)
    {
        if (regions.Count < 2)
            return;

        var writeIndex = 0;
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
                // Not adjacent, write current and move to next
                regions[writeIndex++] = current;
                current = next;
            }
        }

        // Write the last merged region
        regions[writeIndex++] = current;
        
        // Remove excess elements from the end
        if (writeIndex < regions.Count)
        {
            regions.RemoveRange(writeIndex, regions.Count - writeIndex);
        }
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
        // Each slot now holds 4 commands (Opaque + AlphaTest + Water + Transparent)
        var newSize = (int)(newCommandCount * 4 * 5 * sizeof(uint)); // 5 uints per command * 4
        var oldSize = maxChunks * 4 * 5 * sizeof(uint);

        if (newSize <= oldSize)
            return; // Already big enough

        Log.Info($"Resizing indirect draw buffer from {maxChunks} to {newCommandCount} slots (x4 commands)");

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

        // IMPORTANT: Initialize the newly-allocated tail.
        // GL buffer storage is uninitialized; leaving garbage commands can cause stray draws
        // (e.g. chunks appearing at incorrect offsets) until the slot is overwritten.
        if (newSize > oldSize)
        {
            ClearBufferUIntRange(newBuffer, (IntPtr)oldSize, newSize - oldSize, 0u);
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

        ClearCommandSlot(slot);
    }

    /// <summary>
    /// Zero out a command slot and mark its chunk mapping as invalid.
    /// This does NOT return the slot to the free list.
    /// </summary>
    public void ClearCommandSlot(int slot)
    {
        if (slot < 0) return;

        // Zero out the command in the buffer so it doesn't draw anything.
        // Command is 5 uints = 20 bytes. We have 4 commands per slot = 80 bytes.
        // Use the pointer overload to avoid any ambiguity about "size" units.
        Span<uint> zeros = stackalloc uint[20];
        zeros.Clear();
        unsafe
        {
            fixed (uint* zerosPtr = zeros)
            {
                GL.NamedBufferSubData((int)indirectDrawBuffer, (IntPtr)(slot * 80), 80, (IntPtr)zerosPtr);
            }

            // Mark chunk info as invalid (-1)
            var invalid = -1;
            GL.NamedBufferSubData((int)chunkInfoBuffer, (IntPtr)(slot * sizeof(int)), sizeof(int), ref invalid);
        }
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

        // Initialize new slots to invalid (-1) so they never produce a valid chunk index.
        // Without this, new slots can read as 0 and map to chunk 0 in the shader.
        if (newCapacity > oldCapacity)
        {
            var byteOffset = (IntPtr)(oldCapacity * sizeof(int));
            var byteCount = (int)((newCapacity - oldCapacity) * sizeof(int));
            ClearBufferUIntRange(newInfoBuffer, byteOffset, byteCount, 0xFFFFFFFFu);
        }
        
        GL.DeleteBuffer(chunkInfoBuffer);
        chunkInfoBuffer = newInfoBuffer;
    }
}
