#version 460
// Mesh compaction: builds compact vertex + index buffers from visibility mask

// BEGIN terrain-common.glsl
// terrain-common.glsl
// Core constants, types, and helpers shared by all terrain shaders
// 
// This file contains ONLY:
// - Constants (chunk sizes, water level, block descriptors)
// - Basic macros (packed voxel access)
// - Bindings (textures, UBOs, SSBOs)
// - Hash function (used by all modules)
//
// For specific functionality, include the appropriate module:
// - terrain-noise.glsl: Noise generation (2D/3D, FBM, domain warp)
// - terrain-caves.glsl: Cave generation (cheese, spaghetti)
// - terrain-generation.glsl: Terrain height, density, block types
// - terrain-biomes.glsl: Climate, biomes, region mixing
// - terrain-greedy-meshing.glsl: Greedy meshing pack/unpack

#ifndef TERRAIN_COMMON_GLSL
#define TERRAIN_COMMON_GLSL

// ============================================================================
// Constants
// ============================================================================

const int CHUNK_SIDE_SIZE = 16;
const int CHUNK_Y_SIZE = 384;
const int CHUNK_SIDE_SIZE_SQUARED = CHUNK_SIDE_SIZE * CHUNK_SIDE_SIZE;
const int CHUNK_VOXEL_COUNT = CHUNK_SIDE_SIZE_SQUARED * CHUNK_Y_SIZE;
const int PACKED_CHUNK_VOXEL_COUNT = CHUNK_VOXEL_COUNT; // UNPACKED
const int WATER_LEVEL = 35;

// ============================================================================
// Block Descriptors
// ============================================================================

// Block descriptors (matches BlockDescriptor C# enum)
// These represent geology layers for biome texture selection
const uint BD_AIR = 0u;                    // Air (empty space)
const uint BD_WATER = 1u;                  // Water
const uint BD_SURFACE = 2u;                // Surface layer (grass, snow, sand at surface)
const uint BD_SUBSURFACE = 3u;             // Subsurface layer (dirt below surface)
const uint BD_DEEP_SUBSURFACE = 4u;        // Deep subsurface (rock/stone)
const uint BD_UNDERWATER_SURFACE = 5u;     // Underwater surface (ocean floor)
const uint BD_UNDERWATER_SUBSURFACE = 6u;  // Underwater subsurface (bedrock)
const uint BD_SHORELINE = 7u;              // Shoreline (beach sand)

// ============================================================================
// Helper Macros
// ============================================================================

// Helper to read packed voxel (UNPACKED: 1 voxel per uint)
#define getPackedVoxel(idx, buf) (buf[idx] & 0xFFu)

// Helper to read packed visibility mask (PACKED: 4 masks per uint)
// idx is the voxel index. We shift right by 2 to get uint index, and use bottom 2 bits for byte shift.
#define getPackedVisMask(idx, buf) ((buf[idx >> 2] >> ((idx & 3u) * 8u)) & 0xFFu)

// Helper to write packed voxel (UNPACKED: 1 voxel per uint)
#define setPackedVoxel(idx, val, buf) { buf[idx] = val; }

// Atomic write for packed voxel (UNPACKED: 1 voxel per uint)
#define atomicSetPackedVoxel(idx, val, buf) { buf[idx] = val; }

// ============================================================================
// Bindings (Textures and UBOs)
// ============================================================================

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
    // NEW: Ocean/Land/Altitude thresholds for biome system
    float uOceanThreshold;
    float uDeepOceanThreshold;
    float uAlpineElevation;
    float uCoastRange;
    // Overhang range parameters
    float uOverhangDepthRange;
    float uOverhangHeightRange;
    float uOverhangFalloffRange;
} params;

// ============================================================================
// Core Hash Function
// ============================================================================

// Simple hash function (used by all noise/terrain modules)
uint hash(uint x, uint seed) {
    x += seed;
    x = ((x >> 16) ^ x) * 0x45d9f3bu;
    x = ((x >> 16) ^ x) * 0x45d9f3bu;
    x = (x >> 16) ^ x;
    return x;
}

