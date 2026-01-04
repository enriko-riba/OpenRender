using OpenTK.Mathematics;
using DarkVox.Shared.Abstractions;
using DarkVox.Server.Items;
using DarkVox.World;
using Xunit;
using DarkVox.Shared.World.Registry;
using DarkVox.Shared.World;

namespace DarkVox.Tests.Items;

public class DroppedItemTests
{
    private class MockLootCollector : ILootCollector
    {
        public bool IsAlive { get; set; } = true;
        public Vector3 Position { get; set; }
        public List<(GameObjectId Item, int Count)> CollectedItems { get; } = [];

        public void AddItem(GameObjectId item, int count)
        {
            CollectedItems.Add((item, count));
        }
    }

    [Fact]
    public void Tick_ShouldApplyGravity_WhenNotGrounded()
    {
        var manager = new DroppedItemManager();
        manager.Spawn(GameObjectId.Apple, 1, new Vector3(0, 10, 0), Vector3.Zero);
        
        // Empty world (air)
        manager.Tick(0.1, _ => null, []);

        var snapshot = manager.GetSnapshots().Single();
        Assert.True(snapshot.Position.Y < 10.0f);
    }

    [Fact]
    public void Tick_ShouldStopFalling_WhenHittingGround()
    {
        var manager = new DroppedItemManager();
        manager.Spawn(GameObjectId.Apple, 1, new Vector3(0, 1.5f, 0), new Vector3(0, -10, 0));

        // Solid block at (0, 0, 0)
        static BlockState? BlockProvider(Vector3i pos)
        {
            if (pos.Y == 0) return new BlockState(pos, BlockId.Stone); // Stone is solid
            return null;
        }

        // Tick enough to hit ground
        manager.Tick(0.2, BlockProvider, []);

        var snapshot = manager.GetSnapshots().Single();
        // Should snap to Y = 1.125 (1.0 + 0.125)
        Assert.Equal(1.125f, snapshot.Position.Y, 0.001f);
        
        // Verify velocity is zeroed (internally, but we can check position stability)
        var pos1 = snapshot.Position;
        manager.Tick(0.1, BlockProvider, []);
        var pos2 = manager.GetSnapshots().Single().Position;
        
        Assert.Equal(pos1, pos2);
    }

    [Fact]
    public void Tick_ShouldPickupItem_WhenPlayerInRange()
    {
        var manager = new DroppedItemManager();
        var itemPos = new Vector3(10, 10, 10);
        manager.Spawn(GameObjectId.DiamondSword, 1, itemPos, Vector3.Zero);

        var player = new MockLootCollector { Position = itemPos + new Vector3(1, 0, 0) }; // Distance 1

        manager.Tick(0.1, _ => null, [player]);

        Assert.Empty(manager.GetSnapshots()); // Item removed
        Assert.Single(player.CollectedItems);
        Assert.Equal(GameObjectId.DiamondSword, player.CollectedItems[0].Item);
    }

    [Fact]
    public void Tick_ShouldNotPickupItem_WhenPlayerOutOfRange()
    {
        var manager = new DroppedItemManager();
        var itemPos = new Vector3(10, 10, 10);
        manager.Spawn(GameObjectId.DiamondSword, 1, itemPos, Vector3.Zero);

        var player = new MockLootCollector { Position = itemPos + new Vector3(3, 0, 0) }; // Distance 3

        manager.Tick(0.1, _ => null, [player]);

        Assert.Single(manager.GetSnapshots()); // Item remains
        Assert.Empty(player.CollectedItems);
    }
}
