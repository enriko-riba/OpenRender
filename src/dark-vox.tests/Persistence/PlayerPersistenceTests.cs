using DarkVox.Shared.Gameplay;
using DarkVox.Shared.State;
using DarkVox.Shared.World.Registry;
using OpenTK.Mathematics;
using Xunit;

namespace DarkVox.Tests.Persistence;

/// <summary>
/// Tests for player save/load persistence system.
/// </summary>
public class PlayerPersistenceTests
{
    [Fact]
    public void PlayerSaveData_FromSnapshot_PreservesPosition()
    {
        var position = new Vector3(100, 65, 200);
        var direction = new Vector3(0.707f, 0, 0.707f);
        var attributes = new PlayerAttributesSnapshot(20, 18, 20, 15, 3.5f, 1.2f);
        var inventory = new InventorySnapshot(1, []);
        
        var saveData = PlayerSaveData.FromSnapshot(position, direction, false, attributes, inventory, 3);
        
        Assert.Equal(100, saveData.PositionX);
        Assert.Equal(65, saveData.PositionY);
        Assert.Equal(200, saveData.PositionZ);
        Assert.Equal(position, saveData.GetPosition());
    }
    
    [Fact]
    public void PlayerSaveData_FromSnapshot_PreservesDirection()
    {
        var position = Vector3.Zero;
        var direction = new Vector3(0.5f, 0.5f, 0.707f);
        var attributes = new PlayerAttributesSnapshot(20, 20, 20, 20, 5.0f, 0f);
        var inventory = new InventorySnapshot(1, []);
        
        var saveData = PlayerSaveData.FromSnapshot(position, direction, false, attributes, inventory, 0);
        
        Assert.Equal(0.5f, saveData.DirectionX);
        Assert.Equal(0.5f, saveData.DirectionY);
        Assert.Equal(0.707f, saveData.DirectionZ);
        Assert.Equal(direction, saveData.GetDirection());
    }
    
    [Fact]
    public void PlayerSaveData_FromSnapshot_PreservesAttributes()
    {
        var position = Vector3.Zero;
        var direction = -Vector3.UnitZ;
        var attributes = new PlayerAttributesSnapshot(
            MaxHealth: 20,
            Health: 15,
            MaxFood: 20,
            Food: 12,
            Saturation: 2.5f,
            Exhaustion: 3.2f);
        var inventory = new InventorySnapshot(1, []);
        
        var saveData = PlayerSaveData.FromSnapshot(position, direction, false, attributes, inventory, 0);
        
        Assert.Equal(20, saveData.MaxHealth);
        Assert.Equal(15, saveData.Health);
        Assert.Equal(20, saveData.MaxFood);
        Assert.Equal(12, saveData.Food);
        Assert.Equal(2.5f, saveData.Saturation);
        Assert.Equal(3.2f, saveData.Exhaustion);
    }
    
    [Fact]
    public void PlayerSaveData_FromSnapshot_PreservesGhostMode()
    {
        var position = Vector3.Zero;
        var direction = -Vector3.UnitZ;
        var attributes = new PlayerAttributesSnapshot(20, 20, 20, 20, 5.0f, 0f);
        var inventory = new InventorySnapshot(1, []);
        
        var normalSave = PlayerSaveData.FromSnapshot(position, direction, false, attributes, inventory, 0);
        var ghostSave = PlayerSaveData.FromSnapshot(position, direction, true, attributes, inventory, 0);
        
        Assert.False(normalSave.IsGhostMode);
        Assert.True(ghostSave.IsGhostMode);
    }
    
    [Fact]
    public void PlayerSaveData_FromSnapshot_PreservesHotbarSlot()
    {
        var position = Vector3.Zero;
        var direction = -Vector3.UnitZ;
        var attributes = new PlayerAttributesSnapshot(20, 20, 20, 20, 5.0f, 0f);
        var inventory = new InventorySnapshot(1, []);
        
        var saveData = PlayerSaveData.FromSnapshot(position, direction, false, attributes, inventory, 7);
        
        Assert.Equal(7, saveData.SelectedHotbarSlot);
    }
    
