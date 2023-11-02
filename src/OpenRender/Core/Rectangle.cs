
namespace OpenRender.Core;

public struct Rectangle : IEquatable<Rectangle>
{
    public int X;
    public int Y;
    public int Width;
    public int Height;

    public Rectangle() { }
    public Rectangle(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public override readonly bool Equals([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] object? obj) => obj is not null && obj is Rectangle rectangle && Equals(rectangle);

    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public readonly bool Equals(Rectangle other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;


    public static bool operator ==(Rectangle left, Rectangle right) => left.Equals(right);

    public static bool operator !=(Rectangle left, Rectangle right) => !(left == right);

    public static Rectangle operator +(Rectangle left, Rectangle right) => new()
    {
        X = left.X + right.X,
        Y = left.Y + right.Y,
        Width = left.Width + right.Width,
        Height = left.Height + right.Height
    };
}