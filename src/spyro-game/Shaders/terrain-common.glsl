// terrain-common.glsl
// Shared terrain generation logic for compute shaders

// Constants
const int CHUNK_SIDE_SIZE = 16;
const int CHUNK_Y_SIZE = 384;
const int CHUNK_SIDE_SIZE_SQUARED = CHUNK_SIDE_SIZE * CHUNK_SIDE_SIZE;
const int CHUNK_VOXEL_COUNT = CHUNK_SIDE_SIZE_SQUARED * CHUNK_Y_SIZE;
const int PACKED_CHUNK_VOXEL_COUNT = CHUNK_VOXEL_COUNT; // UNPACKED
const int WATER_LEVEL = 35;

// Block types
const uint BLOCK_NONE = 0u;
const uint BLOCK_WATER_LEVEL = 1u;
const uint BLOCK_ROCK = 2u;
const uint BLOCK_SAND = 3u;
const uint BLOCK_DIRT = 4u;
const uint BLOCK_GRASS_DIRT = 5u;
const uint BLOCK_BEDROCK = 8u; // Added BedRock

// Helper to read packed voxel (UNPACKED: 1 voxel per uint)
#define getPackedVoxel(idx, buf) (buf[idx] & 0xFFu)

// Helper to read packed visibility mask (UNPACKED: 1 mask per uint)
#define getPackedVisMask(idx, buf) (buf[idx] & 0xFFu)

// Helper to write packed voxel (UNPACKED: 1 voxel per uint)
#define setPackedVoxel(idx, val, buf) { buf[idx] = val; }

// Atomic write for packed voxel (UNPACKED: 1 voxel per uint)
#define atomicSetPackedVoxel(idx, val, buf) { buf[idx] = val; }

// Bindings (M1/M2)
layout(binding = 6) uniform sampler1D uHeightSpline;
layout(binding = 7) uniform usampler2D uBiomeLUT;

layout(std430, binding = 10) readonly buffer TerrainParams {
    uint uSeed;
    float uWorldScale;
    float uMacroScale;
    float uContScale; float uErodeScale; float uRidgeScale;
    float uWarpScale; float uWarpStrength;
    float uBaseTemp; float uLapseRate; float uBaseHum; float uCoastDry;
    float uClimateScale; float uClimateWarp;
    float uRegionCellSize; float uRegionJitter; float uRegionFeather; uint uMaxRegionMix;
    float uCheeseFreq; float uCheeseAmp; float uSpaghettiFreq; float uSpaghettiAmp;
    float uCaveThreshold; float uCurlScale; float uCurlStrength;
    // Terrain shaping parameters
    float uCoastThreshold;
    float uMountainThreshold;
    float uCliffFreq;
    float uCliffAmp;
    float uOverhangFreq;
    float uOverhangAmp;
    float uShorelineRange;
    float uSubsurfaceDepth;
    float uCaveDepthFade;
    float uCaveSlopeFadeMin;
    float uCaveSlopeFadeMax;
    float uCaveFloodExt;
    // NEW: Ocean/Land/Altitude thresholds for terrain-type-aware biome system
    float uOceanThreshold;
    float uDeepOceanThreshold;
    float uAlpineElevation;
    float uCoastRange;
} params;

// Hardcoded biome IDs (must match BiomeDefinition constants in C#)
const uint OCEAN_BIOME_ID = 0u;
const uint ALPINE_BIOME_ID = 9u;
const uint DEFAULT_FALLBACK_BIOME_ID = 2u; // Plains

// Simple hash function
uint hash(uint x, uint seed) {
    x += seed;
    x = ((x >> 16) ^ x) * 0x45d9f3bu;
    x = ((x >> 16) ^ x) * 0x45d9f3bu;
    x = (x >> 16) ^ x;
    return x;
}

