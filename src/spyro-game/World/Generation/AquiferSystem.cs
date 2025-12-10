namespace SpyroGame.World.Generation;

/// <summary>
/// Simplified aquifer system for water determination.
/// 
/// ARCHITECTURE: Water is a downstream effect of biome selection:
/// - Ocean biomes (selected when continentalness < threshold) have low BaseHeight
/// - Terrain below water level in ocean biomes becomes water
/// - No separate "lake" detection - lakes can be added later via surface features
/// 
/// This system simply answers: "Should this air block be water?"
/// based on the biome already being Ocean/DeepOcean.
/// </summary>
internal sealed class AquiferSystem
{
    private readonly TerrainConfig _config;
    
    /// <summary>Gets whether the aquifer is ready for queries.</summary>
    public bool IsValid { get; private set; }

    /// <summary>Initializes the aquifer system.</summary>
    public AquiferSystem(TerrainConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Initialize from climate cache (for compatibility, but we don't need aquifer noise anymore).
    /// </summary>
    public void InitializeFromClimateCache(ChunkClimateCache climateCache)
    {
        IsValid = climateCache.IsValid;
    }

    /// <summary>
    /// Get water body info for a column.
    /// Simple logic: Ocean biomes with terrain below water level = ocean water.
    /// </summary>
    public WaterBodyInfo GetWaterBodyInfo(
        int columnIndex, 
        BiomeId biomeId, 
        float terrainHeight,
        float continentalness01)
    {
        // Ocean biomes get water if terrain is below sea level
        if (biomeId is BiomeId.Ocean or BiomeId.DeepOcean)
        {
            if (terrainHeight < VoxelHelper.WaterLevel)
            {
                return WaterBodyInfo.Ocean();
            }
        }
        
        return WaterBodyInfo.None;
    }

    /// <summary>Check if a specific voxel should be water.</summary>
    public static bool ShouldBeWater(WaterBodyInfo waterBody, int y)
    {
        return waterBody.HasWater && y <= waterBody.WaterLevel;
    }

    /// <summary>Invalidate the cache.</summary>
    public void Invalidate()
    {
        IsValid = false;
    }
}
