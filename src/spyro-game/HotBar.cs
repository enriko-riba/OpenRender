using OpenRender.Components;
using OpenRender.Core;
using OpenTK.Mathematics;
using SpyroGame.World;

namespace SpyroGame;

internal class HotBar : Sprite
{
    public const int Width = 182 * 3;
    public const int Height = 22 * 3;
    private Inventory inventory;

    public static HotBar Create(int x, int y, World.Inventory inventory, Color4 color)
    {
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/gui/hotbar.png");
        return new HotBar(mesh, material, x, y, inventory, color);
    }

    public HotBar(Mesh mesh, Material material, int x, int y, World.Inventory inventory, Color4 color) : base(mesh, material)
    {
        this.inventory = inventory;
        SetPosition(new(x, y));
        Size = new Vector2i(Width, Height);
        Tint = color;
        Pivot = new(0.5f, 1.0f);
    }

    public override void OnDraw(double elapsed)
    {
        base.OnDraw(elapsed);

        //  for each item in inventory slots 0-9 render the item in the corresponding hotbar slot
        for (var i = 0; i < 9; i++)
        {
            var item = inventory.GetItem(i);
            if (item.IsEmpty) continue;
            //var itemSprite = ItemSprite.CreateForItem(item.Item);
            //if (itemSprite == null) continue;
            //var slotX = (int)(Position.X - Width / 2 + 4 + i * (20 * 3 + 2));
            //var slotY = (int)(Position.Y - Height + 4);
            //itemSprite.SetPosition(new(slotX, slotY));
            //itemSprite.Size = new Vector2i(20 * 3, 20 * 3);
            //itemSprite.Pivot = new(0.0f, 0.0f);
            //itemSprite.Tint = Color4.White;
            //itemSprite.OnDraw(elapsed);
            ////  render item count if more than 1
            //if (item.Count > 1)
            //{
            //    var font = GuiManager.DefaultFont;
            //    var text = item.Count.ToString();
            //    var textWidth = font.MeasureTextWidth(text, 12 * 3);
            //    var textSprite = new TextSprite(font, text, slotX + (20 * 3) - textWidth - 2, slotY + (20 * 3) - (14 * 3), 12 * 3, Color4.White);
            //    textSprite.OnDraw(elapsed);
            //}
        }
        //  render outline for the selected slot
    }
}
