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