#endif // TERRAIN_COMMON_GLSL
// END terrain-common.glsl
// BEGIN terrain-noise.glsl
// terrain-noise.glsl
// Noise generation functions for terrain generation
// Includes: 2D/3D noise, FBM (Fractal Brownian Motion), domain warping

#ifndef TERRAIN_NOISE_GLSL
#define TERRAIN_NOISE_GLSL

// Requires: terrain-common.glsl (for hash function and params)

// ============================================================================
// 2D Noise Functions
// ============================================================================

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

// ============================================================================
// 3D Noise Functions
// ============================================================================

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

// ============================================================================
// Domain Warping
// ============================================================================

vec2 domainWarp(vec2 p, uint seed) {
    vec2 q = vec2(
        fbm(p + vec2(0.0, 0.0), seed, 2, 0.5, 2.0),
        fbm(p + vec2(5.2, 1.3), seed, 2, 0.5, 2.0)
    );
    return q * params.uWarpStrength;
}

#endif // TERRAIN_NOISE_GLSL
// END terrain-noise.glsl
// BEGIN terrain-caves.glsl
// terrain-caves.glsl
// Cave generation system (cheese caves, spaghetti tunnels)

#ifndef TERRAIN_CAVES_GLSL
#define TERRAIN_CAVES_GLSL

// Requires: terrain-common.glsl, terrain-noise.glsl

// ============================================================================
// Cave Density Functions
// ============================================================================

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

#endif // TERRAIN_CAVES_GLSL
// END terrain-caves.glsl
// BEGIN terrain-generation.glsl
// terrain-generation.glsl
// Core terrain generation (height maps, density, block types)

#ifndef TERRAIN_GENERATION_GLSL
#define TERRAIN_GENERATION_GLSL

// Requires: terrain-common.glsl, terrain-noise.glsl, terrain-caves.glsl

// ============================================================================
// Macro Fields (Continentalness, Erosion, Peaks/Valleys)
// ============================================================================

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

// ============================================================================
// Height Calculation
// ============================================================================

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

// ============================================================================
// 3D Density (for overhangs)
// ============================================================================

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
    // Use configurable ranges instead of hardcoded values
    if (tC > params.uMountainThreshold && 
        y > baseHeight - params.uOverhangDepthRange && 
        y < baseHeight + params.uOverhangHeightRange) {
        float mountainness = smoothstep(params.uMountainThreshold, 1.0, tC);
        // 3D noise for overhangs
        float overhangNoise = fbm3D(p * params.uOverhangFreq, params.uSeed + 2000u, 3, 0.5, 2.0);
        // Add overhang effect near the surface
        float heightFactor = 1.0 - abs((y - baseHeight) / params.uOverhangFalloffRange);
        heightFactor = clamp(heightFactor, 0.0, 1.0);
        density += overhangNoise * params.uOverhangAmp * mountainness * heightFactor;
    }
    
    return density;
}

// ============================================================================
// Helper Functions
// ============================================================================

int generateHeight(int wx, int wz) {
    // M2: Use new height generation
    float h = getHeight(vec2(wx, wz));
    int height = int(h);
    return clamp(height, 0, CHUNK_Y_SIZE - 1);
}

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

// ============================================================================
// Block Type Generation
// ============================================================================

uint generateBlockType(int height, int y, int wx, int wz) {
    if (y > height) {
        if (y <= WATER_LEVEL) {
            return BD_WATER;
        }
        return BD_AIR;
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
            return BD_AIR;
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
                    return BD_WATER;
                }
                
                // Dry cave under land
                return BD_AIR;
            }
            return BD_AIR;
        }
    }
    
    // Surface block
    if (y == height) {
        if (y < WATER_LEVEL) {
            // Underwater surface (ocean floor or shallow water)
            if (y >= WATER_LEVEL - 1) return BD_SHORELINE; // 1 block below water
            return BD_UNDERWATER_SUBSURFACE; // Deep underwater (bedrock)
        } else {
            // Above water surface (land)
            if (y <= WATER_LEVEL + int(params.uShorelineRange)) {
                // Shoreline check
                if (isNearWater(wx, wz)) return BD_SHORELINE;
            }
            return BD_SURFACE;
        }
    }
    
    // Sub-surface
    int depth = height - y;
    if (depth <= int(params.uSubsurfaceDepth)) return BD_SUBSURFACE;
    return BD_DEEP_SUBSURFACE;
}

