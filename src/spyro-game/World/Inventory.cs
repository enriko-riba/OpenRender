using SpyroGame.Shared.State;
using SpyroGame.World.Registry;

namespace SpyroGame.World;

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

        var itemDef = ItemRegistry.Items.TryGetValue(item, out var def) ? def : null;
        if (itemDef == null) return;

        var maxStack = itemDef.MaxStackSize;

        // 1. Try to stack with existing items (Hotbar first, then Storage)
        for (var i = 0; i < SlotCount; i++)
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

        // 2. Place in empty slots (Hotbar first, then Storage)
        for (var i = 0; i < SlotCount; i++)
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

        for (var i = 0; i < SlotCount; i++)
        {
            slots[i].Item = src[i].Item;
            slots[i].Count = src[i].Count;
        }

        Version = snapshot.Version;
    }
}
