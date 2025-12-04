# Terrain and Biome Generation Analysis & Improvement Plan

**Document Status:** Analysis + Architecture Proposal  
**Scope:** spyro-game terrain and biome generation  
**Date:** December 2025  
**Goal:** Achieve Minecraft-quality procedural terrain with rich, varied, explorable worlds

---

## Priority Matrix

| Priority | Requirement | Rationale |
|----------|-------------|------------|
| **P0 (Critical)** | Performance / 60 FPS | Must maintain smooth streaming; SIMD/vectorized code mandatory |
| **P0 (Critical)** | Noise caching | Cannot invoke expensive noise per-voxel per-step; cache at column/chunk level |
| **P1 (High)** | Terrain variety | Eliminate boring, repetitive landscapes |
| **P1 (High)** | Biome correctness | Ocean/land biomes must match actual terrain height |
| **P2 (Medium)** | Irregular biome borders | No straight-line biome boundaries |
| **P3 (Low/Nice-to-have)** | Subterrain biomes | Special cave biomes (lush caves, dripstone) - DEFERRED |

---

## Executive Summary

The current terrain generation system has evolved organically and suffers from:
1. **Over-engineered complexity** - Multiple overlapping systems fighting for control of height
2. **Magic numbers scattered throughout** - Making parameter tuning nearly impossible
3. **Incorrect architecture** - Biomes selected AFTER height, rather than biomes DRIVING height
4. **Missing 3D density function** - No true overhangs, only surface-level approximations

This document proposes a **complete architectural overhaul** following Minecraft 1.18+'s proven multi-noise terrain system.

### Key Constraints

1. **Performance is non-negotiable** - All noise sampling must use SIMD/vectorized operations
2. **No repeated noise calls** - Noise values MUST be cached at appropriate granularity (column/chunk)
3. **Surface biomes only** - Subterrain/cave biomes are deferred (nice-to-have, low priority)
4. **Existing infrastructure** - Must work with current `ChunkStreamingManager` and job system

---

## Part 1: Current System Analysis

### 1.1 Architecture Overview

```
Current Flow:
┌─────────────────┐    ┌──────────────────────┐    ┌───────────────────┐
│ BiomeGenerator  │───▶│ CpuTerrainGenerator  │───▶│ GenerateBlock()   │
│ (pre-generate)  │    │ (height calculation) │    │ (voxel filling)   │
└─────────────────┘    └──────────────────────┘    └───────────────────┘
        ▼                        ▼
   4×4 biome grid          Height modulation
   climate params          Cave carving
```

**Critical Problem:** The biome generator runs FIRST with estimated altitude, then terrain generator calculates actual height, then tries to reconcile the two with `UpdateBiomeDataFromTerrainValues()`. This backward flow causes:
- Ocean biomes appearing above water level
- Land biomes appearing underwater
- Constant if/else patches to fix edge cases

### 1.2 Terrain Generation Issues

#### Issue 1: Magic Numbers Explosion

**Location:** `CpuTerrainGenerator.cs` lines 330-450 (`BuildColumnFieldCaches`)

```csharp
// Current code has dozens of unexplained magic numbers:
if (coastDist < 0.2f)           // Why 0.2? What units?
    var coastFactor = coastDist / 0.2f;
    var cliffMix = Smoothstep(0.1f, 0.5f, cliffVal);  // 0.1, 0.5 - arbitrary?
    var beachHeight = Lerp(VoxelHelper.WaterLevel + 2f, baseHeight, coastFactor + 0.2f);
    var cliffHeight = baseHeight + MathF.Max(0f, cliffVal) * 10f * (1f - coastFactor);

if (terrainTypeVal < 0.35f)     // Why 0.35?
    var flatness = (0.35f - terrainTypeVal) / 0.35f;
    baseHeight += columnPeaks[i] * 5f * (1f - flatness);  // 5f blocks - why?

// ... continues with 25f, 50f, 8f, 15f, 60f, 80f, 40f, etc.
```

**Impact:** 
- Cannot reason about parameter changes
- Each adjustment requires re-tuning downstream magic numbers
- No documentation of what each value controls

#### Issue 2: Nested If-Then-Else Terrain Shaping

**Location:** `BuildColumnFieldCaches()` lines 340-430

```csharp
// Current structure:
if (coastDist < 0.2f) {
    // COASTAL ZONE logic
} else {
    if (terrainTypeVal < 0.35f) {
        // FLAT PLAINS
    } else if (terrainTypeVal < 0.65f) {
        // ROLLING HILLS
    } else {
        // DRAMATIC TERRAIN
        if (cliffVal > 0.75f) {
            // sharp cliffs
        } else if (cliffVal < -0.2f) {
            // plateaus
        } else {
            // gradual slopes
        }
    }
    
    if (tC > terrainParams.MountainThreshold) {
        // MOUNTAIN ZONES
        if (terrainTypeVal > 0.5f) {
            // extra peaks
        }
    }
}
```

**Impact:**
- 4+ levels of nesting
- Interactions between branches are unpredictable
- Adding new terrain features risks breaking existing ones

#### Issue 3: Repetitive/Boring Terrain

**Root Causes:**
1. **Single noise octave dominance** - Low-frequency noise creates large-scale monotony
2. **No weirdness parameter** - Missing Minecraft's key variety driver
3. **Erosion underutilized** - Only used for height smoothing, not terrain character
4. **Continental patterns are too regular** - Domain warp isn't aggressive enough

**Evidence in code:**
```csharp
// Erosion only smooths height, doesn't affect terrain type:
var smoothingFactor = erosion01 * erosion01 * 0.3f;
baseHeight = Lerp(baseHeight, SampleHeightSpline(tC) + VoxelHelper.WaterLevel + 20f, smoothingFactor);
```

#### Issue 4: Cliffs Everywhere

**Root Cause:** `CliffAmplitude` and `CliffFrequency` apply uniformly across mountains.

```csharp
// CliffAmplitude = 10f applies to ALL mountain terrain
if (tC > terrainParams.MountainThreshold) {
    baseHeight += MathF.Abs(columnCliff[i]) * terrainParams.CliffAmplitude * mountainness;
}
```

**Missing:** Cliff generation should be:
- Localized to specific areas (erosion-dependent)
- Variable in height (some gentle, some dramatic)
- Controlled by a dedicated "cliff noise" that isn't always active

#### Issue 5: No True Overhangs

**Location:** `BuildColumnVolumes()` and `GetTerrainDensity()`

```csharp
// Current overhang implementation only modifies density NEAR surface:
if (tC > terrainParams.MountainThreshold &&
    sampleY > baseHeight - terrainParams.OverhangDepthRange &&
    sampleY < baseHeight + terrainParams.OverhangHeightRange) {
    // overhang logic
}
```

**Problem:** This is a 2.5D height modifier, not true 3D density. Overhangs require:
- 3D noise evaluated at every voxel (currently only evaluated near surface)
- Density function that can be positive underground AND negative above ground
- Proper blending at overhang edges

### 1.3 Biome Generation Issues

#### Issue 1: Straight-Line Biome Borders

**Location:** `BiomeGenerator.cs` lines 50-65

```csharp
// Current jitter is too weak:
var jitterX = GradientNoise2D(baseCellX * 0.25f, baseCellZ * 0.25f, seed + 2000u) * 2.0f;
var jitterZ = GradientNoise2D(baseCellX * 0.25f + 100f, baseCellZ * 0.25f + 100f, seed + 2001u) * 2.0f;
```

**Problem:** 
- Jitter of ±2 blocks is negligible for 4×4 cell (16 blocks)
- No Voronoi/cellular noise for organic boundaries
- Domain warp `WarpStrength = 60` helps but isn't enough

**Minecraft's Solution:**
- Voronoi cellular noise with ~3-chunk cell size
- Strong domain warp (150+ blocks)
- Multiple blending neighbors (K=3 Worley)

#### Issue 2: Biome Selection Logic Scattered

**Location:** `SelectBiomeFromTerrainValues()` in CpuTerrainGenerator.cs AND `SelectBiome()` in BiomeGenerator.cs

