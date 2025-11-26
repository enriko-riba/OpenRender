// terrain-biomes.glsl
// Biome system (climate calculation, biome selection, region mixing)

#ifndef TERRAIN_BIOMES_GLSL
#define TERRAIN_BIOMES_GLSL

// Requires: terrain-common.glsl, terrain-noise.glsl, terrain-generation.glsl

// ============================================================================
// Biome Constants
// ============================================================================

// Hardcoded biome IDs (must match BiomeDefinition constants in C#)
const uint OCEAN_BIOME_ID = 0u;
const uint ALPINE_BIOME_ID = 9u;
const uint DEFAULT_FALLBACK_BIOME_ID = 2u; // Plains

// ============================================================================
// Climate Calculation
// ============================================================================

float getTemperature(vec3 p) {
    // Start from uBaseTemp
    float temp = params.uBaseTemp;
    
    // Subtract altitude lapse: T -= uLapseRate * max(0, y - seaLevel)
    float altitude = max(0.0, p.y - float(WATER_LEVEL));
    temp -= params.uLapseRate * altitude;
    
#ifdef IS_FRAGMENT_SHADER
    // Fast noise for visuals
    float noise = smoothNoise(p.xz * params.uClimateScale, params.uSeed + 700u);
#else
    // Add low-freq noise and domain warp
    vec2 wp = p.xz * params.uClimateWarp;
    vec2 warp = domainWarp(wp, params.uSeed + 600u);
    float noise = fbm((p.xz + warp) * params.uClimateScale, params.uSeed + 700u, 2, 0.5, 2.0);
#endif
    
    // Add noise to temp (range [-1, 1] -> scale to e.g. +/- 0.2)
    temp += noise * 0.2;
    
    return clamp(temp, 0.0, 1.0);
}

float getHumidity(vec3 p) {
    // Start from uBaseHum
    float hum = params.uBaseHum;
    
    // Reduce by coast drying proportional to |C - coastValue| (distance from ocean)
    float C = getContinentalness(p.xz);
    float coastVal = params.uCoastThreshold; // Use config value instead of hardcoded 0.35
    float distFromCoast = max(0.0, C - coastVal);
    
    hum -= distFromCoast * params.uCoastDry;
    
#ifdef IS_FRAGMENT_SHADER
    // Fast noise for visuals
    float noise = smoothNoise(p.xz * params.uClimateScale, params.uSeed + 900u);
#else
    // Add low-freq noise and warp
    vec2 wp = p.xz * params.uClimateWarp;
    vec2 warp = domainWarp(wp, params.uSeed + 800u);
    float noise = fbm((p.xz + warp) * params.uClimateScale, params.uSeed + 900u, 2, 0.5, 2.0);
#endif
    
    hum += noise * 0.2;
    
    return clamp(hum, 0.0, 1.0);
}

// ============================================================================
// Biome Selection
// ============================================================================

