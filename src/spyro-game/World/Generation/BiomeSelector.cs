namespace SpyroGame.World;

/// <summary>
/// Config-driven biome selector using BiomeDefinition climate ranges.
/// Phase 3 of the terrain pipeline: Biome selection happens AFTER terrain height is known,
/// using cached climate values from ChunkClimateCache.
/// 
/// All biome selection is driven by BiomeDefinition properties:
/// - AllowedTerrain: OceanOnly, LandOnly, CoastOnly, MountainOnly, Any
/// - Temperature/Humidity: Climate ranges for matching
/// - MinElevation/MaxElevation: Height constraints
/// - Priority: Higher priority biomes checked first
/// 
/// Performance: Zero allocations during selection - all arrays pre-allocated.
/// </summary>
internal sealed class BiomeSelector
{
    // Pre-allocated arrays for biome distance calculations (reused per call)
    private readonly float[] _biomeDistances;
    private readonly int _biomeCount;
    
    // Reference to biome definitions sorted by priority (highest first)
    private readonly BiomeDefinition[] _biomes;
    
    // Terrain thresholds from config
    private float _oceanThreshold;
    private float _deepOceanThreshold;
    private float _alpineElevation;
    private float _shorelineRange;
    private float _coastRange;
    private float _lakeThreshold;
    private float _lakeMinContinentalness;
    
