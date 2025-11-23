// terrain-common.glsl
// Shared terrain generation logic for compute shaders

// Constants
const int CHUNK_SIDE_SIZE = 16;
const int CHUNK_Y_SIZE = 128;
const int CHUNK_SIDE_SIZE_SQUARED = CHUNK_SIDE_SIZE * CHUNK_SIDE_SIZE;
const int CHUNK_VOXEL_COUNT = CHUNK_SIDE_SIZE_SQUARED * CHUNK_Y_SIZE;
const int WATER_LEVEL = 35;

// Block types
const uint BLOCK_NONE = 0u;
const uint BLOCK_WATER_LEVEL = 1u;
const uint BLOCK_ROCK = 2u;
const uint BLOCK_SAND = 3u;
const uint BLOCK_DIRT = 4u;
const uint BLOCK_GRASS_DIRT = 5u;
const uint BLOCK_BEDROCK = 8u; // Added BedRock

// Bindings (M1/M2)
layout(binding = 0) uniform sampler1D uHeightSpline;
layout(binding = 1) uniform usampler2D uBiomeLUT;

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
    float uCaveThreshold; float uCurlScale; float uCurlStrength; float pad0;
} params;

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
    vec2 wp = p * params.uWarpScale;
    vec2 warp = domainWarp(wp, params.uSeed);
    return fbm((p + warp) * params.uContScale, params.uSeed, 3, 0.5, 2.0);
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
    
    // Add some variation based on PV and E
    // If erosion is low (rugged), add peaks
    // If erosion is high (flat), reduce peaks
    float ruggedness = (1.0 - E * 0.5 - 0.5); // [0, 1] roughly
    height += PV * 20.0 * ruggedness;
    
    return height;
}

// Simple 2D noise (Legacy)
float noise2D(int x, int z, uint seed) {
    uint n = hash(uint(x) + hash(uint(z), seed), seed);
    return float(n) / float(0xFFFFFFFFu) * 2.0 - 1.0;
}

// Bilinear interpolation (Legacy)
float interpolatedNoise(float x, float z, uint seed) {
    int ix = int(floor(x));
    int iz = int(floor(z));
    float fx = fract(x);
    float fz = fract(z);
    
    // Smooth the interpolation
    fx = fx * fx * (3.0 - 2.0 * fx);
    fz = fz * fz * (3.0 - 2.0 * fz);
    
    float v00 = noise2D(ix, iz, seed);
    float v10 = noise2D(ix + 1, iz, seed);
    float v01 = noise2D(ix, iz + 1, seed);
    float v11 = noise2D(ix + 1, iz + 1, seed);
    
    float v0 = mix(v00, v10, fx);
    float v1 = mix(v01, v11, fx);
    return mix(v0, v1, fz);
}

// Multi-octave noise (Legacy)
float multiOctaveNoise(float x, float z, uint seed, int octaves) {
    float total = 0.0;
    float frequency = 1.0;
    float amplitude = 1.0;
    float maxValue = 0.0;
    
    for (int i = 0; i < octaves; i++) {
        total += interpolatedNoise(x * frequency, z * frequency, seed + uint(i * 100)) * amplitude;
        maxValue += amplitude;
        amplitude *= 0.5;
        frequency *= 2.0;
    }
    
    return total / maxValue;
}

// Generate terrain height
int generateHeight(int wx, int wz) {
    // M2: Use new height generation
    float h = getHeight(vec2(wx, wz));
    int height = int(h);
    return clamp(height, 0, CHUNK_Y_SIZE - 1);
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
    
    // Surface block
    if (y == height) {
        if (y < WATER_LEVEL) {
            // Underwater surface
            if (y >= WATER_LEVEL - 1) return BLOCK_SAND; // 1 block below water
            return BLOCK_BEDROCK; // Deep underwater
        } else {
            // Above water surface
            if (y <= WATER_LEVEL + 2) {
                // Shoreline check
                if (isNearWater(wx, wz)) return BLOCK_SAND;
            }
            return BLOCK_GRASS_DIRT;
        }
    }
    
    // Sub-surface
    int depth = height - y;
    if (depth <= 2) return BLOCK_DIRT;
    return BLOCK_ROCK;
}
