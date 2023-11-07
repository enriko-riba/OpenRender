using OpenRender.Components;
using OpenRender.Core;
using OpenTK.Mathematics;

using static Samples.Snake.Constants;

namespace Samples.Snake;

internal class Ground : Sprite
{
    public static Ground Create(int x, int y, int width, int height, Color4 color)
    {
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/atlas.png");
        return new Ground(mesh, material, x, y, width, height, color);
    }

    public Ground(Mesh mesh, Material material, int x, int y, int width, int height, Color4 color) : base(mesh, material)
    {
        SetPosition(new(x, y));
        SourceRectangle = new Rectangle(65, 65, SmallTileSourceSize, SmallTileSourceSize);   //  position of white quad
        Size = new Vector2i(width, height);
        Tint = color;
    }
}
