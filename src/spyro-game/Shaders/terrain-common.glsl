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
// - terrain-climate.glsl: Noise generation, macro fields, and biome selection helpers
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
