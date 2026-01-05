using DarkVox.Shared.State;
using DarkVox.Shared.World.Registry;

namespace DarkVox.Shared.Gameplay;

/// <summary>
/// Represents an item slot in the inventory.
/// </summary>
public struct InventoryItem
{
    /// <summary>The game object ID of the item in this slot.</summary>
    public GameObjectId Item;
    /// <summary>Number of items in this slot.</summary>
    public int Count;

    /// <summary>Returns true if this slot is empty.</summary>
    public readonly bool IsEmpty => Count <= 0 || Item == GameObjectId.Air;
}

/// <summary>
/// Player inventory with hotbar and storage slots.
/// Server-authoritative; client applies snapshots from server.
/// </summary>
public class Inventory
{
    public const int HotbarSize = 9;
    public const int StorageSize = 27;
    public const int SlotCount = HotbarSize + StorageSize; // 36 total slots

    private readonly InventoryItem[] slots = new InventoryItem[SlotCount];
    private int selectedSlot = 0;

    public int Version { get; private set; }

    public int SelectedSlot
    {
        get => selectedSlot;
        set => selectedSlot = Math.Clamp(value, 0, HotbarSize - 1); // Only hotbar slots are selectable
    }

    public InventoryItem SelectedItem => slots[selectedSlot];

    /// <summary>
    /// Normalizes the hotbar to the "shortcut" model:
    /// - Hotbar slots contain at most 1 item each.
    /// - Duplicate item types in hotbar are removed (returned to storage).
    /// Any overflow is stacked into storage.
    /// Intended for server-side use after loading persisted inventory.
    /// </summary>
    public void NormalizeHotbarShortcuts()
    {
        var seen = new HashSet<GameObjectId>();

        for (var i = 0; i < HotbarSize; i++)
        {
            var slot = slots[i];
            if (slot.IsEmpty)
            {
                if (slots[i].Item != GameObjectId.Air || slots[i].Count != 0)
                {
                    slots[i] = default;
                    Version++;
                }
                continue;
            }

            // Only one hotbar slot per item type.
            if (seen.Contains(slot.Item))
            {
                slots[i] = default;
                Version++;
                ReturnItemToStorage(slot);
                continue;
            }

            seen.Add(slot.Item);

            // Hotbar should be a shortcut: keep 1, return overflow to storage.
            if (slot.Count > 1)
            {
                var overflow = slot.Count - 1;
                slot.Count = 1;
                slots[i] = slot;
                Version++;
                ReturnItemToStorage(new InventoryItem { Item = slot.Item, Count = overflow });
            }
        }
    }

    public void AddItem(GameObjectId item, int count = 1)
    {
        if (item == GameObjectId.Air) return;
        if (count <= 0) return;

        var itemDef = GameContentRegistry.Get(item);
        var maxStack = itemDef.MaxStackSize;

        // Hotbar is a shortcut bar: keep exactly 1 in hotbar, stack quantities into storage.
        // Ensure the item appears in the hotbar (when possible), but never increase hotbar count beyond 1.
        var isInHotbar = false;
        for (var i = 0; i < HotbarSize; i++)
        {
            if (slots[i].Item == item && slots[i].Count > 0)
            {
                isInHotbar = true;
                break;
            }
        }

        if (!isInHotbar && count > 0)
        {
            for (var i = 0; i < HotbarSize; i++)
            {
                if (slots[i].IsEmpty)
                {
                    slots[i].Item = item;
                    slots[i].Count = 1;
                    count--;
                    Version++;
                    break;
                }
            }
        }

        // 3) Stack into existing storage stacks (slots 9-35)
        for (var i = HotbarSize; i < SlotCount && count > 0; i++)
        {
            if (slots[i].Item == item && slots[i].Count < maxStack)
            {
                var space = maxStack - slots[i].Count;
                var toAdd = Math.Min(space, count);
                if (toAdd > 0)
                {
                    slots[i].Count += toAdd;
                    count -= toAdd;
                    Version++;
                }
            }
        }

        // 4) Place remaining into empty storage slots
        for (var i = HotbarSize; i < SlotCount && count > 0; i++)
        {
            if (slots[i].IsEmpty)
            {
                var toPlace = Math.Min(count, maxStack);
                slots[i].Item = item;
                slots[i].Count = toPlace;
                count -= toPlace;
                Version++;
            }
        }
    }

