namespace SpyroGame.World.Registry;

/// <summary>
/// Represents a single drop entry in a loot table.
/// </summary>
/// <param name="Item">The item that can drop.</param>
/// <param name="MinCount">Minimum count when this drop occurs.</param>
/// <param name="MaxCount">Maximum count when this drop occurs.</param>
/// <param name="Chance">Chance of this drop occurring (0.0 to 1.0).</param>
public readonly record struct LootEntry(GameObjectId Item, int MinCount, int MaxCount, float Chance);

/// <summary>
/// Defines what items drop when a block is broken.
/// Supports both self-drops and chance-based additional drops.
/// </summary>
public sealed class LootTable
{
    /// <summary>
    /// If true, the block drops itself as an item.
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
    /// Pre-built loot table for blocks that drop nothing.
    /// </summary>
    public static readonly LootTable Nothing = new() { DropsSelf = false };

    /// <summary>
    /// Creates a loot table for blocks that don't drop themselves
    /// but have a chance to drop other items (e.g., leaves dropping saplings).
    /// </summary>
    /// <param name="drops">The possible drops with their chances.</param>
    public static LootTable WithChanceDrops(params LootEntry[] drops) => new()
    {
        DropsSelf = false,
        AdditionalDrops = drops
    };

    /// <summary>
    /// Creates a loot table that drops the block itself plus additional items.
    /// </summary>
    /// <param name="drops">Additional possible drops with their chances.</param>
    public static LootTable SelfWithExtras(params LootEntry[] drops) => new()
    {
        DropsSelf = true,
        AdditionalDrops = drops
    };

    /// <summary>
    /// Generates the drops for this loot table.
    /// </summary>
    /// <param name="blockId">The block being broken (used if DropsSelf is true).</param>
    /// <param name="random">Random instance for chance calculations. Uses Random.Shared if null.</param>
    /// <returns>List of (GameObjectId, Count) tuples representing the drops.</returns>
    public List<(GameObjectId Item, int Count)> GenerateDrops(BlockId blockId, Random? random = null)
    {
        random ??= Random.Shared;
        var drops = new List<(GameObjectId, int)>();

        // Drop self if configured - blocks use same ID as game objects
        if (DropsSelf)
        {
            var itemId = blockId.ToGameObjectId();
            if (itemId != GameObjectId.Air)
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
