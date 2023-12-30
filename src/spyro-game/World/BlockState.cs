using System.Runtime.InteropServices;

namespace SpyroGame.World;

[StructLayout(LayoutKind.Sequential)]
internal struct BlockState
{
    public int Index { get; set; }

    public BlockType BlockType { get; set; }

    /// <summary>
    /// 0 = South, 1 = East, 2 = North, 3 = West, 4 = Top, 5 = Bottom
    /// </summary>
    public BlockDirection FrontDirection { get; set; }

    public readonly bool IsAir => BlockType == BlockType.None;
    
    public readonly bool IsTransparent => BlockType is BlockType.Water or BlockType.None;

    public override readonly string ToString() => $"{Index}:{BlockType}";
}

public enum BlockType 
{
    None,
    Rock,
    Dirt,
    GrassDirt,
    Grass,
    Sand,
    Snow,
    Water,
}

public enum BlockDirection
{
    South,
    East,
    North,
    West,
    Top,
    Bottom
}