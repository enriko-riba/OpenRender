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

    private const int FontSize = 16;
    private const int OverlayPadding = 4;
    private static readonly Vector3 CountColor = new(0.95f, 0.98f, 0.2f);
    private const int ScaleFactor = 1;

    private readonly Inventory inventory;
    private readonly Sprite[] slotSprites = new Sprite[Inventory.SlotCount];
    private readonly GameObjectId[] lastDisplayedItems = new GameObjectId[Inventory.SlotCount];

    private InventoryItem heldItem;
    private int dragSourceSlot = -1; // Track where the drag started
    private GameObjectId lastHeldItemId = GameObjectId.Air;
    private readonly Sprite heldItemSprite;
    private int lastInventoryVersion = -1;

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

    public bool IsOpen
    {
        get => IsVisible;
        set
        {
            IsVisible = value;
            
            foreach (var child in Children)
            {
                child.IsVisible = value;
            }

            // When inventory closes, cancel any local drag state.
            // Server snapshots remain authoritative and will drive the UI.
            if (!value)
            {
                heldItemSprite.IsVisible = false;

                heldItem = default;
                dragSourceSlot = -1;
                lastHeldItemId = GameObjectId.Air;
            }
        }
    }

    public static InventorySprite Create(Inventory inventory, ITextRenderer textRenderer)
    {
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/gui/inventory.png");
        return new InventorySprite(mesh, material, inventory, textRenderer);
    }

    public InventorySprite(Mesh mesh, Material material, Inventory inventory, ITextRenderer textRenderer)
        : base(mesh, material)
    {
        this.inventory = inventory;

        Pivot = new Vector2(0.5f, 0.5f);
        RenderGroup = RenderGroup.UI;
        DisableCulling = true;
        IsVisible = false;

        Size = new Vector2i(Size.X * ScaleFactor, Size.Y * ScaleFactor);

        InitializeSlots();

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

        if (mouse.IsButtonPressed(MouseButton.Left))
        {
            var slotIndex = GetSlotAtMouse(mouse.Position);
            if (slotIndex != -1)
            {
                HandleLeftClick(slotIndex);
                return true;
            }
        }
        else if (mouse.IsButtonPressed(MouseButton.Right))
        {
            var slotIndex = GetSlotAtMouse(mouse.Position);
            if (slotIndex != -1)
            {
                HandleRightClick(slotIndex);
                return true;
            }
        }

        return true;
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

    private Rectangle GetSlotScreenRectangle(int index)
    {
        if (Scene == null) return new Rectangle(0, 0, 0, 0);

        slotSprites[index].GetWorldPosition(out var pos);
        return new Rectangle((int)pos.X, (int)pos.Y, ContentSize, ContentSize);
    }

    private void HandleLeftClick(int slotIndex)
    {
        var slotItem = inventory.GetItem(slotIndex);
        var isHotbar = slotIndex < Inventory.HotbarSize;

        if (heldItem.IsEmpty)
        {
            if (!slotItem.IsEmpty)
            {
                if (isHotbar)
                {
                    // Request server to return hotbar item to storage.
                    OnReturnToStorage?.Invoke(new ReturnToStorageCommand(slotIndex));
                }
                else
                {
                    // Start drag (UI-only). Server remains authoritative.
                    heldItem = slotItem;
                    dragSourceSlot = slotIndex;
                }
            }
        }
        else
        {
            // End drag - request move on server.
            if (dragSourceSlot >= 0)
            {
                OnInventoryMove?.Invoke(new InventoryMoveCommand(
                    SourceSlot: dragSourceSlot,
                    TargetSlot: slotIndex,
                    Count: isHotbar ? 1 : -1));
            }

            heldItem = default;
            dragSourceSlot = -1;
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
        OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSourceSlot, slotIndex, 1));

        // Return remaining items to source slot or inventory
        heldItem.Count--;
        if (heldItem.Count > 0)
        {
            // Return remaining to source slot if it was a storage slot and is empty
            if (dragSourceSlot >= Inventory.HotbarSize && inventory.GetItem(dragSourceSlot).IsEmpty)
            {
                inventory.SetItem(dragSourceSlot, heldItem);
            }
            else
            {
                // Return to inventory storage
                inventory.ReturnItemToStorage(heldItem);
            }
        }

        // End drag operation
        heldItem = default;
        dragSourceSlot = -1;
    }

    /// <summary>
    /// Handles dropping an item onto a storage slot.
    /// - Empty slot: place entire stack (move operation)
    /// - Same item: merge stacks (fills target, reduces held count)
    /// - Different item: swap items (ends drag with items exchanged)
    /// </summary>
    private void HandleStorageDrop(int slotIndex, InventoryItem slotItem)
    {
        if (slotItem.IsEmpty)
        {
            // Place entire held stack in empty storage slot - move operation
            inventory.SetItem(slotIndex, heldItem);
            
            // Send move command to server
            OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSourceSlot, slotIndex, -1));
            
            heldItem = default;
            dragSourceSlot = -1;
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
                OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSourceSlot, slotIndex, toAdd));
            }

            // Always end drag operation for merge - remaining items stay in source slot
            if (heldItem.Count > 0)
            {
                // Return remaining to source slot if empty, otherwise to inventory
                if (dragSourceSlot >= 0 && inventory.GetItem(dragSourceSlot).IsEmpty)
                {
                    inventory.SetItem(dragSourceSlot, heldItem);
                }
                else
                {
                    inventory.ReturnItemToStorage(heldItem);
                }
            }
            heldItem = default;
            dragSourceSlot = -1;
        }
        else
        {
            // Different items - swap and END drag operation
            var previousItem = slotItem;
            inventory.SetItem(slotIndex, heldItem);
            
            // Send move command to server (swap = move entire stack)
            OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSourceSlot, slotIndex, -1));
            
            // Return the swapped item to the original source slot
            if (dragSourceSlot >= 0 && inventory.GetItem(dragSourceSlot).IsEmpty)
            {
                inventory.SetItem(dragSourceSlot, previousItem);
            }
            else
            {
                // Source slot is occupied or invalid, return to storage
                inventory.ReturnItemToStorage(previousItem);
            }
            
            heldItem = default;
            dragSourceSlot = -1;
        }
    }

    private void HandleRightClick(int slotIndex)
    {
        var slotItem = inventory.GetItem(slotIndex);
        var isHotbar = slotIndex < Inventory.HotbarSize;

        if (heldItem.IsEmpty)
        {
            // Start drag (UI-only). No local mutation; server snapshots drive actual state.
            if (!slotItem.IsEmpty)
            {
                heldItem = slotItem;
                dragSourceSlot = slotIndex;
            }
        }
        else
        {
            if (isHotbar)
            {
                // Configure hotbar by requesting 1 item moved to that slot.
                if (dragSourceSlot >= 0)
                {
                    OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSourceSlot, slotIndex, 1));
                }
            }
            else
            {
                // Place 1 item
                if (dragSourceSlot >= 0)
                {
                    OnInventoryMove?.Invoke(new InventoryMoveCommand(dragSourceSlot, slotIndex, 1));
                }
            }

            heldItem = default;
            dragSourceSlot = -1;
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
                dragSourceSlot = -1;
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
                    dragSourceSlot = -1;
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
                remaining -= Math.Min(remaining, maxStack);
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
