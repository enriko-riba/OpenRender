using DarkVox.Shared.Commands;
using DarkVox.Shared.Gameplay;
using DarkVox.Shared.World.Registry;
using Xunit;

namespace DarkVox.Tests.Inventory;

/// <summary>
/// Tests for server-side inventory synchronization commands.
/// These verify that commands sent from client are processed correctly by the server.
/// </summary>
public class InventorySyncTests
{
    [Fact]
    public void ReturnToStorageCommand_ShouldClearHotbarSlot()
    {
        // Arrange: inventory with item in hotbar slot 0
        var inventory = new DarkVox.Shared.Gameplay.Inventory();
        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });
        
        // Simulate server processing the command
        var command = new ReturnToStorageCommand(0);
        
        // Act: apply the command (as server would)
        var slot = command.HotbarSlot;
        var item = inventory.GetItem(slot);
        inventory.SetItem(slot, default);
        inventory.ReturnItemToStorage(item);
        
        // Assert: hotbar slot should be empty
        Assert.True(inventory.GetItem(0).IsEmpty);
    }
    
    [Fact]
    public void ReturnToStorageCommand_ShouldMoveItemToStorage()
    {
        // Arrange: inventory with item in hotbar slot 0
        var inventory = new DarkVox.Shared.Gameplay.Inventory();
        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });
        
        // Simulate server processing the command
        var command = new ReturnToStorageCommand(0);
        
        // Act: apply the command (as server would)
        var slot = command.HotbarSlot;
        var item = inventory.GetItem(slot);
        inventory.SetItem(slot, default);
        inventory.ReturnItemToStorage(item);
        
        // Assert: item should be in storage (slot 9+)
        var storageItem = inventory.GetItem(9);
        Assert.Equal(GameObjectId.Stone, storageItem.Item);
        Assert.Equal(1, storageItem.Count);
    }
    
    [Fact]
    public void ReturnToStorageCommand_WithStackInStorage_ShouldMerge()
    {
        // Arrange: inventory with item in hotbar and existing stack in storage
        var inventory = new DarkVox.Shared.Gameplay.Inventory();
        inventory.SetItem(0, new InventoryItem { Item = GameObjectId.Stone, Count = 1 });
        inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stone, Count = 10 });
        
        // Simulate server processing the command
        var command = new ReturnToStorageCommand(0);
        
        // Act: apply the command (as server would)
        var slot = command.HotbarSlot;
        var item = inventory.GetItem(slot);
        inventory.SetItem(slot, default);
        inventory.ReturnItemToStorage(item);
        
        // Assert: hotbar empty, storage merged
        Assert.True(inventory.GetItem(0).IsEmpty);
        Assert.Equal(GameObjectId.Stone, inventory.GetItem(9).Item);
        Assert.Equal(11, inventory.GetItem(9).Count);
    }
    
    [Fact]
    public void ReturnToStorageCommand_InvalidSlot_ShouldBeIgnored()
    {
        // Arrange: empty inventory
        var inventory = new DarkVox.Shared.Gameplay.Inventory();
        
        // Act: try to return from invalid slot (simulating server validation)
        var command = new ReturnToStorageCommand(-1);
        var slot = command.HotbarSlot;
        
        // Server should validate slot range
        if (slot >= 0 && slot < DarkVox.Shared.Gameplay.Inventory.HotbarSize)
        {
            var item = inventory.GetItem(slot);
            if (!item.IsEmpty)
            {
                inventory.SetItem(slot, default);
                inventory.ReturnItemToStorage(item);
            }
        }
        
        // Assert: no crash, no change
        Assert.True(inventory.GetItem(0).IsEmpty);
    }
    
    [Fact]
    public void ReturnToStorageCommand_EmptySlot_ShouldBeIgnored()
    {
        // Arrange: empty inventory
        var inventory = new DarkVox.Shared.Gameplay.Inventory();
        
        // Act: try to return from empty slot
        var command = new ReturnToStorageCommand(0);
        var slot = command.HotbarSlot;
        
        if (slot >= 0 && slot < DarkVox.Shared.Gameplay.Inventory.HotbarSize)
        {
            var item = inventory.GetItem(slot);
            if (!item.IsEmpty)
            {
                inventory.SetItem(slot, default);
                inventory.ReturnItemToStorage(item);
            }
        }
        
        // Assert: no crash, no change
        Assert.True(inventory.GetItem(0).IsEmpty);
    }
}
