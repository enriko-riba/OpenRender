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
/// <remarks>Initializes the aquifer system.</remarks>
internal sealed class AquiferSystem(TerrainConfig config)
{    
    /// <summary>
    /// Get water body info for a column.
    /// Simple logic: Ocean biomes with terrain below water level = ocean water.
    /// </summary>
    public static WaterBodyInfo GetWaterBodyInfo(
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
    public static bool ShouldBeWater(WaterBodyInfo waterBody, int y) => waterBody.HasWater && y <= waterBody.WaterLevel;
}

