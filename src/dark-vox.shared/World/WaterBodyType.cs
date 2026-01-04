namespace DarkVox.Shared.World;

/// <summary>
/// Water body type for a terrain column.
/// Simplified to Ocean vs None - water is determined by biome + terrain height.
/// 
/// ARCHITECTURE: Water is a DOWNSTREAM effect of biome selection:
/// 1. Biome selected based on climate (continentalness < threshold = ocean biome)
/// 2. Ocean biomes have low BaseHeight, producing terrain below water level
/// 3. Air blocks below water level in ocean biomes become water
/// 
/// Lakes are handled at the block level using aquifer noise, not as a separate water body type.
/// </summary>
public enum WaterBodyType
{
    /// <summary>No water at this column.</summary>
    None = 0,
    
    /// <summary>
    /// Ocean water. Biome is Ocean/DeepOcean and terrain is below sea level.
    /// Water level is always VoxelHelper.WaterLevel (global sea level).
    /// </summary>
    Ocean = 1,

    /// <summary>
    /// Inland water body (aquifer-driven). Water level can vary per-column.
    /// This is still deterministic and coordinate-driven; it is NOT runtime simulation.
    /// </summary>
    Inland = 2
}

/// <summary>
/// Water body information for a terrain column.
/// </summary>
public readonly struct WaterBodyInfo
{
    /// <summary>Type of water body.</summary>
    public WaterBodyType Type { get; init; }
    
    /// <summary>Water surface Y level (only valid when Type != None).</summary>
    public float WaterLevel { get; init; }
    
    /// <summary>Whether this column has water.</summary>
    public bool HasWater => Type != WaterBodyType.None;
    
    /// <summary>Whether this column is ocean water.</summary>
    public bool IsOcean => Type == WaterBodyType.Ocean;

    /// <summary>Whether this column is inland water.</summary>
    public bool IsInland => Type == WaterBodyType.Inland;
    
    /// <summary>No water body.</summary>
    public static WaterBodyInfo None => new() { Type = WaterBodyType.None, WaterLevel = -1f };
    
    /// <summary>Ocean water at global sea level.</summary>
    public static WaterBodyInfo Ocean() => new() 
    { 
        Type = WaterBodyType.Ocean, 
        WaterLevel = VoxelHelper.WaterLevel 
    };

    /// <summary>Inland water with a local water level.</summary>
    public static WaterBodyInfo Inland(float waterLevel) => new()
    {
        Type = WaterBodyType.Inland,
        WaterLevel = waterLevel
    };
}
