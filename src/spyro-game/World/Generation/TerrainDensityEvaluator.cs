namespace SpyroGame.World.Generation;

/// <summary>
/// Evaluates terrain height and density using biome properties.
/// Implements Stage 2 of the Minecraft-style terrain pipeline where biome properties
/// (BaseHeight, HeightVariation, PeaksInfluence, ErosionSensitivity) DRIVE the terrain shape.
/// 
/// This replaces the old height-spline-first approach where biomes were selected AFTER
/// terrain was already calculated.
/// 
/// Key principle: Biomes control terrain shape, not vice versa.
/// </summary>
/// <remarks>
/// Initializes a new instance of <see cref="TerrainDensityEvaluator"/>.
/// </remarks>
/// <param name="config">Terrain configuration.</param>
internal sealed class TerrainDensityEvaluator(TerrainConfig config)
{
    private readonly TerrainConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private TerrainConfig.TerrainGenerationParams _params = config.GetGenerationParams();

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
        else
        {
            // Smooth ocean floor up to coast to avoid underwater cliffs
            var distToCoast = _params.OceanThreshold - continentalness01;
            
            // If within 0.15 continentalness of coast (e.g. 0.25 to 0.40)
            if (distToCoast < _config.TerrainShaping.UnderwaterCoastalSmoothingDistance)
            {
                var coastFactor = distToCoast / _config.TerrainShaping.UnderwaterCoastalSmoothingDistance; // 0 at coast, 1 at deep
                // Blend towards just below water level at the coast
                var maxOceanHeight = VoxelHelper.WaterLevel - _config.TerrainShaping.MaxOceanHeightOffset;
                // Use quadratic ease-out for smoother transition
                height = Lerp(maxOceanHeight, height, coastFactor * coastFactor);
            }
        }
        
        return height;
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
        // Sample height spline using continentalness
        var baseHeight = SampleSpline(heightSpline, continentalness01) + VoxelHelper.WaterLevel;
        
        // Add basic PV variation for land areas
        if (continentalness01 >= _params.OceanThreshold)
        {
            var roughness = 1f - erosion01;
            var pvContribution = (peaksValleys - 0.5f) * 20f * roughness;
            baseHeight += pvContribution;
        }
        
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
