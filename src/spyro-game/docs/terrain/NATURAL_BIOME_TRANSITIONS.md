# Natural Biome Transitions - Final Fix

## Problem
Biome boundaries looked like "spray paint" with individual snow blocks scattered throughout Taiga/Tundra biomes. While boundaries were curved (via Voronoi), the texture blending zone was **far too wide**, creating a mixing area that extended almost an entire chunk (12 blocks).

## Root Causes (Two Separate Issues)

### 1. Block-Level Alpine Probability (FIXED ✅)
**Original Issue**: `getBiomeId()` used per-block probability with position-based hash, creating random Alpine blocks scattered across terrain.

**Fix Applied**: Changed to macro-scale regional determination using:
- Large-scale FBM noise (scale 0.015, 2 octaves) for organic boundary shape
- Altitude influence combined with noise
- Threshold at 0.65 creates clean regional Alpine areas
- Voronoi system handles the detailed boundary shape

### 2. Excessive Texture Feathering (NEEDS FIXING ❌)
**Current Issue**: `BiomeRegionParams.FeatherWidth = 12f` creates a 12-block blending zone at boundaries.

**Problem Scale**:
- Chunk size = 16 blocks
- Feather width = 12 blocks = **75% of a chunk**!
- Result: Almost entire chunks are in "transition mode"

**Recommended Fix**: Reduce `FeatherWidth` drastically:

```csharp
public sealed class BiomeRegionParams
{
    public float CellSizeChunks { get; set; } = 4f;       // 4x4 chunks per Voronoi cell - OK
    public float JitterStrength { get; set; } = 0.35f;    // 35% jitter - OK
    public float FeatherWidth { get; set; } = 2f;         // CHANGE FROM 12 TO 2!
    public int MaxRegionMix { get; set; } = 3;            // Max 3 biomes blend - OK
}
```

## Visual Impact

### Current (FeatherWidth = 12):
```
Forest Forest Forest Forest Forest Forest Forest Forest
Forest Forest Mix   Mix   Mix   Mix   Mix   Mix   Mix
Forest Mix   Mix   Mix   Mix   Mix   Mix   Mix   Mix
Mix   Mix   Mix   Mix   Mix   Alpine Alpine Alpine
Mix   Mix   Mix   Mix   Mix   Alpine Alpine Alpine
Alpine Alpine Alpine Alpine Alpine Alpine Alpine Alpine
```
**Result**: 3-4 blocks of pure biome, then 12 blocks of mixing = spray paint

### Proposed (FeatherWidth = 2):
```
Forest Forest Forest Forest Forest Forest Forest Forest
Forest Forest Forest Forest Forest Mix   Alpine Alpine
Forest Forest Forest Forest Forest Mix   Alpine Alpine
Forest Forest Forest Forest Forest Alpine Alpine Alpine
Alpine Alpine Alpine Alpine Alpine Alpine Alpine Alpine
Alpine Alpine Alpine Alpine Alpine Alpine Alpine Alpine
```
**Result**: Large pure biome regions, only 2 blocks of smooth transition

## Implementation Details

### `getBiomeId()` Function (Already Fixed)
```glsl
uint getBiomeId(vec3 p) {
    // Ocean check (hard boundary)
    if (tC < params.uOceanThreshold) return OCEAN_BIOME_ID;
    
    // Climate-based land biome
    uint baseBiomeId = texture(uBiomeLUT, vec2(t, h)).r;
    
    // Alpine macro-scale influence
    float approxElevation = texture(uHeightSpline, tC).r + float(WATER_LEVEL);
    float altitudeInfluence = smoothstep(
        params.uAlpineElevation - 30.0,
        params.uAlpineElevation + 30.0,
        approxElevation
    );
    
    // Large-scale boundary noise (scale 0.015 = ~67 block wavelength)
    float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0);
    boundaryNoise = boundaryNoise * 0.5 + 0.5;
    
    // Combined alpine favorability
    float alpineFavor = altitudeInfluence * 0.7 + boundaryNoise * 0.3;
    
    // Threshold creates macro-scale Alpine regions
    if (alpineFavor > 0.65) return ALPINE_BIOME_ID;
    
    return baseBiomeId;
}
```

**Key Parameters**:
- `scale: 0.015` = ~67-block wavelength (large organic shapes)
- `octaves: 2` = two levels of detail (not noisy)
- `threshold: 0.65` = clear boundary (not 50/50 gradual)

### `getBiomeWeights()` Function (Voronoi - Unchanged)
This function creates the natural Voronoi cell boundaries. It works correctly! The issue is only the **feather width** parameter it uses.

```glsl
// Current code in getBiomeWeights():
float feather = params.uRegionFeather / cellSize;  // <-- This is the problem!

// With uRegionFeather = 12 and cellSize = 64:
// feather = 12 / 64 = 0.1875 (in normalized cell space)
// That's 18.75% of a cell = 12 blocks = HUGE

// With proposed uRegionFeather = 2:
// feather = 2 / 64 = 0.03125
// That's 3.125% of a cell = 2 blocks = Just right!
```

## Required Changes

### C# Code Change
**File**: `src/spyro-game/World/TerrainConfig.cs`

```csharp
public sealed class BiomeRegionParams
{
    public float CellSizeChunks { get; set; } = 4f;
    public float JitterStrength { get; set; } = 0.35f;
    public float FeatherWidth { get; set; } = 2f;  // CHANGED FROM 12
    public int MaxRegionMix { get; set; } = 3;

    public static BiomeRegionParams Default() => new();
}
```

### Shader Code (Already Fixed ✅)
No changes needed - the shader properly uses `params.uRegionFeather`. Once the C# config value changes, it will automatically apply.

## Testing Recommendations

Try these `FeatherWidth` values to find your preference:

| Value | Visual Result |
|-------|---------------|
| `1f` | Very sharp boundaries, almost no blending (may look artificial) |
| `2f` | **Recommended** - Natural 2-block transition, clean regions |
| `3f` | Slightly softer transition, still clean |
| `4f` | Noticeable blending, but acceptable |
| `6f` | Getting too soft, visible mixing |
| `12f` | **Current** - Spray paint effect ❌ |

## Performance Impact
**Positive** - Reducing feather width from 12 to 2:
- Fewer blocks in "mixed" state
- Less texture blending computation in fragment shader
- Better texture cache coherency (more blocks use same texture)

## Summary

The biome system now has:
1. ✅ Large-scale Alpine regions (via macro noise threshold)
2. ✅ Natural Voronoi cell boundaries (already working)
3. ❌ **Excessive texture feathering** (needs config change: 12 → 2)

Once `FeatherWidth` is reduced to `2f`, you'll have:
- Natural curved boundaries with organic pockets
- Clean, recognizable biome regions  
- Smooth 2-block transitions at borders
- No more spray-paint effect!

The fix is literally **one number change** in `TerrainConfig.cs`.
