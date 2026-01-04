using System.Collections.Frozen;

namespace DarkVox.Shared.World.Registry;

/// <summary>
/// Single source of truth for all game content registration.
/// Owns the authoritative registration of blocks and non-block items.
/// Other registries (BlockRegistry/GameObjectRegistry) delegate to this.
/// </summary>
public static class GameContentRegistry
{
    private const string BlockTextureDir = "Resources/voxel/blocks";
    private const string ItemTextureDir = "Resources/voxel/items";

    /// <summary>
    /// All registered objects keyed by <see cref="GameObjectId"/>.
    /// </summary>
    public static FrozenDictionary<GameObjectId, GameObject> Objects { get; }

    /// <summary>
    /// All registered blocks keyed by <see cref="BlockId"/>.
    /// </summary>
    public static FrozenDictionary<BlockId, Block> Blocks { get; }

    static GameContentRegistry()
    {
        var builder = new Dictionary<GameObjectId, GameObject>();
        RegisterAll(builder);

        Objects = builder.ToFrozenDictionary();

        Blocks = builder
            .Select(kvp => (Id: kvp.Key, Block: kvp.Value as Block))
            .Where(x => x.Block != null && x.Id.IsBlock())
            .ToDictionary(x => x.Id.ToBlockId(), x => x.Block!)
            .ToFrozenDictionary();
    }

    /// <summary>
    /// Gets a game object by ID. Returns Air if not found.
    /// </summary>
    public static GameObject Get(GameObjectId id) => Objects.TryGetValue(id, out var obj) ? obj : Objects[GameObjectId.Air];

    /// <summary>
    /// Tries to get a game object by ID.
    /// </summary>
    public static bool TryGet(GameObjectId id, out GameObject? obj) => Objects.TryGetValue(id, out obj);

    /// <summary>
    /// Gets a block by ID. Returns Air block if not found.
    /// </summary>
    public static Block GetBlock(BlockId id) => Blocks.TryGetValue(id, out var block) ? block : Blocks[BlockId.Air];

    /// <summary>
    /// Gets a block by object ID. Returns Air block if not found or not a block.
    /// </summary>
    public static Block GetBlock(GameObjectId id) => id.IsBlock() ? GetBlock(id.ToBlockId()) : Blocks[BlockId.Air];

    /// <summary>
    /// Gets the food object if the ID is food, otherwise null.
    /// </summary>
    public static Food? GetFood(GameObjectId id) => Objects.TryGetValue(id, out var obj) ? obj as Food : null;

    /// <summary>
    /// Gets the tool object if the ID is a tool, otherwise null.
    /// </summary>
    public static Tool? GetTool(GameObjectId id) => Objects.TryGetValue(id, out var obj) ? obj as Tool : null;

    /// <summary>
    /// Checks if the given ID is a block.
    /// </summary>
    public static bool IsBlock(GameObjectId id) => id.IsBlock() && Blocks.ContainsKey(id.ToBlockId());

    /// <summary>
    /// Checks if the given ID is food.
    /// </summary>
    public static bool IsFood(GameObjectId id) => Objects.TryGetValue(id, out var obj) && obj is Food;

    /// <summary>
    /// Checks if the given ID is a tool.
    /// </summary>
    public static bool IsTool(GameObjectId id) => Objects.TryGetValue(id, out var obj) && obj is Tool;

    // === Block property accessors (fast path) ===