#endif // TERRAIN_GENERATION_GLSL
// END terrain-generation.glsl

layout(local_size_x = 16, local_size_y = 1, local_size_z = 16) in;

const uint FACE_POS_X = 0u; const uint FACE_NEG_X = 1u; const uint FACE_POS_Y = 2u; const uint FACE_NEG_Y = 3u; const uint FACE_POS_Z = 4u; const uint FACE_NEG_Z = 5u;
uniform uint uChunkCount; uniform uint uWorldChunksXZ; uniform uint uVertexRegionOffset; uniform uint uIndexRegionOffset;

layout(std430, binding = 9) readonly buffer ChunkIndices { uint chunkIndices[]; };
layout(std430, binding = 0) readonly buffer VoxelData { uint voxelData[]; };
layout(std430, binding = 1) readonly buffer VisibilityMask { uint visibilityMask[]; };
layout(std430, binding = 3) readonly buffer BaseOffsets { uint baseOffsets[]; };
layout(std430, binding = 4) writeonly buffer CompactVertices { uint compactVertices[]; };
layout(std430, binding = 7) writeonly buffer CompactIndices { uint compactIndices[]; };
layout(std430, binding = 8) buffer PerChunkEmit { uint perChunkEmit[]; };
layout(std430, binding = 12) readonly buffer OpaqueCounts { uint opaqueCounts[]; };
layout(std430, binding = 14) buffer WaterEmit { uint waterEmit[]; };

layout(std430, binding = 20) readonly buffer HeightCacheWordsBuffer { uint heightCacheWords[]; };
layout(std430, binding = 21) readonly buffer HeightCacheSlotsBuffer { int heightCacheSlots[]; };

struct WorldEdit { int x, y, z, type; };
layout(std430, binding = 13) readonly buffer WorldEdits { int editCount; int _pad1; int _pad2; int _pad3; WorldEdit edits[]; };

shared uint s_OpaqueCount; shared uint s_WaterCount; shared uint s_OpaqueBase; shared uint s_WaterBase;

const uint HEIGHT_CACHE_HEIGHT_MASK = 0x1FFu;
const uint HEIGHT_CACHE_FLAG_WATER = 1u << 9;
const uint HEIGHT_CACHE_FLAG_EDITED = 1u << 10;
const uint HEIGHT_CACHE_INVALID = 0xFFFFFFFFu;
const int HEIGHT_CACHE_WORDS_PER_CHUNK = (CHUNK_SIDE_SIZE * CHUNK_SIDE_SIZE + 1) / 2;

uint sampleHeightCache(uint chunkIndex, int lx, int lz)
{
    if (chunkIndex >= uint(heightCacheSlots.length()))
        return HEIGHT_CACHE_INVALID;

    int slot = heightCacheSlots[chunkIndex];
    if (slot < 0)
        return HEIGHT_CACHE_INVALID;

    if (lx < 0 || lx >= CHUNK_SIDE_SIZE || lz < 0 || lz >= CHUNK_SIDE_SIZE)
        return HEIGHT_CACHE_INVALID;

    int column = lz * CHUNK_SIDE_SIZE + lx;
    int wordIndex = slot * HEIGHT_CACHE_WORDS_PER_CHUNK + (column >> 1);
    if (wordIndex < 0 || wordIndex >= heightCacheWords.length())
        return HEIGHT_CACHE_INVALID;

    uint word = heightCacheWords[wordIndex];
    return (column & 1) == 0 ? (word & 0xFFFFu) : (word >> 16);
}

uint voxelFromCachedHeight(uint packedValue, int sampleY)
{
    if ((packedValue & HEIGHT_CACHE_FLAG_EDITED) != 0u)
        return 0u;

    uint topExclusive = packedValue & HEIGHT_CACHE_HEIGHT_MASK;
    bool hasWater = (packedValue & HEIGHT_CACHE_FLAG_WATER) != 0u;

    if (sampleY >= int(topExclusive))
    {
        if (hasWater && sampleY <= WATER_LEVEL)
            return BD_WATER;
        return BD_AIR;
    }

    return BD_SUBSURFACE;
}

