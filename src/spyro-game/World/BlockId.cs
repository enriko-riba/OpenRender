namespace SpyroGame.World;

/// <summary>
/// Block identifier with embedded property flags.
/// Lower 10 bits = unique block ID (supports up to 1024 blocks).
/// Upper 6 bits = property flags (Solid, Opaque, Liquid, Translucent, Replaceable).
/// </summary>
/// <remarks>
/// This replaces both BlockType and BlockDescriptor enums.
/// Properties are embedded directly in the enum value via flags, eliminating
/// the need for separate lookup tables. Use extension methods like IsSolid(),
/// IsOpaque() etc. which are simple bitmask operations.
/// </remarks>
[Flags]
public enum BlockId : ushort
{
    // === Property Flags (upper 6 bits) ===
    /// <summary>Blocks movement, needs faces rendered.</summary>
    Solid = 1 << 10,
    /// <summary>Blocks light, culls neighbor faces when adjacent.</summary>
    Opaque = 1 << 11,
    /// <summary>Water/lava flow behavior.</summary>
    Liquid = 1 << 12,
    /// <summary>Partial transparency (water, ice, leaves).</summary>
    Translucent = 1 << 13,
    /// <summary>Can be overwritten by other blocks (air, water, tall grass).</summary>
    Replaceable = 1 << 14,
    /// <summary>Emits light, rendered at full brightness.</summary>
    Emissive = 1 << 15,

    // === Block IDs with embedded flags (lower 10 bits = unique ID) ===
    
    // --- Special (0-9) ---
    /// <summary>Empty space - replaceable, not solid.</summary>
    Air = 0 | Replaceable,
    /// <summary>Water block - liquid, translucent, replaceable.</summary>
    Water = 1 | Liquid | Translucent | Replaceable,
    /// <summary>Lava block - liquid, emissive, replaceable.</summary>
    Lava = 2 | Liquid | Emissive | Replaceable,
    
    // --- Stone variants (2-9) ---
    /// <summary>Basic stone - solid, opaque.</summary>
    Stone = 3 | Solid | Opaque,
    /// <summary>Indestructible bedrock - solid, opaque.</summary>
    Bedrock = 4 | Solid | Opaque,
    /// <summary>Cobblestone - solid, opaque.</summary>
    Cobblestone = 5 | Solid | Opaque,
    /// <summary>Mossy cobblestone - solid, opaque.</summary>
    MossyCobblestone = 6 | Solid | Opaque,
    
    // --- Dirt/Grass (10-19) ---
    /// <summary>Dirt block - solid, opaque.</summary>
    Dirt = 10 | Solid | Opaque,
    /// <summary>Grass block (green top) - solid, opaque.</summary>
    Grass = 11 | Solid | Opaque,
    /// <summary>Grass with snow on top - solid, opaque.</summary>
    GrassSnowy = 12 | Solid | Opaque,
    /// <summary>Taiga forest floor - solid, opaque.</summary>
    Podzol = 13 | Solid | Opaque,
    /// <summary>Mushroom biome ground - solid, opaque.</summary>
    Mycelium = 14 | Solid | Opaque,
    /// <summary>Coarse dirt (no grass growth) - solid, opaque.</summary>
    CoarseDirt = 15 | Solid | Opaque,
    
    // --- Sand (20-29) ---
    /// <summary>Sand block - solid, opaque.</summary>
    Sand = 20 | Solid | Opaque,
    /// <summary>Red sand (mesa biome) - solid, opaque.</summary>
    RedSand = 21 | Solid | Opaque,
    /// <summary>Sandstone - solid, opaque.</summary>
    Sandstone = 22 | Solid | Opaque,
    /// <summary>Red sandstone - solid, opaque.</summary>
    RedSandstone = 23 | Solid | Opaque,
    
    // --- Gravel/Clay (30-39) ---
    /// <summary>Gravel - solid, opaque.</summary>
    Gravel = 30 | Solid | Opaque,
    /// <summary>Clay - solid, opaque.</summary>
    Clay = 31 | Solid | Opaque,
    
    // --- Snow/Ice (40-49) ---
    /// <summary>Snow layer - solid, opaque.</summary>
    Snow = 40 | Solid | Opaque,
    /// <summary>Full snow block - solid, opaque.</summary>
    SnowDirt = 41 | Solid | Opaque,
    /// <summary>Ice - solid, translucent.</summary>
    Ice = 42 | Solid | Translucent,
    /// <summary>Packed ice - solid, translucent.</summary>
    PackedIce = 43 | Solid | Translucent,
    /// <summary>Blue ice - solid, translucent.</summary>
    BlueIce = 44 | Solid | Translucent,
    
