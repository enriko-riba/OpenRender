using OpenRender.Components;
using OpenRender.Core;
using OpenTK.Mathematics;

namespace SpyroGame;

internal class HotBar : Sprite
{
    public const int Width = 182 * 3;
    public const int Height = 22 * 3;

    public static HotBar Create(int x, int y, Color4 color)
    {
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/gui/hotbar.png");
        return new HotBar(mesh, material, x, y, color);
    }

    public HotBar(Mesh mesh, Material material, int x, int y, Color4 color) : base(mesh, material)
    {
        SetPosition(new(x, y));
        Size = new Vector2i(Width, Height);
        Tint = color;
        Pivot = new(0.5f, 1.0f);
    }
}