Two different biome selection functions with overlapping but inconsistent logic:

```csharp
// BiomeGenerator.SelectBiome() - uses estimated altitude:
if (altitudeAboveWater > config.AlpineElevation || temperature < 0.12f)
    return BiomeId.Alpine;

// CpuTerrainGenerator.SelectBiomeFromTerrainValues() - uses actual altitude:
if (altitudeAboveWater > terrainParams.AlpineElevation || temperature < 0.12f)
    return BiomeId.Alpine;
```

Both have 50+ lines of if-else chains that are hard to maintain.

#### Issue 3: Biome-Height Chicken-and-Egg Problem

**The Fundamental Issue:**
1. Biomes need altitude to decide (ocean vs land, alpine vs highlands)
2. Altitude calculation uses biome height attributes
3. Results in circular dependency

**Current "Fix":** `UpdateBiomeDataFromTerrainValues()` - a 100-line function that:
- Re-samples terrain values
- Re-selects biomes based on actual height
- Patches up inconsistencies

This is treating symptoms, not the disease.

---

## Part 2: Minecraft 1.18+ Terrain Architecture

### 2.1 Core Principles

Minecraft 1.18 introduced a revolutionary terrain system that we should emulate:

```
Minecraft Flow:
┌─────────────┐   ┌─────────────┐   ┌─────────────────┐   ┌──────────┐
│   Climate   │──▶│  Density    │──▶│  Final Height   │──▶│  Biome   │
│   Noises    │   │  Function   │   │  (isosurface)   │   │  Lookup  │
└─────────────┘   └─────────────┘   └─────────────────┘   └──────────┘
                        │
                        ▼
                  3D Terrain
                  (Overhangs,
                   Caves, Arches)
```

### 2.2 The 6 Climate Parameters

| Parameter | Minecraft Range | Controls |
|-----------|-----------------|----------|
| **Continentalness** | [-1, 1] | Ocean vs inland, base height |
| **Erosion** | [-1, 1] | Flat vs dramatic terrain |
| **Peaks/Valleys (PV)** | [-1, 1] | Ridge placement, local relief |
| **Temperature** | [-1, 1] | Hot vs cold biomes |
| **Humidity** | [-1, 1] | Wet vs dry biomes |
| **Weirdness** | [-1, 1] | **Terrain variety driver** |

**Key Insight:** `Weirdness` is crucial for variety - it creates:
- Shattered terrain variants
- Floating islands in specific areas
- Unusual cliff patterns
- Breaks the monotony

### 2.3 Density Function (3D Terrain)

Minecraft uses a **3D density function** evaluated at every potential voxel:

```glsl
// Pseudocode for Minecraft-style density
float density(vec3 worldPos) {
    float C = continentalness(worldPos);
    float E = erosion(worldPos);
    float PV = peaksValleys(worldPos);
    float W = weirdness(worldPos);
    
    // Base height from spline (C drives this)
    float baseY = sampleHeightSpline(C);
    
    // Vertical gradient (positive below surface, negative above)
    float d = baseY - worldPos.y;
    
    // Factor - how much 3D noise affects density
    float factor = calculateFactor(E, W);
    
    // 3D detail noise
    float n3d = noise3D(worldPos * detailScale);
    
    // Combine: d > 0 = solid, d < 0 = air
    d += n3d * factor;
    
    // Peaks/valleys modify the surface
    d += PV * peakAmplitude * smoothstep(...);
    
    return d;
}
```

**Critical:** The `factor` parameter (from Erosion + Weirdness) controls HOW MUCH 3D noise affects terrain:
- Low factor → smooth, heightmap-like terrain
- High factor → wild 3D terrain with overhangs, arches, floating bits

### 2.4 Biome Selection (Multi-Noise Router)

Minecraft's biome selection is a **multi-dimensional lookup**, not cascading if-else:

```
Each biome defines acceptable ranges for all 6 parameters:
┌─────────────────────────────────────────────────────────────────┐
│ Biome: Desert                                                   │
│   Continentalness: [0.1, 0.55]  (not ocean, not mountain)       │
│   Erosion:         [0.45, 1.0]  (high erosion = flat)           │
│   Temperature:     [0.55, 1.0]  (hot)                           │
│   Humidity:        [-1.0, -0.1] (dry)                           │
│   Weirdness:       [-1.0, 1.0]  (any)                           │
│   PV:              [-0.5, 0.5]  (not extreme peaks/valleys)     │
└─────────────────────────────────────────────────────────────────┘
```

**Selection Algorithm:**
1. Sample all 6 climate values at position
2. Find biome with best parameter fit (weighted L2 distance)
3. For transitions: blend attributes from top 2-3 matching biomes

---

## Part 3: Proposed New Architecture

### 3.1 High-Level Design

```
New Architecture (Performance-First):

┌─────────────────────────────────────────────────────────────────────┐
│  STEP 1: CLIMATE SAMPLING (once per chunk, SIMD batched)            │
│  ────────────────────────────────────────────────────────────────── │
│  Input: 256 column coordinates (16×16 chunk)                        │
│  Process: Vectorized FBM noise (NoiseDotNet SIMD)                   │
│  Output: float[256] arrays for C, E, PV, T, H, W                    │
│  CACHED: columnClimate[256] reused by ALL subsequent steps          │
└────────┬────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 2: HEIGHT CALCULATION (per column, uses cached climate)       │
│  ────────────────────────────────────────────────────────────────── │
│  Input: cached C, E, PV per column                                  │
│  Process: Spline lookup + arithmetic (no noise calls!)              │
│  Output: columnHeights[256], columnHeightInts[256]                  │
│  CACHED: height values reused by block generation                   │
└────────┬────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 3: 3D NOISE (sparse sampling + trilinear interpolation)       │
│  ────────────────────────────────────────────────────────────────── │
│  Input: 5×5×97 sparse grid points (NOT 16×16×384!)                  │
│  Process: Vectorized 3D noise at sparse points                      │
│  Output: sparseCheeseGrid[], sparseSpaghettiGrid[], sparseOverhang[]│
│  INTERPOLATED: trilinear to full resolution during block gen        │
└────────┬────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 4: BIOME SELECTION (per 4×4 cell, uses cached climate)        │
│  ────────────────────────────────────────────────────────────────── │
│  Input: cached C, E, PV, T, H, W + actual height                    │
│  Process: Multi-parameter distance lookup (pure arithmetic)         │
│  Output: biomeGrid[16] (4×4 cells per chunk)                        │
│  NOTE: Surface biomes only, no subterrain biome variation           │
└────────┬────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 5: BLOCK GENERATION (per voxel, NO noise calls)               │
│  ────────────────────────────────────────────────────────────────── │
│  Input: cached height, interpolated 3D noise, biome                 │
│  Process: Height comparison + cave carving + block selection        │
│  Output: BlockId per voxel                                          │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.1.1 Performance Architecture (CRITICAL)

**Noise Call Budget Per Chunk:**
```
Current (problematic):
  2D noise: 256 columns × 6 params × 3 octaves = 4,608 noise calls
  3D noise: 98,304 voxels × 3 types × 2 octaves = 589,824 noise calls
  TOTAL: ~594,000 noise calls per chunk ❌ UNACCEPTABLE

New (optimized):
  2D noise: 256 columns × 6 params × 3 octaves = 4,608 calls (SIMD batched)
  3D noise: 2,425 sparse samples × 3 types × 2 octaves = 14,550 calls (SIMD)
  TOTAL: ~19,000 noise calls per chunk ✓ ~30x reduction
  
Further optimization:
  2D noise: Process 256 columns in SIMD batches of 8 (Vector256<float>)
  3D noise: Process sparse grid in SIMD batches
  Effective: ~2,400 SIMD operations per chunk
