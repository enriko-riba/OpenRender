using DarkVox.Shared.World.Registry;

namespace DarkVox.Shared.Gameplay.Crafting;

public sealed class CraftingRecipe
{
    public const int GridSlotCount = 4;

    public GameObjectId ResultItem { get; }
    public int ResultCount { get; }
    public GameObjectId[] Pattern { get; }
    public int[] PatternCounts { get; }

    public CraftingRecipe(
        GameObjectId resultItem,
        int resultCount,
        GameObjectId[] pattern,
        int[] patternCounts)
    {
        if (pattern.Length != GridSlotCount)
        {
            throw new ArgumentException($"Pattern must have length {GridSlotCount}.", nameof(pattern));
        }

        if (patternCounts.Length != GridSlotCount)
        {
            throw new ArgumentException($"PatternCounts must have length {GridSlotCount}.", nameof(patternCounts));
        }

        if (resultCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }

        for (var i = 0; i < GridSlotCount; i++)
        {
            if (pattern[i] == GameObjectId.Air)
            {
                if (patternCounts[i] != 0)
                {
                    throw new ArgumentException("Empty pattern slots must have a count of 0.");
                }
            }
            else
            {
                if (patternCounts[i] <= 0)
                {
                    throw new ArgumentException("Non-empty pattern slots must have a positive count.");
                }
            }
        }

        ResultItem = resultItem;
        ResultCount = resultCount;
        Pattern = pattern;
        PatternCounts = patternCounts;
    }
}
