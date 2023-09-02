namespace snake.Logic;
public class SnakeTile
{
    public SnakeTile(int x, int y, Direction direction, FrameType frameType)
    {
        X = x;
        Y = y;
        Direction = direction;
        FrameType = frameType;
    }

    public int X { get; set; }
    public int Y { get; set; }
    public Direction Direction { get; set; }
    public Direction CornerDirection { get; set; }
    public FrameType FrameType { get; set; }

    public override string ToString() => $"({X},{Y}) {FrameType} -> {Direction} {(FrameType == FrameType.BodyCorner ? CornerDirection : string.Empty)}";
}
