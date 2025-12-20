using SpyroGame.World.Registry;
using SpyroGame.Server.World;
using SpyroGame.World;

namespace SpyroGame.Server.World.Generation;

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
    private readonly TerrainConfig _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <summary>
    /// Get water body info for a column.
    /// Deterministic, coordinate-driven water placement.
    /// 
    /// Rules:
    /// - Ocean/DeepOcean biomes: use global sea level (VoxelHelper.WaterLevel)
    /// - Land columns: use aquifer noise to decide if/where water exists
    /// </summary>
    public WaterBodyInfo GetWaterBodyInfo(
        BiomeId biomeId,
        float terrainHeight,
        float continentalness01,
        float aquiferNoise01)
    {
        // Ocean biomes get water if terrain is below sea level.
        if (biomeId is BiomeId.Ocean or BiomeId.DeepOcean)
        {
            if (terrainHeight < VoxelHelper.WaterLevel)
            {
                return WaterBodyInfo.Ocean();
            }

            return WaterBodyInfo.None;
        }

        // Prevent inland water bodies from appearing in the ocean zone.
        if (continentalness01 < _config.OceanThreshold)
        {
            return WaterBodyInfo.None;
        }

        // Aquifer placement: only a subset of land columns contain inland water.
        if (aquiferNoise01 < _config.AquiferThreshold)
        {
            return WaterBodyInfo.None;
        }

        // Local water level: centered around global sea level, with configurable variation.
        // aquiferNoise01 is [0,1] so (n-0.5)*2 is [-1,1].
        var signedNoise = (aquiferNoise01 - 0.5f) * 2f;
        var waterLevel = VoxelHelper.WaterLevel + signedNoise * _config.AquiferVariation;

        // Clamp to prevent unrealistic high-altitude inland water.
        var maxAllowed = VoxelHelper.WaterLevel + _config.AquiferMaxAboveSeaLevel;
        if (waterLevel > maxAllowed)
        {
            waterLevel = maxAllowed;
        }

        // Minimum depth guard: avoid shallow puddles.
        var depth = waterLevel - terrainHeight;
        if (depth < _config.AquiferMinDepth)
        {
            return WaterBodyInfo.None;
        }

        return WaterBodyInfo.Inland(waterLevel);
    }

    /// <summary>Check if a specific voxel should be water.</summary>
    public static bool ShouldBeWater(WaterBodyInfo waterBody, int y)
        => waterBody.HasWater && y <= waterBody.WaterLevel;
}

