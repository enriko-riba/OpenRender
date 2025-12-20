using System.Collections.Frozen;
using SpyroGame.World.Registry;

namespace SpyroGame.World;

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
        Register(builder, ItemId.Apple);
        Register(builder, ItemId.DiamondSword);

        Register(builder, ItemId.RawBeef);
        Register(builder, ItemId.Leather);
        Register(builder, ItemId.RawPorkchop);
        Register(builder, ItemId.RottenFlesh);
        Register(builder, ItemId.Bone);
        Register(builder, ItemId.Arrow);
        
        // Ensure Air item exists if needed, or handle it
        if (!builder.ContainsKey(ItemId.Air))
        {
             builder[ItemId.Air] = new Item(ItemId.Air);
        }
    }

    private static void Register(Dictionary<ItemId, Item> builder, ItemId id)
        => builder[id] = new Item(id);
}
