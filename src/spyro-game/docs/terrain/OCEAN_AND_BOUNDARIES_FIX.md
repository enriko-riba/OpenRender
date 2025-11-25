# Critical Biome Fixes - Ocean on Land & Straight Lines

## Issue 1: 🌊 FIXED - Ocean Biome Appearing on Dry Land

### The Problem
Dark blue ocean biome was appearing on **solid land above sea level**. The light blue water was correctly placed, but the **terrain itself** was being assigned ocean biome textures (bedrock).

### Root Cause
```glsl
// BROKEN CODE:
if (tC < params.uOceanThreshold) {  // tC = continentalness noise [-1,1]
    return OCEAN_BIOME_ID;
}
```

**Continentalness is just a noise field** - it has NO relationship to actual terrain height!

- Low continentalness (< 0.35) was intended to mean "ocean"
- But the **height spline** can generate land above water even with low continentalness
- Result: Land with continentalness 0.3 but height 60 → Incorrectly marked as Ocean

### The Fix
```glsl
// FIXED CODE:
float terrainHeight = getHeight(p.xz);  // Get ACTUAL generated height
bool isUnderwater = terrainHeight < float(WATER_LEVEL);

if (isUnderwater) {
    return OCEAN_BIOME_ID;  // Only ocean if actually underwater!
}
```

**Now checks the REAL terrain height** against water level (35 blocks).

### Why This Happened
The original design assumed:
- `continentalness < threshold` → ocean floor
- `continentalness >= threshold` → land

But the **height spline mapping** can create land at any continentalness value! Example:

```
Height Spline Mapping:
continentalness: 0.0  0.2  0.3  0.35  0.5  0.75  1.0
height:         -80  -50   10   35    50   120   320
                 ^^^  ^^^  ^^^  ^^^
                Ocean     Land! Land!  Land!

The 0.3 continentalness maps to height 10 (ABOVE water level 35)
But code said: "0.3 < 0.35 threshold → Ocean biome"
Result: DRY LAND with OCEAN biome! ❌
```

### Expected Behavior Now
- **Terrain height < 35** (water level) → Ocean biome ✅
- **Terrain height >= 35** → Land biomes (Plains, Forest, etc.) ✅
- **Above-water terrain** never gets ocean textures ✅

---

## Issue 2: ⚠️ Straight Lines Between Biomes (PARTIAL)

### Observations from Image
1. **Dark blue (ocean) vs Cyan (Taiga)** - Sharp line
2. **Cyan (Taiga) vs Green-grey (Highlands)** - Sharp line
3. **Curved coastline** - Works correctly!

### Analysis

#### What's Working ✅
- The **coastline** (land/water boundary) is curved and natural
- This proves the Voronoi system CAN create curves

#### What's NOT Working ❌
- **Land biome boundaries** still have straight sections
- Suggests Voronoi **feathering** isn't being applied

### Possible Causes

#### A. Config Not Loaded
The `FeatherWidth = 2f` we set might not be in your active config file.

**Check**: Look for `terrainconfig.json` or similar file in your project directory.

```json
// Should be:
{
  "BiomeRegions": {
    "CellSizeChunks": 4.0,
    "JitterStrength": 0.35,
    "FeatherWidth": 2.0,  // ← Check this!
    "MaxRegionMix": 3
  }
}

// Might still be:
{
  "BiomeRegions": {
    "FeatherWidth": 12.0  // ← BAD! Old value
  }
}
```

#### B. Fragment Shader Not Using Voronoi
Looking at `voxel-terrain.frag` line 111-114:

```glsl
// CRITICAL FIX: Use ONLY the primary biome (no blending)
uint primaryBiomeId = getBiomeId(voxelCenter);
baseColor = sampleBiomeTexture(primaryBiomeId, int(layer), vTexCoord);
```

**This is the problem!** The fragment shader is:
1. Calling `getBiomeId()` directly (which returns single biome)
2. **NOT calling `getBiomeWeights()`** (which does Voronoi blending)

The Voronoi system exists in `getBiomeWeights()` but **the fragment shader isn't using it**!

### Why Voronoi Isn't Being Used

The comment in the code says:
```glsl
// CRITICAL FIX: Use ONLY the primary biome (no blending)
// Biome blending at the pixel level causes gradient artifacts between biomes.
```

