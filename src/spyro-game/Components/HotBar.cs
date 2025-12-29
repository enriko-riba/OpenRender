using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.World;
using SpyroGame.World.Registry;

namespace SpyroGame.Components;

internal class HotBar : Sprite
{
    public const int FontSize = 16;
    public const int Width = 182 * ScaleFactor;
    public const int Height = 22 * ScaleFactor;
    private const int Slots = 9;
    private const int OverlayPadding = 4;
    private const int ScaleFactor = 3;

    private static readonly Vector3 CountColor = new(0.95f, 0.98f, 0.2f);

    private readonly Inventory inventory;
    private readonly Sprite[] slotSprites = new Sprite[Slots];
    private readonly GameObjectId[] lastDisplayedItems;
    private readonly Sprite activeSlotSprite = default!;
    private readonly ITextRenderer textRenderer;
    private int lastInventoryVersion = -1;

    /// <summary>
    /// Gets or sets visibility for the hotbar and all its children.
    /// </summary>
    public new bool IsVisible
    {
        get => base.IsVisible;
        set
        {
            base.IsVisible = value;
            foreach (var child in Children)
            {
                child.IsVisible = value;
            }
        }
    }

    public static HotBar Create(int x, int y, Inventory inventory, ITextRenderer textRenderer)
    {
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/gui/hotbar.png");
        return new HotBar(mesh, material, x, y, inventory, textRenderer);
    }


    public HotBar(
        Mesh mesh,
        Material material,
        int x, int y,
        Inventory inventory,
        ITextRenderer textRenderer) : base(mesh, material)
    {
        this.textRenderer = textRenderer;
        this.inventory = inventory;
        SetPosition(new(x, y));
        Size = new Vector2i(Width, Height);
        Pivot = new(0.5f, 1.0f);

        // Initialize lastDisplayedItems with a sentinel value to force first update
        lastDisplayedItems = new GameObjectId[Slots];
        for (var i = 0; i < Slots; i++)
        {
            lastDisplayedItems[i] = (GameObjectId)ushort.MaxValue; // Invalid sentinel value
        }

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

        var overlay = new TextOverlay(this, textRenderer, mesh, material);
        AddChild(overlay);
    }


    public override void OnUpdate(Scene scene, double elapsed)
    {
        // CRITICAL: Check inventory version even when hidden to detect changes made
        // while the inventory panel was open. This ensures the HotBar refreshes 
        // properly when it becomes visible again.
        if (lastInventoryVersion != inventory.Version)
        {
            lastInventoryVersion = inventory.Version;
            // Reset all tracked items to force material recreation
            for (var i = 0; i < Slots; i++)
            {
                lastDisplayedItems[i] = (GameObjectId)ushort.MaxValue;
            }
        }

        // Don't update visuals if the hotbar is hidden (e.g., inventory is open)
        if (!base.IsVisible) return;

        //  for each item in inventory slots 0-9 render the item in the corresponding hotbar slot
        for (var i = 0; i < Slots; i++)
        {
            var inventoryItem = inventory.GetItem(i);
            if (inventoryItem.IsEmpty)
            {
                slotSprites[i].IsVisible = false;
                lastDisplayedItems[i] = GameObjectId.Air;
                continue;
            }
            slotSprites[i].IsVisible = true;

            // Only update material if the item changed (creates new material with unique ID)
            if (lastDisplayedItems[i] != inventoryItem.Item)
            {
                lastDisplayedItems[i] = inventoryItem.Item;
                var shader = slotSprites[i].Material.Shader;
                slotSprites[i].Material = ItemTextureManager.CreateMaterialForItem(inventoryItem.Item, shader);
            }

            var renderShape = BlockRenderShape.None;
            var item = GameContentRegistry.Get(inventoryItem.Item);
            if (item is Block blockItem)
            {
                renderShape = GameContentRegistry.GetRenderShape(blockItem.BlockId);
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

    // OnDraw removed

    /// <summary>
    /// Renders total item counts (inventory + hotbar) in the lower-right corner of each populated slot.
    /// </summary>
    private void RenderItemCounts(ITextRenderer textRenderer)
    {
        ArgumentNullException.ThrowIfNull(textRenderer);

        var totals = BuildInventoryTotals();
        for (var slotIndex = 0; slotIndex < Inventory.HotbarSize; slotIndex++)
        {
            var hotbarItem = inventory.GetItem(slotIndex);
            if (hotbarItem.IsEmpty)
            {
                continue;
            }

            if (!totals.TryGetValue(hotbarItem.Item, out var totalCount) || totalCount <= 0)
            {
                continue;
            }

            var slotRect = GetSlotScreenRectangle(slotIndex);
            var text = totalCount.ToString();
            var measurement = textRenderer.Measure(text, FontSize);

            var textX = slotRect.X + slotRect.Width - measurement.Width - OverlayPadding;
            var textY = slotRect.Y + slotRect.Height - measurement.Height - OverlayPadding;
            textX = Math.Max(textX, slotRect.X);
            textY = Math.Max(textY, slotRect.Y);

            textRenderer.Render(text, FontSize, textX, textY, CountColor);
        }
    }

    private Dictionary<GameObjectId, int> BuildInventoryTotals()
    {
        var totals = new Dictionary<GameObjectId, int>();
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var slot = inventory.GetItem(i);
            if (slot.IsEmpty)
            {
                continue;
            }

            totals[slot.Item] = totals.TryGetValue(slot.Item, out var existing) ?
                existing + slot.Count : slot.Count;
        }

        return totals;
    }

    /// <summary>
    /// Returns the screen-aligned rectangle for the given hotbar slot.
    /// </summary>
    public Rectangle GetSlotScreenRectangle(int slotIndex)
    {
        if (slotIndex is < 0 or >= Slots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        var topLeft = GetHotBarTopLeft();
        var localRect = GetSlotLocalRectangle(slotIndex);
        return new Rectangle(
            (int)MathF.Round(topLeft.X + localRect.X),
            (int)MathF.Round(topLeft.Y + localRect.Y),
            localRect.Width,
            localRect.Height);
    }

    private Rectangle GetSlotLocalRectangle(int slotIndex)
    {
        slotSprites[slotIndex].GetPosition(out var pos);
        return new Rectangle((int)pos.X, (int)pos.Y, slotSprites[slotIndex].Size.X, slotSprites[slotIndex].Size.Y);
    }

    private Vector2 GetHotBarTopLeft()
    {
        GetPosition(out var pos);
        var size = Size;
        var pivot = Pivot;
        return new Vector2(
            pos.X - size.X * pivot.X,
            pos.Y - size.Y * pivot.Y);
    }

    private class TextOverlay : SceneNode
    {
        private readonly HotBar hotBar;
        private readonly ITextRenderer textRenderer;

        public TextOverlay(HotBar hotBar, ITextRenderer textRenderer, Mesh mesh, Material material) 
            : base(mesh, material)
        {
            this.hotBar = hotBar;
            this.textRenderer = textRenderer;
            DisableCulling = true;
            RenderGroup = RenderGroup.UI;
        }

        public override void OnDraw(double elapsed) => hotBar.RenderItemCounts(textRenderer);
    }
}