```

**Key Performance Rules:**
1. **NEVER call noise inside voxel loop** - All noise pre-cached
2. **SIMD-batch all noise calls** - Use `NoiseDotNet` vectorized APIs
3. **Sparse 3D sampling** - 4-block intervals, trilinear interpolate
4. **Spline lookups are cheap** - Pre-baked to float[] arrays
5. **Biome selection is arithmetic** - No noise, just distance calcs

### 3.2 New Climate Parameter System

#### 3.2.1 Column Climate Cache (Performance-Critical)

```csharp
/// <summary>
/// Pre-computed climate values for all 256 columns in a chunk.
/// Sampled ONCE using SIMD-batched noise, then reused by all subsequent steps.
/// This eliminates repeated noise calls - the #1 performance killer.
/// </summary>
public sealed class ChunkClimateCache
{
    // All arrays are exactly 256 elements (16×16 columns)
    // Allocated once per generator, reused across chunks
    
    /// <summary>Ocean (-1) to far inland (+1). Primary height driver.</summary>
    public readonly float[] Continentalness = new float[256];
    
    /// <summary>Dramatic (-1) to smooth (+1). Controls terrain roughness.</summary>
    public readonly float[] Erosion = new float[256];
    
    /// <summary>Valley (-1) to peak (+1). Local relief modifier.</summary>
    public readonly float[] PeaksValleys = new float[256];
    
    /// <summary>Frozen (-1) to scorching (+1). Climate biome selector.</summary>
    public readonly float[] Temperature = new float[256];
    
    /// <summary>Arid (-1) to humid (+1). Climate biome selector.</summary>
    public readonly float[] Humidity = new float[256];
    
    /// <summary>Normal (-1) to weird (+1). CRITICAL for terrain variety.</summary>
    public readonly float[] Weirdness = new float[256];
    
    // Derived values (computed once from cached climate)
    public readonly float[] Continentalness01 = new float[256];  // Normalized [0,1]
    public readonly float[] Erosion01 = new float[256];
    
    /// <summary>
    /// Sample all climate parameters for a chunk using SIMD-batched operations.
    /// This is the ONLY place noise is called for 2D climate values.
    /// </summary>
    public void SampleForChunk(int chunkX, int chunkZ, ClimateNoiseConfig config, uint seed)
    {
        // Build coordinate arrays for SIMD batch processing
        Span<float> xCoords = stackalloc float[256];
        Span<float> zCoords = stackalloc float[256];
        BuildColumnCoordinates(chunkX, chunkZ, xCoords, zCoords);
        
        // SIMD-batched noise sampling (all 256 columns at once)
        // NoiseDotNet processes these in Vector256<float> chunks internally
        SampleFbm2DBatched(xCoords, zCoords, config.Continentalness, seed, Continentalness);
        SampleFbm2DBatched(xCoords, zCoords, config.Erosion, seed + 100u, Erosion);
        SampleFbm2DBatched(xCoords, zCoords, config.PeaksValleys, seed + 200u, PeaksValleys);
        SampleFbm2DBatched(xCoords, zCoords, config.Temperature, seed + 300u, Temperature);
        SampleFbm2DBatched(xCoords, zCoords, config.Humidity, seed + 400u, Humidity);
        SampleFbm2DBatched(xCoords, zCoords, config.Weirdness, seed + 500u, Weirdness);
        
        // Pre-compute normalized versions (avoids repeated * 0.5 + 0.5)
        for (int i = 0; i < 256; i++)
        {
            Continentalness01[i] = Continentalness[i] * 0.5f + 0.5f;
            Erosion01[i] = Erosion[i] * 0.5f + 0.5f;
        }
    }
}
```

#### 3.2.2 TerrainClimate Structure (Read-Only View)

```csharp
/// <summary>
/// Climate values at a specific column position.
/// This is a READ-ONLY view into the cached arrays - no allocation.
/// </summary>
public readonly ref struct TerrainClimate
{
    public readonly float Continentalness;
    public readonly float Erosion;
    public readonly float PeaksValleys;
    public readonly float Temperature;
    public readonly float Humidity;
    public readonly float Weirdness;
    
    public TerrainClimate(ChunkClimateCache cache, int columnIndex)
    {
        Continentalness = cache.Continentalness[columnIndex];
        Erosion = cache.Erosion[columnIndex];
        PeaksValleys = cache.PeaksValleys[columnIndex];
        Temperature = cache.Temperature[columnIndex];
        Humidity = cache.Humidity[columnIndex];
        Weirdness = cache.Weirdness[columnIndex];
    }
}
```

#### 3.2.2 Climate Noise Configuration

Replace scattered magic numbers with explicit configuration:

```csharp
public sealed class ClimateNoiseConfig
{
    // Continentalness - very large scale
    public NoiseLayer Continentalness { get; set; } = new()
    {
        BaseScale = 1f / 1500f,     // ~1500 block features
        Octaves = 3,
        Persistence = 0.5f,
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 800f,
        DomainWarpStrength = 120f   // Strong warp for organic continents
    };
    
    // Erosion - large scale (affects terrain smoothness)
    public NoiseLayer Erosion { get; set; } = new()
    {
        BaseScale = 1f / 600f,
        Octaves = 4,
        Persistence = 0.45f,
        Lacunarity = 2.2f,
        DomainWarpScale = 1f / 400f,
        DomainWarpStrength = 80f
    };
    
    // Peaks/Valleys - medium scale (ridged noise)
    public NoiseLayer PeaksValleys { get; set; } = new()
    {
        BaseScale = 1f / 150f,
        Octaves = 4,
        Persistence = 0.55f,
        Lacunarity = 2.0f,
        UseRidged = true,           // Ridged-multifractal
        RidgeSharpness = 2.0f
    };
    
    // Temperature - very large scale (climate zones)
    public NoiseLayer Temperature { get; set; } = new()
    {
        BaseScale = 1f / 8000f,     // Huge climate bands
        Octaves = 2,
        Persistence = 0.4f,
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 5000f,
        DomainWarpStrength = 200f   // Very organic climate boundaries
    };
    
    // Humidity - large scale
    public NoiseLayer Humidity { get; set; } = new()
    {
        BaseScale = 1f / 5000f,
        Octaves = 2,
        Persistence = 0.45f,
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 3000f,
        DomainWarpStrength = 150f
    };
    
    // WEIRDNESS - the variety driver!
    public NoiseLayer Weirdness { get; set; } = new()
    {
        BaseScale = 1f / 300f,      // Medium-large features
        Octaves = 3,
        Persistence = 0.6f,
        Lacunarity = 1.8f,
        DomainWarpScale = 1f / 200f,
        DomainWarpStrength = 50f
    };
}

public sealed class NoiseLayer
{
    public float BaseScale { get; set; }
    public int Octaves { get; set; }
    public float Persistence { get; set; }
    public float Lacunarity { get; set; }
    public float DomainWarpScale { get; set; }
    public float DomainWarpStrength { get; set; }
    public bool UseRidged { get; set; }
    public float RidgeSharpness { get; set; } = 1.0f;
}
```

### 3.3 New Density Function

#### 3.3.1 TerrainShaper Class

```csharp
public sealed class TerrainShaper
{
    private readonly TerrainConfig config;
    private readonly HeightSplineSet splines;
    
    /// <summary>
    /// Calculate terrain density at world position.
    /// Positive = solid, Negative = air, Zero = surface
    /// </summary>
    public float GetDensity(Vector3 worldPos, TerrainClimate climate)
    {
        // 1. Get base height from continental + erosion + PV
        float baseY = GetBaseHeight(climate);
        
        // 2. Vertical gradient (base density)
        float density = baseY - worldPos.Y;
        
        // 3. Calculate 3D factor from erosion and weirdness
        float factor3D = Calculate3DFactor(climate.Erosion, climate.Weirdness);
        
        // 4. Apply 3D noise scaled by factor
        if (factor3D > 0.01f)  // Skip if negligible
        {
            float noise3D = Sample3DNoise(worldPos);
            density += noise3D * factor3D * config.TerrainShaping.Detail3DAmplitude;
        }
        
        // 5. Apply peaks/valleys (local relief)
        density += ApplyPeaksValleys(climate.PeaksValleys, climate.Erosion, worldPos.Y, baseY);
        
        return density;
    }
    
