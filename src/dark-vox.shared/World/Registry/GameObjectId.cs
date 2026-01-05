namespace DarkVox.Shared.World.Registry;

/// <summary>
/// Unified identifier for all game objects (blocks, tools, food, materials).
/// Replaces both BlockId and ItemId with a single type.
/// </summary>
/// <remarks>
/// <para>
/// IDs 0-999 are blocks that can be placed in the world.
/// IDs 1000+ are items that cannot be placed directly.
/// </para>
/// <para>ID ranges:</para>
/// <list type="bullet">
///   <item><description>0-9: Special/Air/Liquids/Stone</description></item>
///   <item><description>10-19: Dirt/Grass</description></item>
///   <item><description>20-29: Sand</description></item>
///   <item><description>30-39: Gravel/Clay</description></item>
///   <item><description>40-49: Snow/Ice</description></item>
///   <item><description>50-59: Terracotta</description></item>
///   <item><description>100-109: Ores</description></item>
///   <item><description>120-139: Wood</description></item>
///   <item><description>140-149: Leaves</description></item>
///   <item><description>160-199: Vegetation</description></item>
///   <item><description>200-219: Light sources</description></item>
///   <item><description>220-239: Glass</description></item>
///   <item><description>1000-1099: Tools</description></item>
///   <item><description>1100-1199: Food</description></item>
///   <item><description>1200-1299: Materials</description></item>
///   <item><description>1300-1399: Placeables</description></item>
///   <item><description>1400-1499: Combat</description></item>
/// </list>
/// </remarks>
public enum GameObjectId : ushort
{
    #region Special (0-9)

    /// <summary>Empty space - replaceable, not solid.</summary>
    Air = 0,
    /// <summary>Water block - liquid, translucent, replaceable.</summary>
    Water = 1,
    /// <summary>Lava block - liquid, emissive, replaceable.</summary>
    Lava = 2,
    /// <summary>Basic stone - solid, opaque.</summary>
    Stone = 3,
    /// <summary>Indestructible bedrock - solid, opaque.</summary>
    Bedrock = 4,
    /// <summary>Cobblestone - solid, opaque.</summary>
    Cobblestone = 5,
    /// <summary>Mossy cobblestone - solid, opaque.</summary>
    MossyCobblestone = 6,
    /// <summary>Granite - solid, opaque.</summary>
    Granite = 7,
    /// <summary>Special ID for breaking animation overlay.</summary>
    BlockBreak = 8,

    #endregion

    #region Dirt/Grass (10-19)

    /// <summary>Dirt block - solid, opaque.</summary>
    Dirt = 10,
    /// <summary>Grass block (green top) - solid, opaque.</summary>
    Grass = 11,
    /// <summary>Grass with snow on top - solid, opaque.</summary>
    GrassSnowy = 12,
    /// <summary>Taiga forest floor - solid, opaque.</summary>
    Podzol = 13,
    /// <summary>Mushroom biome ground - solid, opaque.</summary>
    Mycelium = 14,
    /// <summary>Coarse dirt (no grass growth) - solid, opaque.</summary>
    CoarseDirt = 15,
    /// <summary>Grass block (greenish yellow top) - solid, opaque.</summary>
    GrassH = 16,

    #endregion

    #region Sand (20-29)

    /// <summary>Sand block - solid, opaque.</summary>
    Sand = 20,
    /// <summary>Red sand (mesa biome) - solid, opaque.</summary>
    RedSand = 21,
    /// <summary>Sandstone - solid, opaque.</summary>
    Sandstone = 22,
    /// <summary>Red sandstone - solid, opaque.</summary>
    RedSandstone = 23,

    #endregion

    #region Gravel/Clay (30-39)

    /// <summary>Gravel - solid, opaque.</summary>
    Gravel = 30,
    /// <summary>Clay - solid, opaque.</summary>
    Clay = 31,

    #endregion

    #region Snow/Ice (40-49)

