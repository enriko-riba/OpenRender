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
    /// Offset into vertex buffer for compacted visible vertices (in vertices, not bytes)
    /// </summary>
    public int AtlasOffset;

    /// <summary>
    /// Offset into index buffer for this chunk's indices (in indices, not bytes)
    /// </summary>
    public int IndexOffset;

    /// <summary>
    /// Slot index in the Indirect Draw Buffer (0..MaxChunks-1)
    /// </summary>
    public int CommandSlot;

    /// <summary>
    /// Number of visible faces in this chunk
    /// </summary>
    public int VisibleVoxelCount;

    /// <summary>
    /// Current state of this chunk in the pipeline
    /// </summary>
    public TerrainChunkState State;

    /// <summary>
    /// Frame number when generation started (for stuck detection)
    /// </summary>
    public long GenerationStartFrame;

    /// <summary>
    /// GL sync fence for pending GPU operations (upload/copy).
    /// Zero when no operation is pending.
    /// </summary>
    public nint Fence;
    
    /// <summary>
    /// Maximum surface height in this chunk (highest non-air block Y + 1).
    /// Used for tighter frustum culling AABB. Default 0 means use full chunk height.
    /// </summary>
    public int MaxSurfaceHeight;

    public override readonly string ToString() 
        => $"Chunk[{ChunkIndex}] State={State}, Faces={VisibleVoxelCount}, VtxOff={AtlasOffset}, IdxOff={IndexOffset}, MaxY={MaxSurfaceHeight}";
}

/// <summary>
/// Simplified lifecycle states for chunk processing.
/// Linear progression: Pending → Generating → HasBaseTerrain → Decorating → HasTerrain → Processing → Ready
/// </summary>
public enum TerrainChunkState : byte
{
    /// <summary>
    /// Queued for terrain generation but not submitted yet
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Base terrain generation in progress (background thread)
    /// </summary>
    Generating = 1,

    /// <summary>
    /// Base terrain generated (voxels + heightmap), waiting for neighbors to be ready for decoration
    /// </summary>
    HasBaseTerrain = 5,

    /// <summary>
    /// Vegetation and decoration in progress (background thread)
    /// </summary>
    Decorating = 6,

    /// <summary>
    /// Has full voxel data (terrain + vegetation), needs mesh/light calculation.
    /// Also used when Ready chunk needs reprocessing (neighbor loaded).
    /// </summary>
    HasTerrain = 2,

    /// <summary>
    /// Mesh/light calculation in progress (background thread)
    /// </summary>
    Processing = 3,

    /// <summary>
    /// Fully processed, ready for rendering
    /// </summary>
    Ready = 4
}