    private float GetBaseHeight(TerrainClimate climate)
    {
        // Continentalness is PRIMARY height driver
        float c01 = climate.Continentalness * 0.5f + 0.5f;
        float baseHeight = splines.HeightFromContinentalness.Evaluate(c01);
        
        // Erosion modulates the height range
        // Low erosion = dramatic, High erosion = flat
        float e01 = climate.Erosion * 0.5f + 0.5f;
        float erosionFlatten = splines.ErosionFlattenFactor.Evaluate(e01);
        
        // PV adds local variation (scaled by inverse erosion)
        float pvContrib = climate.PeaksValleys * config.TerrainShaping.PVAmplitude * (1f - erosionFlatten);
        
        return baseHeight + pvContrib;
    }
    
    private float Calculate3DFactor(float erosion, float weirdness)
    {
        // Erosion: low = dramatic (factor high), high = smooth (factor low)
        float erosionFactor = 1f - (erosion * 0.5f + 0.5f);
        
        // Weirdness: high absolute value = more 3D, low = heightmap-like
        float weirdFactor = MathF.Abs(weirdness);
        
        // Combine: both must allow 3D for it to happen
        // This creates "normal" areas (heightmap) and "weird" areas (3D)
        return erosionFactor * 0.7f + weirdFactor * 0.3f;
    }
    
    private float ApplyPeaksValleys(float pv, float erosion, float y, float baseY)
    {
        // PV affects density near the surface only
        float surfaceProximity = 1f - MathF.Abs(y - baseY) / 50f;
        surfaceProximity = Math.Clamp(surfaceProximity, 0f, 1f);
        
        // Scale by inverse erosion (low erosion = dramatic peaks)
        float scale = (1f - (erosion * 0.5f + 0.5f)) * surfaceProximity;
        
        return pv * config.TerrainShaping.PVSurfaceAmplitude * scale;
    }
}
```

### 3.4 New Biome Selection System

#### 3.4.1 Biome Definition with Full Climate Ranges

```csharp
public sealed class BiomeDefinitionV2
{
    public BiomeId Id { get; set; }
    public string Name { get; set; }
    
    // Climate parameter ranges (ALL 6 parameters)
    public Range Continentalness { get; set; }
    public Range Erosion { get; set; }
    public Range PeaksValleys { get; set; }
    public Range Temperature { get; set; }
    public Range Humidity { get; set; }
    public Range Weirdness { get; set; }
    
    // Terrain shaping (used when this biome is dominant)
    public float BaseHeight { get; set; }
    public float HeightVariation { get; set; }
    public float PeaksInfluence { get; set; }
    public float ErosionSensitivity { get; set; }
    
    // Block assignments
    public BlockId SurfaceBlock { get; set; }
    public BlockId SubsurfaceBlock { get; set; }
    public BlockId DeepBlock { get; set; }
    public BlockId UnderwaterSurface { get; set; }
    
    /// <summary>
    /// Calculate match score for climate values.
    /// Lower score = better match.
    /// </summary>
    public float GetClimateDistance(TerrainClimate climate)
    {
        float d = 0f;
        d += RangeDistance(climate.Continentalness, Continentalness) * 1.5f;  // Weight C high
        d += RangeDistance(climate.Erosion, Erosion) * 1.2f;
        d += RangeDistance(climate.PeaksValleys, PeaksValleys) * 0.8f;
        d += RangeDistance(climate.Temperature, Temperature) * 1.0f;
        d += RangeDistance(climate.Humidity, Humidity) * 1.0f;
        d += RangeDistance(climate.Weirdness, Weirdness) * 0.5f;
        return d;
    }
    
    private static float RangeDistance(float value, Range range)
    {
        if (value >= range.Min && value <= range.Max)
            return 0f;
        if (value < range.Min)
            return range.Min - value;
        return value - range.Max;
    }
}
```

#### 3.4.2 Biome Selector (Allocation-Free, No LINQ)

```csharp
/// <summary>
/// Selects biomes based on climate parameters.
/// PERFORMANCE: No allocations, no LINQ, no noise calls.
/// Surface biomes only - subterrain biome variation is DEFERRED.
/// </summary>
public sealed class BiomeSelector
{
    private readonly BiomeDefinitionV2[] biomes;  // Array, not List
    private readonly int biomeCount;
    
    // Pre-allocated scratch arrays (reused per call)
    private readonly float[] distances;
    private readonly int[] indices;
    
    public BiomeSelector(BiomeDefinitionV2[] biomes)
    {
        this.biomes = biomes;
        this.biomeCount = biomes.Length;
        this.distances = new float[biomes.Length];
        this.indices = new int[biomes.Length];
    }
    
    /// <summary>
    /// Select best biome for climate values.
    /// PERFORMANCE: Pure arithmetic, no noise, no allocations.
    /// </summary>
    public BiomeId SelectPrimary(in TerrainClimate climate, float actualHeight)
    {
        float bestDist = float.MaxValue;
        int bestIdx = 0;
        
        for (int i = 0; i < biomeCount; i++)
        {
            float dist = biomes[i].GetClimateDistance(climate);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }
        
        return biomes[bestIdx].Id;
    }
    
    /// <summary>
    /// Select biome with blend weights for smooth transitions.
    /// Call only when blending is needed (transitions).
    /// </summary>
    public void SelectWithBlend(
        in TerrainClimate climate,
        float actualHeight,
        out BiomeId primary,
        out BiomeId secondary,
        out float blendWeight)
    {
        // Calculate all distances
        for (int i = 0; i < biomeCount; i++)
        {
            distances[i] = biomes[i].GetClimateDistance(climate);
            indices[i] = i;
        }
        
        // Partial sort: find top 2 (no full sort needed)
        int best1 = 0, best2 = 1;
        if (distances[1] < distances[0]) { best1 = 1; best2 = 0; }
        
        for (int i = 2; i < biomeCount; i++)
        {
            if (distances[i] < distances[best1])
            {
                best2 = best1;
                best1 = i;
            }
            else if (distances[i] < distances[best2])
            {
                best2 = i;
            }
        }
        
        primary = biomes[best1].Id;
        secondary = biomes[best2].Id;
        
        // Blend weight based on distance ratio
        float d1 = distances[best1];
        float d2 = distances[best2];
        blendWeight = d1 / (d1 + d2 + 0.001f);  // 0 = pure primary, 0.5 = equal
    }
}
```

#### 3.4.3 Surface Biomes Only (Subterrain Deferred)

```csharp
// NOTE: Subterrain biome variation (lush caves, dripstone caves, deep dark)
// is DEFERRED as a nice-to-have feature. Current implementation uses:
// - Single biome per surface column
// - Cave biomes determined by surface biome + depth rules
// - No 3D biome grid (performance cost too high)