// Float Noise Functions
float noise2D_float(vec2 p, uint seed) {
    // FIX: Cast to int first to handle negative coordinates correctly (2's complement)
    // uint(float) is undefined for negative values.
    uint n = hash(uint(int(p.x)) + hash(uint(int(p.y)), seed), seed);
    return float(n) / 4294967295.0 * 2.0 - 1.0;
}

float smoothNoise(vec2 p, uint seed) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    
    float a = noise2D_float(i, seed);
    float b = noise2D_float(i + vec2(1.0, 0.0), seed);
    float c = noise2D_float(i + vec2(0.0, 1.0), seed);
    float d = noise2D_float(i + vec2(1.0, 1.0), seed);
    
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p, uint seed, int octaves, float persistence, float lacunarity) {
    float total = 0.0;
    float frequency = 1.0;
    float amplitude = 1.0;
    float maxValue = 0.0;
    for(int i=0; i<octaves; i++) {
        total += smoothNoise(p * frequency, seed + uint(i*132)) * amplitude;
        maxValue += amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
    }
    return total / maxValue;
}

// 3D Noise Functions
float noise3D_float(vec3 p, uint seed) {
    uint n = hash(uint(int(p.x)) + hash(uint(int(p.y)) + hash(uint(int(p.z)), seed), seed), seed);
    return float(n) / 4294967295.0 * 2.0 - 1.0;
}

float smoothNoise3D(vec3 p, uint seed) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    
    float v000 = noise3D_float(i, seed);
    float v100 = noise3D_float(i + vec3(1.0, 0.0, 0.0), seed);
    float v010 = noise3D_float(i + vec3(0.0, 1.0, 0.0), seed);
    float v110 = noise3D_float(i + vec3(1.0, 1.0, 0.0), seed);
    float v001 = noise3D_float(i + vec3(0.0, 0.0, 1.0), seed);
    float v101 = noise3D_float(i + vec3(1.0, 0.0, 1.0), seed);
    float v011 = noise3D_float(i + vec3(0.0, 1.0, 1.0), seed);
    float v111 = noise3D_float(i + vec3(1.0, 1.0, 1.0), seed);
    
    return mix(
        mix(mix(v000, v100, f.x), mix(v010, v110, f.x), f.y),
        mix(mix(v001, v101, f.x), mix(v011, v111, f.x), f.y),
        f.z
    );
}

float fbm3D(vec3 p, uint seed, int octaves, float persistence, float lacunarity) {
    float total = 0.0;
    float frequency = 1.0;
    float amplitude = 1.0;
    float maxValue = 0.0;
    for(int i=0; i<octaves; i++) {
        total += smoothNoise3D(p * frequency, seed + uint(i*132)) * amplitude;
        maxValue += amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
    }
    return total / maxValue;
}

// Cave Density Functions
float getCheeseDensity(vec3 p) {
    // Cheese caves: 3D noise threshold
    return fbm3D(p * params.uCheeseFreq, params.uSeed + 300u, 2, 0.5, 2.0);
}

float getSpaghettiDensity(vec3 p) {
    // Spaghetti caves: Ridged 3D noise (worms)
    // We want long, thin tunnels. Ridged noise produces "veins".
    // We use two noise fields to create "worms" where both are close to 0.
    float n1 = fbm3D(p * params.uSpaghettiFreq, params.uSeed + 400u, 2, 0.5, 2.0);
    float n2 = fbm3D(p * params.uSpaghettiFreq, params.uSeed + 500u, 2, 0.5, 2.0);
    
    // Combine: density is high when both n1 and n2 are close to 0
    // This creates a tube-like structure
    float dist = sqrt(n1*n1 + n2*n2);
    // Invert so high density = cave
    // Map [0, 1] -> [1, 0] roughly
    return 1.0 - dist * 4.0; // Scale to control thickness
}

// Domain Warp
vec2 domainWarp(vec2 p, uint seed) {
    vec2 q = vec2(
        fbm(p + vec2(0.0, 0.0), seed, 2, 0.5, 2.0),
        fbm(p + vec2(5.2, 1.3), seed, 2, 0.5, 2.0)
    );
    return q * params.uWarpStrength;
}

