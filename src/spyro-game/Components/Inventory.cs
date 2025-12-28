using SpyroGame.Shared.State;
using SpyroGame.World.Registry;

namespace SpyroGame.Components;

public struct InventoryItem
{
    public ItemId Item;
    public int Count;

    public readonly bool IsEmpty => Count <= 0 || Item == ItemId.Air;
}

public class Inventory
{
    public const int HotbarSize = 9;
    public const int StorageSize = 27;
    public const int SlotCount = HotbarSize + StorageSize; // 36 total slots

    private readonly InventoryItem[] slots = new InventoryItem[SlotCount];
    private int selectedSlot = 0;

    public int Version { get; private set; }

    /// <summary>
    /// When true, server snapshots are not applied to this inventory.
    /// Used when the inventory UI is open and the user is manipulating items.
    /// </summary>
    public bool IsUserEditing { get; set; }

    public int SelectedSlot
    {
        get => selectedSlot;
        set => selectedSlot = Math.Clamp(value, 0, HotbarSize - 1); // Only hotbar slots are selectable
    }

    public Inventory()
    {
        // Start with fewer blocks to allow picking up new ones
        AddItem(ItemId.Stone, 10);
        AddItem(ItemId.Dirt, 10);
        AddItem(ItemId.Grass, 10);
        AddItem(ItemId.Cobblestone, 10);
        AddItem(ItemId.OakLog, 10);
        // Light source blocks for testing the new lighting system
        AddItem(ItemId.Torch, 64);
        AddItem(ItemId.Glowstone, 32);
        AddItem(ItemId.Lantern, 16);
        AddItem(ItemId.Glass, 32);

        // Add some items
        AddItem(ItemId.Stick, 5);
        AddItem(ItemId.Apple, 3);
        AddItem(ItemId.DiamondSword, 1);
    }

    public InventoryItem SelectedItem => slots[selectedSlot];

    public void AddItem(ItemId item, int count = 1)
    {
        if (item == ItemId.Air) return;
        if (count <= 0) return;

        var itemDef = ItemRegistry.Items.TryGetValue(item, out var def) ? def : null;
        if (itemDef == null) return;

        var maxStack = itemDef.MaxStackSize;

        // 1. Try to stack with existing items in Storage (indices 9-35)
        for (var i = HotbarSize; i < SlotCount; i++)
        {
            if (slots[i].Item == item && slots[i].Count < maxStack)
            {
                var space = maxStack - slots[i].Count;
                var toAdd = Math.Min(space, count);
                slots[i].Count += toAdd;
                count -= toAdd;
                if (toAdd > 0) Version++;
                if (count <= 0) return;
            }
        }

        // 2. Check if item is already in hotbar - if not, add ONE to an empty hotbar slot
        var isInHotbar = false;
        for (var i = 0; i < HotbarSize; i++)
        {
            if (slots[i].Item == item)
            {
                isInHotbar = true;
                break;
            }
        }

        if (!isInHotbar)
        {
            // Find first empty hotbar slot and place 1 item
            for (var i = 0; i < HotbarSize; i++)
            {
                if (slots[i].IsEmpty)
                {
                    slots[i].Item = item;
                    slots[i].Count = 1;
                    count--;
                    Version++;
                    break; // Only add to ONE hotbar slot
                }
            }
            if (count <= 0) return;
        }

        // 3. Place remaining in empty Storage slots
        for (var i = HotbarSize; i < SlotCount; i++)
        {
            if (slots[i].IsEmpty)
            {
                slots[i].Item = item;
                slots[i].Count = Math.Min(count, maxStack);
                count -= slots[i].Count;
                Version++;
                if (count <= 0) return;
            }
        }
    }

    public bool TryConsumeSelectedItem()
    {
        if (SelectedSlot is < 0 or >= HotbarSize) return false;
        if (slots[SelectedSlot].IsEmpty) return false;

        var itemType = slots[SelectedSlot].Item;
        slots[SelectedSlot].Count--;
        
        if (slots[SelectedSlot].Count <= 0)
        {
            slots[SelectedSlot] = default;
            
            // Try to refill from storage
            for (var i = HotbarSize; i < SlotCount; i++)
            {
                if (slots[i].Item == itemType && slots[i].Count > 0)
                {
                    slots[i].Count--;
                    if (slots[i].Count <= 0) slots[i] = default;
                    
                    slots[SelectedSlot].Item = itemType;
                    slots[SelectedSlot].Count = 1;
                    break;
                }
            }
        }

        Version++;
        return true;
    }

    public void SetItem(int slot, InventoryItem item)
    {
        if (slot is < 0 or >= SlotCount) return;
        slots[slot] = item;
        Version++;
    }

    public InventoryItem GetItem(int slot) => slot is < 0 or >= SlotCount ? default : slots[slot];

    public InventorySnapshot CreateSnapshot()
    {
        var result = new InventoryItemSnapshot[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            var it = slots[i];
            result[i] = new InventoryItemSnapshot(it.Item, it.Count);
        }


        return new InventorySnapshot(Version, result);
    }

    public void ApplySnapshot(InventorySnapshot snapshot)
    {
        // Don't apply server snapshots while user is editing inventory
        if (IsUserEditing)
        {
            return;
        }

        var src = snapshot.Slots;
        if (src == null || src.Length != SlotCount)
        {
            return;
        }

        for (var i = 0; i < SlotCount; i++)
        {
            slots[i].Item = src[i].Item;
            slots[i].Count = src[i].Count;
        }

        Version = snapshot.Version;
    }

    public int GetTotalItemCount(ItemId item)
    {
        var total = 0;
        for (var i = 0; i < SlotCount; i++)
        {
            if (slots[i].Item == item)
            {
                total += slots[i].Count;
            }
        }
        return total;
    }

    /// <summary>
    /// Attempts to return an item to storage slots (indices 9-35).
    /// First tries to stack with existing items of the same type, then uses empty slots.
    /// </summary>
    /// <param name="item">The item to return to storage.</param>
    /// <returns>The remaining count that couldn't be stored (0 if all stored).</returns>
    public int ReturnItemToStorage(InventoryItem item)
    {
        if (item.IsEmpty) return 0;

        var itemDef = ItemRegistry.Items.TryGetValue(item.Item, out var def) ? def : null;
        if (itemDef == null) return item.Count;

        var maxStack = itemDef.MaxStackSize;
        var remaining = item.Count;

        // 1. Try to stack with existing items in Storage (indices 9-35)
        for (var i = HotbarSize; i < SlotCount && remaining > 0; i++)
        {
            if (slots[i].Item == item.Item && slots[i].Count < maxStack)
            {
                var space = maxStack - slots[i].Count;
                var toAdd = Math.Min(space, remaining);
                slots[i].Count += toAdd;
                remaining -= toAdd;
                Version++;
            }
        }

        // 2. Place remaining in empty Storage slots
        for (var i = HotbarSize; i < SlotCount && remaining > 0; i++)
        {
            if (slots[i].IsEmpty)
            {
                var toPlace = Math.Min(remaining, maxStack);
                slots[i].Item = item.Item;
                slots[i].Count = toPlace;
                remaining -= toPlace;
                Version++;
            }
        }

        return remaining;
    }

    /// <summary>
    /// Forces a version increment to trigger UI updates.
    /// </summary>
    public void ForceVersionIncrement() => Version++;
}