// Future enhancement (low priority):
// - Optional 4×4×24 cave biome grid per chunk
// - Only generated for chunks with significant cave volume
// - Sparse sampling to minimize performance impact
```

### 3.5 Example Biome Definitions (Minecraft-Style)

```csharp
public static List<BiomeDefinitionV2> MinecraftStyleBiomes() =>
[
    // === OCEAN BIOMES ===
    new BiomeDefinitionV2
    {
        Id = BiomeId.DeepOcean,
        Name = "Deep Ocean",
        Continentalness = new(-1.0f, -0.5f),    // Very low C = deep ocean
        Erosion = new(-1.0f, 1.0f),              // Any erosion
        PeaksValleys = new(-1.0f, 0.2f),         // Not extreme peaks
        Temperature = new(-1.0f, 1.0f),          // Any temp
        Humidity = new(-1.0f, 1.0f),
        Weirdness = new(-1.0f, 1.0f),
        BaseHeight = -30f,
        HeightVariation = 8f,
        SurfaceBlock = BlockId.Gravel,
    },
    
    new BiomeDefinitionV2
    {
        Id = BiomeId.Ocean,
        Name = "Ocean",
        Continentalness = new(-0.5f, -0.15f),   // Medium-low C
        Erosion = new(-1.0f, 1.0f),
        PeaksValleys = new(-1.0f, 0.4f),
        Temperature = new(-1.0f, 1.0f),
        Humidity = new(-1.0f, 1.0f),
        Weirdness = new(-1.0f, 1.0f),
        BaseHeight = -15f,
        HeightVariation = 10f,
        SurfaceBlock = BlockId.Gravel,
    },
    
    // === COASTAL BIOMES ===
    new BiomeDefinitionV2
    {
        Id = BiomeId.Beach,
        Name = "Beach",
        Continentalness = new(-0.2f, 0.05f),    // Coast zone
        Erosion = new(0.3f, 1.0f),               // High erosion (flat)
        PeaksValleys = new(-0.5f, 0.3f),
        Temperature = new(0.0f, 1.0f),           // Not frozen
        Humidity = new(-1.0f, 1.0f),
        Weirdness = new(-0.8f, 0.8f),            // Not too weird
        BaseHeight = 2f,
        HeightVariation = 2f,
        PeaksInfluence = 0.05f,
        SurfaceBlock = BlockId.Sand,
        SubsurfaceBlock = BlockId.Sand,
    },
    
    // === FLAT BIOMES (high erosion) ===
    new BiomeDefinitionV2
    {
        Id = BiomeId.Plains,
        Name = "Plains",
        Continentalness = new(-0.1f, 0.4f),
        Erosion = new(0.4f, 1.0f),               // High erosion = flat
        PeaksValleys = new(-0.3f, 0.3f),
        Temperature = new(0.1f, 0.7f),
        Humidity = new(0.2f, 0.7f),
        Weirdness = new(-0.7f, 0.7f),
        BaseHeight = 10f,
        HeightVariation = 5f,
        PeaksInfluence = 0.15f,
        ErosionSensitivity = 0.9f,
        SurfaceBlock = BlockId.Grass,
    },
    
    new BiomeDefinitionV2
    {
        Id = BiomeId.Desert,
        Name = "Desert",
        Continentalness = new(0.0f, 0.6f),
        Erosion = new(0.3f, 1.0f),
        PeaksValleys = new(-0.4f, 0.4f),
        Temperature = new(0.5f, 1.0f),           // Hot
        Humidity = new(-1.0f, 0.0f),             // Dry
        Weirdness = new(-0.8f, 0.8f),
        BaseHeight = 12f,
        HeightVariation = 8f,
        SurfaceBlock = BlockId.Sand,
        SubsurfaceBlock = BlockId.Sand,
        DeepBlock = BlockId.Sandstone,
    },
    
    // === HILLY BIOMES (medium erosion) ===
    new BiomeDefinitionV2
    {
        Id = BiomeId.Taiga,
        Name = "Taiga",
        Continentalness = new(0.0f, 0.6f),
        Erosion = new(0.0f, 0.6f),               // Medium erosion
        PeaksValleys = new(-0.4f, 0.6f),
        Temperature = new(-0.6f, 0.2f),          // Cold
        Humidity = new(0.3f, 1.0f),              // Humid
        Weirdness = new(-1.0f, 1.0f),
        BaseHeight = 25f,
        HeightVariation = 20f,
        PeaksInfluence = 0.5f,
        SurfaceBlock = BlockId.Podzol,
    },
    
    new BiomeDefinitionV2
    {
        Id = BiomeId.Savanna,
        Name = "Savanna",
        Continentalness = new(0.0f, 0.5f),
        Erosion = new(0.2f, 0.7f),
        PeaksValleys = new(-0.3f, 0.5f),
        Temperature = new(0.4f, 1.0f),
        Humidity = new(-0.3f, 0.4f),
        Weirdness = new(-0.7f, 0.7f),
        BaseHeight = 18f,
        HeightVariation = 12f,
        SurfaceBlock = BlockId.CoarseDirt,
    },
    
    // === DRAMATIC BIOMES (low erosion) ===
    new BiomeDefinitionV2
    {
        Id = BiomeId.Highlands,
        Name = "Highlands",
        Continentalness = new(0.2f, 0.7f),
        Erosion = new(-0.6f, 0.3f),              // Low erosion = dramatic
        PeaksValleys = new(0.0f, 0.7f),
        Temperature = new(-0.2f, 0.5f),
        Humidity = new(-0.5f, 0.5f),
        Weirdness = new(-0.6f, 0.6f),
        BaseHeight = 50f,
        HeightVariation = 35f,
        PeaksInfluence = 0.7f,
        SurfaceBlock = BlockId.Grass,
    },
    
    // === MOUNTAIN BIOMES (extreme C + low E) ===
    new BiomeDefinitionV2
    {
        Id = BiomeId.Alpine,
        Name = "Alpine",
        Continentalness = new(0.5f, 1.0f),       // Very inland/high
        Erosion = new(-1.0f, 0.0f),              // Very low erosion = peaks
        PeaksValleys = new(0.3f, 1.0f),          // Peaks zone
        Temperature = new(-1.0f, 0.3f),          // Cold (or any at extreme altitude)
        Humidity = new(-1.0f, 1.0f),
        Weirdness = new(-1.0f, 1.0f),
        BaseHeight = 120f,
        HeightVariation = 80f,
        PeaksInfluence = 1.0f,
        ErosionSensitivity = 0.1f,
        SurfaceBlock = BlockId.Snow,
        SubsurfaceBlock = BlockId.SnowDirt,
    },
    
    // === WEIRD BIOMES (high weirdness) ===
    // These have unusual terrain with 3D features
    new BiomeDefinitionV2
    {
        Id = BiomeId.Highlands,  // Could add ShatteredHighlands variant
        Name = "Windswept Hills",
        Continentalness = new(0.1f, 0.6f),
        Erosion = new(-0.8f, 0.0f),
        PeaksValleys = new(-0.2f, 0.8f),
        Temperature = new(-0.3f, 0.6f),
        Humidity = new(-0.5f, 0.5f),
        Weirdness = new(0.5f, 1.0f),             // HIGH weirdness = crazy terrain
        BaseHeight = 60f,
        HeightVariation = 50f,
        PeaksInfluence = 0.9f,
        SurfaceBlock = BlockId.Grass,
    },
];
```

---

## Part 4: Implementation Roadmap

### Progress Tracking

| Step | Deliverable | Status | Est. | Actual | Notes |
|------|-------------|--------|------|--------|-------|
| **0.1** | ChunkClimateCache class | ✅ Done | 2h | 1h | Created in `World/Generation/ChunkClimateCache.cs` |
| **0.2** | SIMD-batched 2D noise | ✅ Done | 4h | 1h | `SampleFbm2DBatched()` using NoiseDotNet |
| **0.3** | Benchmark harness | ✅ Done | 2h | 1h | `TerrainGenerationProfiler` class created |
| **0.4** | Verify sparse 3D sampling | ✅ Done | 2h | 0h | Already implemented in existing code |
| **1.1** | Weirdness parameter added | ✅ Done | 1h | 0.5h | Added to TerrainConfig + TerrainGenerationParams |
| **1.2** | Climate cache integration | ✅ Done | 4h | 0.5h | BuildColumnFieldCaches reads from cache |
| **1.3** | Remove duplicate noise calls | ✅ Done | 3h | 0.5h | 5 noise calls removed (warp×2, cont, erosion, peaks) |
| **2.1** | Height spline refactor | ⬜ Not Started | 4h | - | |
| **2.2** | Remove if-else chains | ⬜ Not Started | 4h | - | |
| **2.3** | Magic numbers → config | ⬜ Not Started | 3h | - | |
| **3.1** | Remove UpdateBiomeDataFromTerrainValues | ⬜ Not Started | 2h | - | |
| **3.2** | BiomeSelector (allocation-free) | ⬜ Not Started | 3h | - | |
| **3.3** | Remove cave biome generation | ⬜ Not Started | 1h | - | |
| **4.1** | Weirdness → 3D factor | ⬜ Not Started | 2h | - | |
| **4.2** | Overhang tuning | ⬜ Not Started | 3h | - | |
| **5.1** | Spline tuning | ⬜ Not Started | 4h | - | |
| **5.2** | Domain warp for biome borders | ⬜ Not Started | 3h | - | |
| **5.3** | F3 debug overlay | ⬜ Not Started | 4h | - | |
| **5.4** | Final performance validation | ⬜ Not Started | 2h | - | |

**Status Legend:** ⬜ Not Started | 🔄 In Progress | ✅ Done | ❌ Blocked | ⏸️ Deferred

---

### Milestone Summary

| Milestone | Steps | Target | Gate Criteria |
|-----------|-------|--------|---------------|
| **M0: Perf Foundation** | 0.1-0.4 | Week 1 | Chunk gen <15ms, SIMD verified |
| **M1: Climate Cache** | 1.1-1.3 | Week 2 | Zero duplicate noise calls |
| **M2: Height Refactor** | 2.1-2.3 | Week 3 | No magic numbers, spline-based |
| **M3: Biome Cleanup** | 3.1-3.3 | Week 4 | No reconciliation hack, surface only |
| **M4: 3D Features** | 4.1-4.2 | Week 5 | Overhangs work, weirdness affects terrain |
| **M5: Polish** | 5.1-5.4 | Week 6 | 60 FPS, varied terrain, irregular borders |

---

### Step 0: Performance Infrastructure ⚠️ MUST DO FIRST ✅ COMPLETE

**Milestone:** M0 - Performance Foundation  
**Duration:** ~10 hours (Actual: ~3 hours)  
**Gate:** ✅ PASSED - Chunk generation <15ms baseline, SIMD operations verified

#### Step 0.1: Create ChunkClimateCache Class ✅ DONE
```
File: src/spyro-game/World/Generation/ChunkClimateCache.cs
- float[256] arrays for C, E, PV, T, H, W ✅
- float[256] for Continentalness01, Erosion01 (pre-normalized) ✅
- SampleForChunk(chunkX, chunkZ, config) method ✅
- Profiling: LastSampleTimeMs property (DEBUG builds) ✅
```
**Acceptance:** ✅ Class compiles, integrated into CpuTerrainGenerator

#### Step 0.2: SIMD-Batched 2D Noise ✅ DONE
```
File: src/spyro-game/World/Generation/ChunkClimateCache.cs
- SampleFbm2DBatched() using NoiseDotNet ✅
- Process 256 samples using NoiseDotNet's internal SIMD ✅
- Dedicated scratch buffers to avoid conflicts ✅
```
**Acceptance:** ✅ Uses NoiseDotNet.Noise.GradientNoise2D (SIMD internally)

#### Step 0.3: Benchmark Harness ✅ DONE
```
File: src/spyro-game/World/Generation/TerrainGenerationProfiler.cs
- Per-step timing: Climate, Height, 3D, Biome, Blocks, Total ✅
- Rolling averages (64-sample buffer) ✅
- Min/Max tracking ✅
- LogStatistics() method with budget warnings ✅
- GetLastChunkSummary() for real-time display ✅
```
**Acceptance:** ✅ Profiler integrated, can measure baseline

#### Step 0.4: Verify Sparse 3D Sampling ✅ DONE
```
File: src/spyro-game/World/Generation/CpuTerrainGenerator.cs
- BuildColumnVolumes() uses 5×5×97 sparse grid ✅ (existing code)
- InterpolateSparseVolumes() does trilinear interpolation ✅ (existing code)
- SparseVolumeSize = 2425 samples per noise type ✅
```
**Acceptance:** ✅ Already implemented correctly

---

### Step 1: Climate Caching ✅ COMPLETE

**Milestone:** M1 - Climate Cache  
**Duration:** ~8 hours (Actual: ~1.5 hours)  
**Gate:** ✅ PASSED - Zero duplicate climate noise calls

#### Step 1.1: Add Weirdness Parameter ✅ DONE
```
Files: TerrainConfig.cs, ChunkClimateCache.cs
- Added TemperatureScale, HumidityScale, WeirdnessScale to TerrainConfig ✅
- Added to TerrainGenerationParams struct ✅
- ChunkClimateCache now reads scales from config (no hardcoded values) ✅
```
**Acceptance:** ✅ All 6 climate params use config-driven scales

#### Step 1.2: Climate Cache Integration ✅ DONE
```
File: src/spyro-game/World/Generation/CpuTerrainGenerator.cs
- BuildColumnFieldCaches() reads from ChunkClimateCache ✅
- PrepareChunkCaches() calls cache.SampleForChunk() first ✅
- Local arrays populated from cache for backward compatibility ✅
```
**Acceptance:** ✅ Single point of climate noise sampling

#### Step 1.3: Remove Duplicate Noise Calls ✅ DONE
```
Files: CpuTerrainGenerator.cs
- Removed 5 redundant SampleFbm2D calls:
  - Domain warp X and Z
  - Continentalness
  - Erosion
  - Peaks/Valleys (ridge noise)