int getBlockFromEdits(int wx, int wy, int wz) { 
    for (int i=0;i<editCount;i++){ 
        if(edits[i].x==wx && edits[i].y==wy && edits[i].z==wz) return edits[i].type; 
    } 
    return -1; 
}

void getFaceCorners(uint face, vec3 pos, out vec3 corners[4]) {
    if (face==FACE_POS_X){ corners[0]=pos+vec3(1,0,0); corners[1]=pos+vec3(1,1,0); corners[2]=pos+vec3(1,1,1); corners[3]=pos+vec3(1,0,1);} 
    else if (face==FACE_NEG_X){ corners[0]=pos+vec3(0,0,1); corners[1]=pos+vec3(0,1,1); corners[2]=pos+vec3(0,1,0); corners[3]=pos+vec3(0,0,0);} 
    else if (face==FACE_POS_Y){ corners[0]=pos+vec3(0,1,0); corners[1]=pos+vec3(0,1,1); corners[2]=pos+vec3(1,1,1); corners[3]=pos+vec3(1,1,0);} 
    else if (face==FACE_NEG_Y){ corners[0]=pos+vec3(0,0,1); corners[1]=pos+vec3(0,0,0); corners[2]=pos+vec3(1,0,0); corners[3]=pos+vec3(1,0,1);} 
    else if (face==FACE_POS_Z){ corners[0]=pos+vec3(1,0,1); corners[1]=pos+vec3(1,1,1); corners[2]=pos+vec3(0,1,1); corners[3]=pos+vec3(0,0,1);} 
    else { corners[0]=pos+vec3(0,0,0); corners[1]=pos+vec3(0,1,0); corners[2]=pos+vec3(1,1,0); corners[3]=pos+vec3(1,0,0);} 
}

uint mapAO(float ao){ 
    // Adjusted thresholds for more subtle AO that doesn't stretch too far
    // Higher thresholds = less aggressive darkening
    if(ao > 0.95) return 4u;  // Nearly full brightness (was 0.9)
    if(ao > 0.85) return 3u;  // Light shade (was 0.7)
    if(ao > 0.65) return 2u;  // Medium shade (was 0.5)
    if(ao > 0.45) return 1u;  // Dark shade (was 0.35)
    return 0u;  // Darkest (corners only)
}

void writeVertex(uint vertexIdx, ivec3 localPos, uint aoIdx, uint face, uint corner, uint blockType, uint lightData){
    uint x=uint(localPos.x)&0x1Fu; 
    uint y=uint(localPos.y)&0x1FFu; 
    uint z=uint(localPos.z)&0x1Fu; 
    uint packed1=x|(y<<5)|(z<<14)|((face&0x7u)<<19)|((aoIdx&0x7u)<<22)|((corner&0x3u)<<25);
    uint packed2=(blockType&0xFFu) | (lightData << 8);
    uint baseIdx=vertexIdx*2u; 
    compactVertices[baseIdx]=packed1; 
    compactVertices[baseIdx+1u]=packed2;
}