    /// <summary>Snow layer - solid, opaque.</summary>
    Snow = 40,
    /// <summary>Full snow block - solid, opaque.</summary>
    SnowDirt = 41,
    /// <summary>Ice - solid, translucent.</summary>
    Ice = 42,
    /// <summary>Packed ice - solid, translucent.</summary>
    PackedIce = 43,
    /// <summary>Blue ice - solid, translucent.</summary>
    BlueIce = 44,

    #endregion

    #region Terracotta (50-59)

    /// <summary>Terracotta - solid, opaque.</summary>
    Terracotta = 50,
    /// <summary>White terracotta - solid, opaque.</summary>
    WhiteTerracotta = 51,
    /// <summary>Orange terracotta - solid, opaque.</summary>
    OrangeTerracotta = 52,
    /// <summary>Red terracotta - solid, opaque.</summary>
    RedTerracotta = 53,
    /// <summary>Brown terracotta - solid, opaque.</summary>
    BrownTerracotta = 54,
    /// <summary>Yellow terracotta - solid, opaque.</summary>
    YellowTerracotta = 55,

    #endregion

    #region Ores (100-109)

    /// <summary>Coal ore - solid, opaque.</summary>
    CoalOre = 100,
    /// <summary>Iron ore - solid, opaque.</summary>
    IronOre = 101,
    /// <summary>Gold ore - solid, opaque.</summary>
    GoldOre = 102,
    /// <summary>Diamond ore - solid, opaque.</summary>
    DiamondOre = 103,
    /// <summary>Copper ore - solid, opaque.</summary>
    CopperOre = 104,

    #endregion

    #region Wood (120-139)

    /// <summary>Oak log - solid, opaque.</summary>
    OakLog = 120,
    /// <summary>Birch log - solid, opaque.</summary>
    BirchLog = 121,
    /// <summary>Spruce log - solid, opaque.</summary>
    SpruceLog = 122,
    /// <summary>Jungle log - solid, opaque.</summary>
    JungleLog = 123,

    #endregion

    #region Leaves (140-149)

    /// <summary>Oak leaves - solid, translucent.</summary>
    OakLeaves = 140,
    /// <summary>Birch leaves - solid, translucent.</summary>
    BirchLeaves = 141,
    /// <summary>Spruce leaves - solid, translucent.</summary>
    SpruceLeaves = 142,
    /// <summary>Jungle leaves - solid, translucent.</summary>
    JungleLeaves = 143,

    #endregion

    #region Vegetation (160-199)

    /// <summary>Tall grass - cross model, replaceable.</summary>
    TallGrass = 160,
    /// <summary>Poppy - cross model.</summary>
    Poppy = 161,
    /// <summary>Dandelion - cross model.</summary>
    Dandelion = 162,
    /// <summary>Blue Orchid - cross model.</summary>
    BlueOrchid = 163,
    /// <summary>Allium - cross model.</summary>
    Allium = 164,
    /// <summary>Azure Bluet - cross model.</summary>
    AzureBluet = 165,
    /// <summary>Red Tulip - cross model.</summary>
    RedTulip = 166,
    /// <summary>Orange Tulip - cross model.</summary>
    OrangeTulip = 167,
    /// <summary>White Tulip - cross model.</summary>
    WhiteTulip = 168,
    /// <summary>Pink Tulip - cross model.</summary>
    PinkTulip = 169,
    /// <summary>Oxeye Daisy - cross model.</summary>
    OxeyeDaisy = 170,
    /// <summary>Cornflower - cross model.</summary>
    Cornflower = 171,
    /// <summary>Lily of the Valley - cross model.</summary>
    LilyOfTheValley = 172,
    /// <summary>Wither Rose - cross model.</summary>
    WitherRose = 173,
    /// <summary>Sunflower - cross model.</summary>
    Sunflower = 174,
    /// <summary>Lilac - cross model.</summary>
    Lilac = 175,
    /// <summary>Rose Bush - cross model.</summary>
    RoseBush = 176,
    /// <summary>Peony - cross model.</summary>
    Peony = 177,
    /// <summary>Dead Bush - cross model.</summary>
    DeadBush = 178,
    /// <summary>Cactus - solid, opaque.</summary>
    Cactus = 179,
    /// <summary>Sugar Cane - cross model.</summary>
    SugarCane = 180,
    /// <summary>Bamboo - cross model.</summary>
    Bamboo = 181,
    /// <summary>Grass patch - cross model, replaceable.</summary>
    GrassPatch = 182,

