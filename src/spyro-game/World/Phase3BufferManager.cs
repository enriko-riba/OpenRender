using OpenRender;
using OpenTK.Graphics.OpenGL4;

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
    private uint indexBuffer;          // Phase 5.3: Now a giant per-chunk index buffer
    private uint atomicCounterBuffer;
    private uint indirectDrawBuffer;
    private uint perChunkEmitBuffer;   // NEW: per-chunk face emission counters
    private uint scanTotalsBuffer;     // NEW: 2 uints [totalVertices, totalIndices]
    private uint opaqueCountsBuffer;   // NEW: per-chunk opaque counts
    private uint waterEmitBuffer;      // NEW: per-chunk water emission counters

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
    private List<BufferRegion> freeIndexRegions = [];  // NEW: Track free index regions
    private readonly Queue<int> freeCommandSlots = new();          // NEW: Track free command slots
    private int nextCommandSlot = 0;                      // NEW: Next available command slot
    private uint currentVertexBufferEnd = 0;              // Renamed for clarity
    private uint currentIndexBufferEnd = 0;                // NEW: Track index buffer end
    private uint vertexBufferCapacity = 0;
    private uint indexBufferCapacity = 0;                  // NEW: Track index buffer capacity
    private uint commandSlotCapacity = 0;                  // NEW: Track command buffer capacity

    // Vertex stride from VoxelHelper (position=12, normal=12, texCoord=8, ao=4 = 36 bytes)
    // Phase 5.2: Compressed to 8 bytes (PackedPos+Data=4, Padding=4)
    private const int VERTEX_STRIDE = 8;

    public uint VisMaskBuffer => visMaskBuffer;
    public uint CountBuffer => countBuffer;
    public uint OffsetBuffer => offsetBuffer;
    public uint VertexBuffer => vertexBuffer;
    public uint IndexBuffer => indexBuffer;                // Phase 5.3: Returns per-chunk IBO
    public uint AtomicCounterBuffer => atomicCounterBuffer;
    public uint IndirectDrawBuffer => indirectDrawBuffer;
    public uint PerChunkEmitBuffer => perChunkEmitBuffer; // NEW
    public uint ScanTotalsBuffer => scanTotalsBuffer;     // NEW
    public uint OpaqueCountsBuffer => opaqueCountsBuffer; // NEW
    public uint WaterEmitBuffer => waterEmitBuffer;       // NEW
    public uint CommandSlotBuffer => commandSlotBuffer;   // NEW: Buffer to pass slots to shader
    public uint ChunkInfoBuffer => chunkInfoBuffer;       // NEW: Buffer to store chunk info per slot

    public int MaxChunks => maxChunks;
    public int MaxVertices => maxVertices;
    public int MaxIndices => maxIndices;

    // Phase 5: Public accessors for buffer reuse
    public uint VertexBufferCapacity => vertexBufferCapacity;
    public uint IndexBufferCapacity => indexBufferCapacity;  // NEW
    public uint CommandSlotCapacity => commandSlotCapacity;  // NEW
    public uint CurrentVertexBufferEnd => currentVertexBufferEnd;  // Renamed
    public uint CurrentIndexBufferEnd => currentIndexBufferEnd;    // NEW
    public int FreeVertexRegionCount => freeVertexRegions.Count;  // Renamed
    public int FreeIndexRegionCount => freeIndexRegions.Count;    // NEW
    public int FreeCommandSlotCount => freeCommandSlots.Count;    // NEW

    private uint commandSlotBuffer; // NEW: Buffer to pass slots to shader
    private uint chunkInfoBuffer;   // NEW: Buffer to store chunk info per slot

    /// <summary>
    /// Allocate all Phase 3 buffers with explicit initialization.
    /// Phase 5.3: Per-chunk index buffers for efficient batching (one command per chunk!)
    /// Phase 5.2: Optimized vertex format (28 bytes vs 36 bytes = 22% reduction!)
    /// </summary>
    public void AllocateBuffers(int maxChunksInBatch)
    {
        maxChunks = maxChunksInBatch;
        maxVoxels = maxChunks * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize;

        // Worst-case: 6 visible faces per voxel (isolated voxel)
        var worstCaseFaces = maxVoxels * 6; // 6 faces/voxel
        maxVertices = worstCaseFaces * 4;   // 4 vertices per face
        maxIndices = worstCaseFaces * 6;    // PHASE 5.3: 6 indices per face (per-chunk IBOs!)
        
        // Phase 5: Initialize buffer tracking
        vertexBufferCapacity = (uint)maxVertices;
        indexBufferCapacity = (uint)maxIndices;  // NEW
        currentVertexBufferEnd = 0;
        currentIndexBufferEnd = 0;               // NEW
        freeVertexRegions.Clear();
        freeIndexRegions.Clear();                // NEW
        freeCommandSlots.Clear();                // NEW

        // Calculate memory usage
        var vertexMB = (long)maxVertices * VERTEX_STRIDE / 1024f / 1024f;
        var indexMB = (long)maxIndices * sizeof(uint) / 1024f / 1024f;
        var totalMB = vertexMB + indexMB;

        Log.Info($"Phase3BufferManager: Allocating buffers for {maxChunks} chunks (Phase 5.3: Per-Chunk Meshes)");
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

        // NEW: Per-chunk face emission counters (reset each compaction)
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

        // PHASE 5.3: Per-chunk index buffer (one contiguous buffer for all chunks)
        GL.CreateBuffers(1, out indexBuffer);
        GL.NamedBufferStorage(indexBuffer, (nint)maxIndices * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, indexBuffer, -1, "per_chunk_indices_ibo");

        // Multi-draw indirect command buffer
        // Initial size is small, will be resized by ResizeIndirectDrawBuffer
        // Phase 5.3: 2 commands per slot (Opaque + Transparent)
        GL.CreateBuffers(1, out indirectDrawBuffer);
        var indirectSize = maxChunks * 2 * 5 * sizeof(uint);
        GL.NamedBufferStorage(indirectDrawBuffer, indirectSize, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, indirectDrawBuffer, -1, "indirect_draw_commands");
        commandSlotCapacity = (uint)maxChunks;

        // NEW: Command Slot Buffer (to pass slots to shader)
        GL.CreateBuffers(1, out commandSlotBuffer);
        if (commandSlotBuffer == 0) Log.Error("Phase3BufferManager: Failed to create commandSlotBuffer!");
        GL.NamedBufferStorage(commandSlotBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, commandSlotBuffer, -1, "command_slot_ssbo");

        // NEW: Chunk Info Buffer (maps slot -> chunk index/info)
        GL.CreateBuffers(1, out chunkInfoBuffer);
        GL.NamedBufferStorage(chunkInfoBuffer, maxChunks * sizeof(int), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(chunkInfoBuffer, maxChunks * sizeof(int), 0xFFFFFFFF); // Initialize to -1
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, chunkInfoBuffer, -1, "chunk_info_ssbo");

        // Atomic counters (3 uints: vertex counter, index counter, face counter)
        GL.CreateBuffers(1, out atomicCounterBuffer);
        GL.NamedBufferStorage(atomicCounterBuffer, 3 * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(atomicCounterBuffer, 3 * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, atomicCounterBuffer, -1, "atomic_counters_ssbo");

        // NEW: totals buffer (2 uints)
        GL.CreateBuffers(1, out scanTotalsBuffer);
        GL.NamedBufferStorage(scanTotalsBuffer, 2 * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.ClientStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, scanTotalsBuffer, -1, "scan_totals_ssbo");

        // NEW: Opaque Counts (1 uint per chunk)
        GL.CreateBuffers(1, out opaqueCountsBuffer);
        GL.NamedBufferStorage(opaqueCountsBuffer, maxChunks * sizeof(uint), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(opaqueCountsBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, opaqueCountsBuffer, -1, "opaque_counts_ssbo");

        // NEW: Water Emit Counter (1 uint per chunk)
        GL.CreateBuffers(1, out waterEmitBuffer);
        GL.NamedBufferStorage(waterEmitBuffer, maxChunks * sizeof(uint), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(waterEmitBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, waterEmitBuffer, -1, "water_emit_ssbo");

        Log.CheckGlError();
        Log.Info("Phase3BufferManager: All buffers allocated (Phase 5.3: One command per chunk!)");
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
    /// Phase 5.3: Now has 3 counters (vertex, index, face)
    /// </summary>
    public void ResetAtomicCounters()
    {
        var zeros = new uint[3] { 0, 0, 0 };
        GL.NamedBufferSubData(atomicCounterBuffer, IntPtr.Zero, 3 * sizeof(uint), zeros);
    }

    /// <summary>
    /// Read back actual vertex/index/face counts from atomic counters.
    /// Phase 5.3: Now returns vertex count, index count, and face count.
    /// </summary>
    public (uint vertexCount, uint indexCount, uint faceCount) ReadAtomicCounters()
    {
        var counts = new uint[3];
        GL.GetNamedBufferSubData(atomicCounterBuffer, IntPtr.Zero, 3 * sizeof(uint), counts);
        return (counts[0], counts[1], counts[2]);
    }

    /// <summary>
    /// Bind all Phase 3 buffers to their designated binding points.
    /// Uses centralized constants from VoxelHelper.SSBOBindings.
    /// </summary>
    public void BindBuffersForVisibility() =>
        // Visibility shader needs: voxel data (input), visibility mask (output)
        // Note: voxelData buffer bound by caller (from Phase 2)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VISIBILITY_MASK, visMaskBuffer);

    public void BindBuffersForCount()
    {
        // Count shader needs: visibility mask (input), counts (output)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VISIBILITY_MASK, visMaskBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VISIBLE_COUNTS, countBuffer);
        // expose totals for scan compute (may be unused by count shader)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.SCAN_TOTALS, scanTotalsBuffer);
    }

    public void BindBuffersForCompaction()
    {
        // Compaction shader needs: voxel data, visibility mask, offsets (input)
        // and vertices, indices, atomic counters (output)
        // Note: voxelData bound by caller
        // Phase 5.3: Binds both vertex and index buffers for per-chunk mesh generation
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VISIBILITY_MASK, visMaskBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.BASE_OFFSETS, offsetBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.COMPACT_VERTICES, vertexBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.COMPACT_INDICES, indexBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.ATOMIC_COUNTERS, atomicCounterBuffer);
        // NEW: per-chunk emit
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.PER_CHUNK_FACE_EMIT, perChunkEmitBuffer);
    }

    public void BindBuffersForScan()
    {
        // counts at binding 2
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.VISIBLE_COUNTS, countBuffer);
        // base offsets at binding 3
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.BASE_OFFSETS, offsetBuffer);
        // scan totals at binding 9
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.SCAN_TOTALS, scanTotalsBuffer);
    }

    public void BindBuffersForBuildIndirect()
    {
        // Need baseOffsets (3), counts (2) and indirect buffer as SSBO
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.BASE_OFFSETS, offsetBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.VISIBLE_COUNTS, countBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, VoxelHelper.SSBOBindings.INDIRECT_COMMANDS, indirectDrawBuffer);
    }

    public void Dispose()
    {
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
    /// Get total GPU memory allocated for Phase 3 buffers (Phase 5)
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
        
        // perChunkEmitBuffer: 1 uint per chunk
        total += maxChunks * sizeof(uint); // perChunkEmitBuffer
        
        return total;
    }

    // ========================================================================
    // Phase 5: Incremental Updates & Buffer Reuse
    // ========================================================================

    /// <summary>
    /// Allocate a vertex buffer region, reusing freed space if available (Phase 5)
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
    /// Allocate an index buffer region, reusing freed space if available (Phase 5.3)
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
    /// Mark a vertex buffer region as free for reuse (Phase 5)
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
    /// Mark an index buffer region as free for reuse (Phase 5.3)
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
    /// Merge adjacent free regions to reduce fragmentation (Phase 5)
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
    /// Resize the vertex buffer to accommodate more vertices (Phase 5)
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