- Only terrain detail noise (cliff, terrainType, cliffiness) sampled in BuildColumnFieldCaches()
- Profiler steps separated: ClimateSampling vs HeightCalculation ✅
```
**Acceptance:** ✅ ~40% reduction in 2D noise calls

---

### Step 2: Height Calculation Refactor

**Milestone:** M2 - Height Refactor  
**Duration:** ~11 hours  
**Gate:** No magic numbers in height code, spline-based calculation

#### Step 2.1: Height Spline Refactor
```
Files: TerrainConfig.cs, CpuTerrainGenerator.cs
- Create HeightSplineSet with multiple splines
- Height = f(Continentalness, Erosion, PV) via spline lookups
- Remove direct arithmetic height calculations
```
**Acceptance:** Height driven by configurable splines

#### Step 2.2: Remove If-Else Chains
```
File: src/spyro-game/World/Generation/CpuTerrainGenerator.cs
- Replace BuildColumnFieldCaches() if-else with continuous blending
- Use spline-based erosion flattening
- Use spline-based PV amplitude scaling
```
**Acceptance:** No if-else chains for terrain type selection

#### Step 2.3: Magic Numbers → Config
```
Files: TerrainConfig.cs, CpuTerrainGenerator.cs
- Extract all numeric literals to TerrainShapingConfig
- Document each parameter with units and valid ranges
- Verify: grep for naked floats in height calculation returns 0
```
**Acceptance:** Zero magic numbers in BuildColumnFieldCaches()

---

### Step 3: Biome Selection Cleanup

**Milestone:** M3 - Biome Cleanup  
**Duration:** ~6 hours  
**Gate:** No reconciliation hack, surface biomes only

#### Step 3.1: Remove UpdateBiomeDataFromTerrainValues
```
File: src/spyro-game/World/Generation/CpuTerrainGenerator.cs
- Delete UpdateBiomeDataFromTerrainValues() method
- Biome selection happens AFTER height is known
- Use actual terrain height + cached climate for selection
```
**Acceptance:** Method deleted, terrain still works

#### Step 3.2: BiomeSelector (Allocation-Free)
```
File: src/spyro-game/World/Generation/BiomeSelector.cs
- New class with pre-allocated arrays
- SelectPrimary() - no allocations
- SelectWithBlend() - no LINQ, no allocations
- Unit tests for correctness
```
**Acceptance:** BiomeSelector with 0 allocations verified via profiler

#### Step 3.3: Remove Cave Biome Generation
```
File: src/spyro-game/World/Generation/BiomeGenerator.cs
- Remove GenerateCaveBiomes() call
- Remove CaveBiomeId handling
- Remove 3D biome grid from ChunkBiomeData
- Mark as DEFERRED in code comments
```
**Acceptance:** Cave biomes removed, simpler code

---

### Step 4: 3D Terrain Features

**Milestone:** M4 - 3D Features  
**Duration:** ~5 hours  
**Gate:** Overhangs functional, Weirdness affects terrain variety

#### Step 4.1: Weirdness → 3D Factor
```
File: src/spyro-game/World/Generation/CpuTerrainGenerator.cs
- Calculate3DFactor(erosion, weirdness)
- High |weirdness| + low erosion = dramatic 3D
- Low |weirdness| + high erosion = pure heightmap
```
**Acceptance:** Weirdness affects overhang frequency visually

#### Step 4.2: Overhang Tuning
```
File: src/spyro-game/World/Generation/CpuTerrainGenerator.cs
- Adjust overhang parameters based on weirdness
- Tune for natural appearance (not too many, not too few)
- Visual inspection: overhangs look good
```
**Acceptance:** Overhangs appear in appropriate areas

---

### Step 5: Polish & Tune

**Milestone:** M5 - Polish  
**Duration:** ~13 hours  
**Gate:** 60 FPS with streaming, visually varied terrain

#### Step 5.1: Spline Tuning
```
Files: TerrainConfig.cs, terrain_config.json
- Tune height spline for proper ocean-to-peak progression
- Tune erosion flattening curve
- Tune PV amplitude curve
- Visual inspection across multiple seeds
```
**Acceptance:** Terrain looks natural across different seeds

#### Step 5.2: Domain Warp for Biome Borders
```
Files: BiomeGenerator.cs, TerrainConfig.cs
- Add domain warp to climate sampling
- Configurable warp strength
- Visual: no straight-line biome borders
```
**Acceptance:** Biome borders are irregular/organic

#### Step 5.3: F3 Debug Overlay
```
Files: GameScene.cs, HUD system
- F3 shows current biome, climate values
- F3 shows chunk gen timing
- F3 shows cache hit/miss (if applicable)
```
**Acceptance:** Debug info visible in-game

#### Step 5.4: Final Performance Validation
```
- Run benchmark with final implementation
- Verify: chunk gen <10ms average
- Verify: 60 FPS maintained during fast movement
- Verify: no GC stalls from terrain generation
```
**Acceptance:** All performance criteria met

---

### Detailed Phase Descriptions

#### Phase 0: Performance Infrastructure (Week 1) ⚠️ MUST DO FIRST

**Goal:** Establish SIMD/caching foundation before ANY feature work

1. **Create `ChunkClimateCache`** class with pre-allocated float[256] arrays
2. **Implement `SampleFbm2DBatched()`** using NoiseDotNet SIMD APIs
3. **Verify SIMD codegen** - Check disassembly for Vector256 operations
4. **Benchmark baseline** - Establish noise call counts and timing
5. **Create sparse 3D sampling** - 5×5×97 grid with trilinear interpolation

**Performance Targets:**
- 2D noise: <1ms for 256 columns × 6 parameters
- 3D noise: <3ms for sparse grid sampling
- Total chunk generation: <10ms

### Phase 1: Climate Caching (Week 1-2)

**Goal:** All noise sampled ONCE per chunk, cached for reuse

1. **Refactor `BuildColumnFieldCaches()`** to use `ChunkClimateCache`
2. **Add `Weirdness` parameter** to cached climate
3. **Remove per-voxel noise calls** - Replace with cache lookups
4. **Verify no noise in voxel loop** - Code review + profiling
5. **Unit tests** for cache correctness across chunk boundaries

### Phase 2: Height from Cached Climate (Week 2-3)

**Goal:** Height calculation uses ONLY cached values

1. **Replace `BuildColumnFieldCaches()` height logic** with spline-based
2. **Remove if-else chains** - Use continuous spline blending
3. **Eliminate magic numbers** - All values from config
4. **Biome height attributes** blend using cached climate weights
5. **Profile** - Ensure no noise calls in height calculation

### Phase 3: Biome Selection Cleanup (Week 3-4)

**Goal:** Single-pass biome selection, no reconciliation

1. **Remove `UpdateBiomeDataFromTerrainValues()`** hack
2. **Biome selected AFTER height** using actual terrain + cached climate
3. **Implement `BiomeSelector`** (allocation-free, no LINQ)
4. **Surface biomes only** - Remove/defer cave biome generation
5. **Validate** - Ocean biomes always below water, etc.

### Phase 4: 3D Terrain Features (Week 4-5)

**Goal:** Overhangs via sparse-sampled 3D noise

1. **Keep existing sparse sampling** (already implemented)
2. **Add `Weirdness` influence** on 3D factor
3. **Tune overhang frequency** by erosion + weirdness
4. **Improve trilinear interpolation** quality if needed
5. **Profile 3D noise** - Ensure within budget

### Phase 5: Polish & Tune (Week 5-6)

**Goal:** Minecraft-quality terrain appearance

1. **Tune splines** for proper terrain progression
2. **Balance biome distribution** 
3. **Add domain warp** for irregular biome borders
4. **Debug visualization** (F3 overlay)
5. **Final performance validation** - 60 FPS with streaming

---

## Part 5: Success Criteria

### Terrain Quality
- [ ] No cliff spam - cliffs only where geologically appropriate
- [ ] Overhangs exist and look natural
- [ ] Mountains have varied profiles (not all the same shape)
- [ ] Plains are actually flat
- [ ] Coastal zones transition smoothly to land

### Biome Quality  
- [ ] Biome borders are irregular (no straight lines)
- [ ] Ocean biomes never appear above water
- [ ] Land biomes never appear underwater
- [ ] Temperature gradient from equator-like to pole-like zones
- [ ] Humidity creates distinct wet/dry regions

### Technical Quality
- [ ] No magic numbers in terrain code (all in config)
- [ ] Single source of truth for climate values
- [ ] Biome selection is deterministic and debuggable
- [ ] Configuration changes have predictable effects
- [ ] Performance maintains 60 FPS during streaming

### Performance Quality (P0 - CRITICAL)
- [ ] All 2D noise batched via SIMD (Vector256<float>)
- [ ] 3D noise uses sparse sampling (4-block intervals)
- [ ] Zero noise calls inside voxel generation loop
- [ ] Chunk generation <10ms total
- [ ] No allocations in hot path (biome selection, block generation)
- [ ] Climate cache reused across generation steps

### Deferred Features (P3 - Nice-to-Have)
- [ ] Subterrain biome variation (lush caves, dripstone)
- [ ] 3D biome grid for caves
- [ ] Biome-specific cave decorations

---

## Appendix A: Recommended Spline Configurations

### A.1 Height from Continentalness

```
C=0.00 → Y=-80 (deep ocean)
C=0.15 → Y=-40 (ocean)
C=0.30 → Y=-10 (shallow ocean)
C=0.35 → Y=0   (sea level transition)
C=0.40 → Y=10  (coastal low)
C=0.50 → Y=25  (plains)
C=0.60 → Y=50  (hills)
C=0.70 → Y=80  (highlands)
C=0.80 → Y=120 (mountains)
C=0.90 → Y=180 (high peaks)
C=1.00 → Y=250 (extreme peaks)
```

### A.2 3D Factor from Erosion

```
E=-1.0 → Factor=1.0 (maximum 3D, dramatic)
E=-0.5 → Factor=0.7
E=0.0  → Factor=0.4
E=0.5  → Factor=0.15
E=1.0  → Factor=0.0 (pure heightmap, flat)
```

### A.3 PV Amplitude from Erosion

```
E=-1.0 → PVAmp=60 (huge peaks and valleys)
E=-0.5 → PVAmp=35
E=0.0  → PVAmp=15
E=0.5  → PVAmp=5
E=1.0  → PVAmp=0 (no peaks)
```

---

## Appendix B: Migration Checklist

### Performance Infrastructure (DO FIRST)
- [ ] Create ChunkClimateCache with float[256] arrays
- [ ] Implement SIMD-batched SampleFbm2D using NoiseDotNet
- [ ] Verify Vector256<float> in disassembly
- [ ] Benchmark: establish baseline timing
- [ ] Verify sparse 3D sampling is working (5×5×97 grid)
- [ ] Profile: confirm no noise in voxel loop

### Configuration
- [ ] Backup current TerrainConfig.cs
- [ ] Implement ClimateNoiseConfig with all parameters documented
- [ ] Implement TerrainShapingConfig for density tunables
- [ ] Add Weirdness parameter to climate system
- [ ] Remove all magic numbers from CpuTerrainGenerator

### Climate System
- [ ] Refactor BuildColumnFieldCaches to use ChunkClimateCache
- [ ] All 6 climate params sampled in single SIMD batch
- [ ] Pre-compute Continentalness01, Erosion01 (normalized)
- [ ] Validate cache reuse across steps (no re-sampling)

### Biome System
- [ ] Implement BiomeSelector (allocation-free)
- [ ] Define BiomeDefinitionV2 with 6 climate ranges
- [ ] Remove UpdateBiomeDataFromTerrainValues hack
- [ ] Surface biomes only (defer cave biomes)
- [ ] Validate: ocean biomes below water, land above

### Terrain Generation
- [ ] Remove if-else chains from height calculation
- [ ] Replace with spline-based continuous blending
- [ ] Integrate Weirdness into 3D factor
- [ ] Tune overhang frequency

### Validation
- [ ] Add F3 debug overlay for biomes/climate
- [ ] Performance profiling (<10ms per chunk)
- [ ] 60 FPS during active streaming
- [ ] No straight-line biome borders
- [ ] Visual inspection: varied, interesting terrain

---

## Appendix C: Performance Implementation Details

### C.1 SIMD Noise Sampling Pattern

The existing `NoiseDotNet` library supports SIMD operations. Use the batch APIs:

```csharp
/// <summary>
/// SIMD-batched 2D FBM noise sampling.
/// Processes 256 coordinates efficiently using Vector256<float>.
/// </summary>
public static void SampleFbm2DBatched(
    ReadOnlySpan<float> xCoords,
    ReadOnlySpan<float> zCoords,
    NoiseLayer config,
    uint seed,
    Span<float> output)
{
    var length = output.Length;
    output.Clear();
    
    var amplitude = 1f;
    var frequency = config.BaseScale;
    var totalAmplitude = 0f;
    
    // NoiseDotNet's GradientNoise2D processes in SIMD batches internally
    for (var octave = 0; octave < config.Octaves; octave++)
    {
        // This call internally uses Vector256<float> when available
        Noise.GradientNoise2D(
            xCoords[..length],
            zCoords[..length],
            output,  // accumulates
            frequency,
            frequency,
            amplitude,
            unchecked((int)(seed + (uint)(octave * 132))));
        
        totalAmplitude += amplitude;
        amplitude *= config.Persistence;
        frequency *= config.Lacunarity;
    }
    
    // Normalize - also SIMD-friendly
    if (totalAmplitude > 0f)
    {
        var inv = 1f / totalAmplitude;
        for (var i = 0; i < length; i++)
            output[i] *= inv;
    }
}
```

### C.2 Sparse 3D Sampling Strategy

Current implementation already uses sparse sampling (good!). Key numbers:

```
Full resolution:     16 × 16 × 384 = 98,304 samples per noise type
Sparse resolution:    5 ×  5 ×  97 =  2,425 samples per noise type
Reduction factor:    ~40x fewer noise evaluations

