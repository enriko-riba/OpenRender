using OpenTK.Mathematics;

namespace DarkVox.Shared.World;

public enum TerrainChunkState
{
    Unknown = 0,
    Pending = 1,
    Processing = 2,
    Ready = 3,
    HasTerrain = 4,
    Unloaded = 5,
}

/// <summary>
/// Lightweight client-side descriptor for a chunk.
/// Used by rendering/meshing systems to track per-chunk state without forcing voxel data to be resident.
/// </summary>
public sealed class ChunkDescriptor
{
    public int ChunkIndex { get; set; }

    public Vector3i ChunkPosition => VoxelHelper.GetChunkPositionGlobal(ChunkIndex);

    public long Version { get; set; }

    public TerrainChunkState State { get; set; }

    public int VisibleVoxelCount { get; set; }

    public bool IsDirty { get; set; }

    public int AtlasOffset { get; set; }

    public int IndexOffset { get; set; }

    public int CommandSlot { get; set; } = -1;

    public int GenerationStartFrame { get; set; }

    public ulong Fence { get; set; }

    public int MaxSurfaceHeight { get; set; }

    public long MeshVersion { get; set; }

    public ChunkDescriptor() { }

    public ChunkDescriptor(int chunkIndex)
    {
        ChunkIndex = chunkIndex;
        State = TerrainChunkState.Pending;
    }
}
