using System.Runtime.InteropServices;

namespace SpyroGame.World;

/// <summary>
/// Describes the state and GPU resource locations for a single chunk.
/// CPU-side metadata that tracks GPU buffer offsets and sync state.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ChunkDescriptor
{
    /// <summary>
    /// World-space chunk index (flattened: z * WorldChunksXZ + x)
    /// </summary>
    public int ChunkIndex;

    /// <summary>
    /// Offset into ChunkVoxels SSBO (in voxels, not bytes)
    /// </summary>
    public int VoxelBufferOffset;

    /// <summary>
    /// Offset into AtlasSSBO for compacted visible voxels
    /// </summary>
    public int AtlasOffset;

    /// <summary>
    /// Number of visible voxels in this chunk (instanceCount for rendering)
    /// </summary>
    public int VisibleVoxelCount;

    /// <summary>
    /// Current state of this chunk in the pipeline
    /// </summary>
    public TerrainChunkState State;

    /// <summary>
    /// GL fence object for tracking GPU completion (IntPtr to avoid unsafe)
    /// </summary>
    public IntPtr Fence;

    /// <summary>
    /// Frame number when this chunk was last accessed (for LRU eviction)
    /// </summary>
    public long LastAccessFrame;

    /// <summary>
    /// Priority for generation (0=highest, based on distance to camera)
    /// </summary>
    public int Priority;

    public override string ToString()
    {
        return $"Chunk[{ChunkIndex}] State={State}, Visible={VisibleVoxelCount}, AtlasOff={AtlasOffset}";
    }
}

/// <summary>
/// Lifecycle states for a chunk descriptor.
/// Tracks progression through the GPU pipeline.
/// </summary>
public enum TerrainChunkState : byte
{
    /// <summary>
    /// Queued for generation but not submitted yet
    /// </summary>
    Pending = 0,

    /// <summary>
    /// GPU generation pass in flight (fence not signaled)
    /// </summary>
    Generating = 1,

    /// <summary>
    /// GPU visibility pass in flight
    /// </summary>
    CountingVisibility = 2,

    /// <summary>
    /// GPU compaction pass in flight
    /// </summary>
    Compacting = 3,

    /// <summary>
    /// Ready for rendering (fence signaled, data valid)
    /// </summary>
    Ready = 4,

    /// <summary>
    /// Needs regeneration due to edit or update
    /// </summary>
    Dirty = 5,

    /// <summary>
    /// Marked for unloading (will be removed next frame)
    /// </summary>
    Unloading = 6
}

/// <summary>
/// Extension methods for ChunkDescriptor
/// </summary>
public static class ChunkDescriptorExtensions
{
    /// <summary>
    /// Check if chunk is in a terminal state (ready or failed)
    /// </summary>
    public static bool IsComplete(this ChunkDescriptor descriptor)
    {
        return descriptor.State == TerrainChunkState.Ready || 
               descriptor.State == TerrainChunkState.Dirty;
    }

    /// <summary>
    /// Check if chunk is currently processing on GPU
    /// </summary>
    public static bool IsInFlight(this ChunkDescriptor descriptor)
    {
        return descriptor.State == TerrainChunkState.Generating ||
               descriptor.State == TerrainChunkState.CountingVisibility ||
               descriptor.State == TerrainChunkState.Compacting;
    }

    /// <summary>
    /// Check if chunk needs GPU work
    /// </summary>
    public static bool NeedsProcessing(this ChunkDescriptor descriptor)
    {
        return descriptor.State == TerrainChunkState.Pending || 
               descriptor.State == TerrainChunkState.Dirty;
    }
}
