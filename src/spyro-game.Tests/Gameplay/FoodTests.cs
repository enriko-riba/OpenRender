using SpyroGame.Shared.Gameplay;
using SpyroGame.World.Registry;
using Xunit;

namespace SpyroGame.Tests.Gameplay;

public class FoodTests
{
    [Fact]
    public void FoodItem_ShouldHaveCorrectProperties()
    {
        var apple = new FoodItem(ItemId.Apple, nutrition: 4, saturationModifier: 0.3f);

        Assert.Equal(ItemId.Apple, apple.Id);
        Assert.Equal(4, apple.Nutrition);
        Assert.Equal(0.3f, apple.SaturationModifier);
        Assert.Equal(4 * 0.3f * 2f, apple.SaturationRestored); // 2.4f
        Assert.False(apple.CanAlwaysEat);
    }

    [Fact]
    public void FoodItem_CanAlwaysEat_ShouldBeConfigurable()
    {
        var goldenApple = new FoodItem(ItemId.Apple, nutrition: 4, saturationModifier: 1.2f, canAlwaysEat: true);

        Assert.True(goldenApple.CanAlwaysEat);
    }

    [Fact]
    public void ItemRegistry_IsFood_ShouldReturnTrueForFoodItems()
    {
        Assert.True(ItemRegistry.IsFood(ItemId.Apple));
        Assert.True(ItemRegistry.IsFood(ItemId.RawBeef));
        Assert.True(ItemRegistry.IsFood(ItemId.RawPorkchop));
        Assert.True(ItemRegistry.IsFood(ItemId.RottenFlesh));
    }

    [Fact]
    public void ItemRegistry_IsFood_ShouldReturnFalseForNonFoodItems()
    {
        Assert.False(ItemRegistry.IsFood(ItemId.Stone));
        Assert.False(ItemRegistry.IsFood(ItemId.Stick));
        Assert.False(ItemRegistry.IsFood(ItemId.DiamondSword));
        Assert.False(ItemRegistry.IsFood(ItemId.Bone));
    }

    [Fact]
    public void ItemRegistry_GetFood_ShouldReturnFoodItemForFoodItems()
    {
        var apple = ItemRegistry.GetFood(ItemId.Apple);
        Assert.NotNull(apple);
        Assert.Equal(4, apple.Nutrition);
    }

    [Fact]
    public void ItemRegistry_GetFood_ShouldReturnNullForNonFoodItems()
    {
        var stone = ItemRegistry.GetFood(ItemId.Stone);
        Assert.Null(stone);
    }
}

public class PlayerAttributesFoodTests
{
    [Fact]
    public void ConsumeFood_ShouldRestoreHunger()
    {
        var attributes = new PlayerAttributes();
        attributes.SetFood(10); // Set food to half

        var consumed = attributes.ConsumeFood(nutrition: 4, saturation: 2.4f);

        Assert.True(consumed);
        Assert.Equal(14, attributes.Food);
    }

    [Fact]
    public void ConsumeFood_ShouldRestoreSaturation()
    {
        var attributes = new PlayerAttributes();
        attributes.SetFood(15, saturation: 0); // Low saturation

        attributes.ConsumeFood(nutrition: 4, saturation: 5.0f);

        Assert.True(attributes.Saturation > 0);
    }

    [Fact]
    public void ConsumeFood_ShouldCapSaturationAtFoodLevel()
    {
        var attributes = new PlayerAttributes();
        attributes.SetFood(10, saturation: 0);

        // Try to restore more saturation than food level
        attributes.ConsumeFood(nutrition: 4, saturation: 20.0f);

        // Saturation should be capped at food level (14 after eating)
        Assert.True(attributes.Saturation <= attributes.Food);
    }

    [Fact]
    public void ConsumeFood_ShouldCapFoodAtMaxFood()
    {
        var attributes = new PlayerAttributes();
        attributes.SetFood(18); // Almost full

        attributes.ConsumeFood(nutrition: 4, saturation: 2.4f);

        Assert.Equal(20, attributes.Food); // Should be capped at 20
    }

    [Fact]
    public void ConsumeFood_ShouldReturnFalse_WhenFoodIsFull()
    {
        var attributes = new PlayerAttributes();
        // Default food is 20 (full)

        var consumed = attributes.ConsumeFood(nutrition: 4, saturation: 2.4f, canAlwaysEat: false);

        Assert.False(consumed);
        Assert.Equal(20, attributes.Food); // Unchanged
    }

    [Fact]
    public void ConsumeFood_CanAlwaysEat_ShouldAllowEatingWhenFull()
    {
        var attributes = new PlayerAttributes();
        // Default food is 20 (full)

        var consumed = attributes.ConsumeFood(nutrition: 4, saturation: 2.4f, canAlwaysEat: true);

        Assert.True(consumed);
        Assert.Equal(20, attributes.Food); // Still capped at 20
    }

    [Fact]
    public void ConsumeFood_ShouldWorkWithRegisteredFoodItems()
    {
        var attributes = new PlayerAttributes();
        attributes.SetFood(10, saturation: 0);

        var apple = ItemRegistry.GetFood(ItemId.Apple);
        Assert.NotNull(apple);

        var consumed = attributes.ConsumeFood(apple.Nutrition, apple.SaturationRestored, apple.CanAlwaysEat);

        Assert.True(consumed);
        Assert.Equal(14, attributes.Food); // 10 + 4
    }
}