    // --- Terracotta (50-59) ---
    /// <summary>Terracotta - solid, opaque.</summary>
    Terracotta = 50 | Solid | Opaque,
    /// <summary>White terracotta - solid, opaque.</summary>
    WhiteTerracotta = 51 | Solid | Opaque,
    /// <summary>Orange terracotta - solid, opaque.</summary>
    OrangeTerracotta = 52 | Solid | Opaque,
    /// <summary>Red terracotta - solid, opaque.</summary>
    RedTerracotta = 53 | Solid | Opaque,
    /// <summary>Brown terracotta - solid, opaque.</summary>
    BrownTerracotta = 54 | Solid | Opaque,
    /// <summary>Yellow terracotta - solid, opaque.</summary>
    YellowTerracotta = 55 | Solid | Opaque,
    
    // --- Ores (100-109) ---
    /// <summary>Coal ore - solid, opaque.</summary>
    CoalOre = 100 | Solid | Opaque,
    /// <summary>Iron ore - solid, opaque.</summary>
    IronOre = 101 | Solid | Opaque,
    /// <summary>Gold ore - solid, opaque.</summary>
    GoldOre = 102 | Solid | Opaque,
    /// <summary>Diamond ore - solid, opaque.</summary>
    DiamondOre = 103 | Solid | Opaque,
    /// <summary>Copper ore - solid, opaque.</summary>
    CopperOre = 104 | Solid | Opaque,
    
    // --- Wood (120-139) ---
    /// <summary>Oak log - solid, opaque.</summary>
    OakLog = 120 | Solid | Opaque,
    /// <summary>Birch log - solid, opaque.</summary>
    BirchLog = 121 | Solid | Opaque,
    /// <summary>Spruce log - solid, opaque.</summary>
    SpruceLog = 122 | Solid | Opaque,
    /// <summary>Jungle log - solid, opaque.</summary>
    JungleLog = 123 | Solid | Opaque,
    
    // --- Leaves (140-149) ---
    /// <summary>Oak leaves - solid (for collision), translucent.</summary>
    OakLeaves = 140 | Solid | Translucent,
    /// <summary>Birch leaves - solid, translucent.</summary>
    BirchLeaves = 141 | Solid | Translucent,
    /// <summary>Spruce leaves - solid, translucent.</summary>
    SpruceLeaves = 142 | Solid | Translucent,
    /// <summary>Jungle leaves - solid, translucent.</summary>
    JungleLeaves = 143 | Solid | Translucent,
    
    // --- Light Sources (200-219) ---
    /// <summary>Torch - non-solid, emissive, light value 14.</summary>
    Torch = 200 | Emissive,
    /// <summary>Wall torch - non-solid, emissive, light value 14.</summary>
    WallTorch = 201 | Emissive,
    /// <summary>Soul torch - non-solid, emissive, light value 10.</summary>
    SoulTorch = 202 | Emissive,
    /// <summary>Glowstone - solid, opaque, emissive, light value 15.</summary>
    Glowstone = 203 | Solid | Opaque | Emissive,
    /// <summary>Sea lantern - solid, translucent, emissive, light value 15.</summary>
    SeaLantern = 204 | Solid | Translucent | Emissive,
    /// <summary>Lantern - non-solid, emissive, light value 15.</summary>
    Lantern = 205 | Emissive,
    /// <summary>Soul lantern - non-solid, emissive, light value 10.</summary>
    SoulLantern = 206 | Emissive,
    /// <summary>Redstone lamp (off) - solid, opaque.</summary>
    RedstoneLamp = 207 | Solid | Opaque,
    /// <summary>Redstone lamp (on) - solid, opaque, emissive, light value 15.</summary>
    RedstoneLampOn = 208 | Solid | Opaque | Emissive,
    /// <summary>End rod - non-solid, emissive, light value 14.</summary>
    EndRod = 209 | Emissive,
    /// <summary>Shroomlight - solid, opaque, emissive, light value 15.</summary>
    Shroomlight = 210 | Solid | Opaque | Emissive,
    /// <summary>Jack o'Lantern - solid, opaque, emissive, light value 15.</summary>
    JackOLantern = 211 | Solid | Opaque | Emissive,
    /// <summary>Campfire - non-solid, emissive, light value 15.</summary>
    Campfire = 212 | Emissive,
    /// <summary>Soul campfire - non-solid, emissive, light value 10.</summary>
    SoulCampfire = 213 | Emissive,
    
