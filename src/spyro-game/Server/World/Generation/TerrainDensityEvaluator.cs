using SpyroGame.World.Registry;
using SpyroGame.Server.World;
using SpyroGame.World;

namespace SpyroGame.Server.World.Generation;

/// <summary>
/// Evaluates terrain height and density using biome properties.
/// Implements Stage 2 of the Minecraft-style terrain pipeline where biome properties
/// (BaseHeight, HeightVariation, PeaksInfluence, ErosionSensitivity) DRIVE the terrain shape.
/// 
/// This replaces the old height-spline-first approach where biomes were selected AFTER
/// terrain was already calculated.
/// 
/// Key principle: Biomes control terrain shape, not vice versa. Check the TERRAIN_ARCHITECTURE.md document for details.
/// </summary>
/// <remarks>
/// Initializes a new instance of <see cref="TerrainDensityEvaluator"/>.
/// </remarks>
/// <param name="config">Terrain configuration.</param>
internal sealed class TerrainDensityEvaluator(TerrainConfig config)
{
    private readonly TerrainConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private TerrainConfig.TerrainGenerationParams _params = config.GetGenerationParams();

    // Biomes should define local shape (flat/jagged) more than absolute elevation.
    // Macro elevation comes from continentalness spline ("continents") + erosion/weirdness shaping.
    private const float LandBiomeHeightInfluence = 0.55f;
    private const float BeachBiomeHeightInfluence = 0.55f;
    private const float OceanBiomeHeightInfluence = 0.85f;

    /// <summary>
    /// Updates the configuration. Call when terrain config changes.
    /// </summary>
    public void UpdateConfig(TerrainConfig config) => _params = config.GetGenerationParams();

    /// <summary>
    /// Calculate terrain height for a column using biome properties.
    /// This is the core of Stage 2 - biome properties drive terrain shape.
    /// 
    /// Height formula:
    ///   height = biome.BaseHeight 
    ///          + biome.HeightVariation * PV * biome.PeaksInfluence
    ///          * (1 - erosion * biome.ErosionSensitivity)
    ///          + WaterLevel
    /// </summary>
    /// <param name="biome">The biome definition for this column.</param>
    /// <param name="peaksValleys">Raw PV noise value [0, 1] from climate cache.</param>
    /// <param name="erosion01">Normalized erosion [0, 1] from climate cache.</param>
    /// <param name="continentalness01">Normalized continentalness [0, 1] for coastal transitions.</param>
    /// <returns>Terrain height in world Y coordinates.</returns>
    public float CalculateBiomeHeight(
        BiomeDefinition biome,
        float peaksValleys,
        float erosion01,
        float continentalness01)
    {
        // Biome's base height is relative to sea level
        var baseHeight = biome.BaseHeight + VoxelHelper.WaterLevel;
        
        // Calculate terrain variation using biome properties
        // PV noise modulated by biome's peaks influence
        var pvContribution = (peaksValleys - 0.5f) * 2f; // Center around 0 [-1, 1]
        var variation = biome.HeightVariation * pvContribution * biome.PeaksInfluence;
        
        // Erosion smoothing - high erosion reduces terrain variation
        var smoothingFactor = 1f - erosion01 * biome.ErosionSensitivity;
        variation *= smoothingFactor;
        
        var height = baseHeight + variation;
        
        // Apply coastal transition for land biomes near ocean threshold
        // This creates natural coastlines by gradually lowering terrain near water
        if (continentalness01 >= _params.OceanThreshold)
        {
            var coastDistance = (continentalness01 - _params.OceanThreshold) / (1f - _params.OceanThreshold);
            
            // Very close to coast - ensure minimum height above water
            if (coastDistance < _config.TerrainShaping.CoastalSmoothingDistance)
            {
                var coastFactor = coastDistance / _config.TerrainShaping.CoastalSmoothingDistance;
                var minCoastHeight = VoxelHelper.WaterLevel + _config.TerrainShaping.MinLandHeightOffset;
                height = Lerp(minCoastHeight, height, coastFactor * coastFactor);
            }
        }
        // Ocean biomes: Keep natural ocean floor depth, no smoothing upward
        
        return height;
    }

    /// <summary>
    /// Blend macro elevation (from height spline / continentalness) with biome-defined height.
    /// This allows e.g. Plains to exist at multiple absolute altitudes while remaining flat.
    /// </summary>
    public static float BlendMacroAndBiomeHeight(float macroHeight, BiomeDefinition biome, float biomeHeight)
    {
        var t = LandBiomeHeightInfluence;
        if (biome.Id is ((int)BiomeId.Ocean) or ((int)BiomeId.DeepOcean))
        {
            t = OceanBiomeHeightInfluence;
        }
        else if (biome.Id == (int)BiomeId.Beach)
        {
            t = BeachBiomeHeightInfluence;
        }

        return macroHeight + (biomeHeight - macroHeight) * t;
    }

