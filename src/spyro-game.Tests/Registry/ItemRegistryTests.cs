using SpyroGame.World.Registry;
using Xunit;

namespace SpyroGame.Tests.Registry;

/// <summary>
/// Tests for the game object registry and type hierarchy.
/// </summary>
public class GameObjectRegistryTests
{
    #region GameObject Base Class Tests

    [Fact]
    public void GameObject_DefaultValues_AreCorrect()
    {
        var obj = GameContentRegistry.Get(GameObjectId.Stick);

        Assert.Equal(GameObjectId.Stick, obj.Id);
        Assert.Equal("Stick", obj.Name);
        Assert.Equal(64, obj.MaxStackSize);
        Assert.Equal(GameObjectCategory.Material, obj.Category);
        Assert.False(obj.CanPlace);
        Assert.False(obj.IsConsumable);
    }

    #endregion

    #region Block Tests

    [Fact]
    public void Block_HasCorrectCategory()
    {
        var block = GameContentRegistry.Get(GameObjectId.Stone) as Block;

        Assert.NotNull(block);
        Assert.Equal(GameObjectCategory.Block, block!.Category);
        Assert.True(block.CanPlace);
    }

    [Fact]
    public void Block_HasCorrectBlockId()
    {
        var block = GameContentRegistry.Get(GameObjectId.Stone) as Block;

        Assert.NotNull(block);
        Assert.Equal(BlockId.Stone, block!.BlockId);
    }

    [Fact]
    public void GameObjectRegistry_ContainsBlocks()
    {
        // Stone should be registered as a Block
        var obj = GameContentRegistry.Get(GameObjectId.Stone);

        Assert.IsType<Block>(obj);
        Assert.Equal(GameObjectCategory.Block, obj.Category);
    }

    #endregion

    #region Food Tests

    [Fact]
    public void Food_HasCorrectCategory()
    {
        var food = new Food(GameObjectId.Apple, nutrition: 4, saturationModifier: 0.3f);

        Assert.Equal(GameObjectCategory.Food, food.Category);
        Assert.True(food.IsConsumable);
        Assert.False(food.CanPlace);
    }

    [Fact]
    public void Food_CalculatesSaturationRestored()
    {
        var food = new Food(GameObjectId.Apple, nutrition: 4, saturationModifier: 0.3f);

        // Saturation = Nutrition * SaturationModifier * 2
        Assert.Equal(4 * 0.3f * 2f, food.SaturationRestored);
    }

    [Fact]
    public void GameObjectRegistry_RegistersFoodItems()
    {
        var apple = GameContentRegistry.GetFood(GameObjectId.Apple);

        Assert.NotNull(apple);
        Assert.Equal(4, apple!.Nutrition);
        Assert.Equal(0.3f, apple.SaturationModifier);
    }

    [Fact]
    public void GameObjectRegistry_IsFood_ReturnsTrueForFood()
    {
        Assert.True(GameContentRegistry.IsFood(GameObjectId.Apple));
        Assert.True(GameContentRegistry.IsFood(GameObjectId.RawBeef));
        Assert.False(GameContentRegistry.IsFood(GameObjectId.Stick));
        Assert.False(GameContentRegistry.IsFood(GameObjectId.Stone));
    }

    #endregion

    #region Tool Tests

    [Fact]
    public void Tool_HasCorrectCategory()
    {
        var tool = new Tool(GameObjectId.DiamondSword, ToolType.Sword, miningSpeed: 1.5f, maxDurability: 1561);

        Assert.Equal(GameObjectCategory.Tool, tool.Category);
        Assert.False(tool.CanPlace);
    }

    [Fact]
    public void Tool_DoesNotStack()
    {
        var tool = new Tool(GameObjectId.DiamondSword, ToolType.Sword, miningSpeed: 1.5f, maxDurability: 1561);

        Assert.Equal(1, tool.MaxStackSize);
    }

    [Fact]
    public void Tool_CalculatesAttackDamage()
    {
        var sword = new Tool(GameObjectId.DiamondSword, ToolType.Sword, miningSpeed: 1.5f, maxDurability: 1561);
        var axe = new Tool(GameObjectId.DiamondAxe, ToolType.Axe, miningSpeed: 8.0f, maxDurability: 1561);

        // Sword: 4.0 + MiningSpeed
        Assert.Equal(4.0f + 1.5f, sword.AttackDamage);
        // Axe: 3.0 + MiningSpeed * 0.5
        Assert.Equal(3.0f + 8.0f * 0.5f, axe.AttackDamage);
    }

    [Fact]
    public void GameObjectRegistry_RegistersTools()
    {
        var tool = GameContentRegistry.GetTool(GameObjectId.DiamondSword);

        Assert.NotNull(tool);
        Assert.Equal(ToolType.Sword, tool!.ToolType);
    }

    [Fact]
    public void GameObjectRegistry_IsTool_ReturnsTrueForTools()
    {
        Assert.True(GameContentRegistry.IsTool(GameObjectId.DiamondSword));
        Assert.False(GameContentRegistry.IsTool(GameObjectId.Stick));
        Assert.False(GameContentRegistry.IsTool(GameObjectId.Stone));
    }

    #endregion

    #region CraftingMaterial Tests

    [Fact]
    public void CraftingMaterial_HasCorrectCategory()
    {
        var mat = new CraftingMaterial(GameObjectId.Stick);

        Assert.Equal(GameObjectCategory.Material, mat.Category);
        Assert.False(mat.CanPlace);
    }

    [Fact]
    public void GameObjectRegistry_RegistersMaterials()
    {
        var stick = GameContentRegistry.Get(GameObjectId.Stick);

        Assert.IsType<CraftingMaterial>(stick);
        Assert.Equal(GameObjectCategory.Material, stick.Category);
    }


    #endregion
}
