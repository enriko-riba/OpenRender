using DarkVox.Shared.State;
using DarkVox.Shared.World.Registry;

namespace DarkVox.Shared.Gameplay.Crafting;

/// <summary>
/// Server-authoritative 2x2 crafting grid state (plus result preview).
/// Slot order: 0=TL, 1=TR, 2=BL, 3=BR.
/// </summary>
public sealed class CraftingGrid
{
    public const int SlotCount = 4;

    private readonly InventoryItem[] slots = new InventoryItem[SlotCount];

    public int Version { get; private set; }

    public InventoryItem ResultPreview { get; private set; }

    public InventoryItem GetSlot(int slot) => slot is < 0 or >= SlotCount ? default : slots[slot];

    public void SetSlot(int slot, InventoryItem item)
    {
        if (slot is < 0 or >= SlotCount) return;
        slots[slot] = item;
        Version++;
    }

    public void Clear()
    {
        Array.Clear(slots);
        ResultPreview = default;
        Version++;
    }

    public void RecomputeResultPreview()
    {
        if (CraftingRecipeRegistry.TryMatch2x2(slots, out var recipe))
        {
            ResultPreview = new InventoryItem { Item = recipe.ResultItem, Count = recipe.ResultCount };
        }
        else
        {
            ResultPreview = default;
        }

        Version++;
    }

    public bool TryConsumeForRecipe(out CraftingRecipe recipe)
    {
        if (!CraftingRecipeRegistry.TryMatch2x2(slots, out recipe))
        {
            return false;
        }

        for (var i = 0; i < SlotCount; i++)
        {
            var wantItem = recipe.Pattern[i];
            var wantCount = recipe.PatternCounts[i];
            if (wantItem == GameObjectId.Air) continue;

            var have = slots[i];
            if (have.Item != wantItem || have.Count < wantCount)
            {
                return false;
            }
        }

        // Consume.
        for (var i = 0; i < SlotCount; i++)
        {
            var wantItem = recipe.Pattern[i];
            var wantCount = recipe.PatternCounts[i];
            if (wantItem == GameObjectId.Air) continue;

            var have = slots[i];
            have.Count -= wantCount;
            if (have.Count <= 0)
            {
                slots[i] = default;
            }
            else
            {
                slots[i] = have;
            }
        }

        Version++;
        return true;
    }

    public CraftingSnapshot BuildSnapshot()
    {
        var slotSnaps = new InventoryItemSnapshot[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            slotSnaps[i] = new InventoryItemSnapshot(slots[i].Item, slots[i].Count);
        }

        var resultSnap = new InventoryItemSnapshot(ResultPreview.Item, ResultPreview.Count);
        return new CraftingSnapshot(Version, slotSnaps, resultSnap);
    }

    public void ApplySnapshot(CraftingSnapshot snapshot)
    {
        if (snapshot.Slots == null || snapshot.Slots.Length != SlotCount)
        {
            return;
        }

        var changed = false;

        for (var i = 0; i < SlotCount; i++)
        {
            var src = snapshot.Slots[i];
            if (slots[i].Item != src.Item || slots[i].Count != src.Count)
            {
                slots[i].Item = src.Item;
                slots[i].Count = src.Count;
                changed = true;
            }
        }

        if (ResultPreview.Item != snapshot.Result.Item || ResultPreview.Count != snapshot.Result.Count)
        {
            ResultPreview = new InventoryItem { Item = snapshot.Result.Item, Count = snapshot.Result.Count };
            changed = true;
        }

        if (changed)
        {
            Version++;
        }
    }

    public InventoryItem[] GetSlotsUnsafe() => slots;
}