bool isSolid(ivec3 pos, uint chunkIdx, vec3 chunkWorldOffset) {
    if (pos.y < 0) return true;
    if (pos.y >= CHUNK_Y_SIZE) return false;

    if (pos.x >= 0 && pos.x < CHUNK_SIDE_SIZE && pos.z >= 0 && pos.z < CHUNK_SIDE_SIZE) {
        uint voxelIdx = chunkIdx * uint(CHUNK_VOXEL_COUNT) + uint(pos.x) + uint(pos.z) * uint(CHUNK_SIDE_SIZE) + uint(pos.y) * uint(CHUNK_SIDE_SIZE_SQUARED);
        uint voxel = voxelData[voxelIdx];
        return (voxel & 0xFFu) > BD_WATER;
    }

    uint currentGlobalIdx = chunkIndices[chunkIdx];
    int chunkX = int(currentGlobalIdx % uWorldChunksXZ);
    int chunkZ = int(currentGlobalIdx / uWorldChunksXZ);
    int nChunkX = chunkX;
    int nChunkZ = chunkZ;
    int localX = pos.x;
    int localZ = pos.z;

    if (pos.x < 0) { nChunkX--; localX += CHUNK_SIDE_SIZE; }
    else if (pos.x >= CHUNK_SIDE_SIZE) { nChunkX++; localX -= CHUNK_SIDE_SIZE; }
    if (pos.z < 0) { nChunkZ--; localZ += CHUNK_SIDE_SIZE; }
    else if (pos.z >= CHUNK_SIDE_SIZE) { nChunkZ++; localZ -= CHUNK_SIDE_SIZE; }

    if (nChunkX >= 0 && nChunkX < int(uWorldChunksXZ) && nChunkZ >= 0 && nChunkZ < int(uWorldChunksXZ)) {
        uint nGlobalIdx = uint(nChunkX + nChunkZ * int(uWorldChunksXZ));
        for (uint i = 0u; i < uChunkCount; i++) {
            if (chunkIndices[i] == nGlobalIdx) {
                if (localX >= 0 && localX < CHUNK_SIDE_SIZE && localZ >= 0 && localZ < CHUNK_SIDE_SIZE) {
                    uint voxelIdx = i * uint(CHUNK_VOXEL_COUNT) + uint(localX) + uint(localZ) * uint(CHUNK_SIDE_SIZE) + uint(pos.y) * uint(CHUNK_SIDE_SIZE_SQUARED);
                    uint voxel = voxelData[voxelIdx];
                    return (voxel & 0xFFu) > BD_WATER;
                }
                break;
            }
        }

        uint packedHeight = sampleHeightCache(nGlobalIdx, localX, localZ);
        if (packedHeight != HEIGHT_CACHE_INVALID) {
            uint descriptor = voxelFromCachedHeight(packedHeight, pos.y);
            if (descriptor != 0u)
                return descriptor > BD_WATER;
        }
    }

    // Procedural fallback: MUST use generateBlockType() to match terrain generation exactly.
    // See docs/terrain/AO_CHUNK_BORDER_FIX.md for detailed explanation.
    int wx = int(chunkWorldOffset.x) + pos.x;
    int wz = int(chunkWorldOffset.z) + pos.z;
    int wy = pos.y;
    int editType = getBlockFromEdits(wx, wy, wz);
    if (editType != -1) return editType > int(BD_WATER);

    int height = generateHeight(wx, wz);
    uint blockType = generateBlockType(height, wy, wx, wz);
    return blockType > BD_WATER;
}