    /// <summary>
    /// Calculate terrain height using legacy spline-based approach.
    /// Used as fallback when biome is null or for compatibility.
    /// </summary>
    /// <param name="continentalness01">Normalized continentalness [0, 1].</param>
    /// <param name="peaksValleys">Raw PV noise value [0, 1].</param>
    /// <param name="erosion01">Normalized erosion [0, 1].</param>
    /// <param name="heightSpline">Pre-baked height spline array.</param>
    /// <returns>Terrain height in world Y coordinates.</returns>
    public float CalculateSplineHeight(
        float continentalness01,
        float peaksValleys,
        float erosion01,
        ReadOnlySpan<float> heightSpline)
    {
        // Sample height spline using continentalness (macro continents)
        var baseHeight = SampleSpline(heightSpline, continentalness01) + VoxelHelper.WaterLevel;

        // Ocean: keep the spline shape (depths/shore handled by later smoothing).
        if (continentalness01 < _params.OceanThreshold)
        {
            return baseHeight;
        }

        // Land: add macro PV/mountain shaping (Minecraft-style: global fields influence absolute elevation).
        var shaping = _config.TerrainShaping;

        // Treat low erosion as "rough" terrain; use it as a proxy for terrain type.
        var terrainType01 = 1f - Math.Clamp(erosion01, 0f, 1f);

        var pvSigned = (peaksValleys - 0.5f) * 2f; // [-1, 1]

        var peakAmp = shaping.PeakAmplitudeDramatic;
        if (terrainType01 < shaping.FlatPlainsThreshold)
        {
            peakAmp = shaping.PeakAmplitudePlains;
        }
        else if (terrainType01 < shaping.RollingHillsThreshold)
        {
            peakAmp = shaping.PeakAmplitudeHills;
        }

        // Erosion reduces PV influence.
        var erosionAtten = 1f - Math.Clamp(erosion01 * erosion01 * shaping.ErosionSmoothingMax, 0f, 1f);
        baseHeight += pvSigned * peakAmp * erosionAtten;

        // Mountain zones: lift the whole column + add extra alpine peaks.
        var mountainness = Smoothstep(_params.MountainThreshold, shaping.MountainFullThreshold, continentalness01);
        baseHeight += mountainness * shaping.MountainHeightBoost;

        var alpineTerrain = Smoothstep(shaping.AlpinePeakTerrainThreshold, 1f, terrainType01);
        var pvPeaksOnly = MathF.Max(0f, pvSigned);
        baseHeight += pvPeaksOnly * shaping.AlpinePeakAmplitude * mountainness * alpineTerrain * erosionAtten;

        return baseHeight;
    }

    /// <summary>
    /// Calculate 3D terrain density at a voxel position.
    /// Density > 0 = solid, Density < 0 = air.
    /// 
    /// The -y term is the fundamental trick: it creates a horizontal "surface" 
    /// where density transitions from positive (solid) to negative (air).
    /// </summary>
    /// <param name="surfaceHeight">2D terrain height at this column.</param>
    /// <param name="y">Y coordinate of the voxel.</param>
    /// <param name="overhangNoise">3D overhang noise at this position.</param>
    /// <param name="continentalness01">Normalized continentalness for overhang masking.</param>
    /// <param name="factor3D">3D factor from weirdness/erosion for overhang strength.</param>
    /// <returns>Density value (positive = solid, negative = air).</returns>
    public float CalculateDensity(
        float surfaceHeight,
        float y,
        float overhangNoise,
        float continentalness01,
        float factor3D)
    {
        // Base density: positive below surface, negative above
        var density = surfaceHeight - y;
        
        var shaping = _config.TerrainShaping;
        
        // Add 3D overhang effects for dramatic terrain
        if (continentalness01 > shaping.OverhangStartThreshold &&
            y > surfaceHeight - _params.OverhangDepthRange &&
            y < surfaceHeight + _params.OverhangHeightRange)
        {
            var mountainness = Smoothstep(shaping.OverhangStartThreshold, shaping.OverhangFullThreshold, continentalness01);
            var heightFactor = 1f - MathF.Abs((y - surfaceHeight) / _params.OverhangFalloffRange);
            heightFactor = Math.Clamp(heightFactor, 0f, 1f);
            
            density += overhangNoise * _params.OverhangAmplitude * shaping.OverhangMultiplier * mountainness * heightFactor * factor3D;
        }
        
        return density;
    }

    /// <summary>
    /// Calculate the 3D factor from weirdness and erosion.
    /// High |weirdness| + low erosion = dramatic 3D features.
    /// </summary>
    public float Calculate3DFactor(float absWeirdness, float erosion01)
    {
        var shaping = _config.TerrainShaping;
        
        var weirdnessFactor = Smoothstep(shaping.Weirdness3DThresholdLow, shaping.Weirdness3DThresholdHigh, absWeirdness);
        var erosionFactor = 1f - Smoothstep(0f, shaping.Erosion3DThreshold, erosion01);
        var rawFactor = weirdnessFactor * erosionFactor;
        
        return Lerp(shaping.Min3DFactor, shaping.Max3DFactor, rawFactor);
    }

    /// <summary>
    /// Sample a pre-baked spline array using linear interpolation.
    /// </summary>
    private static float SampleSpline(ReadOnlySpan<float> spline, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var resolution = spline.Length;
        var scaled = t * (resolution - 1);
        var i = (int)MathF.Floor(scaled);
        var frac = scaled - i;
        var a = spline[i];
        var b = spline[Math.Min(i + 1, resolution - 1)];
        return a + (b - a) * frac;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (MathF.Abs(edge1 - edge0) < float.Epsilon)
        {
            return x >= edge1 ? 1f : 0f;
        }
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
