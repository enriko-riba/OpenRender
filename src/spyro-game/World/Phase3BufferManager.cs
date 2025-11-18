using OpenRender;
using OpenTK.Graphics.OpenGL4;

namespace SpyroGame.World;

/// <summary>
/// Manages GPU buffer allocation and initialization for Phase 3 (Visibility & Compaction).
/// Implements explicit initialization strategy to avoid garbage data issues.
/// </summary>
public class Phase3BufferManager : IDisposable
{
    // Buffer handles
    private uint visMaskBuffer;
    private uint countBuffer;
    private uint offsetBuffer;
    private uint vertexBuffer;
    private uint indexBuffer;          // Note: No longer used - replaced by shared IBO
    private uint sharedIndexBuffer;     // NEW: Shared quad index buffer (24 bytes)
    private uint atomicCounterBuffer;
    private uint indirectDrawBuffer;    // NEW: Multi-draw indirect command buffer

    // Buffer sizes
    private int maxChunks;
    private int maxVoxels;
    private int maxVertices;
    private int maxIndices;             // Note: No longer allocated - kept for API compatibility

    // Vertex stride from VoxelHelper (position=12, normal=12, texCoord=8, ao=4 = 36 bytes)
    private const int VERTEX_STRIDE = VoxelHelper.VERTEX_STRIDE_BYTES;

    public uint VisMaskBuffer => visMaskBuffer;
    public uint CountBuffer => countBuffer;
    public uint OffsetBuffer => offsetBuffer;
    public uint VertexBuffer => vertexBuffer;
    public uint IndexBuffer => indexBuffer;             // Deprecated - returns 0
    public uint SharedIndexBuffer => sharedIndexBuffer;  // NEW: Returns shared IBO
    public uint AtomicCounterBuffer => atomicCounterBuffer;
    public uint IndirectDrawBuffer => indirectDrawBuffer; // NEW: Indirect command buffer

    public int MaxChunks => maxChunks;
    public int MaxVertices => maxVertices;
    public int MaxIndices => maxIndices;

    /// <summary>
    /// Allocate all Phase 3 buffers with explicit initialization.
    /// Phase 5.2: Optimized vertex format (28 bytes vs 36 bytes = 22% reduction!)
    /// Phase 5.1: Uses shared index buffer instead of per-face indices (saves ~12 GB for full terrain).
    /// </summary>
    public void AllocateBuffers(int maxChunksInBatch)
    {
        maxChunks = maxChunksInBatch;
        maxVoxels = maxChunks * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize;

        // Worst-case: 6 visible faces per voxel (isolated voxel)
        var worstCaseFaces = maxVoxels * 6; // 6 faces/voxel
        maxVertices = worstCaseFaces * 4;   // 4 vertices per face
        maxIndices = 6;                      // PHASE 5.1: Only 6 indices shared across all faces!

        // Calculate memory savings from optimizations
        var oldVertexSize = 36;  // Old format: pos(12) + normal(12) + uv(8) + ao(4)
        var newVertexSize = VERTEX_STRIDE;  // New format: pos(12) + uv(8) + ao(4) + faceIdx(4)
        var vertexSavingsPerVert = oldVertexSize - newVertexSize;
        var vertexSavingsMB = (long)maxVertices * vertexSavingsPerVert / 1024f / 1024f;
        var indexSavingsGB = worstCaseFaces * 6 * sizeof(uint) / 1024f / 1024f / 1024f;

        Log.Info($"Phase3BufferManager: Allocating buffers for {maxChunks} chunks (Phase 5.2: Optimized Vertex Format)");
        Log.Info($"  Max voxels: {maxVoxels:N0}");
        Log.Info($"  Worst-case faces: {worstCaseFaces:N0}");
        Log.Info($"  Max vertices: {maxVertices:N0} ({(long)maxVertices * VERTEX_STRIDE / 1024f / 1024f:F2} MB, saves {vertexSavingsMB:F2} MB vs old format)");
        Log.Info($"  Shared indices: {maxIndices} (24 bytes) - saves ~{indexSavingsGB:F2} GB vs per-face indices!");
        Log.Info($"  Total memory optimization: {vertexSavingsMB + indexSavingsGB * 1024:F2} MB saved!");

        // Visibility mask (1 uint per voxel, lower 6 bits used)
        GL.CreateBuffers(1, out visMaskBuffer);
        GL.NamedBufferStorage(visMaskBuffer, maxVoxels * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(visMaskBuffer, maxVoxels * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, visMaskBuffer, -1, "vis_mask_ssbo");

        // Visible counts (1 uint per chunk)
        GL.CreateBuffers(1, out countBuffer);
        GL.NamedBufferStorage(countBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
        ClearBufferUInt(countBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, countBuffer, -1, "count_visible_ssbo");

        // Base offsets (1 uint per chunk)
        GL.CreateBuffers(1, out offsetBuffer);
        GL.NamedBufferStorage(offsetBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        ClearBufferUInt(offsetBuffer, maxChunks * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, offsetBuffer, -1, "offset_base_ssbo");

        // Compacted vertices
        GL.CreateBuffers(1, out vertexBuffer);
        GL.NamedBufferStorage(vertexBuffer, (nint)maxVertices * VERTEX_STRIDE, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vertexBuffer, -1, "compact_vertices_vbo");

        // PHASE 5.1: Shared index buffer (replaces per-face indices)
        GL.CreateBuffers(1, out sharedIndexBuffer);
        GL.NamedBufferStorage(sharedIndexBuffer, 6 * sizeof(uint), VoxelHelper.SHARED_QUAD_INDICES,
            BufferStorageFlags.None);  // Immutable - never changes
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, sharedIndexBuffer, -1, "shared_quad_ibo");

        // Deprecated individual index buffer (kept for backwards compatibility, but not allocated)
        indexBuffer = 0;

        // NEW: Multi-draw indirect command buffer (for efficient rendering)
        // We don't know face count yet, so allocate worst-case
        GL.CreateBuffers(1, out indirectDrawBuffer);
        var indirectSize = worstCaseFaces * 5 * sizeof(uint); // 5 uints per DrawElementsIndirectCommand
        GL.NamedBufferStorage(indirectDrawBuffer, indirectSize, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, indirectDrawBuffer, -1, "indirect_draw_commands");

        // Atomic counters (2 uints: vertex counter, face counter)
        // Note: Second counter now tracks face count instead of index count
        GL.CreateBuffers(1, out atomicCounterBuffer);
        GL.NamedBufferStorage(atomicCounterBuffer, 2 * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
        ClearBufferUInt(atomicCounterBuffer, 2 * sizeof(uint), 0);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, atomicCounterBuffer, -1, "atomic_counters_ssbo");

        Log.CheckGlError();
        Log.Info("Phase3BufferManager: All buffers allocated (Phase 5.1: Shared IBO enabled)");
    }

    /// <summary>
    /// Clear buffer to known byte value.
    /// Solves Problem 1: Garbage Data in Buffers.
    /// </summary>
    private void ClearBuffer(uint buffer, int sizeBytes, byte value)
    {
        var data = new byte[sizeBytes];
        Array.Fill(data, value);
        GL.NamedBufferSubData(buffer, IntPtr.Zero, sizeBytes, data);
    }

    /// <summary>
    /// Clear buffer of uints to known value.
    /// </summary>
    private void ClearBufferUInt(uint buffer, int sizeBytes, uint value)
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
        var zero = 0u;
        GL.NamedBufferSubData(atomicCounterBuffer, IntPtr.Zero, sizeof(uint), ref zero);
        GL.NamedBufferSubData(atomicCounterBuffer, sizeof(uint), sizeof(uint), ref zero);
    }

    /// <summary>
    /// Read back actual vertex/face counts from atomic counters.
    /// Phase 5.1: Second counter now tracks face count instead of index count.
    /// </summary>
    public (uint vertexCount, uint faceCount) ReadAtomicCounters()
    {
        var counts = new uint[2];
        GL.GetNamedBufferSubData(atomicCounterBuffer, IntPtr.Zero, 2 * sizeof(uint), counts);
        return (counts[0], counts[1]);
    }

    /// <summary>
    /// Bind all Phase 3 buffers to their designated binding points.
    /// Uses centralized constants from VoxelHelper.SSBOBindings.
    /// </summary>
    public void BindBuffersForVisibility()
    {
        // Visibility shader needs: voxel data (input), visibility mask (output)
        // Note: voxelData buffer bound by caller (from Phase 2)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VISIBILITY_MASK, visMaskBuffer);
    }

    public void BindBuffersForCount()
    {
        // Count shader needs: visibility mask (input), counts (output)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VISIBILITY_MASK, visMaskBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VISIBLE_COUNTS, countBuffer);
    }

    public void BindBuffersForCompaction()
    {
        // Compaction shader needs: voxel data, visibility mask, offsets (input)
        // and vertices, atomic counters (output)
        // Note: voxelData bound by caller
        // Phase 5.1: No longer binds index buffer (uses shared IBO instead)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.VISIBILITY_MASK, visMaskBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.BASE_OFFSETS, offsetBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.COMPACT_VERTICES, vertexBuffer);
        // COMPACT_INDICES binding removed - no longer needed
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,
            VoxelHelper.SSBOBindings.ATOMIC_COUNTERS, atomicCounterBuffer);
    }

