namespace SpyroGame.World;

/// <summary>
/// Block identifier - a simple enum with unique IDs.
/// All block properties (solid, opaque, light, etc.) are stored in <see cref="BlockRegistry"/>.
/// Use <see cref="BlockRegistry"/> extension methods for property lookups.
/// </summary>
/// <remarks>
/// IDs are organized into ranges for logical grouping:
/// - 0-9: Special/Air/Liquids
/// - 10-19: Dirt/Grass variants
/// - 20-29: Sand variants
/// - 30-39: Gravel/Clay
/// - 40-49: Snow/Ice
/// - 50-59: Terracotta
/// - 100-109: Ores
/// - 120-139: Wood
/// - 140-149: Leaves
/// - 200-219: Light sources
/// - 220-239: Glass
/// </remarks>
public enum BlockId : ushort
{
    // --- Special (0-9) ---
    /// <summary>Empty space - replaceable, not solid.</summary>
    Air = 0,
    /// <summary>Water block - liquid, translucent, replaceable.</summary>
    Water = 1,
    /// <summary>Lava block - liquid, emissive, replaceable.</summary>
    Lava = 2,

    // --- Stone variants (3-9) ---
    /// <summary>Basic stone - solid, opaque.</summary>
    Stone = 3,
    /// <summary>Indestructible bedrock - solid, opaque.</summary>
    Bedrock = 4,
    /// <summary>Cobblestone - solid, opaque.</summary>
    Cobblestone = 5,
    /// <summary>Mossy cobblestone - solid, opaque.</summary>
    MossyCobblestone = 6,

    // --- Dirt/Grass (10-19) ---
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

    // --- Sand (20-29) ---
    /// <summary>Sand block - solid, opaque.</summary>
    Sand = 20,
    /// <summary>Red sand (mesa biome) - solid, opaque.</summary>
    RedSand = 21,
    /// <summary>Sandstone - solid, opaque.</summary>
    Sandstone = 22,
    /// <summary>Red sandstone - solid, opaque.</summary>
    RedSandstone = 23,

    // --- Gravel/Clay (30-39) ---
    /// <summary>Gravel - solid, opaque.</summary>
    Gravel = 30,
    /// <summary>Clay - solid, opaque.</summary>
    Clay = 31,

    // --- Snow/Ice (40-49) ---
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

    // --- Terracotta (50-59) ---
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

    // --- Ores (100-109) ---
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

    // --- Wood (120-139) ---
    /// <summary>Oak log - solid, opaque.</summary>
    OakLog = 120,
    /// <summary>Birch log - solid, opaque.</summary>
    BirchLog = 121,
    /// <summary>Spruce log - solid, opaque.</summary>
    SpruceLog = 122,
    /// <summary>Jungle log - solid, opaque.</summary>
    JungleLog = 123,

    // --- Leaves (140-149) ---
    /// <summary>Oak leaves - solid, translucent.</summary>
    OakLeaves = 140,
    /// <summary>Birch leaves - solid, translucent.</summary>
    BirchLeaves = 141,
    /// <summary>Spruce leaves - solid, translucent.</summary>
    SpruceLeaves = 142,
    /// <summary>Jungle leaves - solid, translucent.</summary>
    JungleLeaves = 143,

    // --- Light Sources (200-219) ---
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
    /// <summary>Redstone lamp (on) - solid, opaque, emissive, light value 15.</summary>
    RedstoneLampOn = 208,
    /// <summary>End rod - non-solid, emissive, light value 14.</summary>
    EndRod = 209,
    /// <summary>Shroomlight - solid, opaque, emissive, light value 15.</summary>
    Shroomlight = 210,
    /// <summary>Jack o'Lantern - solid, opaque, emissive, light value 15.</summary>
    JackOLantern = 211,
    /// <summary>Campfire - non-solid, emissive, light value 15.</summary>
    Campfire = 212,
    /// <summary>Soul campfire - non-solid, emissive, light value 10.</summary>
    SoulCampfire = 213,

    // --- Glass (220-239) ---
    /// <summary>Glass - solid, translucent, fully transparent to light (filter 0).</summary>
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
    /// <summary>Tinted glass - solid, translucent, blocks all light (filter 15) but see-through.</summary>
    TintedGlass = 237,

    // --- Vegetation (160-199) ---
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
    /// <summary>Sunflower - cross model (top/bottom).</summary>
    Sunflower = 174,
    /// <summary>Lilac - cross model (top/bottom).</summary>
    Lilac = 175,
    /// <summary>Rose Bush - cross model (top/bottom).</summary>
    RoseBush = 176,
    /// <summary>Peony - cross model (top/bottom).</summary>
    Peony = 177,
    /// <summary>Dead Bush - cross model.</summary>
    DeadBush = 178,
    /// <summary>Cactus - solid, opaque (custom model later).</summary>
    Cactus = 179,
    /// <summary>Sugar Cane - cross model.</summary>
    SugarCane = 180,
    /// <summary>Bamboo - cross model.</summary>
    Bamboo = 181,
}

/// <summary>
/// Extension methods for BlockId. All property checks delegate to <see cref="BlockRegistry"/>.
/// </summary>
public static class BlockIdExtensions
{
    /// <summary>
    /// Gets the block ID value (for texture array indexing, etc.).
    /// </summary>
    public static ushort GetId(this BlockId block) => (ushort)block;

    /// <summary>
    /// Returns true if this block is solid (blocks movement, needs faces rendered).
    /// </summary>
    public static bool IsSolid(this BlockId block) => BlockRegistry.IsSolid(block);

    /// <summary>
    /// Returns true if this block is opaque (blocks light, culls neighbor faces).
    /// </summary>
    public static bool IsOpaque(this BlockId block) => BlockRegistry.IsOpaque(block);

    /// <summary>
    /// Returns true if this block is a liquid (water, lava).
    /// </summary>
    public static bool IsLiquid(this BlockId block) => BlockRegistry.IsLiquid(block);

    /// <summary>
    /// Returns true if this block is translucent (partial transparency).
    /// </summary>
    public static bool IsTranslucent(this BlockId block) => BlockRegistry.IsTranslucent(block);

    /// <summary>
    /// Returns true if this block can be replaced by other blocks (air, water, tall grass).
    /// </summary>
    public static bool IsReplaceable(this BlockId block) => BlockRegistry.IsReplaceable(block);

    /// <summary>
    /// Returns true if this block is emissive (glows).
    /// </summary>
    public static bool IsEmissive(this BlockId block) => BlockRegistry.IsEmissive(block);

    /// <summary>
    /// Returns true if this block is transparent (air or liquid - not solid and replaceable).
    /// </summary>
    public static bool IsTransparent(this BlockId block) => !block.IsSolid() || block.IsLiquid();

    /// <summary>
    /// Returns true if this block is water.
    /// </summary>
    public static bool IsWater(this BlockId block) => block == BlockId.Water;

    /// <summary>
    /// Returns true if this block is air.
    /// </summary>
    public static bool IsAir(this BlockId block) => block == BlockId.Air;
}
