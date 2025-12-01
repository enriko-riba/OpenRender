namespace SpyroGame.World;

/// <summary>
/// Biome identifiers. These map to BiomeDefinition entries in TerrainConfig.
/// </summary>
public enum BiomeId : byte
{
    Ocean = 0,
    Beach = 1,
    Plains = 2,
    Savanna = 3,
    Desert = 4,
    Rainforest = 5,
    Taiga = 6,
    Tundra = 7,
    Highlands = 8,
    Alpine = 9,
    DeepOcean = 10,
    River = 11,
    Swamp = 12,
    
    // Special marker
    Unknown = 255
}

/// <summary>
/// Cave biome identifiers for 3D underground biome system.
/// Minecraft 1.18+ uses a 4×4×24 grid (16-block Y cells from Y=-64 to Y=320).
/// Our chunk height is 384, so we use a 4×4×24 grid (16-block Y cells).
/// </summary>
public enum CaveBiomeId : byte
{
    /// <summary>
    /// Standard underground caves with stone, dirt, and gravel.
    /// Spawns near surface and in most underground areas.
    /// </summary>
    StandardCave = 0,
    
    /// <summary>
    /// Lush caves with moss, glow berries, and clay pools.
    /// Found in humid areas with azalea trees above.
    /// </summary>
    LushCave = 1,
    
    /// <summary>
    /// Dripstone caves with pointed stalactites and stalagmites.
    /// Common in dry regions underground.
    /// </summary>
    DripstoneCave = 2,
    
    /// <summary>
    /// Deep dark biome at very low Y levels.
    /// Features sculk and low light levels.
    /// </summary>
    DeepDark = 3,
    
    /// <summary>
    /// Frozen caves in cold biomes.
    /// Contains ice formations and packed ice.
    /// </summary>
    FrozenCave = 4,
    
    /// <summary>
    /// Ocean caves that connect to underwater areas.
    /// May be flooded with water.
    /// </summary>
    OceanCave = 5,
    
    /// <summary>
    /// Fallback for areas without specific cave features.
    /// </summary>
    None = 255
}