uint getLight(ivec3 pos, uint chunkIdx, vec3 chunkWorldOffset) {
    if (pos.y >= CHUNK_Y_SIZE) return 0x0Fu; // Full Sky Light above world
    if (pos.y < 0) return 0u; // No light below world

    if (pos.x >= 0 && pos.x < CHUNK_SIDE_SIZE && pos.z >= 0 && pos.z < CHUNK_SIDE_SIZE) {
        uint voxelIdx = chunkIdx * uint(CHUNK_VOXEL_COUNT) + uint(pos.x) + uint(pos.z) * uint(CHUNK_SIDE_SIZE) + uint(pos.y) * uint(CHUNK_SIDE_SIZE_SQUARED);
        uint voxel = voxelData[voxelIdx];
        return (voxel >> 16) & 0xFFu;
    }

    uint currentGlobalIdx = chunkIndices[chunkIdx];
    int chunkX = int(currentGlobalIdx % uWorldChunksXZ);
    int chunkZ = int(currentGlobalIdx / uWorldChunksXZ);
    int nChunkX = chunkX;
    int nChunkZ = chunkZ;
    int localX = pos.x;
    int localZ = pos.z;

    if (pos.x < 0) { nChunkX--; localX += CHUNK_SIDE_SIZE; }
    else if (pos.x >= CHUNK_SIDE_SIZE) { nChunkX++; localX -= CHUNK_SIDE_SIZE; }
    if (pos.z < 0) { nChunkZ--; localZ += CHUNK_SIDE_SIZE; }
    else if (pos.z >= CHUNK_SIDE_SIZE) { nChunkZ++; localZ -= CHUNK_SIDE_SIZE; }

    if (nChunkX >= 0 && nChunkX < int(uWorldChunksXZ) && nChunkZ >= 0 && nChunkZ < int(uWorldChunksXZ)) {
        uint nGlobalIdx = uint(nChunkX + nChunkZ * int(uWorldChunksXZ));
        for (uint i = 0u; i < uChunkCount; i++) {
            if (chunkIndices[i] == nGlobalIdx) {
                if (localX >= 0 && localX < CHUNK_SIDE_SIZE && localZ >= 0 && localZ < CHUNK_SIDE_SIZE) {
                    uint voxelIdx = i * uint(CHUNK_VOXEL_COUNT) + uint(localX) + uint(localZ) * uint(CHUNK_SIDE_SIZE) + uint(pos.y) * uint(CHUNK_SIDE_SIZE_SQUARED);
                    uint voxel = voxelData[voxelIdx];
                    return (voxel >> 16) & 0xFFu;
                }
                break;
            }
        }
    }

    // Fallback for unloaded chunks:
    // If the neighbor is not in the batch, we must guess its light level.
    // We use the procedural generation to check if it's solid.
    // If it's Solid -> Light 0.
    // If it's Air/Water -> Assume Sky Light 15 (Daylight).
    // This prevents "Dark Walls" on chunk boundaries (cliffs).
    // Side effect: Cave endings at chunk borders will appear bright (light bleeding),
    // but this is preferable to dark artifacts on the surface.
    
    // Re-use isSolid logic (which handles edits + procedural)
    // We need to reconstruct the world position for isSolid
    // chunkWorldOffset is passed in.
    // But isSolid takes local pos. We have local pos (which might be -1 or 16).
    // isSolid handles out-of-bounds local pos by doing its own neighbor lookup.
    // But we want the PROCEDURAL fallback of isSolid.
    
    // Let's just call isSolid directly. It has the logic.
    if (isSolid(pos, chunkIdx, chunkWorldOffset)) {
        return 0u; // Solid blocks are dark
    }
    
    // It's transparent (Air/Water).
    // Check if it's above the terrain height (Sky) or below (Cave/Overhang).
    int wx = int(chunkWorldOffset.x) + pos.x;
    int wz = int(chunkWorldOffset.z) + pos.z;
    int height = generateHeight(wx, wz);
    
    if (pos.y > height) return 0x0Fu; // Above terrain -> Sky Light 15
    
    // Below terrain (Cave or Overhang shadow) -> Dark
    return 0u;
}

