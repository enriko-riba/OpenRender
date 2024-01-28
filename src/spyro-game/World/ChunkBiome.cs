namespace SpyroGame.World;

/// <summary>
/// Represents a biome spanning a certain world area.
/// </summary>
/// <param name="BaseHeight"></param>
public record struct ChunkBiome(LandType LandType, float BaseHeight, Climate Climate) { }


public enum LandType
{
    DeepOcean,
    Ocean,
    ShallowOcean,
    Beach,
    LowLand,
    Land,
    Hills,
    Mountain,
    HighMountain,
    Peaks,
}

public enum Climate
{
    /// <summary>
    /// Very humid, warm.
    /// </summary>
    Tropical,

    /// <summary>
    /// Dry, warm.
    /// </summary>
    Arid,

    /// <summary>
    /// Humid, temperate.
    /// </summary>
    Temperate,

    /// <summary>
    /// 
    /// </summary>
    Continental,

    /// <summary>
    /// Snow, frozen.
    /// </summary>
    Polar,
}
