using System;

namespace SpyroGame.World;

public struct InventoryItem
{
    public BlockId Block;
    public int Count;

    public bool IsEmpty => Count <= 0 || Block.IsAir();
}

public class Inventory
{
    public const int HotbarSize = 9;
    public const int StorageSize = 27;
    public const int SlotCount = HotbarSize + StorageSize; // 36 total slots

    private readonly InventoryItem[] slots = new InventoryItem[SlotCount];
    private int selectedSlot = 0;

    public int SelectedSlot
    {
        get => selectedSlot;
        set => selectedSlot = Math.Clamp(value, 0, HotbarSize - 1); // Only hotbar slots are selectable
    }

    public Inventory()
    {
        // Start with fewer blocks to allow picking up new ones
        AddItem(BlockId.Stone, 10);
        AddItem(BlockId.Dirt, 10);
        AddItem(BlockId.Grass, 10);
        AddItem(BlockId.Cobblestone, 10);
        AddItem(BlockId.OakLog, 10);
        // Leave remaining hotbar slots empty for testing pickup
    }

    public InventoryItem GetSelectedItem()
    {
        if (SelectedSlot is < 0 or >= SlotCount) return default;
        return slots[SelectedSlot];
    }

    public void AddItem(BlockId block, int count = 1)
    {
        if (block.IsAir()) return;

        // 1. Try to stack with existing items (Hotbar first, then Storage)
        for (var i = 0; i < SlotCount; i++)
        {
            // Check if same block type (ignoring flags if they differ, but usually they shouldn't)
            if (slots[i].Block.GetId() == block.GetId() && slots[i].Count < 64)
            {
                var space = 64 - slots[i].Count;
                var toAdd = Math.Min(space, count);
                slots[i].Count += toAdd;
                count -= toAdd;
                if (count <= 0) return;
            }
        }

        // 2. Place in empty slots (Hotbar first, then Storage)
        for (var i = 0; i < SlotCount; i++)
        {
            if (slots[i].IsEmpty)
            {
                slots[i].Block = block;
                slots[i].Count = count; // Assuming count <= 64 for simplicity
                return;
            }
        }
    }

    public bool TryConsumeSelectedItem()
    {
        if (SelectedSlot is < 0 or >= HotbarSize) return false;
        if (slots[SelectedSlot].IsEmpty) return false;

        slots[SelectedSlot].Count--;
        if (slots[SelectedSlot].Count <= 0)
        {
            slots[SelectedSlot] = default;
        }
        return true;
    }

    public InventoryItem GetItem(int slot) => slot is < 0 or >= SlotCount ? default : slots[slot];
}
