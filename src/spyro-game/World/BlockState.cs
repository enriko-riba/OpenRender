using OpenTK.Mathematics;

namespace SpyroGame.World;

public struct BlockState
{
    public BlockState(int index, Chunk chunk)
    {
        Index = index;
        ChunkIndex = chunk.Index;
        var lx = index % VoxelHelper.ChunkSideSize;
        var ly = index / VoxelHelper.ChunkSideSizeSquare;
        var lz = (index / VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
        LocalPosition = new Vector3i(lx, ly, lz);
        GlobalPosition = chunk.Position + LocalPosition;
        Aabb = new AABB(GlobalPosition, GlobalPosition + Vector3i.One);
    }

    public BlockState(Vector3i globalPosition, BlockType blockType)
    {
        GlobalPosition = globalPosition;
        BlockType = blockType;
        // Calculate local position and chunk index from global position
        ChunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(globalPosition);
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(ChunkIndex);
        LocalPosition = globalPosition - chunkPos;
        Index = LocalPosition.X + LocalPosition.Z * VoxelHelper.ChunkSideSize + LocalPosition.Y * VoxelHelper.ChunkSideSizeSquare;
        Aabb = new AABB(GlobalPosition, GlobalPosition + Vector3i.One);
    }

    public int Index { get; private set; }

    public int ChunkIndex { get; private set; }

    public AABB Aabb { get; private set; }
    
    public BlockType BlockType { get; set; }

    /// <summary>
    /// 0 = South, 1 = East, 2 = North, 3 = West, 4 = Top, 5 = Bottom
    /// </summary>
    public BlockDirection FrontDirection { get; set; }


    /// <summary>
    /// Gets or sets the blocks visibility.
    /// Note that only visible blocks are being rendered.
    /// </summary>
    public bool IsVisible { get; internal set; }

    /// <summary>
    /// Packed ambient occlusion values per face (3 bits per face).
    /// </summary>
    public uint PackedAO { get; internal set; }

    /// <summary>
    /// Returns true if the block is None or WaterLevel.
    /// </summary>
    public readonly bool IsTransparent => BlockType is BlockType.WaterLevel or BlockType.None;

    public Vector3i LocalPosition { get; private set; }
    public Vector3i GlobalPosition { get; private set; } 

    public override readonly string ToString() => $"{BlockType}@{LocalPosition}/{ChunkIndex}";
}
