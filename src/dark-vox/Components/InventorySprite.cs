using OpenRender;
using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using DarkVox.Shared.Commands;
using DarkVox.Shared.Gameplay;
using DarkVox.Shared.World.Registry;
using DarkVox.World;
using DarkVox.Shared.Gameplay.Crafting;

namespace DarkVox.Components;

internal class InventorySprite : Sprite
{
    private const int SlotSize = 68;
    private const int ContentSize = 64;
    private const int ContentOffset = 2;
    private const int SlotSpacing = 72;

    private const int StorageStartX = 30;
    private const int StorageStartY = 334;
    private const int HotbarStartX = 30;
    private const int HotbarStartY = 566;

    // Crafting (2x2) and result slot positions (relative to the inventory panel top-left).
    private const int CraftingStartX = 390;
    private const int CraftingStartY = 70;
    private const int CraftingResultX = 614;
    private const int CraftingResultY = 110;

    private const int FontSize = 16;
    private const int OverlayPadding = 4;
    private static readonly Vector3 CountColor = new(0.95f, 0.98f, 0.2f);
    private const int ScaleFactor = 1;

    private readonly Inventory inventory;
    private readonly CraftingGrid crafting;
    private readonly Sprite[] slotSprites = new Sprite[Inventory.SlotCount];
    private readonly GameObjectId[] lastDisplayedItems = new GameObjectId[Inventory.SlotCount];

    private readonly Sprite[] craftingSlotSprites = new Sprite[CraftingGrid.SlotCount];
    private readonly GameObjectId[] lastDisplayedCraftingItems = new GameObjectId[CraftingGrid.SlotCount];
    private readonly Sprite craftingResultSprite;
    private GameObjectId lastDisplayedCraftingResult = GameObjectId.Air;

    private InventoryItem heldItem;
    private SlotRef dragSource = default;
    private bool isFullStackPickup;
    private GameObjectId lastHeldItemId = GameObjectId.Air;
    private readonly Sprite heldItemSprite;
    private int lastInventoryVersion = -1;
    private int lastCraftingVersion = -1;

    private bool leftWasDown;
    private bool rightWasDown;

    private enum DragPaintMode
    {
        None = 0,
        LeftDistribute = 1,
        RightPaintOne = 2,
    }

    private DragPaintMode paintMode;
    private readonly HashSet<SlotRef> paintVisited = [];
    private readonly List<SlotRef> paintSequence = [];

    /// <summary>
    /// Called when an inventory move operation is performed.
    /// The callback should send the move command to the server.
    /// </summary>
    public Action<InventoryMoveCommand>? OnInventoryMove { get; set; }

    /// <summary>
    /// Called when an item is returned from hotbar to storage.
    /// The callback should send the return-to-storage command to the server.
    /// </summary>
    public Action<ReturnToStorageCommand>? OnReturnToStorage { get; set; }

    /// <summary>
    /// Called when an item is moved between inventory and crafting grid.
    /// </summary>
    public Action<ContainerMoveCommand>? OnContainerMove { get; set; }

    /// <summary>
    /// Called when the player clicks the crafting result slot.
    /// </summary>
    public Action<CraftFromGridCommand>? OnCraftFromGrid { get; set; }

    /// <summary>
    /// Called when the player requests clearing the 2x2 crafting grid.
    /// </summary>
    public Action<ClearCraftingGridCommand>? OnClearCraftingGrid { get; set; }

    public bool IsOpen
    {
        get => IsVisible;
        set
        {
            IsVisible = value;

            // NOTE: Do not blindly toggle all children visible when opening.
            // Slot sprites are initialized with a placeholder texture (stick.png) and must be
            // shown/hidden based on actual inventory state via UpdateSlots().

            // When inventory closes, cancel any local drag state.
            // Server snapshots remain authoritative and will drive the UI.
            if (!value)
            {
                // Ensure no slot sprites remain visible while closed.
                for (var i = 0; i < Inventory.SlotCount; i++)
                {
                    slotSprites[i].IsVisible = false;
                }

                heldItemSprite.IsVisible = false;

                heldItem = default;
                dragSource = default;
                lastHeldItemId = GameObjectId.Air;

                for (var i = 0; i < CraftingGrid.SlotCount; i++)
                {
                    craftingSlotSprites[i].IsVisible = false;
                }

                craftingResultSprite.IsVisible = false;

                paintMode = DragPaintMode.None;
                paintVisited.Clear();
                paintSequence.Clear();
                leftWasDown = false;
                rightWasDown = false;
            }
            else
            {
                // Inventory just opened: refresh slot visuals immediately.
                UpdateSlots();
                UpdateCraftingSlots();
                lastInventoryVersion = inventory.Version;
                lastCraftingVersion = crafting.Version;
            }
        }
    }

    public static InventorySprite Create(Inventory inventory, CraftingGrid crafting, ITextRenderer textRenderer)
    {
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/gui/inventory.png");
        return new InventorySprite(mesh, material, inventory, crafting, textRenderer);
    }

    public InventorySprite(Mesh mesh, Material material, Inventory inventory, CraftingGrid crafting, ITextRenderer textRenderer)
        : base(mesh, material)
    {
        this.inventory = inventory;
        this.crafting = crafting;

        Pivot = new Vector2(0.5f, 0.5f);
        RenderGroup = RenderGroup.UI;
        DisableCulling = true;
        IsVisible = false;

        Size = new Vector2i(Size.X * ScaleFactor, Size.Y * ScaleFactor);

        InitializeSlots();

        craftingResultSprite = Sprite.Create("Resources/voxel/items/stick.png");
        craftingResultSprite.Size = new Vector2i(ContentSize);
        craftingResultSprite.Pivot = Vector2.Zero;
        craftingResultSprite.SetPosition(new Vector2(CraftingResultX + ContentOffset, CraftingResultY + ContentOffset));
        craftingResultSprite.IsVisible = false;
        AddChild(craftingResultSprite);

        heldItemSprite = Sprite.Create("Resources/voxel/items/stick.png");
        heldItemSprite.Size = new Vector2i(ContentSize);
        heldItemSprite.Pivot = new Vector2(0.5f, 0.5f); // Center pivot so sprite follows mouse cursor
        heldItemSprite.IsVisible = false;
        AddChild(heldItemSprite);

        var overlay = new TextOverlay(this, textRenderer, mesh, material);
        AddChild(overlay);
    }