    [Fact]
    public void PlayerSaveData_FromSnapshot_PreservesInventory()
    {
        var position = Vector3.Zero;
        var direction = -Vector3.UnitZ;
        var attributes = new PlayerAttributesSnapshot(20, 20, 20, 20, 5.0f, 0f);
        var slots = new InventoryItemSnapshot[]
        {
            new(GameObjectId.DiamondPickaxe, 1),
            new(GameObjectId.Air, 0),
            new(GameObjectId.Stone, 64)
        };
        var inventory = new InventorySnapshot(1, slots);
        
        var saveData = PlayerSaveData.FromSnapshot(position, direction, false, attributes, inventory, 0);
        
        Assert.Equal(3, saveData.InventorySlots.Length);
        Assert.Equal("DiamondPickaxe", saveData.InventorySlots[0].ItemId);
        Assert.Equal(1, saveData.InventorySlots[0].Count);
        Assert.Equal("Stone", saveData.InventorySlots[2].ItemId);
        Assert.Equal(64, saveData.InventorySlots[2].Count);
    }
    
    [Fact]
    public void DebugPlayerIdentity_HasConsistentUuid()
    {
        var uuid1 = DebugPlayerIdentity.DebugPlayerUuid;
        var uuid2 = DebugPlayerIdentity.DebugPlayerUuid;
        
        Assert.Equal(uuid1, uuid2);
        Assert.Equal(new Guid("12345678-1234-1234-1234-123456789abc"), uuid1);
    }
    
    [Fact]
    public void DebugPlayerIdentity_GetDebugPlayerId_ReturnsConsistentId()
    {
        var id1 = DebugPlayerIdentity.GetDebugPlayerId();
        var id2 = DebugPlayerIdentity.GetDebugPlayerId();
        
        Assert.Equal(id1, id2);
        Assert.Equal(DebugPlayerIdentity.DebugPlayerUuid, id1.Value);
    }
    
    [Fact]
    public void GameObjectIdExtensions_TryParse_ParsesEnumName()
    {
        Assert.True(GameObjectIdExtensions.TryParse("Stone", out var result));
        Assert.Equal(GameObjectId.Stone, result);
    }
    
    [Fact]
    public void GameObjectIdExtensions_TryParse_ParsesEnumNameCaseInsensitive()
    {
        Assert.True(GameObjectIdExtensions.TryParse("STONE", out var result));
        Assert.Equal(GameObjectId.Stone, result);
        
        Assert.True(GameObjectIdExtensions.TryParse("stone", out result));
        Assert.Equal(GameObjectId.Stone, result);
    }
    
    [Fact]
    public void GameObjectIdExtensions_TryParse_ParsesNumericValue()
    {
        Assert.True(GameObjectIdExtensions.TryParse("3", out var result));
        Assert.Equal(GameObjectId.Stone, result); // Stone = 3
    }
    
    [Fact]
    public void GameObjectIdExtensions_TryParse_ReturnsFalseForNull()
    {
        Assert.False(GameObjectIdExtensions.TryParse(null, out var result));
        Assert.Equal(GameObjectId.Air, result);
    }
    
    [Fact]
    public void GameObjectIdExtensions_TryParse_ReturnsFalseForEmpty()
    {
        Assert.False(GameObjectIdExtensions.TryParse("", out var result));
        Assert.Equal(GameObjectId.Air, result);
    }
    
    [Fact]
    public void GameObjectIdExtensions_TryParse_ReturnsFalseForInvalidName()
    {
        Assert.False(GameObjectIdExtensions.TryParse("NotARealItem", out var result));
        Assert.Equal(GameObjectId.Air, result);
    }
}