    #endregion

    #region Light Sources (200-219)

    /// <summary>Torch - non-solid, emissive, light value 14.</summary>
    Torch = 200,
    /// <summary>Wall torch - non-solid, emissive, light value 14.</summary>
    WallTorch = 201,
    /// <summary>Soul torch - non-solid, emissive, light value 10.</summary>
    SoulTorch = 202,
    /// <summary>Glowstone - solid, opaque, emissive, light value 15.</summary>
    Glowstone = 203,
    /// <summary>Sea lantern - solid, translucent, emissive, light value 15.</summary>
    SeaLantern = 204,
    /// <summary>Lantern - non-solid, emissive, light value 15.</summary>
    Lantern = 205,
    /// <summary>Soul lantern - non-solid, emissive, light value 10.</summary>
    SoulLantern = 206,
    /// <summary>Redstone lamp (off) - solid, opaque.</summary>
    RedstoneLamp = 207,
    /// <summary>Redstone lamp (on) - solid, opaque, emissive.</summary>
    RedstoneLampOn = 208,
    /// <summary>End rod - non-solid, emissive, light value 14.</summary>
    EndRod = 209,
    /// <summary>Shroomlight - solid, opaque, emissive.</summary>
    Shroomlight = 210,
    /// <summary>Jack o'Lantern - solid, opaque, emissive.</summary>
    JackOLantern = 211,
    /// <summary>Campfire - non-solid, emissive.</summary>
    Campfire = 212,
    /// <summary>Soul campfire - non-solid, emissive.</summary>
    SoulCampfire = 213,

    #endregion

    #region Glass (220-239)

    /// <summary>Glass - solid, translucent.</summary>
    Glass = 220,
    /// <summary>White stained glass - solid, translucent.</summary>
    WhiteStainedGlass = 221,
    /// <summary>Orange stained glass - solid, translucent.</summary>
    OrangeStainedGlass = 222,
    /// <summary>Magenta stained glass - solid, translucent.</summary>
    MagentaStainedGlass = 223,
    /// <summary>Light blue stained glass - solid, translucent.</summary>
    LightBlueStainedGlass = 224,
    /// <summary>Yellow stained glass - solid, translucent.</summary>
    YellowStainedGlass = 225,
    /// <summary>Lime stained glass - solid, translucent.</summary>
    LimeStainedGlass = 226,
    /// <summary>Pink stained glass - solid, translucent.</summary>
    PinkStainedGlass = 227,
    /// <summary>Gray stained glass - solid, translucent.</summary>
    GrayStainedGlass = 228,
    /// <summary>Light gray stained glass - solid, translucent.</summary>
    LightGrayStainedGlass = 229,
    /// <summary>Cyan stained glass - solid, translucent.</summary>
    CyanStainedGlass = 230,
    /// <summary>Purple stained glass - solid, translucent.</summary>
    PurpleStainedGlass = 231,
    /// <summary>Blue stained glass - solid, translucent.</summary>
    BlueStainedGlass = 232,
    /// <summary>Brown stained glass - solid, translucent.</summary>
    BrownStainedGlass = 233,
    /// <summary>Green stained glass - solid, translucent.</summary>
    GreenStainedGlass = 234,
    /// <summary>Red stained glass - solid, translucent.</summary>
    RedStainedGlass = 235,
    /// <summary>Black stained glass - solid, translucent.</summary>
    BlackStainedGlass = 236,
    /// <summary>Tinted glass - solid, translucent, blocks light.</summary>
    TintedGlass = 237,

    #endregion

    #region Tools (1000-1099)