// Macro Fields
float getContinentalness(vec2 p) {
#ifdef IS_FRAGMENT_SHADER
    // Fast approximation for visuals
    return smoothNoise(p * params.uContScale, params.uSeed);
#else
    vec2 wp = p * params.uWarpScale;
    vec2 warp = domainWarp(wp, params.uSeed);
    return fbm((p + warp) * params.uContScale, params.uSeed, 3, 0.5, 2.0);
#endif
}

float getErosion(vec2 p) {
    return fbm(p * params.uErodeScale, params.uSeed + 100u, 3, 0.5, 2.0);
}

float getPeaksValleys(vec2 p) {
    // Ridged noise
    float n = fbm(p * params.uRidgeScale, params.uSeed + 200u, 3, 0.5, 2.0);
    return 1.0 - abs(n);
}

// Height Calculation
float getHeight(vec2 p) {
    float C = getContinentalness(p); // [-1, 1]
    float E = getErosion(p);         // [-1, 1]
    float PV = getPeaksValleys(p);   // [0, 1]
    
    // Remap C to [0, 1] for spline sampling
    float tC = C * 0.5 + 0.5;
    
    // Sample Spline
    float baseHeight = texture(uHeightSpline, tC).r;
    
    // Apply Erosion and Peaks
    // Simple shaping for M2
    // FIX: Add WATER_LEVEL because spline is relative to sea level (0 = coast)
    float height = baseHeight + float(WATER_LEVEL);
    
    // NEW: Only apply erosion and peaks on LAND areas (C >= OceanThreshold)
    // Ocean floors should be smooth, not eroded
    if (tC >= params.uOceanThreshold) {
        // Add some variation based on PV and E
        // If erosion is low (rugged), add peaks
        // If erosion is high (flat), reduce peaks
        float ruggedness = (1.0 - E * 0.5 - 0.5); // [0, 1] roughly
        height += PV * 20.0 * ruggedness;
        
        // Add dramatic cliff noise in mountainous regions
        if (tC > params.uMountainThreshold) {
            // High-frequency ridge noise for cliff faces
            float cliffNoise = fbm(p * params.uCliffFreq, params.uSeed + 1500u, 4, 0.6, 2.5);
            // Make it ridged (abs creates sharp peaks)
            cliffNoise = abs(cliffNoise);
            // Scale by mountain intensity
            float mountainness = smoothstep(params.uMountainThreshold, 0.95, tC);
            height += cliffNoise * params.uCliffAmp * mountainness;
        }
    }
    
    return height;
}

// 3D density function for overhangs in mountains
float getTerrainDensity(vec3 p) {
    vec2 xz = p.xz;
    float y = p.y;
    
    float C = getContinentalness(xz);
    float tC = C * 0.5 + 0.5;
    
    // Base height at this XZ
    float baseHeight = getHeight(xz);
    
    // Simple density: positive below surface, negative above
    float density = baseHeight - y;
    
    // Add 3D overhang noise in mountains
    if (tC > params.uMountainThreshold && y > baseHeight - 50.0 && y < baseHeight + 30.0) {
        float mountainness = smoothstep(params.uMountainThreshold, 1.0, tC);
        // 3D noise for overhangs
        float overhangNoise = fbm3D(p * params.uOverhangFreq, params.uSeed + 2000u, 3, 0.5, 2.0);
        // Add overhang effect near the surface
        float heightFactor = 1.0 - abs((y - baseHeight) / 40.0);
        heightFactor = clamp(heightFactor, 0.0, 1.0);
        density += overhangNoise * params.uOverhangAmp * mountainness * heightFactor;
    }
    
    return density;
}

// Generate terrain height
int generateHeight(int wx, int wz) {
    // M2: Use new height generation
    float h = getHeight(vec2(wx, wz));
    int height = int(h);
    return clamp(height, 0, CHUNK_Y_SIZE - 1);
}