float calculateAO(uint face, uint corner, uint chunkIdx, vec3 chunkWorldOffset, ivec3 p) {
    ivec3 o1,o2,o3;
    
    if (face==FACE_POS_Y){ 
        if(corner==0u){o1=ivec3(-1,1,0);o2=ivec3(0,1,-1);o3=ivec3(-1,1,-1);} 
        else if(corner==1u){o1=ivec3(-1,1,0);o2=ivec3(0,1,1);o3=ivec3(-1,1,1);} 
        else if(corner==2u){o1=ivec3(1,1,0);o2=ivec3(0,1,1);o3=ivec3(1,1,1);} 
        else {o1=ivec3(1,1,0);o2=ivec3(0,1,-1);o3=ivec3(1,1,-1);} 
    }
    else if (face==FACE_NEG_Y){ 
        if(corner==0u){o1=ivec3(-1,-1,0);o2=ivec3(0,-1,1);o3=ivec3(-1,-1,1);} 
        else if(corner==1u){o1=ivec3(-1,-1,0);o2=ivec3(0,-1,-1);o3=ivec3(-1,-1,-1);} 
        else if(corner==2u){o1=ivec3(1,-1,0);o2=ivec3(0,-1,-1);o3=ivec3(1,-1,-1);} 
        else {o1=ivec3(1,-1,0);o2=ivec3(0,-1,1);o3=ivec3(1,-1,1);} 
    }
    else if (face==FACE_POS_Z){ 
        if(corner==0u){o1=ivec3(1,0,1);o2=ivec3(0,-1,1);o3=ivec3(1,-1,1);} 
        else if(corner==1u){o1=ivec3(1,0,1);o2=ivec3(0,1,1);o3=ivec3(1,1,1);} 
        else if(corner==2u){o1=ivec3(-1,0,1);o2=ivec3(0,1,1);o3=ivec3(-1,1,1);} 
        else {o1=ivec3(-1,0,1);o2=ivec3(0,-1,1);o3=ivec3(-1,-1,1);} 
    }
    else if (face==FACE_NEG_Z){ 
        if(corner==0u){o1=ivec3(-1,0,-1);o2=ivec3(0,-1,-1);o3=ivec3(-1,-1,-1);} 
        else if(corner==1u){o1=ivec3(-1,0,-1);o2=ivec3(0,1,-1);o3=ivec3(-1,1,-1);} 
        else if(corner==2u){o1=ivec3(1,0,-1);o2=ivec3(0,1,-1);o3=ivec3(1,1,-1);} 
        else {o1=ivec3(1,0,-1);o2=ivec3(0,-1,-1);o3=ivec3(1,-1,-1);} 
    }
    else if (face==FACE_POS_X){ 
        if(corner==0u){o1=ivec3(1,-1,0);o2=ivec3(1,0,-1);o3=ivec3(1,-1,-1);} 
        else if(corner==1u){o1=ivec3(1,1,0);o2=ivec3(1,0,-1);o3=ivec3(1,1,-1);} 
        else if(corner==2u){o1=ivec3(1,1,0);o2=ivec3(1,0,1);o3=ivec3(1,1,1);} 
        else {o1=ivec3(1,-1,0);o2=ivec3(1,0,1);o3=ivec3(1,-1,1);} 
    }
    else { // FACE_NEG_X
        if(corner==0u){o1=ivec3(-1,-1,0);o2=ivec3(-1,0,1);o3=ivec3(-1,-1,1);} 
        else if(corner==1u){o1=ivec3(-1,1,0);o2=ivec3(-1,0,1);o3=ivec3(-1,1,1);} 
        else if(corner==2u){o1=ivec3(-1,1,0);o2=ivec3(-1,0,-1);o3=ivec3(-1,1,-1);} 
        else {o1=ivec3(-1,-1,0);o2=ivec3(-1,0,-1);o3=ivec3(-1,-1,-1);} 
    }
    
    // Use isSolid (with procedural fallback) for correct cross-chunk AO
    bool s1=isSolid(p+o1,chunkIdx,chunkWorldOffset); 
    bool s2=isSolid(p+o2,chunkIdx,chunkWorldOffset); 
    bool s3=isSolid(p+o3,chunkIdx,chunkWorldOffset);
    
    if(s1 && s2) return 0.33; 
    int count=(s1?1:0)+(s2?1:0)+(s3?1:0); 
    if(count==0) return 1.0; 
    if(count==1) return 0.8; 
    if(count==2) return 0.6; 
    return 0.4;
}