    public void Dispose()
    {
        if (visMaskBuffer != 0) GL.DeleteBuffer(visMaskBuffer);
        if (countBuffer != 0) GL.DeleteBuffer(countBuffer);
        if (offsetBuffer != 0) GL.DeleteBuffer(offsetBuffer);
        if (vertexBuffer != 0) GL.DeleteBuffer(vertexBuffer);
        if (indexBuffer != 0) GL.DeleteBuffer(indexBuffer);              // Deprecated, always 0
        if (sharedIndexBuffer != 0) GL.DeleteBuffer(sharedIndexBuffer);  // NEW
        if (indirectDrawBuffer != 0) GL.DeleteBuffer(indirectDrawBuffer); // NEW
        if (atomicCounterBuffer != 0) GL.DeleteBuffer(atomicCounterBuffer);

        visMaskBuffer = countBuffer = offsetBuffer = 0;
        vertexBuffer = indexBuffer = sharedIndexBuffer = indirectDrawBuffer = atomicCounterBuffer = 0;

        Log.Info("Phase3BufferManager: Disposed");
    }

    /// <summary>
    /// Get total GPU memory allocated for Phase 3 buffers (Phase 5)
    /// </summary>
    public long GetAllocatedBytes()
    {
        long total = 0;
        
        // Visibility mask: 1 uint per voxel
        total += maxVoxels * sizeof(uint);
        
        // Count buffer: 1 uint per chunk
        total += maxChunks * sizeof(uint);
        
        // Offset buffer: 1 uint per chunk
        total += maxChunks * sizeof(uint);
        
        // Vertex buffer: worst-case vertices * stride
        total += (long)maxVertices * VERTEX_STRIDE;
        
        // Shared index buffer: always 6 indices
        total += 6 * sizeof(uint);
        
        // Indirect draw buffer: worst-case faces * 5 uints
        total += (long)(maxVoxels * 6) * 5 * sizeof(uint);
        
        // Atomic counter buffer: 2 uints
        total += 2 * sizeof(uint);
        
        return total;
    }
}
