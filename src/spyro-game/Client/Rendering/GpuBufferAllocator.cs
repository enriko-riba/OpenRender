using OpenRender;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.CompilerServices;

namespace SpyroGame.Client.Rendering;

/// <summary>
/// Manages GPU buffer allocation and lifecycle for the streaming terrain system.
/// Handles SSBO creation, resizing, and cleanup with proper GL state management.
/// </summary>
public sealed class GpuBufferAllocator : IDisposable
{
    // Buffer handles
    private uint chunkVoxelsSSBO;
    private uint editMasksSSBO;
    private uint columnMetaSSBO;
    private uint spanIntervalsSSBO;
    private uint visibilityCountsSSBO;
    private uint offsetsSSBO;
    private uint cursorsSSBO;

    // Current capacities (in elements, not bytes)
    private int chunkVoxelsCapacity;
    private int editMasksCapacity;
    private int columnMetaCapacity;
    private int spanIntervalsCapacity;
    private int visibilityCountsCapacity;
    private int offsetsCapacity;
    private int cursorsCapacity;

    // Constants
    private const int VOXELS_PER_CHUNK = 16 * 16 * 128; // 32,768
    private const int COLUMNS_PER_CHUNK = 16 * 16;      // 256
    private const int EDITMASK_WORDS_PER_CHUNK = (VOXELS_PER_CHUNK + 31) / 32; // 1,024

    public GpuBufferAllocator()
    {
        Log.Info("GpuBufferAllocator: Initializing GPU buffers for streaming terrain");
    }

    /// <summary>
    /// Ensure ChunkVoxels buffer can hold the specified number of chunks
    /// </summary>
    public void EnsureChunkVoxelsCapacity(int chunkCount)
    {
        var required = chunkCount * VOXELS_PER_CHUNK;
        if (chunkVoxelsSSBO != 0 && chunkVoxelsCapacity >= required)
            return;

        var sizeBytes = required * sizeof(uint);
        ResizeOrCreateBuffer(
            ref chunkVoxelsSSBO,
            ref chunkVoxelsCapacity,
            required,
            sizeBytes,
            "ChunkVoxels_SSBO",
            BufferStorageFlags.DynamicStorageBit
        );

        Log.Debug($"ChunkVoxels buffer: {chunkCount} chunks, {sizeBytes / 1024 / 1024}MB");
    }

    /// <summary>
    /// Ensure EditMasks buffer can hold the specified number of chunks
    /// </summary>
    public void EnsureEditMasksCapacity(int chunkCount)
    {
        var required = chunkCount * EDITMASK_WORDS_PER_CHUNK;
        if (editMasksSSBO != 0 && editMasksCapacity >= required)
            return;

        var sizeBytes = required * sizeof(uint);
        ResizeOrCreateBuffer(
            ref editMasksSSBO,
            ref editMasksCapacity,
            required,
            sizeBytes,
            "EditMasks_SSBO",
            BufferStorageFlags.DynamicStorageBit
        );

        Log.Debug($"EditMasks buffer: {chunkCount} chunks, {sizeBytes / 1024}KB");
    }

    /// <summary>
    /// Ensure ColumnMeta buffer can hold the specified number of chunks
    /// </summary>
    public void EnsureColumnMetaCapacity(int chunkCount)
    {
        var required = chunkCount * COLUMNS_PER_CHUNK;
        if (columnMetaSSBO != 0 && columnMetaCapacity >= required)
            return;

        // 2 uints per column (topSolid+spanCount, spanOffset)
        var sizeBytes = required * 2 * sizeof(uint);
        ResizeOrCreateBuffer(
            ref columnMetaSSBO,
            ref columnMetaCapacity,
            required,
            sizeBytes,
            "ColumnMeta_SSBO",
            BufferStorageFlags.DynamicStorageBit
        );

        Log.Debug($"ColumnMeta buffer: {chunkCount} chunks, {sizeBytes / 1024}KB");
    }

    /// <summary>
    /// Ensure SpanIntervals buffer can hold the specified number of span pairs
    /// </summary>
    public void EnsureSpanIntervalsCapacity(int spanCount)
    {
        if (spanIntervalsSSBO != 0 && spanIntervalsCapacity >= spanCount)
            return;

        // 2 ints per span (yStart, yEnd)
        var sizeBytes = spanCount * 2 * sizeof(int);
        ResizeOrCreateBuffer(
            ref spanIntervalsSSBO,
            ref spanIntervalsCapacity,
            spanCount,
            sizeBytes,
            "SpanIntervals_SSBO",
            BufferStorageFlags.DynamicStorageBit
        );

        Log.Debug($"SpanIntervals buffer: {spanCount} spans, {sizeBytes / 1024}KB");
    }