    // --- Glass (220-239) ---
    /// <summary>Glass - solid, translucent, fully transparent to light (filter 0).</summary>
    Glass = 220 | Solid | Translucent,
    /// <summary>White stained glass - solid, translucent.</summary>
    WhiteStainedGlass = 221 | Solid | Translucent,
    /// <summary>Orange stained glass - solid, translucent.</summary>
    OrangeStainedGlass = 222 | Solid | Translucent,
    /// <summary>Magenta stained glass - solid, translucent.</summary>
    MagentaStainedGlass = 223 | Solid | Translucent,
    /// <summary>Light blue stained glass - solid, translucent.</summary>
    LightBlueStainedGlass = 224 | Solid | Translucent,
    /// <summary>Yellow stained glass - solid, translucent.</summary>
    YellowStainedGlass = 225 | Solid | Translucent,
    /// <summary>Lime stained glass - solid, translucent.</summary>
    LimeStainedGlass = 226 | Solid | Translucent,
    /// <summary>Pink stained glass - solid, translucent.</summary>
    PinkStainedGlass = 227 | Solid | Translucent,
    /// <summary>Gray stained glass - solid, translucent.</summary>
    GrayStainedGlass = 228 | Solid | Translucent,
    /// <summary>Light gray stained glass - solid, translucent.</summary>
    LightGrayStainedGlass = 229 | Solid | Translucent,
    /// <summary>Cyan stained glass - solid, translucent.</summary>
    CyanStainedGlass = 230 | Solid | Translucent,
    /// <summary>Purple stained glass - solid, translucent.</summary>
    PurpleStainedGlass = 231 | Solid | Translucent,
    /// <summary>Blue stained glass - solid, translucent.</summary>
    BlueStainedGlass = 232 | Solid | Translucent,
    /// <summary>Brown stained glass - solid, translucent.</summary>
    BrownStainedGlass = 233 | Solid | Translucent,
    /// <summary>Green stained glass - solid, translucent.</summary>
    GreenStainedGlass = 234 | Solid | Translucent,
    /// <summary>Red stained glass - solid, translucent.</summary>
    RedStainedGlass = 235 | Solid | Translucent,
    /// <summary>Black stained glass - solid, translucent.</summary>
    BlackStainedGlass = 236 | Solid | Translucent,
    /// <summary>Tinted glass - solid, translucent, blocks all light (filter 15) but see-through.</summary>
    TintedGlass = 237 | Solid | Translucent,
}

/// <summary>
/// Mask constants for extracting BlockId parts.
/// </summary>
public static class BlockIdMasks
{
    /// <summary>Lower 10 bits = unique block ID (0-1023).</summary>
    public const ushort IdMask = 0x03FF;
    /// <summary>Upper 6 bits = property flags.</summary>
    public const ushort FlagsMask = 0xFC00;
}

/// <summary>
/// Extension methods for BlockId flag checking and ID extraction.
/// All methods are simple bitmask operations - no lookup tables required.
/// </summary>
public static class BlockIdExtensions
{
    /// <summary>
    /// Gets the unique block ID (lower 10 bits, range 0-1023).
    /// </summary>
    public static ushort GetId(this BlockId block) 
        => (ushort)((ushort)block & BlockIdMasks.IdMask);

    /// <summary>
    /// Returns true if this block is solid (blocks movement, needs faces rendered).
    /// </summary>
    public static bool IsSolid(this BlockId block) 
        => (block & BlockId.Solid) != 0;

    /// <summary>
    /// Returns true if this block is opaque (blocks light, culls neighbor faces).
    /// </summary>
    public static bool IsOpaque(this BlockId block) 
        => (block & BlockId.Opaque) != 0;

    /// <summary>
    /// Returns true if this block is a liquid (water, lava).
    /// </summary>
    public static bool IsLiquid(this BlockId block) 
        => (block & BlockId.Liquid) != 0;

    /// <summary>
    /// Returns true if this block is translucent (partial transparency).
    /// </summary>
    public static bool IsTranslucent(this BlockId block) 
        => (block & BlockId.Translucent) != 0;

    /// <summary>
    /// Returns true if this block can be replaced by other blocks (air, water, tall grass).
    /// </summary>
    public static bool IsReplaceable(this BlockId block) 
        => (block & BlockId.Replaceable) != 0;

    /// <summary>
    /// Returns true if this block is transparent (air or water - not solid and replaceable).
    /// </summary>
    public static bool IsTransparent(this BlockId block) 
        => !block.IsSolid() || block.IsLiquid();

    /// <summary>
    /// Returns true if this block is water.
    /// </summary>
    public static bool IsWater(this BlockId block) 
        => block.GetId() == BlockId.Water.GetId();

    /// <summary>
    /// Returns true if this block is air.
    /// </summary>
    public static bool IsAir(this BlockId block) 
        => block.GetId() == BlockId.Air.GetId();

    /// <summary>
    /// Returns true if this block is emissive (glows).
    /// </summary>
    public static bool IsEmissive(this BlockId block) 
        => (block & BlockId.Emissive) != 0;
}