/// <summary>
/// NEW ARCHITECTURE: Terrain-Type-Aware Biome Selection with Natural Transitions
/// 
/// Phase 1: Ocean Check
/// - If C < OceanThreshold: return OCEAN biome (hardcoded)
/// 
/// Phase 2: Climate-Based Land Biomes with Altitude Influence
/// - Sample LUT (contains ONLY LandOnly biomes) based on temperature/humidity
/// - Add altitude-based Alpine influence with noise for natural transitions
/// - Blend Alpine with base biome using smooth probability curve
/// 
/// This ensures:
/// - Ocean always stays Ocean (no matter the temperature)
/// - Mountains gradually transition to Alpine with natural variation
/// - Land biomes are distributed by climate with altitude influence
/// - Alpine "pockets" can stretch into forests and vice versa
/// </summary>
uint getBiomeId(vec3 p) {
    float C = getContinentalness(p.xz);
    float tC = C * 0.5 + 0.5; // Remap to [0,1]
    
    // Declare variables once at top scope
    float terrainHeight;
    float heightApprox;  // Used in fragment shader paths
    
    // PHASE 1: Ocean Detection
    // CRITICAL FIX: Ocean ONLY appears on blocks BELOW water level!
    // Previous bug: Ocean extended up entire cliff column if C < threshold
    // New logic: Check BOTH continentalness AND actual Y position
    
    bool isUnderwater;
    
    #ifdef IS_FRAGMENT_SHADER
        // Fragment: Use height approximation
        heightApprox = texture(uHeightSpline, tC).r + float(WATER_LEVEL);
        
        // Ocean ONLY if:
        // 1. Low continentalness (coastal/ocean area)
        // 2. Height is below water
        // 3. Fragment Y position is below water (CRITICAL!)
        isUnderwater = (tC < params.uOceanThreshold) && 
                       (heightApprox <= float(WATER_LEVEL) + 10.0) &&
                       (p.y <= float(WATER_LEVEL));
    #else
        // Compute: Use actual terrain height
        terrainHeight = getHeight(p.xz);
        
        // Ocean ONLY if:
        // 1. Terrain surface is below water
        // 2. Block Y position is below water (CRITICAL!)
        isUnderwater = (terrainHeight <= float(WATER_LEVEL)) && 
                       (p.y <= float(WATER_LEVEL));
    #endif
    
    if (isUnderwater) {
        return OCEAN_BIOME_ID;
    }
    
    // PHASE 1.5: Beach Detection
    // Beach appears at shoreline (just above water in coastal areas)
    #ifdef IS_FRAGMENT_SHADER
        // Fragment: Approximate based on continentalness (reuse heightApprox from Phase 1)
        bool isCoastal = (tC < params.uCoastRange);
        bool likelyBeach = isCoastal && (tC > params.uOceanThreshold) && (tC < params.uOceanThreshold + 0.1);
        if (likelyBeach) {
            return 1u;  // BEACH_BIOME_ID
        }
    #else
        // Compute: Precise based on actual height (reuse terrainHeight from Phase 1)
        float heightAboveWater = terrainHeight - float(WATER_LEVEL);
        bool isCoastal = (tC < params.uCoastRange);
        
        if (isCoastal && heightAboveWater >= 0.0 && heightAboveWater <= 3.0) {
            return 1u;  // BEACH_BIOME_ID
        }
    #endif
    
    // PHASE 2: Climate-Based Land Biomes with Boundary Noise
    // ALL climate biomes get organic boundaries via noise-shifted temperature/humidity
    float t = getTemperature(p);
    float h = getHumidity(p);
    
    // Add boundary noise to T/H BEFORE LUT lookup
    // This creates curved organic boundaries between ALL climate zones
    float noiseT = fbm(p.xz * 0.02, params.uSeed + 4000u, 2, 0.5, 2.0) * 0.08;
    float noiseH = fbm(p.xz * 0.02, params.uSeed + 5000u, 2, 0.5, 2.0) * 0.08;
    
    float tShifted = clamp(t + noiseT, 0.0, 1.0);
    float hShifted = clamp(h + noiseH, 0.0, 1.0);
    
    // Sample biome LUT with noise-shifted coordinates
    uint baseBiomeId = texture(uBiomeLUT, vec2(tShifted, hShifted)).r;
    
    // Skip special biomes if LUT returns them
    if (baseBiomeId == OCEAN_BIOME_ID || baseBiomeId == 1u) {
        baseBiomeId = DEFAULT_FALLBACK_BIOME_ID;
    }
    
    // PHASE 3: Alpine Override (Dual Triggers with Separate Thresholds)
    // Altitude trigger: elevation >= 250 (high mountains)
    // Cold trigger: temperature < 0.15 (arctic, any elevation)
    
    bool alpineFromAltitude = false;
    bool alpineFromCold = false;
    
    #ifdef IS_FRAGMENT_SHADER
        // Fragment: Use height approximation (reuse heightApprox from Phase 1)
        // Altitude trigger: >= 250 (use higher threshold due to approximation uncertainty)
        if (heightApprox >= 220.0) {
            float altInfluence = smoothstep(220.0, 280.0, heightApprox);
            float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0) * 0.5 + 0.5;
            float alpineFavor = altInfluence * 0.7 + boundaryNoise * 0.3;
            alpineFromAltitude = (alpineFavor > 0.55);
        }
    #else
        // Compute: Use actual terrain height (reuse terrainHeight from Phase 1)
        // Altitude trigger: >= 250 with smoothstep transition
        if (terrainHeight >= 220.0) {
            float altInfluence = smoothstep(220.0, 280.0, terrainHeight);
            float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0) * 0.5 + 0.5;
            float alpineFavor = altInfluence * 0.7 + boundaryNoise * 0.3;
            alpineFromAltitude = (alpineFavor > 0.55);
        }
    #endif
    
    // Cold trigger: temperature < 0.15, NO height restriction
    // This allows arctic tundra with snow at sea level
    if (t < 0.20) {
        float coldInfluence = smoothstep(0.20, 0.10, t);
        float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0) * 0.5 + 0.5;
        float alpineFavor = coldInfluence * 0.7 + boundaryNoise * 0.3;
        alpineFromCold = (alpineFavor > 0.55);
    }
    
    if (alpineFromAltitude || alpineFromCold) {
        return ALPINE_BIOME_ID;
    }
    
    return baseBiomeId;
}