    public bool TryConsumeSelectedItem()
    {
        if (SelectedSlot is < 0 or >= HotbarSize) return false;
        if (slots[SelectedSlot].IsEmpty) return false;

        var itemType = slots[SelectedSlot].Item;

        // Consume from storage first while keeping the hotbar as a shortcut (Count=1).
        for (var i = HotbarSize; i < SlotCount; i++)
        {
            if (slots[i].Item == itemType && slots[i].Count > 0)
            {
                slots[i].Count--;
                if (slots[i].Count <= 0) slots[i] = default;
                Version++;
                return true;
            }
        }

        // No storage remainder: consume the last item by clearing the hotbar slot.
        slots[SelectedSlot] = default;
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

    public InventorySnapshot BuildSnapshot()
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
        var src = snapshot.Slots;
        if (src == null || src.Length != SlotCount)
        {
            return;
        }

        // Check if any slot actually changed
        var changed = false;
        for (var i = 0; i < SlotCount; i++)
        {
            if (slots[i].Item != src[i].Item || slots[i].Count != src[i].Count)
            {
                slots[i].Item = src[i].Item;
                slots[i].Count = src[i].Count;
                changed = true;
            }
        }

        // Always increment version if content changed, so UI detects the update
        if (changed)
        {
            Version++;
        }
    }

    public int GetTotalItemCount(GameObjectId item)
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

        var itemDef = GameContentRegistry.Get(item.Item);
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
    /// Attempts to move items from one slot to another.
    /// Handles stacking, swapping, and partial moves.
    /// </summary>
    /// <param name="sourceSlot">The slot to move from.</param>
    /// <param name="targetSlot">The slot to move to.</param>
    /// <param name="count">Number of items to move. Use -1 for entire stack.</param>
    /// <returns>True if the move was successful.</returns>
    public bool TryMoveItem(int sourceSlot, int targetSlot, int count = -1)
    {
        if (sourceSlot is < 0 or >= SlotCount) return false;
        if (targetSlot is < 0 or >= SlotCount) return false;
        if (sourceSlot == targetSlot) return false;

        var source = slots[sourceSlot];
        if (source.IsEmpty) return false;

        var target = slots[targetSlot];
        var moveCount = count < 0 ? source.Count : Math.Min(count, source.Count);

        // Check if dropping from storage to hotbar - use special hotbar drop logic
        // This only applies when source is a storage slot and target is a hotbar slot
        if (sourceSlot >= HotbarSize && targetSlot < HotbarSize)
        {
            return ExecuteHotbarDrop(sourceSlot, targetSlot, source, target, moveCount);
        }

        // Standard move/swap/stack logic for all other cases
        if (target.IsEmpty)
        {
            // Move to empty slot
            slots[targetSlot] = new InventoryItem { Item = source.Item, Count = moveCount };
            source.Count -= moveCount;
            slots[sourceSlot] = source.Count <= 0 ? default : source;
        }
        else if (target.Item == source.Item)
        {
            // Stack same items
            var itemDef = GameContentRegistry.Get(source.Item);
            var maxStack = itemDef.MaxStackSize;
            var spaceAvailable = maxStack - target.Count;
            var toMove = Math.Min(moveCount, spaceAvailable);
            if (toMove <= 0) return false;

            target.Count += toMove;
            slots[targetSlot] = target;
            source.Count -= toMove;
            slots[sourceSlot] = source.Count <= 0 ? default : source;
        }
        else
        {
            // Swap different items (only if moving entire stack)
            if (count >= 0 && count < source.Count) return false;

            slots[sourceSlot] = target;
            slots[targetSlot] = source;
        }

        Version++;
        return true;
    }

    /// <summary>
    /// Executes a hotbar drop operation with proper item management:
    /// - Returns any existing item in the hotbar slot to storage
    /// - Removes duplicates of the source item from other hotbar slots
    /// - Places exactly 1 item in the target hotbar slot
    /// - Returns remaining items to storage
    /// </summary>
    private bool ExecuteHotbarDrop(int sourceSlot, int targetSlot, InventoryItem source, InventoryItem target, int moveCount)
    {
        // Return existing hotbar item to storage (if any)
        if (!target.IsEmpty)
        {
            var remaining = ReturnItemToStorage(target);
            if (remaining > 0)
            {
                // Can't fit the displaced item - operation fails
                return false;
            }
        }

        // Clear the source slot FIRST, before returning duplicates
        // This prevents duplicates from stacking into the source slot
        slots[sourceSlot] = default;

        // Remove duplicates of source item from other hotbar slots
        for (var i = 0; i < HotbarSize; i++)
        {
            if (i != targetSlot && slots[i].Item == source.Item)
            {
                var duplicate = slots[i];
                ReturnItemToStorage(duplicate);
                slots[i] = default;
            }
        }

        // Place exactly 1 item in the hotbar slot
        slots[targetSlot] = new InventoryItem { Item = source.Item, Count = 1 };

        // Return remaining items to storage
        source.Count -= 1; // We moved 1 item to hotbar

        if (source.Count > 0)
        {
            ReturnItemToStorage(source);
        }

        Version++;
        return true;
    }

    /// <summary>
    /// Forces a version increment to trigger UI updates.
    /// </summary>
    public void ForceVersionIncrement() => Version++;
}
