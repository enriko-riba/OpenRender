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

    public bool IsDestroyed { get; set; }
}

public enum BlockType 
{
    None,
    Rock,
    Dirt,
    Grass,
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