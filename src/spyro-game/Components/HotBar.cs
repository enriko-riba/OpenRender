using OpenRender.Components;
using OpenRender.Core;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.World;
using SpyroGame.World.Registry;

namespace SpyroGame.Components;

internal class HotBar : Sprite
{
    private const int ScaleFactor = 3;
    public const int Width = 182 * ScaleFactor;
    public const int Height = 22 * ScaleFactor;
    private const int Slots = 9;

    private readonly Inventory inventory;
    private readonly Sprite[] slotSprites = new Sprite[Slots];
    private readonly Sprite activeSlotSprite = default!;

    public static HotBar Create(int x, int y, Inventory inventory)
    {
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/gui/hotbar.png");
        return new HotBar(mesh, material, x, y, inventory);
    }

    public HotBar(
        Mesh mesh,
        Material material,
        int x, int y,
        Inventory inventory) : base(mesh, material)
    {
        this.inventory = inventory;
        SetPosition(new(x, y));
        Size = new Vector2i(Width, Height);
        Pivot = new(0.5f, 1.0f);


        for (var i = 0; i < Slots; i++)
        {
            slotSprites[i] = Sprite.Create("Resources/voxel/items/stick.png");
            slotSprites[i].Size = new(16 * ScaleFactor);
            slotSprites[i].Pivot = new(0, 0);
            slotSprites[i].SetPosition(new(3 * ScaleFactor + (20 * ScaleFactor * i), 3 * ScaleFactor));

            AddChild(slotSprites[i]);
        }

        activeSlotSprite = Sprite.Create("Resources/gui/hotbar_selection.png");
        activeSlotSprite.Size = new(22 * ScaleFactor);
        activeSlotSprite.Pivot = new(0, 0);
        AddChild(activeSlotSprite);
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        //  for each item in inventory slots 0-9 render the item in the corresponding hotbar slot
        for (var i = 0; i < Slots; i++)
        {
            var inventoryItem = inventory.GetItem(i);
            if (inventoryItem.IsEmpty)
            {
                slotSprites[i].IsVisible = false;
                continue;
            }
            slotSprites[i].IsVisible = true;

            var mat = ItemTextureManager.GetMaterial(inventoryItem.Item);
            mat.Shader = slotSprites[i].Material.Shader;
            slotSprites[i].Material = mat;

            var renderShape = BlockRenderShape.None;
            var item = ItemRegistry.Get(inventoryItem.Item);
            if (item is BlockItem blockItem)
            {
                renderShape = BlockRegistry.GetRenderShape(blockItem.BlockId);
            }

            //  cross billboards use the simple atlas 3x1 frames layout but only the last frame holds the texture
            slotSprites[i].SourceRectangle = renderShape == BlockRenderShape.CrossBillboard
                ? new Rectangle(100, 0, 50, 50)
                : new Rectangle(0, 0, 50, 50);
        }

        //  outline for the selected slot
        activeSlotSprite.SetPosition(new Vector2i(inventory.SelectedSlot * 20 * ScaleFactor, 0));
        base.OnUpdate(scene, elapsed);
    }
}
