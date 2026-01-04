using DarkVox.Shared.World.Registry;
using Xunit;

namespace DarkVox.Tests.Registry;

public class LootTableTests
{
    [Fact]
    public void LootTable_Self_DropsSelfAsItem()
    {
        var lootTable = LootTable.Self;
        var drops = lootTable.GenerateDrops(BlockId.Stone);

        Assert.Single(drops);
        Assert.Equal(GameObjectId.Stone, drops[0].Item);
        Assert.Equal(1, drops[0].Count);
    }

    [Fact]
    public void LootTable_Nothing_DropsNothing()
    {
        var lootTable = LootTable.Nothing;
        var drops = lootTable.GenerateDrops(BlockId.TallGrass);

        Assert.Empty(drops);
    }

    [Fact]
    public void LootTable_WithChanceDrops_DropsNothingByDefault()
    {
        // 0% chance drops should never drop
        var lootTable = LootTable.WithChanceDrops(
            new LootEntry(GameObjectId.Stick, 1, 1, 0.0f)
        );

        var drops = lootTable.GenerateDrops(BlockId.OakLeaves);

        Assert.Empty(drops);
    }

    [Fact]
    public void LootTable_WithChanceDrops_AlwaysDropsAt100Percent()
    {
        // 100% chance drops should always drop
        var lootTable = LootTable.WithChanceDrops(
            new LootEntry(GameObjectId.Stick, 1, 2, 1.0f)
        );

        var drops = lootTable.GenerateDrops(BlockId.OakLeaves);

        Assert.Single(drops);
        Assert.Equal(GameObjectId.Stick, drops[0].Item);
        Assert.InRange(drops[0].Count, 1, 2);
    }

    [Fact]
    public void LootTable_SelfWithExtras_DropsSelfAndExtras()
    {
        // 100% chance for extras
        var lootTable = LootTable.SelfWithExtras(
            new LootEntry(GameObjectId.Stick, 1, 1, 1.0f)
        );

        var drops = lootTable.GenerateDrops(BlockId.OakLog);

        Assert.Equal(2, drops.Count);
        Assert.Contains(drops, d => d.Item == GameObjectId.OakLog);
        Assert.Contains(drops, d => d.Item == GameObjectId.Stick);
    }

    [Fact]
    public void LootTable_GenerateDrops_RespectsMinMaxCount()
    {
        var random = new Random(42); // Fixed seed for reproducibility
        var lootTable = LootTable.WithChanceDrops(
            new LootEntry(GameObjectId.Stick, 2, 5, 1.0f)
        );

        // Generate many drops to test range
        for (var i = 0; i < 100; i++)
        {
            var drops = lootTable.GenerateDrops(BlockId.OakLeaves, random);
            Assert.Single(drops);
            Assert.InRange(drops[0].Count, 2, 5);
        }
    }

    [Fact]
    public void BlockRegistry_LeavesHaveLootTables()
    {
        var oakLeaves = GameContentRegistry.GetBlock(BlockId.OakLeaves);
        Assert.NotNull(oakLeaves.LootTable);
        Assert.False(oakLeaves.LootTable.DropsSelf);
        Assert.NotEmpty(oakLeaves.LootTable.AdditionalDrops);
    }

    [Fact]
    public void BlockRegistry_VegetationHasLootTables()
    {
        var tallGrass = GameContentRegistry.GetBlock(BlockId.TallGrass);
        Assert.NotNull(tallGrass.LootTable);
        Assert.False(tallGrass.LootTable.DropsSelf);
    }

    [Fact]
    public void BlockRegistry_SolidBlocksDropSelf()
    {
        var stone = GameContentRegistry.GetBlock(BlockId.Stone);
        Assert.NotNull(stone.LootTable);
        Assert.True(stone.LootTable.DropsSelf);

        var drops = stone.LootTable.GenerateDrops(BlockId.Stone);
        Assert.Single(drops);
        Assert.Equal(GameObjectId.Stone, drops[0].Item);
    }

    [Fact]
    public void LootEntry_FixedCountWhenMinEqualsMax()
    {
        var random = new Random(42);
        var lootTable = LootTable.WithChanceDrops(
            new LootEntry(GameObjectId.Apple, 3, 3, 1.0f)
        );

        for (var i = 0; i < 10; i++)
        {
            var drops = lootTable.GenerateDrops(BlockId.OakLeaves, random);
            Assert.Single(drops);
            Assert.Equal(3, drops[0].Count);
        }
    }
}
