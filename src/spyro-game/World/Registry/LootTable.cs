namespace SpyroGame.World.Registry;

/// <summary>
/// Represents a single drop entry in a loot table.
/// </summary>
public readonly record struct LootEntry(
    /// <summary>The item that can drop.</summary>
    ItemId Item,
    /// <summary>Minimum count when this drop occurs.</summary>
    int MinCount,
    /// <summary>Maximum count when this drop occurs.</summary>
    int MaxCount,
    /// <summary>Chance of this drop occurring (0.0 to 1.0).</summary>
    float Chance);

/// <summary>
/// Defines what items drop when a block is broken.
/// </summary>
public sealed class LootTable
{
    /// <summary>
    /// If true, the block drops itself as an item (converted via BlockId to ItemId).
    /// This is the default for most solid blocks.
    /// </summary>
    public bool DropsSelf { get; init; } = true;

    /// <summary>
    /// Additional drops with their chances.
    /// These are rolled independently of DropsSelf.
    /// </summary>
    public LootEntry[] AdditionalDrops { get; init; } = [];

    /// <summary>
    /// Pre-built loot table for blocks that drop themselves.
    /// </summary>
    public static readonly LootTable Self = new() { DropsSelf = true };

    /// <summary>
    /// Pre-built loot table for blocks that drop nothing (vegetation, leaves without drops).
    /// </summary>
    public static readonly LootTable Nothing = new() { DropsSelf = false };

    /// <summary>
    /// Creates a loot table for vegetation/leaves that don't drop themselves
    /// but have a chance to drop other items.
    /// </summary>
    public static LootTable WithChanceDrops(params LootEntry[] drops) => new()
    {
        DropsSelf = false,
        AdditionalDrops = drops
    };

    /// <summary>
    /// Creates a loot table that drops self plus additional items.
    /// </summary>
    public static LootTable SelfWithExtras(params LootEntry[] drops) => new()
    {
        DropsSelf = true,
        AdditionalDrops = drops
    };

    /// <summary>
    /// Generates the drops for this loot table.
    /// </summary>
    /// <param name="blockId">The block being broken (used if DropsSelf is true).</param>
    /// <param name="random">Random instance for chance calculations.</param>
    /// <returns>List of (ItemId, Count) tuples representing the drops.</returns>
    public List<(ItemId Item, int Count)> GenerateDrops(BlockId blockId, Random? random = null)
    {
        random ??= Random.Shared;
        var drops = new List<(ItemId, int)>();

        // Drop self if configured
        if (DropsSelf)
        {
            // Convert BlockId to ItemId (they share the same numeric values for blocks)
            if (Enum.TryParse<ItemId>(blockId.ToString(), out var itemId))
            {
                drops.Add((itemId, 1));
            }
        }

        // Roll for additional drops
        foreach (var entry in AdditionalDrops)
        {
            if (random.NextSingle() < entry.Chance)
            {
                var count = entry.MinCount == entry.MaxCount 
                    ? entry.MinCount 
                    : random.Next(entry.MinCount, entry.MaxCount + 1);
                
                if (count > 0)
                {
                    drops.Add((entry.Item, count));
                }
            }
        }

        return drops;
    }
}
