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

        inventory.AddItem(GameObjectId.Stone, 10);

        // Should have 1 stone in hotbar slot 0
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(GameObjectId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Should have 9 stones in storage slot 9
        var storageItem = inventory.GetItem(9);
        Assert.Equal(GameObjectId.Stone, storageItem.Item);
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
        inventory.AddItem(GameObjectId.Stone, 10);

        // Add 5 more stones - should NOT add another to hotbar
        inventory.AddItem(GameObjectId.Stone, 5);

        // Should still have only 1 stone in hotbar slot 0
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(GameObjectId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Should have 14 stones in storage (9 + 5)
        var storageItem = inventory.GetItem(9);
        Assert.Equal(GameObjectId.Stone, storageItem.Item);
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
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 60 });

        // Add 10 stones
        inventory.AddItem(GameObjectId.Stone, 10);

        // Should have 1 stone in hotbar (new item type for hotbar)
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(GameObjectId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Storage slot 9 should have 64 stones (60 + 4, capped at max stack)
        var storageItem9 = inventory.GetItem(9);
        Assert.Equal(GameObjectId.Stone, storageItem9.Item);
        Assert.Equal(64, storageItem9.Count);

        // Remaining 5 stones (10 - 1 hotbar - 4 to fill stack) should be in next storage slot
        var storageItem10 = inventory.GetItem(10);
        Assert.Equal(GameObjectId.Stone, storageItem10.Item);
        Assert.Equal(5, storageItem10.Count);
    }

    [Fact]
    public void AddItem_ShouldStackInExistingStorageSlot_WhenItemAlreadyInHotbar()
    {
        var inventory = CreateEmptyInventory();

        // Manually place 1 stone in hotbar and 60 in storage
        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 60 });

        // Add 10 more stones
        inventory.AddItem(GameObjectId.Stone, 10);

        // Hotbar should still have 1 stone (no change)
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(GameObjectId.Stone, hotbarItem.Item);
        Assert.Equal(1, hotbarItem.Count);

        // Storage slot 9 should have 64 stones (60 + 4)
        var storageItem9 = inventory.GetItem(9);
        Assert.Equal(GameObjectId.Stone, storageItem9.Item);
        Assert.Equal(64, storageItem9.Count);

        // Remaining 6 stones should be in next storage slot
        var storageItem10 = inventory.GetItem(10);
        Assert.Equal(GameObjectId.Stone, storageItem10.Item);
        Assert.Equal(6, storageItem10.Count);
    }

    [Fact]
    public void AddItem_MultipleItemTypes_ShouldEachGetOneHotbarSlot()
    {
        var inventory = CreateEmptyInventory();

        inventory.AddItem(GameObjectId.Stone, 10);
        inventory.AddItem(GameObjectId.Dirt, 10);
        inventory.AddItem(GameObjectId.Grass, 10);

        // Hotbar slot 0: 1 stone
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(0).Item);
        Assert.Equal(1, inventory.GetItem(0).Count);

        // Hotbar slot 1: 1 dirt
        Assert.Equal(GameObjectId.Dirt, inventory.GetItem(1).Item);
        Assert.Equal(1, inventory.GetItem(1).Count);

        // Hotbar slot 2: 1 grass
        Assert.Equal(GameObjectId.Grass, inventory.GetItem(2).Item);
        Assert.Equal(1, inventory.GetItem(2).Count);

        // Storage should have 9 of each
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(9).Item);
        Assert.Equal(9, inventory.GetItem(9).Count);

        Assert.Equal(GameObjectId.Dirt, inventory.GetItem(10).Item);
        Assert.Equal(9, inventory.GetItem(10).Count);

        Assert.Equal(GameObjectId.Grass, inventory.GetItem(11).Item);
        Assert.Equal(9, inventory.GetItem(11).Count);
    }

    [Fact]
    public void AddItem_WithZeroCount_ShouldDoNothing()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.AddItem(GameObjectId.Stone, 0);

        Assert.Equal(versionBefore, inventory.Version);
        Assert.True(inventory.GetItem(0).IsEmpty);
    }

    [Fact]
    public void AddItem_WithAirItem_ShouldDoNothing()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.AddItem(GameObjectId.Air, 10);

        Assert.Equal(versionBefore, inventory.Version);
        Assert.True(inventory.GetItem(0).IsEmpty);
    }

    [Fact]
    public void AddItem_SingleItem_ShouldOnlyGoToHotbar()
    {
        var inventory = CreateEmptyInventory();

        inventory.AddItem(GameObjectId.Stone, 1);

        // Should have 1 stone in hotbar slot 0
        var hotbarItem = inventory.GetItem(0);
        Assert.Equal(GameObjectId.Stone, hotbarItem.Item);
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
            inventory.SetItem(i, new InventoryItem { Item = (GameObjectId)(i + 3), Count = 1 }); // Stone=3, etc.
        }

        // Add a new item type (Lantern = 104)
        inventory.AddItem(GameObjectId.Lantern, 5);

        // Should go directly to storage since hotbar is full
        var storageItem = inventory.GetItem(9);
        Assert.Equal(GameObjectId.Lantern, storageItem.Item);
        Assert.Equal(5, storageItem.Count);
    }

    [Fact]
    public void GetTotalItemCount_ShouldSumHotbarAndStorage()
    {
        var inventory = CreateEmptyInventory();

        // Place 1 in hotbar, 9 in storage
        inventory.AddItem(GameObjectId.Stone, 10);

        var total = inventory.GetTotalItemCount(GameObjectId.Stone);
        Assert.Equal(10, total);
    }

    [Fact]
    public void ReturnItemToStorage_ShouldStackWithExisting()
    {
        var inventory = CreateEmptyInventory();

        // Place 60 stones in storage
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 60 });

        // Return 10 stones
        var remaining = inventory.ReturnItemToStorage(new InventoryItem { Item = GameObjectId.Stone, Count = 10 });

        Assert.Equal(0, remaining);
        Assert.Equal(64, inventory.GetItem(9).Count); // 60 + 4 (capped)
        Assert.Equal(6, inventory.GetItem(10).Count); // overflow
    }

    [Fact]
    public void TryConsumeSelectedItem_ShouldRefillFromStorage()
    {
        var inventory = CreateEmptyInventory();

        // Place 1 stone in hotbar slot 0 and 5 in storage
        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 5 });
        inventory.SelectedSlot = 0;

        // Consume the hotbar stone
        var result = inventory.TryConsumeSelectedItem();

        Assert.True(result);
        // Hotbar should be refilled from storage
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(0).Item);
        Assert.Equal(1, inventory.GetItem(0).Count);
        // Storage should have 4 remaining
        Assert.Equal(4, inventory.GetItem(9).Count);
    }

    [Fact]
    public void SetItem_ShouldUpdateVersion()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });

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
            inventory.SetItem(i, new InventoryItem { Item = GameObjectId.Bedrock, Count = 64 });
        }

        // Try to return stones
        var remaining = inventory.ReturnItemToStorage(new InventoryItem { Item = GameObjectId.Stone, Count = 10 });

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

        inventory.SetItem(-1, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });
        inventory.SetItem(100, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });

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
        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 5 });

        // The inventory allows it (SetItem is direct), but UI logic should enforce count=1
        var item = inventory.GetItem(0);
        Assert.Equal(GameObjectId.Stone, item.Item);
        Assert.Equal(5, item.Count); // SetItem doesn't enforce hotbar rules
    }

    #region Swap and Merge Tests (simulating InventorySprite behavior)

    [Fact]
    public void SwapItems_ShouldExchangeSlotContents()
    {
        var inventory = CreateEmptyInventory();

        // Setup: Stone in slot 9, Dirt in slot 10
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 32 });
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Dirt, Count = 16 });

        // Simulate swap (pick up from 9, drop on 10)
        var heldItem = inventory.GetItem(9);
        var targetItem = inventory.GetItem(10);

        inventory.SetItem(10, heldItem); // Place held in target
        inventory.SetItem(9, targetItem); // Place target in source

        // Verify swap
        Assert.Equal(GameObjectId.Dirt, inventory.GetItem(9).Item);
        Assert.Equal(16, inventory.GetItem(9).Count);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(32, inventory.GetItem(10).Count);
    }

    [Fact]
    public void MergeItems_ShouldCombineStacks()
    {
        var inventory = CreateEmptyInventory();

        // Setup: 32 stones in slot 9, 16 stones in slot 10
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 32 });
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Stone, Count = 16 });

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
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 60 });
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Stone, Count = 32 });

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
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 32 });

        // Simulate move to empty slot 10
        var heldItem = inventory.GetItem(9);
        inventory.SetItem(9, default); // Clear source
        inventory.SetItem(10, heldItem); // Place in target

        // Verify move
        Assert.True(inventory.GetItem(9).IsEmpty);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(32, inventory.GetItem(10).Count);
    }

    #endregion

    #region TryMoveItem Tests (Server-side inventory move validation)

    [Fact]
    public void TryMoveItem_ToEmptySlot_ShouldMoveEntireStack()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 32 });

        var result = inventory.TryMoveItem(9, 10);

        Assert.True(result);
        Assert.True(inventory.GetItem(9).IsEmpty);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(32, inventory.GetItem(10).Count);
    }

    [Fact]
    public void TryMoveItem_ToEmptySlot_WithPartialCount_ShouldMoveSpecifiedAmount()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 32 });

        var result = inventory.TryMoveItem(9, 10, 10);

        Assert.True(result);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(9).Item);
        Assert.Equal(22, inventory.GetItem(9).Count); // 32 - 10
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(10, inventory.GetItem(10).Count);
    }

    [Fact]
    public void TryMoveItem_ToSameItemSlot_ShouldStack()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Stone, Count = 30 });

        var result = inventory.TryMoveItem(9, 10);

        Assert.True(result);
        Assert.True(inventory.GetItem(9).IsEmpty);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(50, inventory.GetItem(10).Count);
    }

    [Fact]
    public void TryMoveItem_ToSameItemSlot_WhenTargetFull_ShouldPartialStack()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Stone, Count = 50 });

        var result = inventory.TryMoveItem(9, 10);

        Assert.True(result);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(9).Item);
        Assert.Equal(6, inventory.GetItem(9).Count); // 20 - 14 (only 14 fit)
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(64, inventory.GetItem(10).Count); // Full stack
    }

    [Fact]
    public void TryMoveItem_ToSameItemSlot_WhenTargetAlreadyFull_ShouldFail()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Stone, Count = 64 });

        var result = inventory.TryMoveItem(9, 10);

        Assert.False(result);
        // Both slots should be unchanged
        Assert.Equal(20, inventory.GetItem(9).Count);
        Assert.Equal(64, inventory.GetItem(10).Count);
    }

    [Fact]
    public void TryMoveItem_ToDifferentItemSlot_ShouldSwap()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Dirt, Count = 30 });

        var result = inventory.TryMoveItem(9, 10);

        Assert.True(result);
        Assert.Equal(GameObjectId.Dirt, inventory.GetItem(9).Item);
        Assert.Equal(30, inventory.GetItem(9).Count);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(10).Item);
        Assert.Equal(20, inventory.GetItem(10).Count);
    }

    [Fact]
    public void TryMoveItem_ToDifferentItemSlot_WithPartialCount_ShouldFail()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Dirt, Count = 30 });

        // Partial move to different item slot doesn't make sense - should fail
        var result = inventory.TryMoveItem(9, 10, 10);

        Assert.False(result);
        // Both slots should be unchanged
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(9).Item);
        Assert.Equal(20, inventory.GetItem(9).Count);
        Assert.Equal(GameObjectId.Dirt, inventory.GetItem(10).Item);
        Assert.Equal(30, inventory.GetItem(10).Count);
    }

    [Fact]
    public void TryMoveItem_FromEmptySlot_ShouldFail()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(10, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });

        var result = inventory.TryMoveItem(9, 10); // Slot 9 is empty

        Assert.False(result);
        Assert.True(inventory.GetItem(9).IsEmpty);
        Assert.Equal(20, inventory.GetItem(10).Count);
    }

    [Fact]
    public void TryMoveItem_ToSameSlot_ShouldFail()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });

        var result = inventory.TryMoveItem(9, 9);

        Assert.False(result);
        Assert.Equal(20, inventory.GetItem(9).Count);
    }

    [Fact]
    public void TryMoveItem_WithInvalidSourceSlot_ShouldFail()
    {
        var inventory = CreateEmptyInventory();

        var result = inventory.TryMoveItem(-1, 10);
        Assert.False(result);

        result = inventory.TryMoveItem(100, 10);
        Assert.False(result);
    }

    [Fact]
    public void TryMoveItem_WithInvalidTargetSlot_ShouldFail()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });

        var result = inventory.TryMoveItem(9, -1);
        Assert.False(result);

        result = inventory.TryMoveItem(9, 100);
        Assert.False(result);
    }

    [Fact]
    public void TryMoveItem_ShouldIncrementVersion()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 20 });
        var versionBefore = inventory.Version;

        inventory.TryMoveItem(9, 10);

        Assert.True(inventory.Version > versionBefore);
    }

    [Fact]
    public void TryMoveItem_WhenFails_ShouldNotIncrementVersion()
    {
        var inventory = CreateEmptyInventory();
        var versionBefore = inventory.Version;

        inventory.TryMoveItem(9, 10); // Source is empty, should fail

        Assert.Equal(versionBefore, inventory.Version);
    }

    [Fact]
    public void TryMoveItem_StorageToHotbar_ShouldWork()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(15, new InventoryItem { Item = GameObjectId.DiamondSword, Count = 1 });

        var result = inventory.TryMoveItem(15, 0); // Storage slot 15 to hotbar slot 0

        Assert.True(result);
        Assert.True(inventory.GetItem(15).IsEmpty);
        Assert.Equal(GameObjectId.DiamondSword, inventory.GetItem(0).Item);
        Assert.Equal(1, inventory.GetItem(0).Count);
    }

    [Fact]
    public void TryMoveItem_HotbarToStorage_ShouldWork()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });

        var result = inventory.TryMoveItem(0, 15); // Hotbar slot 0 to storage slot 15

        Assert.True(result);
        Assert.True(inventory.GetItem(0).IsEmpty);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(15).Item);
        Assert.Equal(1, inventory.GetItem(15).Count);
    }

    [Fact]
    public void TryMoveItem_HotbarSwap_ShouldWork()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });
        inventory.SetItem(5, new InventoryItem { Item = GameObjectId.Dirt, Count = 1 });

        var result = inventory.TryMoveItem(0, 5);

        Assert.True(result);
        Assert.Equal(GameObjectId.Dirt, inventory.GetItem(0).Item);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(5).Item);
    }

    [Fact]
    public void TryMoveItem_WithCountGreaterThanAvailable_ShouldMoveOnlyAvailable()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 10 });

        var result = inventory.TryMoveItem(9, 10, 50); // Try to move 50 but only 10 available

        Assert.True(result);
        Assert.True(inventory.GetItem(9).IsEmpty); // All moved
        Assert.Equal(10, inventory.GetItem(10).Count); // Only 10 available
    }

    #endregion

    #region ExecuteHotbarDrop Tests (Storage to Hotbar with stacking)

    [Fact]
    public void TryMoveItem_StorageToHotbarWithExistingItem_ShouldReturnToStorageAndStack()
    {
        var inventory = CreateEmptyInventory();
        // Setup: Sword in storage slot 15, Glass in hotbar slot 8, Glass stack in storage slot 20
        inventory.SetItem(15, new InventoryItem { Item = GameObjectId.DiamondSword, Count = 1 });
        inventory.SetItem(8, new InventoryItem { Item = GameObjectId.Glass, Count = 1 });
        inventory.SetItem(20, new InventoryItem { Item = GameObjectId.Glass, Count = 31 });

        // Move sword from storage to hotbar slot 8
        var result = inventory.TryMoveItem(15, 8, 1);

        Assert.True(result);
        // Hotbar slot 8 should now have sword
        Assert.Equal(GameObjectId.DiamondSword, inventory.GetItem(8).Item);
        Assert.Equal(1, inventory.GetItem(8).Count);
        // Source slot should be empty
        Assert.True(inventory.GetItem(15).IsEmpty);
        // Glass should be stacked: 31 + 1 = 32
        Assert.Equal(GameObjectId.Glass, inventory.GetItem(20).Item);
        Assert.Equal(32, inventory.GetItem(20).Count);
    }

    [Fact]
    public void TryMoveItem_StorageToHotbar_ShouldRemoveDuplicatesFromOtherHotbarSlots()
    {
        var inventory = CreateEmptyInventory();
        // Setup: Stone in storage, same Stone already in hotbar slot 3
        inventory.SetItem(15, new InventoryItem { Item = GameObjectId.Stone, Count = 5 });
        inventory.SetItem(3, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });

        // Move stone from storage to hotbar slot 0
        var result = inventory.TryMoveItem(15, 0, 1);

        Assert.True(result);
        // Hotbar slot 0 should have stone
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(0).Item);
        Assert.Equal(1, inventory.GetItem(0).Count);
        // Hotbar slot 3 should be empty (duplicate removed)
        Assert.True(inventory.GetItem(3).IsEmpty);
        // Remaining items should be returned to storage
        // Original source had 5, moved 1 to hotbar, duplicate 1 returned = 5 total in storage
        var totalInStorage = 0;
        for (var i = Components.Inventory.HotbarSize; i < Components.Inventory.SlotCount; i++)
        {
            if (inventory.GetItem(i).Item == GameObjectId.Stone)
            {
                totalInStorage += inventory.GetItem(i).Count;
            }
        }
        Assert.Equal(5, totalInStorage); // 4 remaining + 1 from duplicate
    }

    [Fact]
    public void TryMoveItem_StorageToEmptyHotbar_ShouldPlaceOneItem()
    {
        var inventory = CreateEmptyInventory();
        inventory.SetItem(15, new InventoryItem { Item = GameObjectId.Stone, Count = 10 });

        var result = inventory.TryMoveItem(15, 0, 1);

        Assert.True(result);
        // Hotbar slot should have exactly 1 item
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(0).Item);
        Assert.Equal(1, inventory.GetItem(0).Count);
        // Remaining 9 should be back in storage (source slot was cleared, items returned)
        var storageCount = 0;
        for (var i = Components.Inventory.HotbarSize; i < Components.Inventory.SlotCount; i++)
        {
            if (inventory.GetItem(i).Item == GameObjectId.Stone)
            {
                storageCount += inventory.GetItem(i).Count;
            }
        }
        Assert.Equal(9, storageCount);
    }

    #endregion
}
