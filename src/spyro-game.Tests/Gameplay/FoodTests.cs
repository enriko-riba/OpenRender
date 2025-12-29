using SpyroGame.Shared.Gameplay;
using SpyroGame.World.Registry;
using Xunit;

namespace SpyroGame.Tests.Gameplay;

public class FoodTests
{
    [Fact]
    public void FoodItem_ShouldHaveCorrectProperties()
    {
        var apple = new Food(GameObjectId.Apple, nutrition: 4, saturationModifier: 0.3f);

        Assert.Equal(GameObjectId.Apple, apple.Id);
        Assert.Equal(4, apple.Nutrition);
        Assert.Equal(0.3f, apple.SaturationModifier);
        Assert.Equal(4 * 0.3f * 2f, apple.SaturationRestored); // 2.4f
        Assert.False(apple.CanAlwaysEat);
    }

    [Fact]
    public void FoodItem_CanAlwaysEat_ShouldBeConfigurable()
    {
        var goldenApple = new Food(GameObjectId.Apple, nutrition: 4, saturationModifier: 1.2f, canAlwaysEat: true);

        Assert.True(goldenApple.CanAlwaysEat);
    }

    [Fact]
    public void ItemRegistry_IsFood_ShouldReturnTrueForFoodItems()
    {
        Assert.True(GameContentRegistry.IsFood(GameObjectId.Apple));
        Assert.True(GameContentRegistry.IsFood(GameObjectId.RawBeef));
        Assert.True(GameContentRegistry.IsFood(GameObjectId.RawPorkchop));
        Assert.True(GameContentRegistry.IsFood(GameObjectId.RottenFlesh));
    }

    [Fact]
    public void ItemRegistry_IsFood_ShouldReturnFalseForNonFoodItems()
    {
        Assert.False(GameContentRegistry.IsFood(GameObjectId.Stone));
        Assert.False(GameContentRegistry.IsFood(GameObjectId.Stick));
        Assert.False(GameContentRegistry.IsFood(GameObjectId.DiamondSword));
        Assert.False(GameContentRegistry.IsFood(GameObjectId.Bone));
    }

    [Fact]
    public void ItemRegistry_GetFood_ShouldReturnFoodItemForFoodItems()
    {
        var apple = GameContentRegistry.GetFood(GameObjectId.Apple);
        Assert.NotNull(apple);
        Assert.Equal(4, apple.Nutrition);
    }

    [Fact]
    public void ItemRegistry_GetFood_ShouldReturnNullForNonFoodItems()
    {
        var stone = GameContentRegistry.GetFood(GameObjectId.Stone);
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

        var apple = GameContentRegistry.GetFood(GameObjectId.Apple);
        Assert.NotNull(apple);

        var consumed = attributes.ConsumeFood(apple.Nutrition, apple.SaturationRestored, apple.CanAlwaysEat);

        Assert.True(consumed);
        Assert.Equal(14, attributes.Food); // 10 + 4
    }
}
