namespace SpyroGame.World;

/// <summary>
/// Block type identifiers for the Minecraft-style terrain system.
/// Each value represents an actual material/block type, not a geological layer.
/// The shader uses BlockId to look up textures via a palette.
/// </summary>
/// <remarks>
/// This replaces the old BlockDescriptor enum which represented geological layers.
/// BlockId represents actual materials (Grass, Stone, Sand, etc.).
/// Maximum 256 block types (1 byte storage per voxel).
/// </remarks>
public enum BlockId : byte
{
    // === SPECIAL ===
    Air = 0,
    Water = 1,
    
    // === STONE VARIANTS ===
    Stone = 2,
    Bedrock = 3,
    Cobblestone = 4,
    MossyCobblestone = 5,
    
    // === DIRT/GRASS ===
    Dirt = 10,
    Grass = 11,          // Grass block (green top)
    GrassSnowy = 12,     // Grass with snow on top
    Podzol = 13,         // Taiga forest floor
    Mycelium = 14,       // Mushroom biome
    CoarseDirt = 15,
    
    // === SAND ===
    Sand = 20,
    RedSand = 21,
    Sandstone = 22,
    RedSandstone = 23,
    
    // === GRAVEL/CLAY ===
    Gravel = 30,
    Clay = 31,
    
    // === SNOW/ICE ===
    Snow = 40,           // Snow layer
    SnowBlock = 41,      // Full snow block
    Ice = 42,
    PackedIce = 43,
    BlueIce = 44,
    
    // === TERRACOTTA ===
    Terracotta = 50,
    WhiteTerracotta = 51,
    OrangeTerracotta = 52,
    RedTerracotta = 53,
    BrownTerracotta = 54,
    YellowTerracotta = 55,
    
    // === ORES (Future) ===
    CoalOre = 100,
    IronOre = 101,
    GoldOre = 102,
    DiamondOre = 103,
    CopperOre = 104,
    
    // === WOOD (Future) ===
    OakLog = 120,
    BirchLog = 121,
    SpruceLog = 122,
    JungleLog = 123,
    
    // === LEAVES (Future) ===
    OakLeaves = 140,
    BirchLeaves = 141,
    SpruceLeaves = 142,
    JungleLeaves = 143,
}

/// <summary>
/// Extension methods for BlockId.
/// </summary>
public static class BlockIdExtensions
{
    /// <summary>
    /// Returns true if this block is transparent (Air or Water).
    /// </summary>
    public static bool IsTransparent(this BlockId block)
    {
        return block == BlockId.Air || block == BlockId.Water;
    }
    
    /// <summary>
    /// Returns true if this block is a solid that blocks movement.
    /// </summary>
    public static bool IsSolid(this BlockId block)
    {
        return block != BlockId.Air && block != BlockId.Water;
    }
    
    /// <summary>
    /// Returns true if this block is water.
    /// </summary>
    public static bool IsWater(this BlockId block)
    {
        return block == BlockId.Water;
    }
    
    /// <summary>
    /// Returns true if this block is air.
    /// </summary>
    public static bool IsAir(this BlockId block)
    {
        return block == BlockId.Air;
    }
}