This was done to avoid **texture blending artifacts** (gradual color transitions between grass and sand textures, which looks bad).

**The Solution**: Voronoi should be used for **biome selection** (choosing which biome a block belongs to), not for **texture blending** (mixing textures at pixel level).

### Recommended Architecture

There are two approaches:

#### Option A: Per-Block Biome Assignment (Current - Causes Lines)
```
For each block:
  biome = getBiomeId(blockCenter)  // Single biome
  texture = biomeTextures[biome]
```

**Pros**: No texture blending artifacts  
**Cons**: Sharp biome boundaries (what you're seeing)

#### Option B: Voronoi-Based Block Assignment (Recommended)
```
For each block:
  RegionMix mix = getBiomeWeights(blockCenter)  // Voronoi
  biome = mix.biomeIds[0]  // Take PRIMARY biome only
  texture = biomeTextures[biome]
```

**Pros**: Smooth Voronoi boundaries, still no texture blending  
**Cons**: Requires fragment shader change

### The Fix for Straight Lines

Change `voxel-terrain.frag` line 111-114 to:

```glsl
// Use Voronoi for biome selection, but don't blend textures
vec3 voxelCenter = floor(vWorldPos - N * 0.01) + 0.5;

// Get Voronoi region mix
RegionMix mix = getBiomeWeights(voxelCenter);

// Use PRIMARY biome only (no texture blending)
uint primaryBiomeId = mix.biomeIds[0];  // Changed from getBiomeId()
baseColor = sampleBiomeTexture(primaryBiomeId, int(layer), vTexCoord);
```

This will:
- ✅ Use Voronoi system for curved boundaries
- ✅ Still assign single biome per block (no gradient)
- ✅ Respect `FeatherWidth` parameter
- ✅ Create natural transitions

---

## Summary of Changes

### File: `terrain-common.glsl` - `getBiomeId()` Function

**Before**:
```glsl
if (tC < params.uOceanThreshold) {
    return OCEAN_BIOME_ID;  // ❌ Based on noise, not actual height
}
```

**After**:
```glsl
float terrainHeight = getHeight(p.xz);
bool isUnderwater = terrainHeight < float(WATER_LEVEL);

if (isUnderwater) {
    return OCEAN_BIOME_ID;  // ✅ Based on actual terrain height
}
```

### File: `voxel-terrain.frag` - Biome Selection (NEEDS FIXING)

**Current** (causes straight lines):
```glsl
uint primaryBiomeId = getBiomeId(voxelCenter);
```

**Should be** (uses Voronoi):
```glsl
RegionMix mix = getBiomeWeights(voxelCenter);
uint primaryBiomeId = mix.biomeIds[0];
```

---

## Testing

### Test 1: Ocean Biome Fix
1. Fly over coastal areas
2. Press F3 to enable biome debug view
3. **Check**: Dark blue (ocean) should ONLY appear:
   - In actual water (light blue water blocks)
   - On ocean floor (below water level)
4. **Never**: On dry land above water level

### Test 2: Biome Boundaries (After Fragment Fix)
1. Find boundary between two land biomes (Taiga/Highlands)
2. **Check**: Boundary should be:
   - Curved and organic
   - Follow Voronoi cell pattern
   - Have 2-block smooth transition (if FeatherWidth = 2)
3. **Should not**: Be perfectly straight for long distances

---

## Why Continentalness ≠ Terrain Height

**Continentalness**: A noise field used to **influence** terrain shape  
**Terrain Height**: The **actual generated** elevation after applying:
- Height spline mapping
- Erosion
- Ridge noise
- Cliff noise
- Domain warping

**Example Flow**:
```
continentalness(x,z) = 0.3  (just a noise value)
         ↓
height_spline[0.3] = 10 meters  (lookup in spline)
         ↓
+ erosion effects
+ ridge noise
+ cliff carving
         ↓
final_height(x,z) = 45 meters  (ABOVE water level!)
```

**Biome must check `final_height`, not `continentalness`!**

---

## Next Steps

1. ✅ Ocean fix is complete (already applied)
2. ❌ Fragment shader needs update to use `getBiomeWeights()` for curved boundaries
3. ⚠️ Verify config file has `FeatherWidth: 2.0`

Would you like me to apply the fragment shader fix now?
