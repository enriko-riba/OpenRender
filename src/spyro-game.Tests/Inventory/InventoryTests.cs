using SpyroGame.Components;
using SpyroGame.World.Registry;
using Xunit;

namespace SpyroGame.Tests.Inventory;

public class InventoryTests
{
    /// <summary>
    /// Creates an empty inventory without default items.
    /// </summary>
    private static Components.Inventory CreateEmptyInventory()
    {
        var inventory = new Components.Inventory();
        // Clear all slots
        for (var i = 0; i < Components.Inventory.SlotCount; i++)
        {
            inventory.SetItem(i, default);
        }
        return inventory;
    }

    [Fact]
    public void AddItem_ShouldPlaceOneInHotbar_AndRestInStorage()
    {
        var inventory = CreateEmptyInventory();

        inventory.AddItem(ItemId.Stone, 10);

        // Should have 1 stone in hotbar slot 0
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(ItemId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Should have 9 stones in storage slot 9
        var storageItem = inventory.GetItem(9);
        Assert.Equal(ItemId.Stone, storageItem.Item);
        Assert.Equal(9, storageItem.Count);

        // Hotbar slots 1-8 should be empty
        for (var i = 1; i < Components.Inventory.HotbarSize; i++)
        {
            Assert.True(inventory.GetItem(i).IsEmpty, $"Hotbar slot {i} should be empty");
        }
    }

    [Fact]
    public void AddItem_ShouldNotDuplicateInHotbar_WhenItemAlreadyExists()
    {
        var inventory = CreateEmptyInventory();

        // Add 10 stones - should place 1 in hotbar, 9 in storage
        inventory.AddItem(ItemId.Stone, 10);

        // Add 5 more stones - should NOT add another to hotbar
        inventory.AddItem(ItemId.Stone, 5);

        // Should still have only 1 stone in hotbar slot 0
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(ItemId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Should have 14 stones in storage (9 + 5)
        var storageItem = inventory.GetItem(9);
        Assert.Equal(ItemId.Stone, storageItem.Item);
        Assert.Equal(14, storageItem.Count);

        // Hotbar slots 1-8 should be empty
        for (var i = 1; i < Components.Inventory.HotbarSize; i++)
        {
            Assert.True(inventory.GetItem(i).IsEmpty, $"Hotbar slot {i} should be empty");
        }
    }

    [Fact]
    public void AddItem_ShouldStackInExistingStorageSlot_BeforeCreatingNew()
    {
        var inventory = CreateEmptyInventory();

        // Manually place 60 stones in storage slot 9
        inventory.SetItem(9, new InventoryItem { Item = ItemId.Stone, Count = 60 });

        // Add 10 stones
        inventory.AddItem(ItemId.Stone, 10);

        // Should have 1 stone in hotbar (new item type for hotbar)
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(ItemId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Storage slot 9 should have 64 stones (60 + 4, capped at max stack)
        var storageItem9 = inventory.GetItem(9);
        Assert.Equal(ItemId.Stone, storageItem9.Item);
        Assert.Equal(64, storageItem9.Count);

        // Remaining 5 stones (10 - 1 hotbar - 4 to fill stack) should be in next storage slot
        var storageItem10 = inventory.GetItem(10);
        Assert.Equal(ItemId.Stone, storageItem10.Item);
        Assert.Equal(5, storageItem10.Count);
    }

    [Fact]
    public void AddItem_ShouldStackInExistingStorageSlot_WhenItemAlreadyInHotbar()
    {
        var inventory = CreateEmptyInventory();

        // Manually place 1 stone in hotbar and 60 in storage
        inventory.SetItem(0, new InventoryItem { Item = ItemId.Stone, Count = 1 });
        inventory.SetItem(9, new InventoryItem { Item = ItemId.Stone, Count = 60 });

        // Add 10 more stones
        inventory.AddItem(ItemId.Stone, 10);

        // Hotbar should still have 1 stone (no change)
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(ItemId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Storage slot 9 should have 64 stones (60 + 4)
        var storageItem9 = inventory.GetItem(9);
        Assert.Equal(ItemId.Stone, storageItem9.Item);
        Assert.Equal(64, storageItem9.Count);

        // Remaining 6 stones should be in next storage slot
        var storageItem10 = inventory.GetItem(10);
        Assert.Equal(ItemId.Stone, storageItem10.Item);
        Assert.Equal(6, storageItem10.Count);
    }

    [Fact]
    public void AddItem_MultipleItemTypes_ShouldEachGetOneHotbarSlot()
    {
        var inventory = CreateEmptyInventory();

        inventory.AddItem(ItemId.Stone, 10);
        inventory.AddItem(ItemId.Dirt, 10);
        inventory.AddItem(ItemId.Grass, 10);

        // Hotbar slot 0: 1 stone
        Assert.Equal(ItemId.Stone, inventory.GetItem(0).Item);
        Assert.Equal(1, inventory.GetItem(0).Count);

        // Hotbar slot 1: 1 dirt
        Assert.Equal(ItemId.Dirt, inventory.GetItem(1).Item);
        Assert.Equal(1, inventory.GetItem(1).Count);

        // Hotbar slot 2: 1 grass
        Assert.Equal(ItemId.Grass, inventory.GetItem(2).Item);
        Assert.Equal(1, inventory.GetItem(2).Count);

        // Storage should have 9 of each
        Assert.Equal(ItemId.Stone, inventory.GetItem(9).Item);
        Assert.Equal(9, inventory.GetItem(9).Count);

        Assert.Equal(ItemId.Dirt, inventory.GetItem(10).Item);
        Assert.Equal(9, inventory.GetItem(10).Count);

        Assert.Equal(ItemId.Grass, inventory.GetItem(11).Item);
        Assert.Equal(9, inventory.GetItem(11).Count);
    }

    [Fact]
    public void AddItem_WithZeroCount_ShouldDoNothing()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.AddItem(ItemId.Stone, 0);

        Assert.Equal(versionBefore, inventory.Version);
        Assert.True(inventory.GetItem(0).IsEmpty);
    }

    [Fact]
    public void AddItem_WithAirItem_ShouldDoNothing()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.AddItem(ItemId.Air, 10);

        Assert.Equal(versionBefore, inventory.Version);
        Assert.True(inventory.GetItem(0).IsEmpty);
    }

    [Fact]
    public void AddItem_SingleItem_ShouldOnlyGoToHotbar()
    {
        var inventory = CreateEmptyInventory();

        inventory.AddItem(ItemId.Stone, 1);

        // Should have 1 stone in hotbar slot 0
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(ItemId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Storage should be empty
        Assert.True(inventory.GetItem(9).IsEmpty);
    }

    [Fact]
    public void AddItem_WhenHotbarFull_ShouldGoDirectlyToStorage()
    {
        var inventory = CreateEmptyInventory();

        // Fill all hotbar slots with different items
        for (var i = 0; i < Components.Inventory.HotbarSize; i++)
        {
            inventory.SetItem(i, new InventoryItem { Item = (ItemId)(i + 3), Count = 1 }); // Stone=3, etc.
        }

        // Add a new item type (Lantern = 104)
        inventory.AddItem(ItemId.Lantern, 5);

        // Should go directly to storage since hotbar is full
        var storageItem = inventory.GetItem(9);
        Assert.Equal(ItemId.Lantern, storageItem.Item);
        Assert.Equal(5, storageItem.Count);
    }

    [Fact]
    public void GetTotalItemCount_ShouldSumHotbarAndStorage()
    {
        var inventory = CreateEmptyInventory();

        // Place 1 in hotbar, 9 in storage
        inventory.AddItem(ItemId.Stone, 10);

        var total = inventory.GetTotalItemCount(ItemId.Stone);
        Assert.Equal(10, total);
    }

    [Fact]
    public void ReturnItemToStorage_ShouldStackWithExisting()
    {
        var inventory = CreateEmptyInventory();

        // Place 60 stones in storage
        inventory.SetItem(9, new InventoryItem { Item = ItemId.Stone, Count = 60 });

        // Return 10 stones
        var remaining = inventory.ReturnItemToStorage(new InventoryItem { Item = ItemId.Stone, Count = 10 });

        Assert.Equal(0, remaining);
        Assert.Equal(64, inventory.GetItem(9).Count); // 60 + 4 (capped)
        Assert.Equal(6, inventory.GetItem(10).Count); // overflow
    }

    [Fact]
    public void TryConsumeSelectedItem_ShouldRefillFromStorage()
    {
        var inventory = CreateEmptyInventory();

        // Place 1 stone in hotbar slot 0 and 5 in storage
        inventory.SetItem(0, new InventoryItem { Item = ItemId.Stone, Count = 1 });
        inventory.SetItem(9, new InventoryItem { Item = ItemId.Stone, Count = 5 });
        inventory.SelectedSlot = 0;

        // Consume the hotbar stone
        var result = inventory.TryConsumeSelectedItem();

        Assert.True(result);
        // Hotbar should be refilled from storage
        Assert.Equal(ItemId.Stone, inventory.GetItem(0).Item);
        Assert.Equal(1, inventory.GetItem(0).Count);
        // Storage should have 4 remaining
        Assert.Equal(4, inventory.GetItem(9).Count);
    }

    [Fact]
    public void SetItem_ShouldUpdateVersion()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.SetItem(0, new InventoryItem { Item = ItemId.Stone, Count = 1 });

        Assert.True(inventory.Version > versionBefore);
    }

    [Fact]
    public void ForceVersionIncrement_ShouldIncrementVersion()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.ForceVersionIncrement();

        Assert.Equal(versionBefore + 1, inventory.Version);
    }

    [Fact]
    public void ReturnItemToStorage_WhenStorageFull_ShouldReturnRemaining()
    {
        var inventory = CreateEmptyInventory();

        // Fill all storage slots with different items
        for (var i = Components.Inventory.HotbarSize; i < Components.Inventory.SlotCount; i++)
        {
            inventory.SetItem(i, new InventoryItem { Item = ItemId.Bedrock, Count = 64 });
        }

        // Try to return stones
        var remaining = inventory.ReturnItemToStorage(new InventoryItem { Item = ItemId.Stone, Count = 10 });

        // Should return all 10 since there's no space
        Assert.Equal(10, remaining);
    }

    [Fact]
    public void ReturnItemToStorage_WithEmptyItem_ShouldReturnZero()
    {
        var inventory = CreateEmptyInventory();

        var remaining = inventory.ReturnItemToStorage(default);

        Assert.Equal(0, remaining);
    }

    [Fact]
    public void GetItem_OutOfRange_ShouldReturnDefault()
    {
        var inventory = CreateEmptyInventory();

        var item = inventory.GetItem(-1);
        Assert.True(item.IsEmpty);

        item = inventory.GetItem(100);
        Assert.True(item.IsEmpty);
    }

    [Fact]
    public void SetItem_OutOfRange_ShouldDoNothing()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.SetItem(-1, new InventoryItem { Item = ItemId.Stone, Count = 1 });
        inventory.SetItem(100, new InventoryItem { Item = ItemId.Stone, Count = 1 });

        // Version should not change for invalid operations
        Assert.Equal(versionBefore, inventory.Version);
    }

