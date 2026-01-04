using DarkVox.Server.Persistence;
using DarkVox.Shared.Gameplay;
using DarkVox.Shared.State;
using DarkVox.Shared.World.Registry;
using OpenTK.Mathematics;
using Xunit;

namespace DarkVox.Tests.Persistence;

public sealed class ServerPlayerPersistenceTests
{
    [Fact]
    public void SaveAndLoad_WorldScoped_RoundTripsCanonicalWorldRoot()
    {
        var worldDir = Path.Combine(Path.GetTempPath(), "darkvox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worldDir);

        var playerUuid = Guid.NewGuid();

        var nonEmpty = PlayerSaveData.FromSnapshot(
            position: new Vector3(1, 2, 3),
            direction: Vector3.UnitX,
            isGhostMode: false,
            attributes: new PlayerAttributesSnapshot(20, 19, 20, 18, 0, 0),
            inventory: new InventorySnapshot(1, [new InventoryItemSnapshot(GameObjectId.Stone, 64)]),
            selectedHotbarSlot: 0);

        PlayerPersistence.Save(playerUuid, nonEmpty, worldDir);

        var loaded = PlayerPersistence.Load(playerUuid, worldDir);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.InventorySlots);
        Assert.Equal("Stone", loaded.InventorySlots[0].ItemId);
        Assert.Equal(64, loaded.InventorySlots[0].Count);
    }
}
