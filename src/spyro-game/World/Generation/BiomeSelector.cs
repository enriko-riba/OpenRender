namespace SpyroGame.World;

/// <summary>
/// Allocation-free biome selector using Minecraft-style multi-parameter climate matching.
/// Phase 3 of the terrain pipeline: Biome selection happens AFTER terrain height is known,
/// using cached climate values from ChunkClimateCache.
/// 
/// Performance: Zero allocations during selection - all arrays pre-allocated.
/// </summary>
internal sealed class BiomeSelector
{
    // Pre-allocated arrays for biome distance calculations (reused per call)
    private readonly float[] _biomeDistances;
    private readonly int _biomeCount;
    
    // Reference to biome definitions (snapshot as array for iteration performance)
    private readonly BiomeDefinition[] _biomes;
    
    // Terrain thresholds from config
    private float _oceanThreshold;
    private float _deepOceanThreshold;
    private float _alpineElevation;
    private float _shorelineRange;
    
    /// <summary>
    /// Creates a new biome selector with pre-allocated arrays.
    /// </summary>
    /// <param name="biomes">Collection of biome definitions to select from.</param>
    public BiomeSelector(IReadOnlyList<BiomeDefinition> biomes)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        _biomes = [.. biomes];  // Copy to array for iteration performance
        _biomeCount = _biomes.Length;
        _biomeDistances = new float[_biomeCount];
    }
    
    /// <summary>
    /// Update terrain thresholds from config.
    /// Call this when config changes.
    /// </summary>
    public void UpdateConfig(TerrainConfig config)
    {
        _oceanThreshold = config.OceanThreshold;
        _deepOceanThreshold = config.DeepOceanThreshold;
        _alpineElevation = config.AlpineElevation;
        _shorelineRange = config.ShorelineRange;
    }
    
    /// <summary>
    /// Select the best-matching biome for the given climate values and terrain height.
    /// Zero allocations - uses pre-allocated arrays for distance calculations.
    /// </summary>
    /// <param name="continentalness01">Continentalness [0,1] where 0=deep ocean, 1=far inland.</param>
    /// <param name="temperature01">Temperature [0,1] where 0=frozen, 1=hot.</param>
    /// <param name="humidity01">Humidity [0,1] where 0=arid, 1=wet.</param>
    /// <param name="erosion01">Erosion [0,1] where low=dramatic terrain, high=flat.</param>
    /// <param name="peaksValleys01">Peaks/Valleys [0,1] from ridge noise.</param>
    /// <param name="actualHeight">Actual terrain height at this column (Y coordinate).</param>
    /// <returns>Selected BiomeId.</returns>
    public BiomeId SelectPrimary(
        float continentalness01,
        float temperature01,
        float humidity01,
        float erosion01,
        float peaksValleys01,
        float actualHeight)
    {
        // Fast path: Use terrain height to determine ocean vs land
        // This is the definitive check - terrain below water = ocean biome
        var isUnderwater = actualHeight < VoxelHelper.WaterLevel;
        var altitudeAboveWater = actualHeight - VoxelHelper.WaterLevel;
        
        // ============ OCEAN BIOMES (based on actual terrain height) ============
        if (isUnderwater)
        {
            // Use continentalness to distinguish deep ocean from regular ocean
            return continentalness01 < _deepOceanThreshold ? BiomeId.DeepOcean : BiomeId.Ocean;
        }
        
        // ============ BEACH BIOME ============
        // Beach is determined by HEIGHT - terrain at or very close to water level
        if (altitudeAboveWater <= _shorelineRange)
        {
            return BiomeId.Beach;
        }
        
        // ============ ALPINE BIOME ============
        // Use actual terrain height for altitude-based alpine
        if (altitudeAboveWater > _alpineElevation || temperature01 < 0.12f)
        {
            return BiomeId.Alpine;
        }
        
        // ============ MOUNTAIN/HIGHLANDS ============
        if (peaksValleys01 > 0.7f && erosion01 < 0.4f)
        {
            return temperature01 < 0.35f ? BiomeId.Alpine : BiomeId.Highlands;
        }
        
        // ============ CLIMATE-BASED LAND BIOMES ============
        // Hot biomes
        if (temperature01 > 0.65f)
        {
            if (humidity01 < 0.30f) return BiomeId.Desert;
            if (humidity01 < 0.55f) return BiomeId.Savanna;
            return BiomeId.Rainforest;
        }
        
        // Cold biomes
        if (temperature01 < 0.35f)
        {
            if (humidity01 > 0.50f) return BiomeId.Taiga;
            return BiomeId.Tundra;
        }
        
        // Temperate biomes
        if (humidity01 > 0.55f)
        {
            if (continentalness01 < 0.50f && erosion01 > 0.6f)
                return BiomeId.Swamp;
            return BiomeId.Taiga;
        }
        
        if (peaksValleys01 > 0.55f || erosion01 < 0.45f)
            return BiomeId.Highlands;
        
        return BiomeId.Plains;
    }
    
    /// <summary>
    /// Select biome with blend weights for smooth transitions.
    /// Zero allocations - uses pre-allocated arrays.
    /// Only call when blending is actually needed (biome transitions).
    /// </summary>
    /// <param name="continentalness01">Continentalness [0,1].</param>
    /// <param name="temperature01">Temperature [0,1].</param>
    /// <param name="humidity01">Humidity [0,1].</param>
    /// <param name="erosion01">Erosion [0,1].</param>
    /// <param name="peaksValleys01">Peaks/Valleys [0,1].</param>
    /// <param name="actualHeight">Actual terrain height.</param>
    /// <param name="primary">Output: Primary biome ID.</param>
    /// <param name="secondary">Output: Secondary biome ID for blending.</param>
    /// <param name="blendWeight">Output: Blend weight [0,1]. 0=pure primary, 0.5=equal blend.</param>
    public void SelectWithBlend(
        float continentalness01,
        float temperature01,
        float humidity01,
        float erosion01,
        float peaksValleys01,
        float actualHeight,
        out BiomeId primary,
        out BiomeId secondary,
        out float blendWeight)
    {
        // For terrain-based biomes (ocean, beach, alpine), no blending
        var isUnderwater = actualHeight < VoxelHelper.WaterLevel;
        var altitudeAboveWater = actualHeight - VoxelHelper.WaterLevel;
        
        if (isUnderwater)
        {
            primary = continentalness01 < _deepOceanThreshold ? BiomeId.DeepOcean : BiomeId.Ocean;
            secondary = primary;
            blendWeight = 0f;
            return;
        }
        
        if (altitudeAboveWater <= _shorelineRange)
        {
            primary = BiomeId.Beach;
            secondary = BiomeId.Beach;
            blendWeight = 0f;
            return;
        }
        
        if (altitudeAboveWater > _alpineElevation)
        {
            primary = BiomeId.Alpine;
            secondary = BiomeId.Alpine;
            blendWeight = 0f;
            return;
        }
        
        // For land biomes, use temperature+humidity distance matching
        // Calculate distances for all land biomes
        var best1Idx = -1;
        var best2Idx = -1;
        var best1Dist = float.MaxValue;
        var best2Dist = float.MaxValue;
        
        for (var i = 0; i < _biomeCount; i++)
        {
            var biome = _biomes[i];
            
            // Skip ocean/beach/alpine - handled above
            if (biome.Id == (int)BiomeId.Ocean || biome.Id == (int)BiomeId.DeepOcean || 
                biome.Id == (int)BiomeId.Beach || biome.Id == (int)BiomeId.Alpine)
            {
                _biomeDistances[i] = float.MaxValue;
                continue;
            }
            
            // Calculate weighted climate distance using Temperature and Humidity
            var tIn = biome.Temperature.Contains(temperature01);
            var hIn = biome.Humidity.Contains(humidity01);
            var dist = tIn && hIn ? 0f 
                : MathF.Abs(biome.Temperature.Center - temperature01) + MathF.Abs(biome.Humidity.Center - humidity01);
            
            _biomeDistances[i] = dist;
            
            // Track top 2
            if (dist < best1Dist)
            {
                best2Dist = best1Dist;
                best2Idx = best1Idx;
                best1Dist = dist;
                best1Idx = i;
            }
            else if (dist < best2Dist)
            {
                best2Dist = dist;
                best2Idx = i;
            }
        }
        
        if (best1Idx < 0)
        {
            primary = BiomeId.Plains;
            secondary = BiomeId.Plains;
            blendWeight = 0f;
            return;
        }
        
        primary = (BiomeId)_biomes[best1Idx].Id;
        secondary = best2Idx >= 0 ? (BiomeId)_biomes[best2Idx].Id : primary;
        
        // Blend weight based on distance ratio
        blendWeight = best1Dist / (best1Dist + best2Dist + 0.001f);
    }
}