    [Fact]
    public void SelectedSlot_ShouldClampToValidRange()
    {
        var inventory = CreateEmptyInventory();

        inventory.SelectedSlot = -5;
        Assert.Equal(0, inventory.SelectedSlot);

        inventory.SelectedSlot = 100;
        Assert.Equal(Components.Inventory.HotbarSize - 1, inventory.SelectedSlot);
    }

    [Fact]
    public void HotbarSlot_ShouldOnlyHoldOneItem()
    {
        var inventory = CreateEmptyInventory();

        // Manually set hotbar slot with count > 1 (shouldn't happen in normal flow)
        inventory.SetItem(0, new InventoryItem { Item = ItemId.Stone, Count = 5 });

        // The inventory allows it (SetItem is direct), but UI logic should enforce count=1
        var item = inventory.GetItem(0);
        Assert.Equal(ItemId.Stone, item.Item);
        Assert.Equal(5, item.Count); // SetItem doesn't enforce hotbar rules
    }

    #region Swap and Merge Tests (simulating InventorySprite behavior)

    [Fact]
    public void SwapItems_ShouldExchangeSlotContents()
    {
        var inventory = CreateEmptyInventory();

        // Setup: Stone in slot 9, Dirt in slot 10
        inventory.SetItem(9, new InventoryItem { Item = ItemId.Stone, Count = 32 });
        inventory.SetItem(10, new InventoryItem { Item = ItemId.Dirt, Count = 16 });

        // Simulate swap (pick up from 9, drop on 10)
        var heldItem = inventory.GetItem(9);
        var targetItem = inventory.GetItem(10);

        inventory.SetItem(10, heldItem); // Place held in target
        inventory.SetItem(9, targetItem); // Place target in source

        // Verify swap
        Assert.Equal(ItemId.Dirt, inventory.GetItem(9).Item);
        Assert.Equal(16, inventory.GetItem(9).Count);
        Assert.Equal(ItemId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(32, inventory.GetItem(10).Count);
    }

    [Fact]
    public void MergeItems_ShouldCombineStacks()
    {
        var inventory = CreateEmptyInventory();

        // Setup: 32 stones in slot 9, 16 stones in slot 10
        inventory.SetItem(9, new InventoryItem { Item = ItemId.Stone, Count = 32 });
        inventory.SetItem(10, new InventoryItem { Item = ItemId.Stone, Count = 16 });

        // Simulate merge: pick up from 9, drop on 10
        var heldItem = inventory.GetItem(9);
        var targetItem = inventory.GetItem(10);
        var maxStack = 64;

        var space = maxStack - targetItem.Count;
        var toAdd = Math.Min(space, heldItem.Count);
        targetItem.Count += toAdd;
        heldItem.Count -= toAdd;

        inventory.SetItem(10, targetItem);
        inventory.SetItem(9, heldItem.Count > 0 ? heldItem : default);

        // Verify merge: target should have 48 (16 + 32), source should be empty
        Assert.Equal(48, inventory.GetItem(10).Count);
        Assert.True(inventory.GetItem(9).IsEmpty);
    }

    [Fact]
    public void MergeItems_WhenTargetFull_ShouldPartialMerge()
    {
        var inventory = CreateEmptyInventory();

        // Setup: 60 stones in slot 9 (target), 32 stones in slot 10 (source)
        inventory.SetItem(9, new InventoryItem { Item = ItemId.Stone, Count = 60 });
        inventory.SetItem(10, new InventoryItem { Item = ItemId.Stone, Count = 32 });

        // Simulate partial merge: pick up from 10, drop on 9
        var heldItem = inventory.GetItem(10);
        var targetItem = inventory.GetItem(9);
        var maxStack = 64;

        var space = maxStack - targetItem.Count; // 4
        var toAdd = Math.Min(space, heldItem.Count); // 4
        targetItem.Count += toAdd;
        heldItem.Count -= toAdd;

        inventory.SetItem(9, targetItem);
        inventory.SetItem(10, heldItem.Count > 0 ? heldItem : default);

        // Verify: target is full (64), remaining (28) stays in source
        Assert.Equal(64, inventory.GetItem(9).Count);
        Assert.Equal(28, inventory.GetItem(10).Count);
    }

    [Fact]
    public void MoveItem_ToEmptySlot_ShouldClearSourceSlot()
    {
        var inventory = CreateEmptyInventory();

        // Setup: 32 stones in slot 9
        inventory.SetItem(9, new InventoryItem { Item = ItemId.Stone, Count = 32 });

        // Simulate move to empty slot 10
        var heldItem = inventory.GetItem(9);
        inventory.SetItem(9, default); // Clear source
        inventory.SetItem(10, heldItem); // Place in target

        // Verify move
        Assert.True(inventory.GetItem(9).IsEmpty);
        Assert.Equal(ItemId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(32, inventory.GetItem(10).Count);
    }

    #endregion
}