// Helper to calculate local slope
float getSlope(int wx, int wz) {
    float h0 = float(generateHeight(wx, wz));
    float h1 = float(generateHeight(wx + 1, wz));
    float h2 = float(generateHeight(wx, wz + 1));
    float h3 = float(generateHeight(wx - 1, wz));
    float h4 = float(generateHeight(wx, wz - 1));
    
    // Average slope magnitude
    float dx = max(abs(h1 - h0), abs(h3 - h0));
    float dz = max(abs(h2 - h0), abs(h4 - h0));
    return sqrt(dx*dx + dz*dz);
}

bool isCave(int wx, int wy, int wz, int depth, float slope) {
    vec3 p = vec3(wx, wy, wz);
    
    // Surface attenuation: reduce cave probability near surface
    // Use config value for fade distance instead of hardcoded 5.0
    float depthAtten = smoothstep(0.0, params.uCaveDepthFade, float(depth));
    
    // Allow entrances on slopes (cliffs/hills)
    // Use config values for slope thresholds instead of hardcoded 0.5, 2.0
    float slopeAtten = smoothstep(params.uCaveSlopeFadeMin, params.uCaveSlopeFadeMax, slope);
    
    // Use the best of both: if deep OR steep, allow cave
    // Favor steep slopes: weight slope more heavily
    float attenuation = max(depthAtten, slopeAtten * 1.2);
    attenuation = clamp(attenuation, 0.0, 1.0);
    
    // Cheese caves
    float cheese = getCheeseDensity(p);
    // Threshold: if cheese > threshold, it's a cave
    // Apply attenuation to the density (or increase threshold)
    if (cheese * attenuation > params.uCaveThreshold) return true;
    
    // Spaghetti caves
    float spaghetti = getSpaghettiDensity(p);
    if (spaghetti * attenuation > params.uCaveThreshold) return true;
    
    return false;
}

// Check if water is nearby (radius 3)
bool isNearWater(int wx, int wz) {
    // Check neighbors in radius 3
    // Optimization: check sparse points first
    for (int dz = -3; dz <= 3; dz+=3) {
        for (int dx = -3; dx <= 3; dx+=3) {
            if (dx == 0 && dz == 0) continue;
            int h = generateHeight(wx + dx, wz + dz);
            if (h <= WATER_LEVEL) return true;
        }
    }
    // Check closer points if needed (optional for performance)
    return false;
}

