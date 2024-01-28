using OpenTK.Mathematics;

namespace SpyroGame.World;


public struct BlockState
{
    public BlockState(int index, Chunk chunk)
    {
        Index = index;
        ChunkIndex = chunk.Index;
        GlobalPosition = chunk.Position + LocalPosition;
        Aabb = new AABB(GlobalPosition, GlobalPosition + Vector3i.One);
    }

    public int Index { get; private set; }

    public int ChunkIndex { get; private set; }

    //public Chunk Chunk { get; private set; }
    public AABB Aabb { get; private set; }

    /// <summary>
    /// 0 = South, 1 = East, 2 = North, 3 = West, 4 = Top, 5 = Bottom
    /// </summary>
    public BlockDirection FrontDirection { get; set; }

    public BlockType BlockType { get; set; }

    public bool IsVisible { get; set; }
    public byte Reserved1;
    public byte Reserved2;
    public byte Reserved3;

    /// <summary>
    /// Returns true if the block type is none.
    /// </summary>
    public readonly bool IsAir => BlockType is BlockType.None;

    /// <summary>
    /// Returns true if the block is none or waterlevel.
    /// </summary>
    public readonly bool IsTransparent => BlockType is BlockType.WaterLevel or BlockType.None;

    public readonly Vector3i LocalPosition => new(Index % VoxelHelper.ChunkSideSize,
                                                  Index / VoxelHelper.ChunkSideSizeSquare,
                                                  Index / VoxelHelper.ChunkSideSize % VoxelHelper.ChunkSideSize);
    public Vector3i GlobalPosition { get; private set; } 

    public override readonly string ToString() => $"{BlockType}@{LocalPosition}/{ChunkIndex}";
}

public enum BlockType
{
    None,
    WaterLevel,
    Rock,
    Sand,
    Dirt,
    GrassDirt,
    Grass,
    Snow,
    BedRock
}

public enum BlockDirection : byte
{
    South,
    East,
    North,
    West,
    Top,
    Bottom
}