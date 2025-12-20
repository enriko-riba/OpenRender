using OpenRender.Components;
using OpenRender.Core;
using OpenTK.Mathematics;

namespace SpyroGame;

internal class HotBar : Sprite
{
    public static HotBar Create(int x, int y, int width, int height, Color4 color)
    {
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/gui/hotbar.png");
        return new HotBar(mesh, material, x, y, width, height, color);
    }

    public HotBar(Mesh mesh, Material material, int x, int y, int width, int height, Color4 color) : base(mesh, material)
    {
        SetPosition(new(x, y));
        Size = new Vector2i(width, height);
        Tint = color;
    }
}
