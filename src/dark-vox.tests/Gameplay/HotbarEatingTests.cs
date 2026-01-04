using OpenTK.Mathematics;
using DarkVox.Server;
using DarkVox.Server.Streaming;
using DarkVox.Shared.Commands;
using DarkVox.Shared.State;
using DarkVox.Shared.World;
using DarkVox.Shared.Gameplay;
using Xunit;
using DarkVox.Shared.World.Registry;

namespace DarkVox.Tests.Gameplay;

public sealed class HotbarEatingTests
{
    [Fact]
    public void EatingFood_ShouldBeServerAuthoritative_AndReplicateViaSnapshot()
    {
        // Arrange
        var world = new VoxelWorld(seed: 123);
        var spawnPosition = new Vector3(0, 80, 0);
        var streamingManager = new ChunkStreamingManager(world);
        var server = new LocalGameServer(world, streamingManager, spawnPosition);

        var playerId = new PlayerId(Guid.NewGuid());
        server.Connect(playerId);

        // Run one tick so the player is created and a snapshot exists.
        server.Tick(0.05);
        Assert.True(server.TryGetSnapshot(playerId, out var snap0));

        // Mutate server-side inventory/attributes into a known state. This uses the
        // server's player instance because the test validates authoritative behavior.
        // (This is acceptable here because the goal is verifying the authoritative path.)
        Assert.True(server.TryGetPlayerForTests(playerId, out var serverPlayer));
        serverPlayer.Inventory.SetItem(0, new InventoryItem { Item = GameObjectId.RawPorkchop, Count = 1 });
        serverPlayer.Inventory.SelectedSlot = 0;
        serverPlayer.Attributes.SetFood(10, saturation: 0);
        serverPlayer.Attributes.Damage(2);
        var healthBefore = serverPlayer.Attributes.Health;

        // Act
        server.Submit(playerId, new EatFoodCommand());
        server.Tick(0.05);
        Assert.True(server.TryGetSnapshot(playerId, out var snap1));

        // Assert
        Assert.True(snap1.Player.Inventory.Slots[0].Count == 0 || snap1.Player.Inventory.Slots[0].Item == GameObjectId.Air);
        Assert.True(snap1.Player.Attributes.Food > 10);
        Assert.True(snap1.Player.Attributes.Saturation > 0);
        Assert.True(snap1.Player.Attributes.Health > healthBefore);
    }
}