// ============================================================================
// Region Mixing (Worley Noise)
// ============================================================================

struct RegionMix {
    uint biomeIds[4];
    float weights[4];
};

// Simple hash for region cells
uint hashRegion(ivec2 p, uint seed) {
    return hash(uint(p.x) + hash(uint(p.y), seed), seed);
}

RegionMix getBiomeWeights(vec3 p) {
    float cellSize = params.uRegionCellSize * float(CHUNK_SIDE_SIZE); // Convert chunks to meters
    vec2 uv = p.xz / cellSize;
    vec2 cell = floor(uv);
    
    // Worley noise: find K nearest centers
    uint ids[4] = uint[](0,0,0,0);
    float dists[4] = float[](1e9, 1e9, 1e9, 1e9);
    
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 neighbor = cell + vec2(x, y);
            
            // Jitter the center
            uint h = hashRegion(ivec2(neighbor), params.uSeed + 1000u);
            vec2 jitter = vec2(
                float(h & 0xFFFFu) / 65535.0,
                float((h >> 16) & 0xFFFFu) / 65535.0
            ) - 0.5;
            jitter *= params.uRegionJitter;
            
            vec2 center = neighbor + 0.5 + jitter;
            float d = distance(uv, center);
            
            // Insert into sorted list (closest first)
            // Determine biome at this cell center
            vec3 centerPos = vec3(center.x * cellSize, p.y, center.y * cellSize);
            uint id = getBiomeId(centerPos);
            
            // Insert
            for (int i = 0; i < 4; i++) {
                if (d < dists[i]) {
                    // Shift down
                    for (int j = 3; j > i; j--) {
                        dists[j] = dists[j-1];
                        ids[j] = ids[j-1];
                    }
                    dists[i] = d;
                    ids[i] = id;
                    break;
                }
            }
        }
    }
    
    // Convert distances to weights
    float feather = params.uRegionFeather / cellSize;
    float totalWeight = 0.0;
    float weights[4] = float[](0.0, 0.0, 0.0, 0.0);
    
    float d0 = dists[0];
    
    for (int i = 0; i < 4; i++) {
        float diff = dists[i] - d0;
        if (diff < feather) {
            float w = 1.0 - (diff / feather);
            w = w * w * (3.0 - 2.0 * w); // Smoothstep
            weights[i] = w;
            totalWeight += w;
        }
    }
    
    if (totalWeight > 0.0) {
        for (int i = 0; i < 4; i++) weights[i] /= totalWeight;
    } else {
        weights[0] = 1.0;
    }
    
    return RegionMix(ids, weights);
}

#endif // TERRAIN_BIOMES_GLSL
