using System.Collections.Frozen;

namespace SpyroGame.World.Registry;

public static class ItemRegistry
{
    public static FrozenDictionary<ItemId, Item> Items { get; private set; }

    static ItemRegistry()
    {
        var builder = new Dictionary<ItemId, Item>();
        RegisterItems(builder);
        Items = builder.ToFrozenDictionary();
    }

    public static Item Get(ItemId id) => Items.TryGetValue(id, out var item) ? item : Items[ItemId.Air];

    /// <summary>
    /// Checks if the given item is a food item.
    /// </summary>
    public static bool IsFood(ItemId id) => Items.TryGetValue(id, out var item) && item is FoodItem;

    /// <summary>
    /// Gets the food item if the item is food, otherwise null.
    /// </summary>
    public static FoodItem? GetFood(ItemId id) => Items.TryGetValue(id, out var item) ? item as FoodItem : null;

    private static void RegisterItems(Dictionary<ItemId, Item> builder)
    {
        // Register BlockItems
        foreach (var block in BlockRegistry.Blocks.Values)
        {
            if (block.Id == BlockId.Air) continue;
            
            if (Enum.TryParse<ItemId>(block.Id.ToString(), out var itemId))
            {
                builder[itemId] = new BlockItem(itemId, block.Id);
            }
        }
        
        
        // Register Pure Items
        Register(builder, ItemId.Stick);
        builder[ItemId.DiamondSword] = new Item(ItemId.DiamondSword) { MiningSpeedMultiplier = 1.5f };

        // Register Food Items (nutrition values from Minecraft wiki)
        // Apple: 4 hunger, 0.3 saturation modifier
        builder[ItemId.Apple] = new FoodItem(ItemId.Apple, nutrition: 4, saturationModifier: 0.3f);
        
        // Raw Beef: 3 hunger, 0.3 saturation modifier
        builder[ItemId.RawBeef] = new FoodItem(ItemId.RawBeef, nutrition: 3, saturationModifier: 0.3f);
        
        // Raw Porkchop: 3 hunger, 0.3 saturation modifier
        builder[ItemId.RawPorkchop] = new FoodItem(ItemId.RawPorkchop, nutrition: 3, saturationModifier: 0.3f);
        
        // Rotten Flesh: 4 hunger, 0.1 saturation modifier (low quality food)
        builder[ItemId.RottenFlesh] = new FoodItem(ItemId.RottenFlesh, nutrition: 4, saturationModifier: 0.1f);

        // Non-food mob drops
        Register(builder, ItemId.Leather);
        Register(builder, ItemId.Bone);
        Register(builder, ItemId.Arrow);

        // Saplings
        Register(builder, ItemId.OakSapling);
        Register(builder, ItemId.BirchSapling);
        Register(builder, ItemId.SpruceSapling);
        Register(builder, ItemId.JungleSapling);

        // Seeds
        Register(builder, ItemId.WheatSeeds);
        
        // Ensure Air item exists
        if (!builder.ContainsKey(ItemId.Air))
        {
             builder[ItemId.Air] = new Item(ItemId.Air);
        }
    }

    private static void Register(Dictionary<ItemId, Item> builder, ItemId id)
        => builder[id] = new Item(id);
}
