using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace SpyroGame.World;

[StructLayout(LayoutKind.Sequential)]
public struct BlockState
{
    public int Index { get; set; }

    /// <summary>
    /// 0 = South, 1 = East, 2 = North, 3 = West, 4 = Top, 5 = Bottom
    /// </summary>
    public BlockDirection FrontDirection { get; set; }

    public BlockType BlockType { get; set; }

    public bool IsVisible { get; set; }
    public byte Reserved1;
    public byte Reserved2;
    public byte Reserved3;

    public readonly bool IsAir => BlockType is BlockType.None;

    public readonly bool IsTransparent => BlockType is BlockType.WaterLevel or BlockType.None;

    public readonly Vector3i LocalPosition => new(Index % VoxelHelper.ChunkSideSize, 
                                                  Index / VoxelHelper.ChunkSideSizeSquare, 
                                                  Index / VoxelHelper.ChunkSideSize % VoxelHelper.ChunkSideSize);

    public override readonly string ToString() => $"{Index}:{BlockType}@{LocalPosition}";
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