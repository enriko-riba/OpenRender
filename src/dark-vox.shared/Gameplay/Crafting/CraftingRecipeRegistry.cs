using DarkVox.Shared.World.Registry;

namespace DarkVox.Shared.Gameplay.Crafting;

/// <summary>
/// Minimal 2x2 crafting recipe registry + shaped pattern matcher.
/// Slot order: 0=TL, 1=TR, 2=BL, 3=BR.
/// </summary>
public static class CraftingRecipeRegistry
{
    private static readonly List<CraftingRecipe> recipes = [];

    static CraftingRecipeRegistry()
    {
        // Handful of basic recipes aligned with Minecraft-style crafting.
        // NOTE: We only use IDs + textures that already exist in the repo.

        // Sticks: 2 logs vertically -> 4 sticks (Minecraft uses planks, but planks are not registered yet).
        Register(new CraftingRecipe(
            resultItem: GameObjectId.Stick,
            resultCount: 4,
            pattern: [GameObjectId.OakLog, GameObjectId.Air, GameObjectId.OakLog, GameObjectId.Air],
            patternCounts: [1, 0, 1, 0]));

        Register(new CraftingRecipe(
            resultItem: GameObjectId.Stick,
            resultCount: 4,
            pattern: [GameObjectId.BirchLog, GameObjectId.Air, GameObjectId.BirchLog, GameObjectId.Air],
            patternCounts: [1, 0, 1, 0]));

        Register(new CraftingRecipe(
            resultItem: GameObjectId.Stick,
            resultCount: 4,
            pattern: [GameObjectId.SpruceLog, GameObjectId.Air, GameObjectId.SpruceLog, GameObjectId.Air],
            patternCounts: [1, 0, 1, 0]));

        Register(new CraftingRecipe(
            resultItem: GameObjectId.Stick,
            resultCount: 4,
            pattern: [GameObjectId.JungleLog, GameObjectId.Air, GameObjectId.JungleLog, GameObjectId.Air],
            patternCounts: [1, 0, 1, 0]));

        // Torches: fuel above stick -> 4 torches.
        // Minecraft uses Coal/Charcoal.
        Register(new CraftingRecipe(
            resultItem: GameObjectId.Torch,
            resultCount: 4,
            pattern: [GameObjectId.Coal, GameObjectId.Air, GameObjectId.Stick, GameObjectId.Air],
            patternCounts: [1, 0, 1, 0]));

        // Arrows: (flint, stick, feather) vertically in Minecraft; we don't have flint/feather yet.
        // Keep a simple, close analogue for testing: bone above stick -> 4 arrows.
        Register(new CraftingRecipe(
            resultItem: GameObjectId.Arrow,
            resultCount: 4,
            pattern: [GameObjectId.Bone, GameObjectId.Air, GameObjectId.Stick, GameObjectId.Air],
            patternCounts: [1, 0, 1, 0]));
    }

    public static void Register(CraftingRecipe recipe) => recipes.Add(recipe);

    public static bool TryMatch2x2(in InventoryItem[] grid, out CraftingRecipe recipe)
    {
        for (var r = 0; r < recipes.Count; r++)
        {
            var candidate = recipes[r];
            if (Matches(candidate, grid))
            {
                recipe = candidate;
                return true;
            }
        }

        recipe = null!;
        return false;
    }

    private static bool Matches(CraftingRecipe recipe, in InventoryItem[] grid)
    {
        for (var i = 0; i < CraftingRecipe.GridSlotCount; i++)
        {
            var wantItem = recipe.Pattern[i];
            var wantCount = recipe.PatternCounts[i];
            var have = grid[i];

            if (wantItem == GameObjectId.Air)
            {
                if (!have.IsEmpty)
                {
                    return false;
                }
                continue;
            }

            if (have.Item != wantItem)
            {
                return false;
            }

            if (have.Count < wantCount)
            {
                return false;
            }
        }

        // Also ensure there are no extra items in "empty" recipe slots (handled above)
        return true;
    }
}