    /// <summary>
    /// Ensure VisibilityCounts buffer can hold the specified number of chunks
    /// </summary>
    public void EnsureVisibilityCountsCapacity(int chunkCount)
    {
        if (visibilityCountsSSBO != 0 && visibilityCountsCapacity >= chunkCount)
            return;

        var sizeBytes = chunkCount * sizeof(int);
        ResizeOrCreateBuffer(
            ref visibilityCountsSSBO,
            ref visibilityCountsCapacity,
            chunkCount,
            sizeBytes,
            "VisibilityCounts_SSBO",
            BufferStorageFlags.DynamicStorageBit
        );

        Log.Debug($"VisibilityCounts buffer: {chunkCount} chunks, {sizeBytes / 1024}KB");
    }

    /// <summary>
    /// Ensure Offsets buffer (prefix sum results) can hold the specified number of chunks
    /// </summary>
    public void EnsureOffsetsCapacity(int chunkCount)
    {
        if (offsetsSSBO != 0 && offsetsCapacity >= chunkCount)
            return;

        var sizeBytes = chunkCount * sizeof(int);
        ResizeOrCreateBuffer(
            ref offsetsSSBO,
            ref offsetsCapacity,
            chunkCount,
            sizeBytes,
            "Offsets_SSBO",
            BufferStorageFlags.DynamicStorageBit
        );

        Log.Debug($"Offsets buffer: {chunkCount} chunks, {sizeBytes / 1024}KB");
    }

    /// <summary>
    /// Ensure Cursors buffer (atomic write positions) can hold the specified number of chunks
    /// </summary>
    public void EnsureCursorsCapacity(int chunkCount)
    {
        if (cursorsSSBO != 0 && cursorsCapacity >= chunkCount)
            return;

        var sizeBytes = chunkCount * sizeof(int);
        ResizeOrCreateBuffer(
            ref cursorsSSBO,
            ref cursorsCapacity,
            chunkCount,
            sizeBytes,
            "Cursors_SSBO",
            BufferStorageFlags.DynamicStorageBit
        );

        Log.Debug($"Cursors buffer: {chunkCount} chunks, {sizeBytes / 1024}KB");
    }

    /// <summary>
    /// Get buffer handle for ChunkVoxels (binding=0)
    /// </summary>
    public uint GetChunkVoxelsBuffer() => chunkVoxelsSSBO;

    /// <summary>
    /// Get buffer handle for EditMasks (binding=1)
    /// </summary>
    public uint GetEditMasksBuffer() => editMasksSSBO;

    /// <summary>
    /// Get buffer handle for ColumnMeta (binding=2)
    /// </summary>
    public uint GetColumnMetaBuffer() => columnMetaSSBO;

    /// <summary>
    /// Get buffer handle for SpanIntervals (binding=3)
    /// </summary>
    public uint GetSpanIntervalsBuffer() => spanIntervalsSSBO;

    /// <summary>
    /// Get buffer handle for VisibilityCounts (binding=20)
    /// </summary>
    public uint GetVisibilityCountsBuffer() => visibilityCountsSSBO;

    /// <summary>
    /// Get buffer handle for Offsets (binding=21)
    /// </summary>
    public uint GetOffsetsBuffer() => offsetsSSBO;

    /// <summary>
    /// Get buffer handle for Cursors (binding=22)
    /// </summary>
    public uint GetCursorsBuffer() => cursorsSSBO;

    /// <summary>
    /// Get current capacity for ChunkVoxels buffer (in voxels)
    /// </summary>
    public int GetChunkVoxelsCapacity() => chunkVoxelsCapacity;

    private void ResizeOrCreateBuffer(
        ref uint bufferHandle,
        ref int capacity,
        int requiredElements,
        int sizeBytes,
        string debugName,
        BufferStorageFlags flags)
    {
        // Delete old buffer if exists
        if (bufferHandle != 0)
        {
            GL.DeleteBuffer(bufferHandle);
            bufferHandle = 0;
        }

        // Create new buffer
        GL.CreateBuffers(1, out bufferHandle);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, bufferHandle, -1, debugName);
        GL.NamedBufferStorage(bufferHandle, sizeBytes, IntPtr.Zero, flags);
        
        capacity = requiredElements;
        
        Log.CheckGlError($"GpuBufferAllocator: {debugName}");
    }

    public void Dispose()
    {
        if (chunkVoxelsSSBO != 0) { GL.DeleteBuffer(chunkVoxelsSSBO); chunkVoxelsSSBO = 0; }
        if (editMasksSSBO != 0) { GL.DeleteBuffer(editMasksSSBO); editMasksSSBO = 0; }
        if (columnMetaSSBO != 0) { GL.DeleteBuffer(columnMetaSSBO); columnMetaSSBO = 0; }
        if (spanIntervalsSSBO != 0) { GL.DeleteBuffer(spanIntervalsSSBO); spanIntervalsSSBO = 0; }
        if (visibilityCountsSSBO != 0) { GL.DeleteBuffer(visibilityCountsSSBO); visibilityCountsSSBO = 0; }
        if (offsetsSSBO != 0) { GL.DeleteBuffer(offsetsSSBO); offsetsSSBO = 0; }
        if (cursorsSSBO != 0) { GL.DeleteBuffer(cursorsSSBO); cursorsSSBO = 0; }

        Log.Info("GpuBufferAllocator: Disposed all GPU buffers");
    }
}