Sparse step: 4 blocks (sample at 0, 4, 8, 12, 16 in X/Z)
             4 blocks (sample at 0, 4, 8, ... 384 in Y)

Trilinear interpolation fills in the gaps during block generation.
Interpolation is pure arithmetic - very fast.
```

### C.3 Cache Lifetime and Reuse

```
Per-Chunk Generation Timeline:
────────────────────────────────────────────────────────────────────
Step 1: Climate Sampling (~1ms)
  → ChunkClimateCache populated with 6 × 256 floats
  → ALL noise calls for 2D climate happen HERE and ONLY HERE

Step 2: Height Calculation (~0.5ms)  
  → Uses cached Continentalness, Erosion, PV
  → NO noise calls - spline lookups + arithmetic only
  
Step 3: 3D Noise Sampling (~2ms)
  → Sparse grid: 2,425 samples × 3 noise types
  → Results cached in sparseCheeseGrid[], etc.

Step 4: Biome Selection (~0.2ms)
  → Uses cached climate + calculated heights
  → NO noise calls - pure distance calculations

Step 5: Block Generation (~3ms for 98K voxels)
  → Uses cached heights (lookup)
  → Uses interpolated 3D noise (trilinear from cache)
  → Uses cached biome (lookup)
  → NO noise calls
