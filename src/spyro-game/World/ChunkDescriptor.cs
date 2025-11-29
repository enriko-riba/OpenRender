using System.Runtime.InteropServices;

namespace SpyroGame.World;

/// <summary>
/// Describes the state and GPU resource locations for a single chunk.
/// CPU-side metadata that tracks GPU buffer offsets and sync state.
/// Phase 5.3: Now tracks both vertex and index buffer regions.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ChunkDescriptor
{
    /// <summary>
    /// World-space chunk index (flattened: z * WorldChunksXZ + x)
    /// </summary>
    public int ChunkIndex;

    /// <summary>
    /// Offset into vertex buffer for compacted visible vertices (in vertices, not bytes)
    /// Phase 5.3: This is the baseVertex for indirect draw commands
    /// </summary>
    public int AtlasOffset;

    /// <summary>
    /// Offset into index buffer for this chunk's indices (in indices, not bytes)
    /// Phase 5.3: NEW - tracks per-chunk index buffer region
    /// </summary>
    public int IndexOffset;

    /// <summary>
    /// Slot index in the Indirect Draw Buffer (0..MaxChunks-1)
    /// Phase 5.3: Tracks where this chunk's draw command is stored on GPU
    /// </summary>
    public int CommandSlot;

    /// <summary>
    /// Number of visible faces in this chunk
    /// Phase 5.3: Used to calculate vertex count (faces * 4) and index count (faces * 6)
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
    /// Frame number when generation started (for stuck detection)
    /// </summary>
    public long GenerationStartFrame;

    /// <summary>
    /// Bitmask of neighbors that were assumed solid because their chunks were missing when this chunk was generated.
    /// Bits: 1=+X, 2=-X, 4=+Z, 8=-Z.
    /// </summary>
    public byte PlaceholderMask;

    /// <summary>
    /// Get total vertex count for this chunk (4 vertices per face)
    /// </summary>
    //public readonly int VertexCount => VisibleVoxelCount * 4;

    /// <summary>
    /// Get total index count for this chunk (6 indices per face)
    /// </summary>
    //public readonly int IndexCount => VisibleVoxelCount * 6;

    public override readonly string ToString() 
        => $"Chunk[{ChunkIndex}] State={State}, Faces={VisibleVoxelCount}, VtxOff={AtlasOffset}, IdxOff={IndexOffset}, PlaceholderMask={PlaceholderMask}";
}

/// <summary>
/// Lifecycle states for a chunk descriptor.
/// Tracks progression through the CPU pipeline.
/// </summary>
public enum TerrainChunkState : byte
{
    /// <summary>
    /// Queued for generation but not submitted yet
    /// </summary>
    Pending = 0,

    /// <summary>
    /// CPU generation in progress
    /// </summary>
    Generating = 1,

    /// <summary>
    /// CPU meshing in progress
    /// </summary>
    CountingVisibility = 2,

    /// <summary>
    /// Ready for rendering
    /// </summary>
    Ready = 3,

    /// <summary>
    /// Needs regeneration due to edit or neighbor loaded
    /// </summary>
    Dirty = 4,

    /// <summary>
    /// Marked for unloading (will be removed next frame)
    /// </summary>
    Unloading = 5
}

/// <summary>
/// Extension methods for ChunkDescriptor
/// </summary>
public static class ChunkDescriptorExtensions
{
    /// <summary>
    /// Check if chunk is in a terminal state (ready or dirty)
    /// </summary>
    public static bool IsComplete(this ChunkDescriptor descriptor) 
        => descriptor.State is TerrainChunkState.Ready or TerrainChunkState.Dirty;

    /// <summary>
    /// Check if chunk is currently processing
    /// </summary>
    public static bool IsInFlight(this ChunkDescriptor descriptor) 
        => descriptor.State is TerrainChunkState.Generating or TerrainChunkState.CountingVisibility;
}