uint generateBlockType(int height, int y, int wx, int wz) {
    if (y > height) {
        if (y <= WATER_LEVEL) {
            return BLOCK_WATER_LEVEL;
        }
        return BLOCK_NONE;
    } 
    
    // Solid blocks (y <= height)
    
    vec2 xz = vec2(wx, wz);
    float C = getContinentalness(xz);
    float tC = C * 0.5 + 0.5;
    
    // NEW: Only carve caves in LAND areas (not in ocean floor)
    // Ocean floors should be solid bedrock/sand, not have caves
    bool isLand = (tC >= params.uOceanThreshold);
    
    // Check for 3D density overhangs in mountains (only on land)
    if (isLand && tC > params.uMountainThreshold && y > height - 50 && y > WATER_LEVEL + 20) {
        vec3 p = vec3(wx, y, wz);
        float density = getTerrainDensity(p);
        if (density < 0.0) {
            // Overhang carved out
            return BLOCK_NONE;
        }
    }
    
    // Cave Carving (only on land, not in ocean)
    // Don't carve bedrock (y=0) or ocean floor
    if (isLand && y > 0) {
        int depth = height - y;
        // Calculate slope for breach logic
        // Extended range to depth < 15 (from 12) for better breach detection
        float slope = 0.0;
        if (depth < 15) {
            slope = getSlope(wx, wz);
        }
        
        if (isCave(wx, y, wz, depth, slope)) {
            // If it's a cave, it's Air (unless it's below water level, then it might be flooded)
            if (y <= WATER_LEVEL) {
                // Flooded cave logic:
                // If the terrain column is underwater (ocean), flood the cave.
                // This prevents "water portals" on the ocean floor.
                // Caves under land (height > WATER_LEVEL) remain dry (Air), preserving "air pockets".
                // Use config value for flooding extension instead of hardcoded 8
                if (y <= WATER_LEVEL + int(params.uCaveFloodExt)) {
                    return BLOCK_WATER_LEVEL;
                }
                
                // Dry cave under land
                return BLOCK_NONE;
            }
            return BLOCK_NONE;
        }
    }
    
    // Surface block
    if (y == height) {
        if (y < WATER_LEVEL) {
            // Underwater surface (ocean floor or shallow water)
            if (y >= WATER_LEVEL - 1) return BLOCK_SAND; // 1 block below water
            return BLOCK_BEDROCK; // Deep underwater
        } else {
            // Above water surface (land)
            if (y <= WATER_LEVEL + int(params.uShorelineRange)) {
                // Shoreline check
                if (isNearWater(wx, wz)) return BLOCK_SAND;
            }
            return BLOCK_GRASS_DIRT;
        }
    }
    
    // Sub-surface
    int depth = height - y;
    if (depth <= int(params.uSubsurfaceDepth)) return BLOCK_DIRT;
    return BLOCK_ROCK;
}

// ============================================================================
// M4: Climate and Biomes
// ============================================================================

struct RegionMix {
    uint biomeIds[4];
    float weights[4];
};

// Simple hash for region cells
uint hashRegion(ivec2 p, uint seed) {
    return hash(uint(p.x) + hash(uint(p.y), seed), seed);
}

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

/// <summary>
/// NEW ARCHITECTURE: Terrain-Type-Aware Biome Selection
/// 
/// Phase 1: Ocean/Land Check
/// - If C < OceanThreshold: return OCEAN biome (hardcoded)
/// 
/// Phase 2: Altitude Override
/// - If elevation > AlpineElevation: return ALPINE biome (hardcoded)
/// 
/// Phase 3: Climate-Based Land Biomes
/// - Sample LUT (contains ONLY LandOnly biomes)
/// - LUT is indexed by temperature x humidity
/// 
/// This ensures:
/// - Ocean always stays Ocean (no matter the temperature)
/// - Mountains always become Alpine (no matter the climate)
/// - Land biomes are properly distributed by climate
/// </summary>
uint getBiomeId(vec3 p) {
    float C = getContinentalness(p.xz);
    float tC = C * 0.5 + 0.5; // Remap to [0,1]
    
    // PHASE 1: Ocean Check (Priority 100)
    // If continentalness is below ocean threshold, it's ocean regardless of climate
    if (tC < params.uOceanThreshold) {
        return OCEAN_BIOME_ID;
    }
    
    // PHASE 2: Altitude Override (Priority 90)
    // If elevation is above alpine threshold, it's alpine regardless of climate
    // Use approximate elevation from height spline (fast, no full getHeight calculation)
    float approxElevation = texture(uHeightSpline, tC).r + float(WATER_LEVEL);
    if (approxElevation > params.uAlpineElevation) {
        return ALPINE_BIOME_ID;
    }
    
    // PHASE 3: Climate-Based Land Biomes (Priority 50)
    // Sample the LUT which contains only LandOnly biomes
    float t = getTemperature(p);
    float h = getHumidity(p);
    
    // Sample biome LUT (R8UI texture, contains only LAND biomes)
    uint biomeId = texture(uBiomeLUT, vec2(t, h)).r;
    
    // Fallback safety: if LUT returns 0 (which might be Ocean ID), use default land biome
    if (biomeId == OCEAN_BIOME_ID) {
        return DEFAULT_FALLBACK_BIOME_ID;
    }
    
    return biomeId;
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
