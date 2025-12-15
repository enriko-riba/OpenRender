using System;
using SpyroGame.Shared.State;

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

    public int Version { get; private set; }

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
        // Light source blocks for testing the new lighting system
        AddItem(BlockId.Torch, 64);
        AddItem(BlockId.Glowstone, 32);
        AddItem(BlockId.Lantern, 16);
        AddItem(BlockId.Glass, 32);
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
                if (toAdd > 0) Version++;
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
                Version++;
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

        Version++;
        return true;
    }

    public InventoryItem GetItem(int slot) => slot is < 0 or >= SlotCount ? default : slots[slot];

    public InventorySnapshot CreateSnapshot()
    {
        var result = new InventoryItemSnapshot[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            var it = slots[i];
            result[i] = new InventoryItemSnapshot(it.Block, it.Count);
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

        for (var i = 0; i < SlotCount; i++)
        {
            slots[i].Block = src[i].Block;
            slots[i].Count = src[i].Count;
        }

        Version = snapshot.Version;
    }
}
