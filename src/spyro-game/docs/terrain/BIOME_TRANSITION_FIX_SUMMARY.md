# Biome Transition Fix Summary

## Changes Made

### 1. Shader: `terrain-common.glsl` - `getBiomeId()` Function
**Purpose**: Create large-scale Alpine regions with natural boundaries instead of per-block random spray

**Key Changes**:
- Removed per-block probability with position hash
- Added macro-scale FBM noise (scale 0.015, 2 octaves)
- Combined altitude influence (70%) with boundary noise (30%)
- Threshold at 0.65 creates clean regional Alpine areas
- Voronoi system handles detailed boundary shape

**Result**: Alpine appears as large coherent regions with organic curved boundaries

### 2. Config: `TerrainConfig.cs` - `BiomeRegionParams.FeatherWidth`
**Purpose**: Reduce texture blending zone from 75% of a chunk to just 2 blocks

**Key Change**:
```csharp
public float FeatherWidth { get; set; } = 2f;  // Was: 12f
```

**Impact**:
- **Before**: 12-block feathering = almost entire chunks in transition
- **After**: 2-block feathering = clean biomes with subtle transitions

## Visual Comparison

### Before Fixes
```
❌ Spray Paint Effect:
F F F M M M M A A A
F F M M M M M M A A
F M M M M M M M M A
M M M M M M M M A A

F = Forest, M = Mixed, A = Alpine
Problem: Only 3 pure blocks, then 7 blocks of mixing!
```

### After Fixes
```
✅ Natural Boundaries:
F F F F F F M A A A
F F F F F F M A A A
F F F F F F A A A A
F F F F F A A A A A

Problem: Large pure regions, 1-2 blocks smooth transition
```

## Files Modified

1. **`src/spyro-game/Shaders/terrain-common.glsl`**
   - Modified: `getBiomeId()` function
   - Lines: ~520-560

2. **`src/spyro-game/World/TerrainConfig.cs`**
   - Modified: `BiomeRegionParams.FeatherWidth` default value
   - Lines: ~430-445

3. **`src/spyro-game/docs/terrain/NATURAL_BIOME_TRANSITIONS.md`**
   - Created: Full documentation of the fix
   - Explains both shader and config changes

## Testing

### Expected Results:
- ✅ Large Alpine regions at high altitudes
- ✅ Natural curved boundaries (via Voronoi)
- ✅ Clean biome interiors (95% pure biome)
- ✅ Smooth 2-block transitions at borders
- ✅ No individual snow blocks scattered in forests
- ✅ Organic pockets of one biome extending into another

### Adjustment Options:
If 2 blocks feels too sharp, try these values:
- `1f` - Very sharp (almost no transition)
- `2f` - **Recommended** (clean + natural)
- `3f` - Softer (still good)
- `4f` - Noticeable blending
- `6f` - Getting too soft

## Technical Details

### Feather Width Math:
```
CellSize = 4 chunks × 16 blocks = 64 blocks
FeatherWidth = 2 blocks

In Voronoi code:
feather = FeatherWidth / CellSize
feather = 2 / 64 = 0.03125 (normalized cell space)

This means:
- 3.125% of cell diameter is transition zone
- 96.875% is pure biome
- Perfect for clean natural look!
```

### Alpine Boundary Math:
```
Altitude influence: smoothstep(170, 230, elevation)
- Below 170: 0% Alpine chance
- 170-230: Gradual ramp
- Above 230: 100% Alpine chance

Boundary noise: fbm(pos * 0.015)
- Scale 0.015 = 1 / 66.67 blocks wavelength
- Creates large organic variations

Combined: 70% altitude + 30% noise
- Threshold 0.65 creates clear boundary
- Still has natural variation from noise
```

## Performance Impact

### Positive Changes:
- ✅ Fewer blocks in "mixed" state (less texture sampling)
- ✅ Better texture cache coherency
- ✅ Reduced fragment shader blending work
- ✅ Cleaner texture atlasing

### Neutral Changes:
- The macro-scale noise in `getBiomeId()` adds minimal cost (2 octaves FBM)
- Called per-block during generation (not per-frame)

## Migration Notes

### For Existing Worlds:
These changes affect **terrain generation only**. Existing chunks are **not modified**.

To see the fix:
1. Fly to ungenerated terrain, OR
2. Delete `*.chunk` files to regenerate

### For New Worlds:
Changes apply automatically. No migration needed.

## Rollback (If Needed)

If you prefer the old behavior:

### Revert Shader Changes:
```glsl
// In getBiomeId(), replace the whole "Alpine favorability" section with:
float alpineProb = alpineProbBase + (alpineNoise - 0.5) * 0.4 * transitionInfluence;
alpineProb = clamp(alpineProb, 0.0, 1.0);

uint posHash = hash(uint(int(p.x)) + hash(uint(int(p.z)), params.uSeed + 4000u), params.uSeed + 5000u);
float threshold = float(posHash) / 4294967295.0;

if (alpineProb > threshold) return ALPINE_BIOME_ID;
```

### Revert Config Changes:
```csharp
public float FeatherWidth { get; set; } = 12f;  // Original value
```

But honestly, you won't want to! 😊