    /// <summary>Diamond sword - high damage combat weapon.</summary>
    DiamondSword = 1000,
    /// <summary>Diamond pickaxe - mines stone and ores quickly.</summary>
    DiamondPickaxe = 1001,
    /// <summary>Diamond axe - chops wood quickly.</summary>
    DiamondAxe = 1002,
    /// <summary>Diamond shovel - digs dirt and sand quickly.</summary>
    DiamondShovel = 1003,

    #endregion

    #region Food (1100-1199)

    /// <summary>Apple - restores 4 hunger.</summary>
    Apple = 1100,
    /// <summary>Raw beef - restores 3 hunger.</summary>
    RawBeef = 1101,
    /// <summary>Raw porkchop - restores 3 hunger.</summary>
    RawPorkchop = 1102,
    /// <summary>Rotten flesh - restores 4 hunger (low quality).</summary>
    RottenFlesh = 1103,

    #endregion

    #region Materials (1200-1299)

    /// <summary>Stick - basic crafting material.</summary>
    Stick = 1200,
    /// <summary>Leather - mob drop, used for armor.</summary>
    Leather = 1201,
    /// <summary>Bone - mob drop from skeletons.</summary>
    Bone = 1202,

    /// <summary>Coal - mining resource, used as fuel.</summary>
    Coal = 1203,
    #endregion

    #region Placeables (1300-1399)

    /// <summary>Oak sapling - grows into oak tree.</summary>
    OakSapling = 1300,
    /// <summary>Birch sapling - grows into birch tree.</summary>
    BirchSapling = 1301,
    /// <summary>Spruce sapling - grows into spruce tree.</summary>
    SpruceSapling = 1302,
    /// <summary>Jungle sapling - grows into jungle tree.</summary>
    JungleSapling = 1303,
    /// <summary>Wheat seeds - can be planted on farmland.</summary>
    WheatSeeds = 1310,

    #endregion

    #region Combat (1400-1499)

    /// <summary>Arrow - ammunition for bows.</summary>
    Arrow = 1400,

    #endregion
}

/// <summary>
/// Extension methods for <see cref="GameObjectId"/>.
/// </summary>
public static class GameObjectIdExtensions
{
    /// <summary>
    /// Gets the numeric value of the ID for storage and indexing.
    /// </summary>
    public static ushort GetId(this GameObjectId id) => (ushort)id;

    /// <summary>
    /// Returns true if this ID represents a block (can be placed in world).
    /// </summary>
    public static bool IsBlock(this GameObjectId id) => (ushort)id < 1000;

    /// <summary>
    /// Returns true if this ID represents air.
    /// </summary>
    public static bool IsAir(this GameObjectId id) => id == GameObjectId.Air;

    /// <summary>
    /// Returns true if this ID represents water.
    /// </summary>
    public static bool IsWater(this GameObjectId id) => id == GameObjectId.Water;

    /// <summary>
    /// Converts a GameObjectId to a BlockId for world storage.
    /// Only valid for IDs &lt; 1000.
    /// </summary>
    public static BlockId ToBlockId(this GameObjectId id) => (BlockId)(ushort)id;

    /// <summary>
    /// Converts a BlockId to GameObjectId.
    /// </summary>
    public static GameObjectId ToGameObjectId(this BlockId blockId) => (GameObjectId)(ushort)blockId;

    /// <summary>
    /// Tries to parse a string representation of a GameObjectId.
    /// </summary>
    public static bool TryParse(string? value, out GameObjectId result)
    {
        if (string.IsNullOrEmpty(value))
        {
            result = GameObjectId.Air;
            return false;
        }

        // Try parsing as enum name first
        if (Enum.TryParse<GameObjectId>(value, ignoreCase: true, out result))
        {
            return true;
        }

        // Try parsing as numeric value
        if (ushort.TryParse(value, out var numericValue))
        {
            result = (GameObjectId)numericValue;
            return true;
        }

        result = GameObjectId.Air;
        return false;
    }
}