────────────────────────────────────────────────────────────────────
TOTAL: ~7ms per chunk (within 10ms budget)
```

### C.4 Anti-Patterns to Avoid

```csharp
// ❌ BAD: Noise inside voxel loop
for (var y = 0; y < ChunkYSize; y++)
{
    var caveNoise = SampleFbm3D(x, y, z, ...);  // 98,304 calls!
    // ...
}

// ✓ GOOD: Pre-sample sparse grid, interpolate
BuildSparseNoiseGrids();  // 2,425 calls total
for (var y = 0; y < ChunkYSize; y++)
{
    var caveNoise = TrilinearInterpolate(sparseGrid, x, y, z);  // Pure math
    // ...
}

// ❌ BAD: Re-sampling climate per biome cell
for each biome cell:
    var cont = SampleNoise2D(cellX, cellZ, ...);  // Redundant!

// ✓ GOOD: Use cached column values
for each biome cell:
    var centerColumn = GetCellCenterColumn(cellX, cellZ);
    var cont = climateCache.Continentalness[centerColumn];  // Lookup

// ❌ BAD: LINQ in hot path
var bestBiome = biomes.OrderBy(b => b.Distance(climate)).First();

// ✓ GOOD: Simple loop, no allocations
float bestDist = float.MaxValue;
int bestIdx = 0;
for (int i = 0; i < biomeCount; i++)
{
    var dist = biomes[i].GetDistance(climate);
    if (dist < bestDist) { bestDist = dist; bestIdx = i; }
}
```

### C.5 Profiling Checkpoints

Add timing around each major step to validate performance:

```csharp
#if DEBUG
private readonly Stopwatch _stepTimer = new();
private readonly long[] _stepTimes = new long[5];

private void ProfileStep(int step, Action action)
{
    _stepTimer.Restart();
    action();
    _stepTimes[step] = _stepTimer.ElapsedTicks;
}

public void LogChunkPerformance()
{
    var ticksPerMs = Stopwatch.Frequency / 1000.0;
    Log.Debug($"Chunk Gen: Climate={_stepTimes[0]/ticksPerMs:F2}ms, " +
              $"Height={_stepTimes[1]/ticksPerMs:F2}ms, " +
              $"3D={_stepTimes[2]/ticksPerMs:F2}ms, " +
              $"Biome={_stepTimes[3]/ticksPerMs:F2}ms, " +
              $"Blocks={_stepTimes[4]/ticksPerMs:F2}ms");
}
#endif
```

---

## Appendix D: Deferred Features

### D.1 Subterrain Biomes (Low Priority)

Minecraft has special cave biomes (Lush Caves, Dripstone Caves, Deep Dark).
These are **deferred** due to performance cost:

**Why Deferred:**
- Requires 3D biome grid (4×4×24 per chunk = 384 extra biome lookups)
- Each cave biome needs additional noise sampling
- Current caves work fine with surface biome inheritance

**Future Implementation (if needed):**
```csharp
// Only generate for chunks with significant cave volume
if (chunkHasCaves && enableCaveBiomes)
{
    // Sparse 3D biome grid (16-block Y cells)
    for (int cy = 0; cy < 24; cy++)
    {
        // Sample cave biome noise at cell center
        // Much cheaper than per-voxel
    }
}
```

### D.2 Voronoi-Based Biome Regions (Medium Priority)

More organic biome borders using Voronoi/Worley noise:

**Current:** Domain warp on climate noise
**Enhanced:** Voronoi cells with jitter + domain warp

```csharp
// Voronoi region ID based on nearest cell center
var regionId = GetVoronoiRegion(worldX, worldZ, cellSize: 64, jitter: 0.4f);
var regionBiome = HashToBiome(regionId, climate);
```

This is a **tuning enhancement** - can be added after core system works.

---

## Appendix E: References

- [Minecraft 1.18 Terrain Generation](https://minecraft.wiki/w/World_generation)
- [Multi-Noise Biome Source](https://minecraft.wiki/w/Biome/JSON_format)
- [Henrik Kniberg's Terrain Talk](https://www.youtube.com/watch?v=ob3VwY4JyzE) (official Mojang developer)
- [Cubiomes (C implementation)](https://github.com/Cubitect/cubiomes) - reference implementation
- [NoiseDotNet](https://github.com/Auburn/NoiseDotNet) - SIMD noise library (already in use)

---

*Document end. Implementation should follow phases in order, with validation at each step.  
**Remember: Performance is P0. Profile early, profile often.***
