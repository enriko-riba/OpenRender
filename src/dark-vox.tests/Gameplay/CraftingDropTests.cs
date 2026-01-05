using OpenTK.Mathematics;
using DarkVox.Server;
using DarkVox.Server.Streaming;
using DarkVox.Shared.Commands;
using DarkVox.Shared.Gameplay;
using DarkVox.Shared.State;
using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using Xunit;

namespace DarkVox.Tests.Gameplay;

public sealed class CraftingDropTests
{
    [Fact]
    public void RightClickDropIntoCraftingSlot_ShouldMoveExactlyOneItem()
    {
        // Arrange
        var world = new VoxelWorld(seed: 123);
        var spawnPosition = new Vector3(0, 80, 0);
        var streamingManager = new ChunkStreamingManager(world);
        var server = new LocalGameServer(world, streamingManager, spawnPosition);

        var playerId = new PlayerId(Guid.NewGuid());
        server.Connect(playerId);

        server.Tick(0.05);
        Assert.True(server.TryGetPlayerForTests(playerId, out var serverPlayer));

        // Put a known stack into storage slot 9 (hotbar is 0-8 and is special).
        serverPlayer.Inventory.SetItem(9, new InventoryItem { Item = GameObjectId.Stick, Count = 10 });

        // Act: simulate "right-click drop" by moving Count=1 from inventory -> crafting slot 0.
        server.Submit(playerId, new ContainerMoveCommand(
            SourceContainer: ItemContainer.Inventory,
            SourceSlot: 9,
            TargetContainer: ItemContainer.Crafting,
            TargetSlot: 0,
            Count: 1));

        server.Tick(0.05);
        Assert.True(server.TryGetSnapshot(playerId, out var snapshot));

        // Assert
        Assert.Equal(9, snapshot.Player.Inventory.Slots[9].Count);
        Assert.Equal(GameObjectId.Stick, snapshot.Player.Crafting.Slots[0].Item);
        Assert.Equal(1, snapshot.Player.Crafting.Slots[0].Count);
    }
}