    private void InitializeSlots()
    {
        // Storage (9-35)
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 9; col++)
            {
                var index = 9 + row * 9 + col;
                var x = StorageStartX + col * SlotSpacing + ContentOffset;
                var y = StorageStartY + row * SlotSpacing + ContentOffset;
                CreateSlotSprite(index, x, y);
            }
        }

        // Hotbar (0-8)
        for (var col = 0; col < 9; col++)
        {
            var index = col;
            var x = HotbarStartX + col * SlotSpacing + ContentOffset;
            var y = HotbarStartY + ContentOffset;
            CreateSlotSprite(index, x, y);
        }

        // Crafting 2x2 (TL, TR, BL, BR)
        CreateCraftingSlotSprite(0, CraftingStartX, CraftingStartY);
        CreateCraftingSlotSprite(1, CraftingStartX + SlotSpacing, CraftingStartY);
        CreateCraftingSlotSprite(2, CraftingStartX, CraftingStartY + SlotSpacing);
        CreateCraftingSlotSprite(3, CraftingStartX + SlotSpacing, CraftingStartY + SlotSpacing);
    }

    private void CreateCraftingSlotSprite(int index, int slotX, int slotY)
    {
        var sprite = Sprite.Create("Resources/voxel/items/stick.png");
        sprite.Size = new Vector2i(ContentSize);
        sprite.Pivot = Vector2.Zero;
        sprite.SetPosition(new Vector2(slotX + ContentOffset, slotY + ContentOffset));
        sprite.IsVisible = false;

        craftingSlotSprites[index] = sprite;
        AddChild(sprite);
    }

    private void CreateSlotSprite(int index, int x, int y)
    {
        var sprite = Sprite.Create("Resources/voxel/items/stick.png");
        sprite.Size = new Vector2i(ContentSize);
        sprite.Pivot = Vector2.Zero;
        sprite.SetPosition(new Vector2(x, y));
        sprite.IsVisible = false;

        slotSprites[index] = sprite;
        AddChild(sprite);
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        if (!IsVisible) return;

        if (inventory.Version != lastInventoryVersion)
        {
            UpdateSlots();
            lastInventoryVersion = inventory.Version;
        }

        if (crafting.Version != lastCraftingVersion)
        {
            UpdateCraftingSlots();
            lastCraftingVersion = crafting.Version;
        }

        base.OnUpdate(scene, elapsed);
    }

    public override void OnResize(Scene scene, ResizeEventArgs e)
    {
        base.OnResize(scene, e);
        SetPosition(new Vector2(e.Width / 2f, e.Height / 2f));
    }

    private void UpdateSlots()
    {
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var item = inventory.GetItem(i);
            var sprite = slotSprites[i];

            if (item.IsEmpty)
            {
                sprite.IsVisible = false;
                lastDisplayedItems[i] = GameObjectId.Air;
            }
            else
            {
                sprite.IsVisible = true;
                
                // Update material only when item type changes
                if (lastDisplayedItems[i] != item.Item)
                {
                    lastDisplayedItems[i] = item.Item;
                    var shader = sprite.Material.Shader;
                    var newMaterial = ItemTextureManager.CreateMaterialForItem(item.Item, shader);
                    sprite.Material = newMaterial;
                }

                var renderShape = BlockRenderShape.None;
                var itemDef = GameContentRegistry.Get(item.Item);
                if (itemDef is Block blockItem)
                {
                    renderShape = GameContentRegistry.GetRenderShape(blockItem.BlockId);
                }

                sprite.SourceRectangle = renderShape == BlockRenderShape.CrossBillboard
                    ? new Rectangle(100, 0, 50, 50)
                    : new Rectangle(0, 0, 50, 50);
            }
        }
    }

    private void UpdateCraftingSlots()
    {
        for (var i = 0; i < CraftingGrid.SlotCount; i++)
        {
            var item = crafting.GetSlot(i);
            var sprite = craftingSlotSprites[i];

            if (item.IsEmpty)
            {
                sprite.IsVisible = false;
                lastDisplayedCraftingItems[i] = GameObjectId.Air;
                continue;
            }

            sprite.IsVisible = true;

            if (lastDisplayedCraftingItems[i] != item.Item)
            {
                lastDisplayedCraftingItems[i] = item.Item;
                var shader = sprite.Material.Shader;
                sprite.Material = ItemTextureManager.CreateMaterialForItem(item.Item, shader);
            }

            sprite.SourceRectangle = new Rectangle(0, 0, 50, 50);
        }

        var result = crafting.ResultPreview;
        if (result.IsEmpty)
        {
            craftingResultSprite.IsVisible = false;
            lastDisplayedCraftingResult = GameObjectId.Air;
        }
        else
        {
            craftingResultSprite.IsVisible = true;
            if (lastDisplayedCraftingResult != result.Item)
            {
                lastDisplayedCraftingResult = result.Item;
                var shader = craftingResultSprite.Material.Shader;
                craftingResultSprite.Material = ItemTextureManager.CreateMaterialForItem(result.Item, shader);
            }

            craftingResultSprite.SourceRectangle = new Rectangle(0, 0, 50, 50);
        }
    }

    public bool ProcessInput(MouseState mouse, KeyboardState keyboard)
    {
        if (!IsOpen) return false;

        // Update held item sprite position - use screen coordinates directly
        if (!heldItem.IsEmpty)
        {
            heldItemSprite.IsVisible = true;
            
            if (lastHeldItemId != heldItem.Item)
            {
                lastHeldItemId = heldItem.Item;
                var shader = heldItemSprite.Material.Shader;
                heldItemSprite.Material = ItemTextureManager.CreateMaterialForItem(heldItem.Item, shader);
            }

            // Position relative to InventorySprite's center (which is at screen center)
            // The sprite is a child of InventorySprite, so we need local coordinates
            if (Scene != null)
            {
                GetWorldPosition(out var inventoryWorldPos);
                var localX = mouse.Position.X - inventoryWorldPos.X;
                var localY = mouse.Position.Y - inventoryWorldPos.Y;
                heldItemSprite.SetPosition(new Vector2(localX, localY));
            }
        }
        else
        {
            heldItemSprite.IsVisible = false;
            lastHeldItemId = GameObjectId.Air;
        }

        var leftDown = mouse.IsButtonDown(MouseButton.Left);
        var rightDown = mouse.IsButtonDown(MouseButton.Right);
        var leftPressed = mouse.IsButtonPressed(MouseButton.Left);
        var rightPressed = mouse.IsButtonPressed(MouseButton.Right);

        // Craft result is an immediate click (no drag paint).
        if (leftPressed)
        {
            var hit = GetHitSlot(mouse.Position);
            if (hit.Kind == SlotKind.Result)
            {
                if (!crafting.ResultPreview.IsEmpty)
                {
                    OnCraftFromGrid?.Invoke(new CraftFromGridCommand());
                }
                return true;
            }
        }

        // Quick clear action: right-click the result slot to return all crafting items.
        if (rightPressed)
        {
            var hit = GetHitSlot(mouse.Position);
            if (hit.Kind == SlotKind.Result)
            {
                OnClearCraftingGrid?.Invoke(new ClearCraftingGridCommand());
                return true;
            }
        }

        // --- Right-drag painting (drop 1 per slot visited) ---
        if (rightPressed)
        {
            var hit = GetHitSlot(mouse.Position);
            if (hit.Kind != SlotKind.None && hit.Kind != SlotKind.Result)
            {
                if (hit.Kind == SlotKind.Crafting)
                {
                    HandleCraftingRightClickPressed(hit);
                    return true;
                }

                if (heldItem.IsEmpty)
                {
                    // Normal right-click behavior: split stack (rounded up).
                    HandleRightClick(hit);
                    return true;
                }

                // Start painting with the held stack.
                paintMode = DragPaintMode.RightPaintOne;
                paintVisited.Clear();
                paintSequence.Clear();
                TryPaintDropOne(hit);
                return true;
            }
        }

        if (paintMode == DragPaintMode.RightPaintOne)
        {
            if (!rightDown)
            {
                paintMode = DragPaintMode.None;
                paintVisited.Clear();
                paintSequence.Clear();
            }
            else if (!heldItem.IsEmpty)
            {
                var hit = GetHitSlot(mouse.Position);
                if (hit.Kind != SlotKind.None && hit.Kind != SlotKind.Result)
                {
                    TryPaintDropOne(hit);
                }
            }
        }

        // --- Left-drag distribution (evenly split across visited slots on release) ---
        if (leftPressed)
        {
            var hit = GetHitSlot(mouse.Position);
            if (hit.Kind != SlotKind.None && hit.Kind != SlotKind.Result)
            {
                if (heldItem.IsEmpty)
                {
                    // Normal left click: pick up / drop full stack.
                    HandleLeftClick(hit);
                    return true;
                }

                var hitKind = hit.Kind == SlotKind.Inventory ? InventoryUiSlotKind.Inventory : InventoryUiSlotKind.Crafting;
                var dragKind = dragSource.Kind == SlotKind.Inventory ? InventoryUiSlotKind.Inventory :
                    dragSource.Kind == SlotKind.Crafting ? InventoryUiSlotKind.Crafting :
                    InventoryUiSlotKind.None;

                var action = InventoryUiInputRules.DecideLeftPressAction(
                    heldItemIsEmpty: heldItem.IsEmpty,
                    hitKind: hitKind,
                    hitIndex: hit.Index,
                    dragSourceKind: dragKind,
                    dragSourceIndex: dragSource.Index);

                if (action == InventoryUiLeftPressAction.HotbarConfigureDrop)
                {
                    var slotItem = inventory.GetItem(hit.Index);
                    HandleHotbarDrop(hit.Index, slotItem);
                    return true;
                }

                if (action == InventoryUiLeftPressAction.NormalClick)
                {
                    // E.g. dragging from hotbar: allow swap/move and allow dropping back into storage.
                    HandleLeftClick(hit);
                    return true;
                }

                // Begin a distribution drag. If the user releases without touching more than one slot,
                // we'll treat it like a normal left-click drop on that slot.
                paintMode = DragPaintMode.LeftDistribute;
                paintVisited.Clear();
                paintSequence.Clear();
                AddPaintTarget(hit);
                return true;
            }
        }

        if (paintMode == DragPaintMode.LeftDistribute)
        {
            if (leftDown)
            {
                var hit = GetHitSlot(mouse.Position);
                if (hit.Kind != SlotKind.None && hit.Kind != SlotKind.Result)
                {
                    AddPaintTarget(hit);
                }
            }
            else if (leftWasDown)
            {
                // Released: apply distribution.
                ApplyLeftDistributeOnRelease();
                return true;
            }
        }

        // If not painting, keep existing single-click semantics.
        if (paintMode == DragPaintMode.None)
        {
            if (leftPressed)
            {
                var hit = GetHitSlot(mouse.Position);
                if (hit.Kind != SlotKind.None && hit.Kind != SlotKind.Result)
                {
                    HandleLeftClick(hit);
                    return true;
                }
            }
            else if (rightPressed)
            {
                var hit = GetHitSlot(mouse.Position);
                if (hit.Kind != SlotKind.None && hit.Kind != SlotKind.Result)
                {
                    HandleRightClick(hit);
                    return true;
                }
            }
        }

        leftWasDown = leftDown;
        rightWasDown = rightDown;

        return true;
    }

    private void AddPaintTarget(SlotRef hit)
    {
        if (!IsEligiblePaintTarget(hit))
        {
            return;
        }

        if (paintVisited.Add(hit))
        {
            paintSequence.Add(hit);
        }
    }

    private bool IsEligiblePaintTarget(SlotRef hit)
    {
        // Never paint into result slot.
        if (hit.Kind == SlotKind.Result || hit.Kind == SlotKind.None)
        {
            return false;
        }

        // Keep hotbar out of crafting-style paint flows.
        if (hit.Kind == SlotKind.Inventory && hit.Index < Inventory.HotbarSize)
        {
            return false;
        }

        // Disallow crafting from hotbar.
        if (dragSource.Kind == SlotKind.Inventory && dragSource.Index < Inventory.HotbarSize)
        {
            return false;
        }

        return true;
    }

    private void TryPaintDropOne(SlotRef hit)
    {
        if (heldItem.IsEmpty) return;
        if (!IsEligiblePaintTarget(hit)) return;

        // Crafting rule: painting over a non-empty crafting slot has no effect.
        if (hit.Kind == SlotKind.Crafting && !crafting.GetSlot(hit.Index).IsEmpty)
        {
            return;
        }

        if (!paintVisited.Add(hit))
        {
            return;
        }

        paintSequence.Add(hit);

        SendMoveFromDragSource(hit, 1);

        heldItem.Count--;
        if (heldItem.Count <= 0)
        {
            heldItem = default;
            dragSource = default;
            isFullStackPickup = false;

            paintMode = DragPaintMode.None;
            paintVisited.Clear();
            paintSequence.Clear();
        }
    }

    private void ApplyLeftDistributeOnRelease()
    {
        try
        {
            if (paintSequence.Count <= 0)
            {
                return;
            }

            if (paintSequence.Count == 1)
            {
                // Treat as normal left click drop.
                HandleLeftClick(paintSequence[0]);
                return;
            }

            if (heldItem.IsEmpty)
            {
                return;
            }

            var itemId = heldItem.Item;
            var maxStack = GameContentRegistry.Get(itemId).MaxStackSize;
            var totalToDistribute = heldItem.Count;

            // Filter to targets that are empty or already contain the same item.
            var eligible = new List<(SlotRef Slot, int Capacity)>(paintSequence.Count);
            foreach (var slot in paintSequence)
            {
                if (!IsEligiblePaintTarget(slot))
                {
                    continue;
                }

                var existing = GetItemAt(slot);
                if (!existing.IsEmpty && existing.Item != itemId)
                {
                    continue;
                }

                var currentCount = existing.IsEmpty ? 0 : existing.Count;
                var capacity = Math.Max(0, maxStack - currentCount);
                if (capacity <= 0) continue;
                eligible.Add((slot, capacity));
            }

            if (eligible.Count == 0)
            {
                return;
            }

            var perSlot = totalToDistribute / eligible.Count;
            var remainder = totalToDistribute % eligible.Count;

            var distributed = 0;
            for (var i = 0; i < eligible.Count; i++)
            {
                var want = perSlot + (remainder > 0 ? 1 : 0);
                if (remainder > 0) remainder--;

                var amount = Math.Min(want, eligible[i].Capacity);
                if (amount <= 0) continue;

                SendMoveFromDragSource(eligible[i].Slot, amount);
                distributed += amount;
            }

            heldItem.Count -= distributed;
            if (heldItem.Count <= 0)
            {
                heldItem = default;
                dragSource = default;
                isFullStackPickup = false;
            }
            else
            {
                // After distributing, the cursor is effectively a partial stack.
                isFullStackPickup = false;
            }
        }
        finally
        {
            paintMode = DragPaintMode.None;
            paintVisited.Clear();
            paintSequence.Clear();
        }
    }

    private InventoryItem GetItemAt(SlotRef slot)
    {
        return slot.Kind switch
        {
            SlotKind.Inventory => inventory.GetItem(slot.Index),
            SlotKind.Crafting => crafting.GetSlot(slot.Index),
            _ => default,
        };
    }

    private void SendMoveFromDragSource(SlotRef target, int count)
    {
        if (count <= 0) return;
        if (dragSource.Kind == SlotKind.None) return;
        if (target.Kind == SlotKind.None || target.Kind == SlotKind.Result) return;

        if (dragSource.Kind == SlotKind.Inventory && dragSource.Index < Inventory.HotbarSize) return;
        if (target.Kind == SlotKind.Inventory && target.Index < Inventory.HotbarSize) return;

        if (dragSource.Kind == SlotKind.Inventory && target.Kind == SlotKind.Inventory)
        {
            OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSource.Index, target.Index, count));
        }
        else if (dragSource.Kind == SlotKind.Inventory && target.Kind == SlotKind.Crafting)
        {
            OnContainerMove?.Invoke(new ContainerMoveCommand(
                SourceContainer: ItemContainer.Inventory,
                SourceSlot: dragSource.Index,
                TargetContainer: ItemContainer.Crafting,
                TargetSlot: target.Index,
                Count: count));
        }
        else if (dragSource.Kind == SlotKind.Crafting && target.Kind == SlotKind.Inventory)
        {
            OnContainerMove?.Invoke(new ContainerMoveCommand(
                SourceContainer: ItemContainer.Crafting,
                SourceSlot: dragSource.Index,
                TargetContainer: ItemContainer.Inventory,
                TargetSlot: target.Index,
                Count: count));
        }
        else if (dragSource.Kind == SlotKind.Crafting && target.Kind == SlotKind.Crafting)
        {
            OnContainerMove?.Invoke(new ContainerMoveCommand(
                SourceContainer: ItemContainer.Crafting,
                SourceSlot: dragSource.Index,
                TargetContainer: ItemContainer.Crafting,
                TargetSlot: target.Index,
                Count: count));
        }
    }

    private int GetSlotAtMouse(Vector2 mousePos)
    {
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var rect = GetSlotScreenRectangle(i);
            if (mousePos.X >= rect.X && mousePos.X < rect.X + rect.Width &&
                mousePos.Y >= rect.Y && mousePos.Y < rect.Y + rect.Height)
            {
                return i;
            }
        }
        return -1;
    }

    private enum SlotKind
    {
        None = 0,
        Inventory = 1,
        Crafting = 2,
        Result = 3,
    }

    private readonly record struct SlotRef(SlotKind Kind, int Index)
    {
        public static SlotRef None => new(SlotKind.None, -1);
    }

    private SlotRef GetHitSlot(Vector2 mousePos)
    {
        var invSlot = GetSlotAtMouse(mousePos);
        if (invSlot != -1)
        {
            return new SlotRef(SlotKind.Inventory, invSlot);
        }

        for (var i = 0; i < CraftingGrid.SlotCount; i++)
        {
            var rect = GetCraftingSlotScreenRectangle(i);
            if (mousePos.X >= rect.X && mousePos.X < rect.X + rect.Width &&
                mousePos.Y >= rect.Y && mousePos.Y < rect.Y + rect.Height)
            {
                return new SlotRef(SlotKind.Crafting, i);
            }
        }

        var resultRect = GetResultSlotScreenRectangle();
        if (mousePos.X >= resultRect.X && mousePos.X < resultRect.X + resultRect.Width &&
            mousePos.Y >= resultRect.Y && mousePos.Y < resultRect.Y + resultRect.Height)
        {
            return new SlotRef(SlotKind.Result, 0);
        }

        return SlotRef.None;
    }

    private Rectangle GetSlotScreenRectangle(int index)
    {
        if (Scene == null) return new Rectangle(0, 0, 0, 0);

        // Use local layout coordinates relative to the inventory panel, similar to HotBar.
        // This avoids depending on world-space transforms for UI hit tests and overlays.
        GetPosition(out var invPos);
        var invTopLeft = new Vector2(
            invPos.X - Size.X * Pivot.X,
            invPos.Y - Size.Y * Pivot.Y);

        slotSprites[index].GetPosition(out var slotLocalPos);
        return new Rectangle(
            (int)MathF.Round(invTopLeft.X + slotLocalPos.X),
            (int)MathF.Round(invTopLeft.Y + slotLocalPos.Y),
            ContentSize,
            ContentSize);
    }

    private Rectangle GetCraftingSlotScreenRectangle(int index)
    {
        if (Scene == null) return new Rectangle(0, 0, 0, 0);

        GetPosition(out var invPos);
        var invTopLeft = new Vector2(
            invPos.X - Size.X * Pivot.X,
            invPos.Y - Size.Y * Pivot.Y);

        craftingSlotSprites[index].GetPosition(out var slotLocalPos);
        return new Rectangle(
            (int)MathF.Round(invTopLeft.X + slotLocalPos.X),
            (int)MathF.Round(invTopLeft.Y + slotLocalPos.Y),
            ContentSize,
            ContentSize);
    }

    private Rectangle GetResultSlotScreenRectangle()
    {
        if (Scene == null) return new Rectangle(0, 0, 0, 0);

        GetPosition(out var invPos);
        var invTopLeft = new Vector2(
            invPos.X - Size.X * Pivot.X,
            invPos.Y - Size.Y * Pivot.Y);

        craftingResultSprite.GetPosition(out var localPos);
        return new Rectangle(
            (int)MathF.Round(invTopLeft.X + localPos.X),
            (int)MathF.Round(invTopLeft.Y + localPos.Y),
            ContentSize,
            ContentSize);
    }

    private void HandleLeftClick(SlotRef hit)
    {
        if (hit.Kind == SlotKind.Inventory)
        {
            var slotIndex = hit.Index;
            var slotItem = inventory.GetItem(slotIndex);
            var isHotbar = slotIndex < Inventory.HotbarSize;
            var dragFromHotbar = dragSource.Kind == SlotKind.Inventory && dragSource.Index >= 0 && dragSource.Index < Inventory.HotbarSize;

            if (heldItem.IsEmpty)
            {
                if (!slotItem.IsEmpty)
                {
                    if (isHotbar)
                    {
                        // Hotbar left-click starts dragging so the shortcut can be moved/swapped
                        // or dropped back into storage.
                        heldItem = slotItem;
                        dragSource = new SlotRef(SlotKind.Inventory, slotIndex);
                        isFullStackPickup = true;
                    }
                    else
                    {
                        heldItem = slotItem;
                        dragSource = new SlotRef(SlotKind.Inventory, slotIndex);
                        isFullStackPickup = true;
                    }
                }

                return;
            }

            // Dropping while holding.
            if (dragSource.Kind == SlotKind.Inventory && dragSource.Index >= 0)
            {
                // Storage -> hotbar is a special "configure shortcut" operation.
                if (isHotbar && dragSource.Index >= Inventory.HotbarSize)
                {
                    HandleHotbarDrop(slotIndex, slotItem);
                    return;
                }

                // Hotbar -> storage: only allow placing into empty, or stacking into same-not-full.
                // Never swap a storage stack into the hotbar.
                if (dragFromHotbar && !isHotbar)
                {
                    if (!slotItem.IsEmpty && slotItem.Item != heldItem.Item)
                    {
                        return;
                    }

                    if (!slotItem.IsEmpty)
                    {
                        var maxStack = GameContentRegistry.Get(slotItem.Item).MaxStackSize;
                        if (slotItem.Count >= maxStack)
                        {
                            return;
                        }
                    }

                    // Move exactly 1 (hotbar shortcut) into storage.
                    if (!inventory.TryMoveItem(dragSource.Index, slotIndex, 1))
                    {
                        return;
                    }

                    OnInventoryMove?.Invoke(new InventoryMoveCommand(
                        SourceSlot: dragSource.Index,
                        TargetSlot: slotIndex,
                        Count: 1));

                    heldItem = default;
                    dragSource = default;
                    isFullStackPickup = false;
                    return;
                }

                // Hotbar -> hotbar: allow move/swap of the shortcut.
                if (dragFromHotbar && isHotbar)
                {
                    if (!inventory.TryMoveItem(dragSource.Index, slotIndex, 1))
                    {
                        return;
                    }

                    OnInventoryMove?.Invoke(new InventoryMoveCommand(
                        SourceSlot: dragSource.Index,
                        TargetSlot: slotIndex,
                        Count: 1));

                    heldItem = default;
                    dragSource = default;
                    isFullStackPickup = false;
                    return;
                }

                OnInventoryMove?.Invoke(new InventoryMoveCommand(
                    SourceSlot: dragSource.Index,
                    TargetSlot: slotIndex,
                    Count: isHotbar ? 1 : (isFullStackPickup ? -1 : heldItem.Count)));
            }
            else if (dragSource.Kind == SlotKind.Crafting)
            {
                // Crafting -> Inventory (storage only)
                if (!isHotbar)
                {
                    OnContainerMove?.Invoke(new ContainerMoveCommand(
                        SourceContainer: ItemContainer.Crafting,
                        SourceSlot: dragSource.Index,
                        TargetContainer: ItemContainer.Inventory,
                        TargetSlot: slotIndex,
                        Count: isFullStackPickup ? -1 : heldItem.Count));
                }
            }

            heldItem = default;
            dragSource = default;
            isFullStackPickup = false;
            return;
        }

        if (hit.Kind == SlotKind.Crafting)
        {
            var craftIndex = hit.Index;
            var slotItem = crafting.GetSlot(craftIndex);

            if (heldItem.IsEmpty)
            {
                if (!slotItem.IsEmpty)
                {
                    heldItem = slotItem;
                    dragSource = new SlotRef(SlotKind.Crafting, craftIndex);
                    isFullStackPickup = true;
                }
                return;
            }

            if (dragSource.Kind == SlotKind.Inventory)
            {
                // Disallow crafting from hotbar shortcuts.
                if (dragSource.Index < Inventory.HotbarSize)
                {
                    return;
                }

                OnContainerMove?.Invoke(new ContainerMoveCommand(
                    SourceContainer: ItemContainer.Inventory,
                    SourceSlot: dragSource.Index,
                    TargetContainer: ItemContainer.Crafting,
                    TargetSlot: craftIndex,
                    Count: isFullStackPickup ? -1 : heldItem.Count));
            }
            else if (dragSource.Kind == SlotKind.Crafting)
            {
                OnContainerMove?.Invoke(new ContainerMoveCommand(
                    SourceContainer: ItemContainer.Crafting,
                    SourceSlot: dragSource.Index,
                    TargetContainer: ItemContainer.Crafting,
                    TargetSlot: craftIndex,
                    Count: isFullStackPickup ? -1 : heldItem.Count));
            }

            heldItem = default;
            dragSource = default;
            isFullStackPickup = false;
        }
    }

    /// <summary>
    /// Handles clicking on a hotbar slot without dragging.
    /// Removes the item from the hotbar and returns it to inventory storage.
    /// </summary>
    private void HandleHotbarRemove(int slotIndex, InventoryItem slotItem)
    {
        // Check if we can return the item to inventory
        if (!CanReturnItemToInventory(slotItem))
        {
            // Cancel - no space in inventory
            return;
        }

        // Return the item to inventory storage
        ReturnItemToInventory(slotItem);
        
        // Clear the hotbar slot
        inventory.SetItem(slotIndex, default);
        
        // Send command to server for authoritative state update
        OnReturnToStorage?.Invoke(new ReturnToStorageCommand(slotIndex));
    }

    /// <summary>
    /// Handles dropping an item onto a hotbar slot.
    /// This "configures" the hotbar to show the item in that slot.
    /// - Removes the same item from any other hotbar slot
    /// - Returns the previous item (if any) to inventory
    /// - Places 1 of the held item in the hotbar slot
    /// - Returns remaining held items to source slot
    /// </summary>
    private void HandleHotbarDrop(int slotIndex, InventoryItem slotItem)
    {
        if (dragSource.Kind != SlotKind.Inventory || dragSource.Index < 0)
        {
            return;
        }

        // Check if we can return the previous item to inventory (if slot is occupied)
        if (!slotItem.IsEmpty)
        {
            // Try to return the current hotbar item to inventory
            var canReturn = CanReturnItemToInventory(slotItem);
            if (!canReturn)
            {
                // Cancel operation - can't make room for the previous item
                return;
            }
        }

        // Remove the same item type from any other hotbar slot
        for (var i = 0; i < Inventory.HotbarSize; i++)
        {
            if (i != slotIndex)
            {
                var otherItem = inventory.GetItem(i);
                if (otherItem.Item == heldItem.Item)
                {
                    // Return this item to inventory storage
                    inventory.ReturnItemToStorage(otherItem);
                    inventory.SetItem(i, default);
                }
            }
        }

        // Return the previous item from target slot to inventory
        if (!slotItem.IsEmpty)
        {
            ReturnItemToInventory(slotItem);
        }

        // Place 1 of the held item in the hotbar slot
        var hotbarItem = new InventoryItem { Item = heldItem.Item, Count = 1 };
        inventory.SetItem(slotIndex, hotbarItem);

        // Send move command to server (source -> hotbar slot, count = 1)
        OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSource.Index, slotIndex, 1));

        // Return remaining items to source slot or inventory
        heldItem.Count--;
        if (heldItem.Count > 0)
        {
            // Return remaining to source slot if it was a storage slot and is empty
            if (dragSource.Index >= Inventory.HotbarSize && inventory.GetItem(dragSource.Index).IsEmpty)
            {
                inventory.SetItem(dragSource.Index, heldItem);
            }
            else
            {
                // Return to inventory storage
                inventory.ReturnItemToStorage(heldItem);
            }
        }

        // End drag operation
        heldItem = default;
        dragSource = default;
    }

    /// <summary>
    /// Handles dropping an item onto a storage slot.
    /// - Empty slot: place entire stack (move operation)
    /// - Same item: merge stacks (fills target, reduces held count)
    /// - Different item: swap items (ends drag with items exchanged)
    /// </summary>
    private void HandleStorageDrop(int slotIndex, InventoryItem slotItem)
    {
        if (dragSource.Kind != SlotKind.Inventory || dragSource.Index < 0)
        {
            return;
        }

        if (slotItem.IsEmpty)
        {
            // Place entire held stack in empty storage slot - move operation
            inventory.SetItem(slotIndex, heldItem);
            
            // Send move command to server
            OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSource.Index, slotIndex, -1));
            
            heldItem = default;
            dragSource = default;
        }
        else if (slotItem.Item == heldItem.Item)
        {
            // Same item - merge stacks
            var itemDef = GameContentRegistry.Get(slotItem.Item);
            var maxStack = itemDef.MaxStackSize;
            var space = maxStack - slotItem.Count;

            if (space > 0)
            {
                var toAdd = Math.Min(space, heldItem.Count);
                slotItem.Count += toAdd;
                heldItem.Count -= toAdd;
                inventory.SetItem(slotIndex, slotItem);
                
                // Send move command to server
                OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSource.Index, slotIndex, toAdd));
            }

            // Always end drag operation for merge - remaining items stay in source slot
            if (heldItem.Count > 0)
            {
                // Return remaining to source slot if empty, otherwise to inventory
                if (dragSource.Index >= 0 && inventory.GetItem(dragSource.Index).IsEmpty)
                {
                    inventory.SetItem(dragSource.Index, heldItem);
                }
                else
                {
                    inventory.ReturnItemToStorage(heldItem);
                }
            }
            heldItem = default;
            dragSource = default;
        }
        else
        {
            // Different items - swap and END drag operation
            var previousItem = slotItem;
            inventory.SetItem(slotIndex, heldItem);
            
            // Send move command to server (swap = move entire stack)
            OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSource.Index, slotIndex, -1));
            
            // Return the swapped item to the original source slot
            if (dragSource.Index >= 0 && inventory.GetItem(dragSource.Index).IsEmpty)
            {
                inventory.SetItem(dragSource.Index, previousItem);
            }
            else
            {
                // Source slot is occupied or invalid, return to storage
                inventory.ReturnItemToStorage(previousItem);
            }
            
            heldItem = default;
            dragSource = default;
        }
    }

    private void HandleCraftingRightClickPressed(SlotRef hit)
    {
        if (hit.Kind != SlotKind.Crafting) return;

        var craftIndex = hit.Index;
        var slotItem = crafting.GetSlot(craftIndex);

        // c) If no items are being dragged: empty the slot.
        if (heldItem.IsEmpty)
        {
            if (slotItem.IsEmpty) return;

            if (TryFindStorageSlotFor(slotItem, out var invSlot))
            {
                OnContainerMove?.Invoke(new ContainerMoveCommand(
                    SourceContainer: ItemContainer.Crafting,
                    SourceSlot: craftIndex,
                    TargetContainer: ItemContainer.Inventory,
                    TargetSlot: invSlot,
                    Count: -1));
            }

            return;
        }

        // d) If crafting slot is empty and user is dragging, start painting.
        // Painting over non-empty crafting slots has no effect (enforced in TryPaintDropOne).
        if (slotItem.IsEmpty)
        {
            paintMode = DragPaintMode.RightPaintOne;
            paintVisited.Clear();
            paintSequence.Clear();
            TryPaintDropOne(hit);
            return;
        }

        // a) Dragging same item: empty the slot.
        if (slotItem.Item == heldItem.Item)
        {
            if (TryFindStorageSlotFor(slotItem, out var invSlot))
            {
                OnContainerMove?.Invoke(new ContainerMoveCommand(
                    SourceContainer: ItemContainer.Crafting,
                    SourceSlot: craftIndex,
                    TargetContainer: ItemContainer.Inventory,
                    TargetSlot: invSlot,
                    Count: -1));
            }
            return;
        }

        // b) Different item: replace the slot with the dragged item.
        // Return existing slot item to inventory storage, then drop the dragged stack into the crafting slot.
        if (!TryFindStorageSlotFor(slotItem, out var storageSlot))
        {
            return;
        }

        OnContainerMove?.Invoke(new ContainerMoveCommand(
            SourceContainer: ItemContainer.Crafting,
            SourceSlot: craftIndex,
            TargetContainer: ItemContainer.Inventory,
            TargetSlot: storageSlot,
            Count: -1));

        if (dragSource.Kind == SlotKind.Inventory)
        {
            if (dragSource.Index < Inventory.HotbarSize) return;

            OnContainerMove?.Invoke(new ContainerMoveCommand(
                SourceContainer: ItemContainer.Inventory,
                SourceSlot: dragSource.Index,
                TargetContainer: ItemContainer.Crafting,
                TargetSlot: craftIndex,
                Count: isFullStackPickup ? -1 : heldItem.Count));
        }
        else if (dragSource.Kind == SlotKind.Crafting)
        {
            OnContainerMove?.Invoke(new ContainerMoveCommand(
                SourceContainer: ItemContainer.Crafting,
                SourceSlot: dragSource.Index,
                TargetContainer: ItemContainer.Crafting,
                TargetSlot: craftIndex,
                Count: isFullStackPickup ? -1 : heldItem.Count));
        }

        heldItem = default;
        dragSource = default;
        isFullStackPickup = false;
    }

    private bool TryFindStorageSlotFor(InventoryItem item, out int slotIndex)
    {
        slotIndex = -1;
        if (item.IsEmpty) return false;

        var maxStack = GameContentRegistry.Get(item.Item).MaxStackSize;

        // Prefer stacking into an existing stack with enough space.
        for (var i = Inventory.HotbarSize; i < Inventory.SlotCount; i++)
        {
            var slot = inventory.GetItem(i);
            if (slot.Item == item.Item)
            {
                var space = maxStack - slot.Count;
                if (space >= item.Count)
                {
                    slotIndex = i;
                    return true;
                }
            }
        }

        // Otherwise pick the first empty storage slot.
        for (var i = Inventory.HotbarSize; i < Inventory.SlotCount; i++)
        {
            var slot = inventory.GetItem(i);
            if (slot.IsEmpty)
            {
                slotIndex = i;
                return true;
            }
        }

        // Finally, allow partial stacking if any space exists.
        for (var i = Inventory.HotbarSize; i < Inventory.SlotCount; i++)
        {
            var slot = inventory.GetItem(i);
            if (slot.Item == item.Item)
            {
                var space = maxStack - slot.Count;
                if (space > 0)
                {
                    slotIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    private void HandleRightClick(SlotRef hit)
    {
        // Minecraft-like right click:
        // - If cursor empty: pick up half (rounded up)
        // - If cursor holds items: place exactly 1 (keep holding remaining)

        if (hit.Kind == SlotKind.Inventory)
        {
            var slotIndex = hit.Index;
            var slotItem = inventory.GetItem(slotIndex);
            var isHotbar = slotIndex < Inventory.HotbarSize;

            // Hotbar right-click always returns the shortcut to storage (never starts dragging).
            if (isHotbar)
            {
                if (slotItem.IsEmpty) return;
                HandleHotbarRemove(slotIndex, slotItem);
                return;
            }

            if (heldItem.IsEmpty)
            {
                if (slotItem.IsEmpty) return;

                var split = DarkVox.Shared.Gameplay.MinecraftUiRules.SplitHalfRoundedUp(slotItem.Count);
                heldItem = new InventoryItem { Item = slotItem.Item, Count = split };
                dragSource = new SlotRef(SlotKind.Inventory, slotIndex);
                isFullStackPickup = false;
                return;
            }

            // Place exactly 1 from held stack.
            if (dragSource.Kind == SlotKind.Inventory && dragSource.Index >= 0)
            {
                OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSource.Index, slotIndex, 1));
            }
            else if (dragSource.Kind == SlotKind.Crafting)
            {
                // Crafting -> Inventory (storage only)
                if (!isHotbar)
                {
                    OnContainerMove?.Invoke(new ContainerMoveCommand(
                        SourceContainer: ItemContainer.Crafting,
                        SourceSlot: dragSource.Index,
                        TargetContainer: ItemContainer.Inventory,
                        TargetSlot: slotIndex,
                        Count: 1));
                }
            }

            heldItem.Count--;
            if (heldItem.Count <= 0)
            {
                heldItem = default;
                dragSource = default;
                isFullStackPickup = false;
            }

            return;
        }

        if (hit.Kind == SlotKind.Crafting)
        {
            var craftIndex = hit.Index;
            var slotItem = crafting.GetSlot(craftIndex);

            if (heldItem.IsEmpty)
            {
                if (slotItem.IsEmpty) return;

                var split = DarkVox.Shared.Gameplay.MinecraftUiRules.SplitHalfRoundedUp(slotItem.Count);
                heldItem = new InventoryItem { Item = slotItem.Item, Count = split };
                dragSource = new SlotRef(SlotKind.Crafting, craftIndex);
                isFullStackPickup = false;
                return;
            }

            if (dragSource.Kind == SlotKind.Inventory)
            {
                // Disallow crafting from hotbar shortcuts.
                if (dragSource.Index < Inventory.HotbarSize)
                {
                    return;
                }

                OnContainerMove?.Invoke(new ContainerMoveCommand(
                    SourceContainer: ItemContainer.Inventory,
                    SourceSlot: dragSource.Index,
                    TargetContainer: ItemContainer.Crafting,
                    TargetSlot: craftIndex,
                    Count: 1));
            }
            else if (dragSource.Kind == SlotKind.Crafting)
            {
                OnContainerMove?.Invoke(new ContainerMoveCommand(
                    SourceContainer: ItemContainer.Crafting,
                    SourceSlot: dragSource.Index,
                    TargetContainer: ItemContainer.Crafting,
                    TargetSlot: craftIndex,
                    Count: 1));
            }

            heldItem.Count--;
            if (heldItem.Count <= 0)
            {
                heldItem = default;
                dragSource = default;
                isFullStackPickup = false;
            }

            return;
        }
    }

    private void HandleStorageRightClickDrop(int slotIndex, InventoryItem slotItem)
    {
        if (slotItem.IsEmpty)
        {
            // Place 1 item in empty slot
            var one = new InventoryItem { Item = heldItem.Item, Count = 1 };
            inventory.SetItem(slotIndex, one);

            heldItem.Count--;
            if (heldItem.Count <= 0)
            {
                heldItem = default;
                dragSource = default;
            }
        }
        else if (slotItem.Item == heldItem.Item)
        {
            // Same item - add 1 if not at max stack
            var itemDef = GameContentRegistry.Get(slotItem.Item);
            if (slotItem.Count < itemDef.MaxStackSize)
            {
                slotItem.Count++;
                inventory.SetItem(slotIndex, slotItem);

                heldItem.Count--;
                if (heldItem.Count <= 0)
                {
                    heldItem = default;
                    dragSource = default;
                }
            }
        }
        else
        {
            // Different items - swap
            inventory.SetItem(slotIndex, heldItem);
            heldItem = slotItem;
        }
    }


    /// <summary>
    /// Checks if an item can be returned to inventory (has space).
    /// </summary>
    private bool CanReturnItemToInventory(InventoryItem item)
    {
        if (item.IsEmpty) return true;

        var itemDef = GameContentRegistry.Get(item.Item);
        var maxStack = itemDef.MaxStackSize;
        var remaining = item.Count;

        // Check existing stacks in storage
        for (var i = Inventory.HotbarSize; i < Inventory.SlotCount && remaining > 0; i++)
        {
            var slot = inventory.GetItem(i);
            if (slot.Item == item.Item && slot.Count < maxStack)
            {
                var space = maxStack - slot.Count;
                remaining -= Math.Min(space, remaining);
            }
        }

        // Check empty slots in storage
        for (var i = Inventory.HotbarSize; i < Inventory.SlotCount && remaining > 0; i++)
        {
            var slot = inventory.GetItem(i);
            if (slot.IsEmpty)
            {
                remaining -= Math.Min(maxStack, remaining);
            }
        }

        return remaining <= 0;
    }

    /// <summary>
    /// Returns an item to inventory storage.
    /// </summary>
    private void ReturnItemToInventory(InventoryItem item)
    {
        if (item.IsEmpty) return;
        inventory.ReturnItemToStorage(item);
    }

    private void RenderItemCounts(ITextRenderer textRenderer)
    {
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var item = inventory.GetItem(i);
            if (item.IsEmpty) continue;

            // For hotbar slots (0-8), don't display count - they always have 1 item
            if (i < Inventory.HotbarSize)
            {
                continue;
            }

            // Show count for all storage slots (count >= 1)
            var rect = GetSlotScreenRectangle(i);
            var text = item.Count.ToString();
            var measure = textRenderer.Measure(text, FontSize);

            var x = rect.X + rect.Width - measure.Width - OverlayPadding;
            var y = rect.Y + rect.Height - measure.Height - OverlayPadding;

            textRenderer.Render(text, FontSize, x, y, CountColor);
        }

        // Render held item count
        if (!heldItem.IsEmpty && heldItem.Count > 1)
        {
            if (Scene != null)
            {
                var mousePos = Scene.SceneManager.MouseState.Position;
                
                var topLeftX = mousePos.X - ContentSize / 2f;
                var topLeftY = mousePos.Y - ContentSize / 2f;

                var text = heldItem.Count.ToString();
                var measure = textRenderer.Measure(text, FontSize);

                var x = topLeftX + ContentSize - measure.Width - OverlayPadding;
                var y = topLeftY + ContentSize - measure.Height - OverlayPadding;

                textRenderer.Render(text, FontSize, x, y, CountColor);
            }
        }

        // Render counts on crafting grid slots
        for (var i = 0; i < CraftingGrid.SlotCount; i++)
        {
            var item = crafting.GetSlot(i);
            if (item.IsEmpty) continue;
            if (item.Count <= 1) continue;

            var rect = GetCraftingSlotScreenRectangle(i);
            var text = item.Count.ToString();
            var measure = textRenderer.Measure(text, FontSize);

            var x = rect.X + rect.Width - measure.Width - OverlayPadding;
            var y = rect.Y + rect.Height - measure.Height - OverlayPadding;
            textRenderer.Render(text, FontSize, x, y, CountColor);
        }

        // Render result count if > 1
        if (!crafting.ResultPreview.IsEmpty && crafting.ResultPreview.Count > 1)
        {
            var rect = GetResultSlotScreenRectangle();
            var text = crafting.ResultPreview.Count.ToString();
            var measure = textRenderer.Measure(text, FontSize);

            var x = rect.X + rect.Width - measure.Width - OverlayPadding;
            var y = rect.Y + rect.Height - measure.Height - OverlayPadding;
            textRenderer.Render(text, FontSize, x, y, CountColor);
        }
    }

    private class TextOverlay : SceneNode
    {
        private readonly InventorySprite inventorySprite;
        private readonly ITextRenderer textRenderer;

        public TextOverlay(InventorySprite inventorySprite, ITextRenderer textRenderer, Mesh mesh, Material material)
            : base(mesh, material)
        {
            this.inventorySprite = inventorySprite;
            this.textRenderer = textRenderer;
            DisableCulling = true;
            RenderGroup = RenderGroup.UI;
        }

        public override void OnDraw(double elapsed)
        {
            if (inventorySprite.IsVisible)
            {
                inventorySprite.RenderItemCounts(textRenderer);
            }
        }
    }
}
