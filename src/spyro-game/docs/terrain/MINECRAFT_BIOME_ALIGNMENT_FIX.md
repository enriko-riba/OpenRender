# Minecraft-Style Biome Selection Alignment Fix

## Problem Statement

Two issues were identified:

1. **Ocean biome not appearing when player is underwater**: Player at Y=34 (underwater, water level=35) showed "Highlands" biome instead of Ocean
2. **Biome borders appearing as straight lines**: The 4×4 sampling grid was creating axis-aligned biome boundaries

## Root Cause Analysis

### Issue 1: Ocean Biome Selection

**The bug**: `SelectBiome()` was checking `if (estimatedAltitude < WaterLevel)` first, but `GetEstimatedAltitude()` at cell centers didn't account for all the additional noise factors that `CpuTerrainGenerator` uses (cheese caves, cliff noise, terrain type variation, etc.).

**The Minecraft architecture** (1.18+): Ocean biomes are determined **PURELY by continentalness noise**, NOT by actual terrain height:
- Low continentalness → Ocean biome selected by noise
- Low continentalness → Height spline returns low value → Terrain ends up underwater
- This creates natural consistency without needing to check Y-level

### Issue 2: Straight Biome Borders

The 4×4 sampling grid itself isn't the problem (Minecraft uses the same resolution). The issue was that noise values cross thresholds at similar X or Z positions across adjacent cells, creating axis-aligned boundaries.

## Solution

### Fix 1: Noise-Only Ocean Selection

Changed `BiomeGenerator.SelectBiome()` to use ONLY continentalness thresholds for ocean biomes:

```csharp
// BEFORE (wrong - checking estimated altitude)
if (estimatedAltitude < VoxelHelper.WaterLevel)
{
    var depth = VoxelHelper.WaterLevel - estimatedAltitude;
    return depth > 25f ? BiomeId.DeepOcean : BiomeId.Ocean;
}
if (cont01 < config.DeepOceanThreshold) return BiomeId.DeepOcean;
if (cont01 < config.OceanThreshold && estimatedAltitude < VoxelHelper.WaterLevel + 5f)
    return BiomeId.Ocean;

// AFTER (correct - noise-only like Minecraft)
if (cont01 < config.DeepOceanThreshold) return BiomeId.DeepOcean;
if (cont01 < config.OceanThreshold) return BiomeId.Ocean;
```

### Fix 2: Per-Cell Jitter for Organic Borders

Added small noise-based jitter to cell sampling coordinates to break grid alignment:

```csharp
// BEFORE (grid-aligned)
var worldX = baseX + cellX * 4 + 2;
var worldZ = baseZ + cellZ * 4 + 2;

// AFTER (with jitter for organic shapes)
var baseCellX = baseX + cellX * 4 + 2;
var baseCellZ = baseZ + cellZ * 4 + 2;
var jitterX = GradientNoise2D(baseCellX * 0.25f, baseCellZ * 0.25f, seed + 2000u) * 0.5f;
var jitterZ = GradientNoise2D(baseCellX * 0.25f + 100f, baseCellZ * 0.25f + 100f, seed + 2001u) * 0.5f;
var worldX = baseCellX + jitterX;
var worldZ = baseCellZ + jitterZ;
```

## Architecture Documentation Updates

Updated `MINECRAFT_TERRAIN_ARCHITECTURE.md` to clarify:

1. Ocean biomes use ONLY continentalness thresholds (not altitude checks)
2. The height spline must be calibrated so `OceanThreshold` aligns with the continentalness value where terrain transitions from underwater to above water
3. Default thresholds: `DeepOceanThreshold = 0.20`, `OceanThreshold = 0.35`

## Files Changed

- `src/spyro-game/World/Generation/BiomeGenerator.cs`
  - `SelectBiome()`: Removed altitude check, use pure continentalness thresholds
  - `GenerateChunkBiomes()`: Added per-cell jitter for organic borders
  
- `src/spyro-game/docs/terrain/MINECRAFT_TERRAIN_ARCHITECTURE.md`
  - Updated biome selection rules to document Minecraft-style noise-only approach
  - Added architectural insight about ocean/terrain consistency

## Key Insight

> **In Minecraft 1.18+, the same noise parameters control BOTH biome selection AND terrain shape.**
> 
> Low continentalness → Ocean biome (by noise threshold)
> Low continentalness → Low terrain height (by height spline)
> 
> This creates natural consistency. The height spline at `OceanThreshold` should produce terrain at approximately water level.

## Current Height Spline Calibration

| cont01 | Relative Height | Absolute Y |
|--------|-----------------|------------|
| 0.20   | (deep ocean)    | ~10        |
| 0.32   | -5              | 30         |
| 0.35   | ~+2.5 (interp)  | ~37.5      |
| 0.38   | +10             | 45         |

With `OceanThreshold = 0.35`, terrain at exactly cont01=0.35 is ~2.5 blocks above water (Y=37.5), which is correct for the land/ocean boundary.