void main(){ 
    uint chunkIdx=gl_WorkGroupID.x; 
    if(chunkIdx>=uChunkCount) return; 
    
    uint actualChunkIdx=chunkIndices[chunkIdx]; 
    uint chunkX=actualChunkIdx%uWorldChunksXZ; 
    uint chunkZ=actualChunkIdx/uWorldChunksXZ; 
    vec3 chunkWorldOffset=vec3(float(chunkX*CHUNK_SIDE_SIZE),0.0,float(chunkZ*CHUNK_SIDE_SIZE));
    
    uint workGroupY=gl_WorkGroupID.y; 
    if(workGroupY>=uint(CHUNK_Y_SIZE)) return; 
    
    int lx=int(gl_LocalInvocationID.x); 
    int lz=int(gl_LocalInvocationID.z); 
    int ly=int(workGroupY);
    
    if(gl_LocalInvocationIndex==0u){ 
        s_OpaqueCount=0u; 
        s_WaterCount=0u; 
    } 
    barrier();
    
    uint voxelIdx=chunkIdx*uint(CHUNK_VOXEL_COUNT)+uint(lx)+uint(lz)*uint(CHUNK_SIDE_SIZE)+uint(ly)*uint(CHUNK_SIDE_SIZE_SQUARED); 
    uint voxel=voxelData[voxelIdx]; 
    uint blockDescriptor=voxel&0xFFu; 
    uint mask=getPackedVisMask(voxelIdx,visibilityMask); 
    
    bool hasFaces=(mask!=0u && blockDescriptor!=0u); 
    bool isWater=(blockDescriptor==BD_WATER); 
    uint faceCount=0u; 
    
    if(hasFaces){ 
        faceCount=uint(bitCount(mask&0x3Fu)); 
        if(faceCount>0u){ 
            if(isWater) atomicAdd(s_WaterCount,faceCount); 
            else atomicAdd(s_OpaqueCount,faceCount);
        } 
    } 
    
    barrier(); 
    
    if(gl_LocalInvocationIndex==0u){ 
        if(s_OpaqueCount>0u) s_OpaqueBase=atomicAdd(perChunkEmit[chunkIdx],s_OpaqueCount); 
        if(s_WaterCount>0u) s_WaterBase=atomicAdd(waterEmit[chunkIdx],s_WaterCount);
    } 
    
    barrier(); 
    
    if(!hasFaces||faceCount==0u) return; 
    
    uint localFaceIdx; 
    if(isWater){ 
        uint opaqueChunkCount=opaqueCounts[chunkIdx]; 
        localFaceIdx=opaqueChunkCount+atomicAdd(s_WaterBase,faceCount);
    } else { 
        localFaceIdx=atomicAdd(s_OpaqueBase,faceCount); 
        uint opaqueChunkCount=opaqueCounts[chunkIdx]; 
        if(localFaceIdx>=opaqueChunkCount) return; 
    }
    
    uint vertexChunkBase=baseOffsets[chunkIdx]; 
    uint indexChunkBase=(vertexChunkBase/4u)*6u; 
    vec3 voxelPos=chunkWorldOffset+vec3(float(lx),float(ly),float(lz)); 
    uint currentFaceOffset=0u; 
    
    for(uint face=0u; face<6u; face++){ 
        uint faceBit=1u<<face; 
        if((mask&faceBit)==0u) continue; 
        
        uint finalFaceIdx=localFaceIdx+currentFaceOffset; 
        currentFaceOffset++; 
        uint vertBaseAbs=uVertexRegionOffset+vertexChunkBase+finalFaceIdx*4u; 
        uint idxWriteBase=uIndexRegionOffset+indexChunkBase+finalFaceIdx*6u; 
        
        vec3 corners[4]; 
        getFaceCorners(face,voxelPos,corners); 
        
        // Use voxel position (lx, ly, lz) as the base for AO calculation, not corner position
        ivec3 voxelLocalPos = ivec3(lx, ly, lz);
        
        // Extract light data from NEIGHBOR (the air block in front of the face)
        // We must use the light level of the space the face is facing into, not the solid block itself (which is dark)
        ivec3 faceOffset;
        if (face == FACE_POS_X) faceOffset = ivec3(1, 0, 0);
        else if (face == FACE_NEG_X) faceOffset = ivec3(-1, 0, 0);
        else if (face == FACE_POS_Y) faceOffset = ivec3(0, 1, 0);
        else if (face == FACE_NEG_Y) faceOffset = ivec3(0, -1, 0);
        else if (face == FACE_POS_Z) faceOffset = ivec3(0, 0, 1);
        else faceOffset = ivec3(0, 0, -1);
        
        uint lightData = getLight(voxelLocalPos + faceOffset, chunkIdx, chunkWorldOffset);
        
        for(uint v=0u; v<4u; v++){
            vec3 worldPos=corners[v];
            vec3 localPosFloat=worldPos-chunkWorldOffset;
            ivec3 localPos=ivec3(round(localPosFloat));
            // Calculate AO from the voxel position, not the corner position
            float ao=calculateAO(face,v,chunkIdx,chunkWorldOffset,voxelLocalPos);
            uint aoIdx=mapAO(ao);
            writeVertex(vertBaseAbs+v,localPos,aoIdx,face,v,blockDescriptor,lightData);
        }
        
        uint localVertBase=finalFaceIdx*4u; 
        compactIndices[idxWriteBase+0u]=localVertBase+0u; 
        compactIndices[idxWriteBase+1u]=localVertBase+1u; 
        compactIndices[idxWriteBase+2u]=localVertBase+2u; 
        compactIndices[idxWriteBase+3u]=localVertBase+2u; 
        compactIndices[idxWriteBase+4u]=localVertBase+3u; 
        compactIndices[idxWriteBase+5u]=localVertBase+0u; 
    }
}
