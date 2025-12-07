namespace SpyroGame.World;

/// <summary>
/// Defines the type of water body at a terrain column.
/// This is the SINGLE SOURCE OF TRUTH for water classification.
/// All water placement, biome selection, and block type decisions must use this enum.
/// </summary>
public enum WaterBodyType
{
    /// <summary>
    /// No water body at this location. Normal land terrain.
    /// </summary>
    None = 0,
    
    /// <summary>
    /// Ocean water. Determined by continentalness below OceanThreshold.
    /// Water level is always VoxelHelper.WaterLevel (global sea level).
    /// Biome must be Ocean or DeepOcean.
    /// </summary>
    Ocean = 1,
    
    /// <summary>
    /// Lake water. Determined by lake noise exceeding threshold on land.
    /// Water level is terrain-relative (baseHeight + offset).
    /// Biome must be Lake.
    /// </summary>
    Lake = 2,
    
    /// <summary>
    /// River water (future). Determined by river network analysis.
    /// Water level follows river channel.
    /// Biome must be River.
    /// </summary>
    River = 3
}

/// <summary>
/// Contains water body information for a single terrain column.
/// Computed once per column and used by all systems.
/// </summary>
public readonly struct WaterBodyInfo
{
    /// <summary>
    /// Type of water body at this column.
    /// </summary>
    public WaterBodyType Type { get; init; }
    
    /// <summary>
    /// Water surface Y level for this column.
    /// Only valid when Type != None.
    /// - Ocean: Always VoxelHelper.WaterLevel (35)
    /// - Lake: Terrain-relative (baseHeight + offset)
    /// - River: Channel-dependent
    /// </summary>
    public float WaterLevel { get; init; }
    
    /// <summary>
    /// Whether this column has any water.
    /// </summary>
    public bool HasWater => Type != WaterBodyType.None;
    
    /// <summary>
    /// Whether this column is ocean water.
    /// </summary>
    public bool IsOcean => Type == WaterBodyType.Ocean;
    
    /// <summary>
    /// Whether this column is lake water.
    /// </summary>
    public bool IsLake => Type == WaterBodyType.Lake;
    
    /// <summary>
    /// No water body - default value.
    /// </summary>
    public static WaterBodyInfo None => new() { Type = WaterBodyType.None, WaterLevel = -1f };
    
    /// <summary>
    /// Creates ocean water info with global sea level.
    /// </summary>
    public static WaterBodyInfo Ocean() => new() 
    { 
        Type = WaterBodyType.Ocean, 
        WaterLevel = VoxelHelper.WaterLevel 
    };
    
    /// <summary>
    /// Creates lake water info with terrain-relative level.
    /// </summary>
    /// <param name="waterLevel">The Y coordinate of the lake surface.</param>
    public static WaterBodyInfo Lake(float waterLevel) => new() 
    { 
        Type = WaterBodyType.Lake, 
        WaterLevel = waterLevel 
    };
}