    /// <summary>
    /// Creates a new biome selector with pre-allocated arrays.
    /// </summary>
    /// <param name="biomes">Collection of biome definitions to select from.</param>
    public BiomeSelector(IReadOnlyList<BiomeDefinition> biomes)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        // Sort by priority (highest first) for correct evaluation order
        _biomes = [.. biomes.OrderByDescending(b => b.Priority)];
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
        _coastRange = config.CoastRange;
        _lakeThreshold = config.LakeThreshold;
        _lakeMinContinentalness = config.LakeMinContinentalness;
    }
    
    /// <summary>
    /// Select the best-matching biome for the given climate values and terrain height.
    /// Fully config-driven using BiomeDefinition properties.
    /// Zero allocations - uses pre-allocated arrays for distance calculations.
    /// </summary>
    /// <param name="continentalness01">Continentalness [0,1] where 0=deep ocean, 1=far inland.</param>
    /// <param name="temperature01">Temperature [0,1] where 0=frozen, 1=hot.</param>
    /// <param name="humidity01">Humidity [0,1] where 0=arid, 1=wet.</param>
    /// <param name="erosion01">Erosion [0,1] where low=dramatic terrain, high=flat.</param>
    /// <param name="peaksValleys01">Peaks/Valleys [0,1] from ridge noise.</param>
    /// <param name="actualHeight">Actual terrain height at this column (Y coordinate).</param>
    /// <param name="waterBodyType">Water body type at this column from single source of truth.</param>
    /// <returns>Selected BiomeId.</returns>
    public BiomeId SelectPrimary(
        float continentalness01,
        float temperature01,
        float humidity01,
        float erosion01,
        float peaksValleys01,
        float actualHeight,
        WaterBodyType waterBodyType = WaterBodyType.None,
        bool hasAdjacentOcean = false)
    {
        // SINGLE SOURCE OF TRUTH: Use water body type to determine ocean/lake status
        // This replaces the old continentalness-based checks that caused mismatches
        var isOcean = waterBodyType == WaterBodyType.Ocean;
        var isLake = waterBodyType == WaterBodyType.Lake;
        var hasWater = waterBodyType != WaterBodyType.None;
        
        // Derive terrain context from water body and height
        // A column is "oceanic" if it has ocean water (terrain below sea level in ocean zone)
        var isOceanic = isOcean;
        var isUnderwater = hasWater; // If there's water, the terrain surface is underwater
        var altitudeAboveWater = actualHeight - VoxelHelper.WaterLevel;
        
        // Lake biome is handled at block level, not biome level
        // But we need to know if this is a lake area for terrain type filtering
        
        // Coast detection: Beach biome ONLY appears where:
        // 1. There IS nearby ocean water (continentalness just above threshold)
        // 2. Terrain is at or just above water level
        // 3. The column itself is NOT underwater
        //
        // CRITICAL FIX: We also need adjacent ocean water to exist.
        // Without water body info from neighbors, we use continentalness as proxy:
        // If continentalness is JUST above threshold, ocean is nearby.
        // But we also require terrain to be LOW (near water level).
        var contDistance = MathF.Abs(continentalness01 - _oceanThreshold);
        
        // Allow beach on both sides of the threshold (land side and "dry ocean" side)
        // This ensures beaches appear even if the noise puts us slightly in the "ocean" zone but we are dry land
        var isNearOceanByContinentalness = contDistance < _coastRange;
        
        var isNearWaterHeight = altitudeAboveWater >= 0 && altitudeAboveWater <= _shorelineRange;
        
        // Coast is ONLY valid if:
        // - Not underwater (this column)
        // - Near ocean by continentalness OR adjacent to ocean water
        // - Terrain is low (at beach height)
        // - NOT in a lake area (lakes have their own biome handling)
        var isCoastal = !isUnderwater && !isOceanic && isNearWaterHeight && !isLake && (hasAdjacentOcean || isNearOceanByContinentalness);
        
        var isMountain = altitudeAboveWater > _alpineElevation;
        
        // Track best climate match among eligible biomes
        var bestBiomeId = BiomeId.Plains;  // Fallback
        var bestScore = float.MaxValue;
        
        // Iterate biomes in priority order (sorted in constructor)
        for (var i = 0; i < _biomeCount; i++)
        {
            var biome = _biomes[i];
            
            // Skip Lake biome - it's handled at block level in CpuTerrainGenerator, not biome level
            if (biome.Id == (int)BiomeId.Lake)
                continue;
            
            // ========== TERRAIN TYPE FILTER ==========
            // Check if biome's AllowedTerrain matches current terrain context
            var terrainMatch = biome.AllowedTerrain switch
            {
                TerrainType.OceanOnly => isOceanic,  // Uses water body type
                TerrainType.LandOnly => !isUnderwater && !isOceanic && !isCoastal,
                TerrainType.CoastOnly => isCoastal,
                TerrainType.MountainOnly => isMountain,
                TerrainType.Any => true,
                _ => true
            };
            
            if (!terrainMatch) continue;
            
            // ========== ELEVATION FILTER ==========
            // Check biome's elevation constraints
            if (actualHeight < biome.MinElevation || actualHeight > biome.MaxElevation)
                continue;
            
            // ========== SPECIAL CASE: DEEP OCEAN ==========
            // For ocean biomes, use continentalness to distinguish deep vs shallow
            if (biome.AllowedTerrain == TerrainType.OceanOnly)
            {
                var isDeep = continentalness01 < _deepOceanThreshold;
                if (biome.Id == (int)BiomeId.DeepOcean && !isDeep) continue;
                if (biome.Id == (int)BiomeId.Ocean && isDeep) continue;
            }
            
            // ========== CLIMATE DISTANCE SCORING ==========
            // Calculate how well the climate matches this biome's ranges
            var tempDist = biome.Temperature.Contains(temperature01) 
                ? 0f 
                : MathF.Min(MathF.Abs(temperature01 - biome.Temperature.Min),
                            MathF.Abs(temperature01 - biome.Temperature.Max));
            
            var humidDist = biome.Humidity.Contains(humidity01)
                ? 0f
                : MathF.Min(MathF.Abs(humidity01 - biome.Humidity.Min),
                            MathF.Abs(humidity01 - biome.Humidity.Max));
            
            // Combined score: priority-weighted distance
            // Higher priority biomes get a bonus (lower score)
            var priorityBonus = (100 - biome.Priority) * 0.01f;  // 0 for p=100, 1.0 for p=0
            var climateScore = tempDist + humidDist + priorityBonus;
            
            // Store for blend calculations
            _biomeDistances[i] = climateScore;
            
            // Track best match
            if (climateScore < bestScore)
            {
                bestScore = climateScore;
                bestBiomeId = (BiomeId)biome.Id;
            }
        }
        
        return bestBiomeId;
    }
    
    /// <summary>
    /// Select biome with blend weights for smooth transitions.
    /// Config-driven using BiomeDefinition properties.
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
        // Derive terrain context
        var isUnderwater = actualHeight < VoxelHelper.WaterLevel;
        var altitudeAboveWater = actualHeight - VoxelHelper.WaterLevel;
        var isOceanic = continentalness01 < _oceanThreshold;
        
        // Distance from ocean threshold - used for coast detection
        var contDistance = MathF.Abs(continentalness01 - _oceanThreshold);
        
        // FIXED: Coast requires BOTH proximity to ocean (by continentalness) AND low elevation
        var isNearOceanByContinentalness = contDistance <= _coastRange;
        var isNearWaterHeight = altitudeAboveWater <= _shorelineRange;
        var isCoastal = !isUnderwater && !isOceanic && isNearOceanByContinentalness && isNearWaterHeight;
        
        var isMountain = altitudeAboveWater > _alpineElevation;
        
        // Calculate distances for all eligible biomes
        var best1Idx = -1;
        var best2Idx = -1;
        var best1Dist = float.MaxValue;
        var best2Dist = float.MaxValue;
        
        for (var i = 0; i < _biomeCount; i++)
        {
            var biome = _biomes[i];
            
            // Check terrain type eligibility
            var terrainMatch = biome.AllowedTerrain switch
            {
                TerrainType.OceanOnly => isUnderwater && isOceanic,
                TerrainType.LandOnly => !isUnderwater && !isOceanic && !isCoastal,
                TerrainType.CoastOnly => isCoastal,
                TerrainType.MountainOnly => isMountain,
                TerrainType.Any => true,
                _ => true
            };
            
            if (!terrainMatch)
            {
                _biomeDistances[i] = float.MaxValue;
                continue;
            }
            
            // Check elevation constraints
            if (actualHeight < biome.MinElevation || actualHeight > biome.MaxElevation)
            {
                _biomeDistances[i] = float.MaxValue;
                continue;
            }
            
            // Special handling for ocean depth distinction
            if (biome.AllowedTerrain == TerrainType.OceanOnly)
            {
                var isDeep = continentalness01 < _deepOceanThreshold;
                if (biome.Id == (int)BiomeId.DeepOcean && !isDeep)
                {
                    _biomeDistances[i] = float.MaxValue;
                    continue;
                }
                if (biome.Id == (int)BiomeId.Ocean && isDeep)
                {
                    _biomeDistances[i] = float.MaxValue;
                    continue;
                }
            }
            
            // Calculate climate distance using biome's ranges
            var tIn = biome.Temperature.Contains(temperature01);
            var hIn = biome.Humidity.Contains(humidity01);
            var dist = tIn && hIn ? 0f 
                : MathF.Abs(biome.Temperature.Center - temperature01) + MathF.Abs(biome.Humidity.Center - humidity01);
            
            // Apply priority bonus
            dist += (100 - biome.Priority) * 0.01f;
            
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