    public static byte GetLightValue(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.LightValue : (byte)0;

    public static byte GetLightFilter(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.LightFilter : (byte)15;

    public static BlockRenderShape GetRenderShape(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.Shape : BlockRenderShape.FullCube;

    public static RenderMethod GetRenderMethod(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.RenderMethod : RenderMethod.Opaque;

    public static int GetLightDecay(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.LightDecay : 15;

    public static bool IsSolid(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsSolid : true;

    public static bool IsOpaque(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsOpaque : true;

    public static bool IsLiquid(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsLiquid : false;

    public static bool IsTranslucent(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsTranslucent : false;

    public static bool IsReplaceable(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsReplaceable : false;

    public static bool IsEmissive(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsEmissive : false;

    public static bool IsTree(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsTree : false;

    public static bool IsVegetation(BlockId block) => Blocks.TryGetValue(block, out var b) ? b.IsVegetation : false;

    /// <summary>
    /// Resolves the expected on-disk texture path for the given object.
    /// Uses explicit <see cref="GameObject.TexturePath"/> when set, otherwise convention-based.
    /// </summary>
    public static string? ResolveTexturePath(GameObjectId id)
    {
        var obj = Get(id);
        if (!string.IsNullOrWhiteSpace(obj.TexturePath))
        {
            return obj.TexturePath;
        }

        if (obj is Block block)
        {
            return $"{BlockTextureDir}/{PascalToSnakeCase(block.BlockId.ToString())}.png";
        }

        return $"{ItemTextureDir}/{PascalToSnakeCase(id.ToString())}.png";
    }

    /// <summary>
    /// Returns all expected texture paths for registered objects.
    /// </summary>
    public static IEnumerable<(GameObjectId Id, string? Path)> GetExpectedTexturePaths() =>
        Objects.Keys.Select(id => (id, ResolveTexturePath(id)));

    // Texture validation is done by the client-side texture loading system.

    private static string PascalToSnakeCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
        {
            return pascalCase;
        }

        var result = new System.Text.StringBuilder();

        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];

            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    result.Append('_');
                }

                result.Append(char.ToLowerInvariant(c));
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    private static void RegisterAll(Dictionary<GameObjectId, GameObject> builder)
    {
        RegisterBlocks(builder);
        RegisterTools(builder);
        RegisterFoods(builder);
        RegisterMaterials(builder);
        RegisterPlaceables(builder);

        builder.TryAdd(GameObjectId.Air, new Block(BlockId.Air) {
            IsSolid = false,
            IsOpaque = false,
            IsReplaceable = true,
            RenderMethod = RenderMethod.None,
            Shape = BlockRenderShape.None,
            LightFilter = 0
        });
    }

    private static void RegisterBlocks(Dictionary<GameObjectId, GameObject> builder)
    {
        // NOTE: This should stay in sync with BlockRegistry registration.
        // We register blocks into the unified object dictionary using the block IDs.

        void Add(BlockId id, Block block) => builder[id.ToGameObjectId()] = block;

        Add(BlockId.Air, new Block(BlockId.Air) {
            IsSolid = false, IsOpaque = false, IsReplaceable = true,
            RenderMethod = RenderMethod.None, Shape = BlockRenderShape.None,
            LightFilter = 0
        });

        Add(BlockId.Water, new Block(BlockId.Water) {
            IsSolid = false, IsOpaque = false, IsLiquid = true, IsTranslucent = true, IsReplaceable = true,
            LightFilter = 2, RenderMethod = RenderMethod.Blend
        });

        Add(BlockId.Lava, new Block(BlockId.Lava) {
            IsSolid = false, IsOpaque = false, IsLiquid = true, IsEmissive = true, IsReplaceable = true,
            LightValue = 15, LightFilter = 0, RenderMethod = RenderMethod.Blend
        });

        Add(BlockId.Stone, new Block(BlockId.Stone) { Hardness = 1.5f });
        Add(BlockId.Bedrock, new Block(BlockId.Bedrock) { Hardness = -1.0f });
        Add(BlockId.Cobblestone, new Block(BlockId.Cobblestone) { Hardness = 2.0f });
        Add(BlockId.MossyCobblestone, new Block(BlockId.MossyCobblestone) { Hardness = 2.0f });
        Add(BlockId.Granite, new Block(BlockId.Granite) { Hardness = 1.5f });

        Add(BlockId.Dirt, new Block(BlockId.Dirt) { Hardness = 0.5f });
        Add(BlockId.Grass, new Block(BlockId.Grass) { Hardness = 0.35f });
        Add(BlockId.GrassSnowy, new Block(BlockId.GrassSnowy) { Hardness = 0.6f });
        Add(BlockId.Podzol, new Block(BlockId.Podzol) { Hardness = 0.5f });
        Add(BlockId.Mycelium, new Block(BlockId.Mycelium) { Hardness = 0.6f });
        Add(BlockId.CoarseDirt, new Block(BlockId.CoarseDirt) { Hardness = 0.5f });
        Add(BlockId.GrassH, new Block(BlockId.GrassH) { Hardness = 0.35f });

        Add(BlockId.Sand, new Block(BlockId.Sand) { Hardness = 0.4f });
        Add(BlockId.RedSand, new Block(BlockId.RedSand) { Hardness = 0.5f });
        Add(BlockId.Sandstone, new Block(BlockId.Sandstone) { Hardness = 0.8f });
        Add(BlockId.RedSandstone, new Block(BlockId.RedSandstone) { Hardness = 0.8f });

        Add(BlockId.Gravel, new Block(BlockId.Gravel) { Hardness = 0.6f });
        Add(BlockId.Clay, new Block(BlockId.Clay) { Hardness = 0.6f });

        Add(BlockId.Snow, new Block(BlockId.Snow) {
            IsSolid = true, IsOpaque = true,
            LightValue = 0, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Add(BlockId.SnowDirt, new Block(BlockId.SnowDirt));

        Add(BlockId.Ice, new Block(BlockId.Ice) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 1, RenderMethod = RenderMethod.Blend
        });
        Add(BlockId.PackedIce, new Block(BlockId.PackedIce) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 1, RenderMethod = RenderMethod.Blend
        });
        Add(BlockId.BlueIce, new Block(BlockId.BlueIce) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 1, RenderMethod = RenderMethod.Blend
        });

        Add(BlockId.Terracotta, new Block(BlockId.Terracotta));
        Add(BlockId.WhiteTerracotta, new Block(BlockId.WhiteTerracotta));
        Add(BlockId.OrangeTerracotta, new Block(BlockId.OrangeTerracotta));
        Add(BlockId.RedTerracotta, new Block(BlockId.RedTerracotta));
        Add(BlockId.BrownTerracotta, new Block(BlockId.BrownTerracotta));
        Add(BlockId.YellowTerracotta, new Block(BlockId.YellowTerracotta));

        Add(BlockId.CoalOre, new Block(BlockId.CoalOre));
        Add(BlockId.IronOre, new Block(BlockId.IronOre));
        Add(BlockId.GoldOre, new Block(BlockId.GoldOre));
        Add(BlockId.DiamondOre, new Block(BlockId.DiamondOre));
        Add(BlockId.CopperOre, new Block(BlockId.CopperOre));

        Add(BlockId.OakLog, new Block(BlockId.OakLog) { IsTree = true });
        Add(BlockId.BirchLog, new Block(BlockId.BirchLog) { IsTree = true });
        Add(BlockId.SpruceLog, new Block(BlockId.SpruceLog) { IsTree = true });
        Add(BlockId.JungleLog, new Block(BlockId.JungleLog) { IsTree = true });

        Add(BlockId.OakLeaves, CreateLeaves(BlockId.OakLeaves));
        Add(BlockId.BirchLeaves, CreateLeaves(BlockId.BirchLeaves));
        Add(BlockId.SpruceLeaves, CreateLeaves(BlockId.SpruceLeaves));
        Add(BlockId.JungleLeaves, CreateLeaves(BlockId.JungleLeaves));

        Add(BlockId.Torch, new Block(BlockId.Torch) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 14, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Add(BlockId.WallTorch, new Block(BlockId.WallTorch) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 14, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Add(BlockId.SoulTorch, new Block(BlockId.SoulTorch) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 10, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Add(BlockId.Glowstone, new Block(BlockId.Glowstone) {
            IsSolid = true, IsOpaque = true, IsEmissive = true,
            LightValue = 15, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Add(BlockId.SeaLantern, new Block(BlockId.SeaLantern) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true, IsEmissive = true,
            LightValue = 15, LightFilter = 1, RenderMethod = RenderMethod.Blend
        });
        Add(BlockId.Lantern, new Block(BlockId.Lantern) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 15, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Add(BlockId.SoulLantern, new Block(BlockId.SoulLantern) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 10, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Add(BlockId.RedstoneLamp, new Block(BlockId.RedstoneLamp));
        Add(BlockId.RedstoneLampOn, new Block(BlockId.RedstoneLampOn) {
            IsSolid = true, IsOpaque = true, IsEmissive = true,
            LightValue = 15, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Add(BlockId.EndRod, new Block(BlockId.EndRod) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 14, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest, Shape = BlockRenderShape.CrossBillboard
        });
        Add(BlockId.Shroomlight, new Block(BlockId.Shroomlight) {
            IsSolid = true, IsOpaque = true, IsEmissive = true,
            LightValue = 15, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Add(BlockId.JackOLantern, new Block(BlockId.JackOLantern) {
            IsSolid = true, IsOpaque = true, IsEmissive = true,
            LightValue = 15, LightFilter = 15, RenderMethod = RenderMethod.Opaque
        });
        Add(BlockId.Campfire, new Block(BlockId.Campfire) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 15, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest
        });
        Add(BlockId.SoulCampfire, new Block(BlockId.SoulCampfire) {
            IsSolid = false, IsOpaque = false, IsEmissive = true,
            LightValue = 10, LightFilter = 0, RenderMethod = RenderMethod.AlphaTest
        });

        BlockId[] glassIds = [
            BlockId.Glass,
            BlockId.WhiteStainedGlass,
            BlockId.OrangeStainedGlass,
            BlockId.MagentaStainedGlass,
            BlockId.LightBlueStainedGlass,
            BlockId.YellowStainedGlass,
            BlockId.LimeStainedGlass,
            BlockId.PinkStainedGlass,
            BlockId.GrayStainedGlass,
            BlockId.LightGrayStainedGlass,
            BlockId.CyanStainedGlass,
            BlockId.PurpleStainedGlass,
            BlockId.BlueStainedGlass,
            BlockId.BrownStainedGlass,
            BlockId.GreenStainedGlass,
            BlockId.RedStainedGlass,
            BlockId.BlackStainedGlass
        ];

        foreach (var gid in glassIds)
        {
            Add(gid, new Block(gid) {
                IsSolid = true, IsOpaque = false, IsTranslucent = true,
                LightFilter = 0, RenderMethod = RenderMethod.Blend,
                Hardness = 0.2f
            });
        }

        Add(BlockId.TintedGlass, new Block(BlockId.TintedGlass) {
            IsSolid = true, IsOpaque = false, IsTranslucent = true,
            LightFilter = 15, RenderMethod = RenderMethod.Blend, Hardness = 0.2f
        });

        BlockId[] flowers = [
            BlockId.TallGrass,
            BlockId.Poppy,
            BlockId.Dandelion,
            BlockId.BlueOrchid,
            BlockId.Allium,
            BlockId.AzureBluet,
            BlockId.RedTulip,
            BlockId.OrangeTulip,
            BlockId.WhiteTulip,
            BlockId.PinkTulip,
            BlockId.OxeyeDaisy,
            BlockId.Cornflower,
            BlockId.LilyOfTheValley,
            BlockId.WitherRose,
            BlockId.Sunflower,
            BlockId.Lilac,
            BlockId.RoseBush,
            BlockId.Peony,
            BlockId.DeadBush,
            BlockId.SugarCane,
            BlockId.Bamboo,
            BlockId.GrassPatch
        ];

        foreach (var fid in flowers)
        {
            var isTallGrass = fid is BlockId.TallGrass or BlockId.GrassPatch;
            var loot = isTallGrass
                ? LootTable.WithChanceDrops(new LootEntry(GameObjectId.WheatSeeds, 1, 1, 0.125f))
                : LootTable.Nothing;

            Add(fid, new Block(fid) {
                IsSolid = false, IsOpaque = false, IsReplaceable = true, IsVegetation = true,
                RenderMethod = RenderMethod.AlphaTest,
                Shape = BlockRenderShape.CrossBillboard,
                Hardness = 0,
                LightFilter = 0,
                LootTable = loot
            });
        }

        Add(BlockId.Cactus, new Block(BlockId.Cactus) {
            IsSolid = true, IsOpaque = true,
            RenderMethod = RenderMethod.AlphaTest,
            Hardness = 0.3f
        });
    }

    private static Block CreateLeaves(BlockId blockId)
    {
        var saplingId = blockId switch
        {
            BlockId.OakLeaves => GameObjectId.OakSapling,
            BlockId.BirchLeaves => GameObjectId.BirchSapling,
            BlockId.SpruceLeaves => GameObjectId.SpruceSapling,
            BlockId.JungleLeaves => GameObjectId.JungleSapling,
            _ => GameObjectId.OakSapling
        };

        var isOak = blockId == BlockId.OakLeaves;

        LootEntry[] drops = isOak
            ? [
                new LootEntry(saplingId, 1, 1, 0.05f),
                new LootEntry(GameObjectId.Stick, 1, 2, 0.02f),
                new LootEntry(GameObjectId.Apple, 1, 1, 0.005f)
              ]
            : [
                new LootEntry(saplingId, 1, 1, 0.05f),
                new LootEntry(GameObjectId.Stick, 1, 2, 0.02f)
              ];

        return new Block(blockId)
        {
            IsSolid = true,
            IsOpaque = false,
            IsTree = true,
            LightFilter = 2,
            RenderMethod = RenderMethod.AlphaTest,
            Hardness = 0.1f,
            LootTable = LootTable.WithChanceDrops(drops)
        };
    }

    private static void RegisterTools(Dictionary<GameObjectId, GameObject> builder)
    {
        builder[GameObjectId.DiamondSword] = new Tool(GameObjectId.DiamondSword, ToolType.Sword, miningSpeed: 1.5f, maxDurability: 1561)
        {
            TexturePath = $"{ItemTextureDir}/diamond_sword.png"
        };

        builder[GameObjectId.DiamondPickaxe] = new Tool(GameObjectId.DiamondPickaxe, ToolType.Pickaxe, miningSpeed: 8.0f, maxDurability: 1561)
        {
            TexturePath = $"{ItemTextureDir}/diamond_pickaxe.png"
        };

        builder[GameObjectId.DiamondAxe] = new Tool(GameObjectId.DiamondAxe, ToolType.Axe, miningSpeed: 8.0f, maxDurability: 1561)
        {
            TexturePath = $"{ItemTextureDir}/diamond_axe.png"
        };

        builder[GameObjectId.DiamondShovel] = new Tool(GameObjectId.DiamondShovel, ToolType.Shovel, miningSpeed: 8.0f, maxDurability: 1561)
        {
            TexturePath = $"{ItemTextureDir}/diamond_shovel.png"
        };
    }

    private static void RegisterFoods(Dictionary<GameObjectId, GameObject> builder)
    {
        builder[GameObjectId.Apple] = new Food(GameObjectId.Apple, nutrition: 4, saturationModifier: 0.3f)
        {
            TexturePath = $"{ItemTextureDir}/apple.png"
        };

        builder[GameObjectId.RawBeef] = new Food(GameObjectId.RawBeef, nutrition: 3, saturationModifier: 0.3f)
        {
            TexturePath = $"{ItemTextureDir}/raw_beef.png"
        };

        builder[GameObjectId.RawPorkchop] = new Food(GameObjectId.RawPorkchop, nutrition: 3, saturationModifier: 0.3f)
        {
            TexturePath = $"{ItemTextureDir}/raw_porkchop.png"
        };

        builder[GameObjectId.RottenFlesh] = new Food(GameObjectId.RottenFlesh, nutrition: 4, saturationModifier: 0.1f)
        {
            TexturePath = $"{ItemTextureDir}/rotten_flesh.png"
        };
    }

    private static void RegisterMaterials(Dictionary<GameObjectId, GameObject> builder)
    {
        builder[GameObjectId.Stick] = new CraftingMaterial(GameObjectId.Stick)
        {
            TexturePath = $"{ItemTextureDir}/stick.png"
        };

        builder[GameObjectId.Leather] = new CraftingMaterial(GameObjectId.Leather)
        {
            TexturePath = $"{ItemTextureDir}/leather.png"
        };

        builder[GameObjectId.Bone] = new CraftingMaterial(GameObjectId.Bone)
        {
            TexturePath = $"{ItemTextureDir}/bone.png"
        };

        builder[GameObjectId.Arrow] = new CombatItem(GameObjectId.Arrow)
        {
            TexturePath = $"{ItemTextureDir}/arrow.png"
        };
    }

    private static void RegisterPlaceables(Dictionary<GameObjectId, GameObject> builder)
    {
        var saplingValidBlocks = new[]
        {
            GameObjectId.Dirt,
            GameObjectId.Grass,
            GameObjectId.GrassSnowy,
            GameObjectId.Podzol,
        };

        builder[GameObjectId.OakSapling] = new Placeable(GameObjectId.OakSapling, GameObjectId.Air)
        {
            TexturePath = $"{ItemTextureDir}/oak_sapling.png",
            ValidPlacementBlocks = saplingValidBlocks
        };

        builder[GameObjectId.BirchSapling] = new Placeable(GameObjectId.BirchSapling, GameObjectId.Air)
        {
            TexturePath = $"{ItemTextureDir}/birch_sapling.png",
            ValidPlacementBlocks = saplingValidBlocks
        };

        builder[GameObjectId.SpruceSapling] = new Placeable(GameObjectId.SpruceSapling, GameObjectId.Air)
        {
            TexturePath = $"{ItemTextureDir}/spruce_sapling.png",
            ValidPlacementBlocks = saplingValidBlocks
        };

        builder[GameObjectId.JungleSapling] = new Placeable(GameObjectId.JungleSapling, GameObjectId.Air)
        {
            TexturePath = $"{ItemTextureDir}/jungle_sapling.png",
            ValidPlacementBlocks = saplingValidBlocks
        };

        builder[GameObjectId.WheatSeeds] = new Placeable(GameObjectId.WheatSeeds, GameObjectId.Air)
        {
            TexturePath = $"{ItemTextureDir}/wheat_seeds.png"
        };
    }
}
