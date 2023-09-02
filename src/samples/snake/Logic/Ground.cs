using OpenRender.Components;
using OpenRender.Core;
using OpenTK.Mathematics;

namespace snake.Logic;

internal class Ground : Sprite
{
    public Ground(int x, int y, int width, int height, Color4 color): base("Resources/atlas.png")
    {
        SetPosition(new(x, y));
        SourceRectangle = new Rectangle(0, 64, 64, 64);   //  position of white quad
        Size = new Vector2i(width, height);
        Tint = color;
    }        
}
