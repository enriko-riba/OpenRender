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

public sealed class CraftingClearTests
{
    [Fact]
    public void ClearCraftingGrid_ShouldReturnItemsToInventoryStorageAndEmptyGrid()
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

        // Put 3 sticks in crafting slot 0.
        serverPlayer.Crafting.SetSlot(0, new InventoryItem { Item = GameObjectId.Stick, Count = 3 });
        serverPlayer.Crafting.RecomputeResultPreview();

        // Act
        server.Submit(playerId, new ClearCraftingGridCommand());

        server.Tick(0.05);
        Assert.True(server.TryGetSnapshot(playerId, out var snapshot));

        // Assert: crafting grid empty
        Assert.True(snapshot.Player.Crafting.Slots.All(s => s.IsEmpty));

        // Assert: sticks were returned into storage (9-35)
        var totalSticksInStorage = snapshot.Player.Inventory.Slots
            .Skip(DarkVox.Shared.Gameplay.Inventory.HotbarSize)
            .Sum(s => s.Item == GameObjectId.Stick ? s.Count : 0);

        Assert.Equal(3, totalSticksInStorage);
    }
}
