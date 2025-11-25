# Water Issues: Curtains & Yellow Blocks Fix

## Issue Summary

1. **Water curtains below water** - Dark seams at chunk borders
2. **Yellow blocks above water** - Beach biome appearing on land far from shore

## Root Cause Analysis

### Issue 1: Water Curtains (Still Occurring)

The visibility culling fix removed the error correction, but the curtains persist. This suggests the **procedural fallback** in `getVoxel()` might be returning different values than the actual generated water.

**Possible causes**:
- `proceduralVoxel()` returns `BD_WATER` for Y <= WATER_LEVEL when Y > height
- But at chunk borders, if height calculation differs slightly, one chunk sees Water, other sees Air
- Result: Both render faces

**The fix**: Ensure water culling is **symmetric** - if both chunks would generate water procedurally, neither should render a face.

### Issue 2: Yellow (Beach) Blocks Above Water

**Beach biome (ID=1, yellow)** is appearing on land blocks above water level.

**Analysis**:
```
Climate LUT returns:  
Temperature + Humidity → Biome ID

For coastal areas:
- Warm + Humid → Beach (ID=1) ❌ WRONG!
```

**The problem**: Beach is in the **biome LUT** as a climate-based biome, but Beach should be **proximity-based**, not climate-based!

**Beach should only appear**:
- At exact water level (Y = 35)
- Within a few blocks of water horizontally
- On blocks with `BD_SHORELINE` descriptor

Currently, `getBiomeId()` returns Beach from the LUT for any coastal climate, regardless of actual proximity to water!

## The Fixes

### Fix 1: Water Curtain - Improve Water Detection

The issue is that at chunk borders, `getVoxel()` falls back to `proceduralVoxel()` which might not match exactly.

**Solution**: Make water check more forgiving at boundaries.

**File**: `compute-visibility.comp`

```glsl
// Current CHECK_FACE macro:
if (currentIsWater) {
    if (neighborIsTransparent && !neighborIsWater) mask |= faceBit;
}

// Problem: If neighbor returns BD_AIR (0) due to fallback mismatch,
// we think it's Air and render the face!

// FIX: For water blocks deeply underwater, be more lenient
if (currentIsWater) {
    // Only render face if neighbor is definitely NOT water
    // If we're deep underwater and neighbor is Air, it's likely a procedural mismatch
    bool deepUnderwater = (int(ly) < WATER_LEVEL - 2);
    bool neighborMightBeWater = (neighborIsTransparent && nv == BD_AIR && deepUnderwater);
    
    if (!neighborIsWater && !neighborMightBeWater && neighborIsTransparent) {
        mask |= faceBit;
    }
}
```

Actually, this is getting complicated. **Better solution**: Fix the procedural generation to be deterministic!

### Fix 2: Beach Biome Detection

**Beach must be proximity-based, not climate-based!**

**File**: `terrain-common.glsl` - `getBiomeId()`

Add beach detection **after** ocean check but **before** climate LUT:

```glsl
uint getBiomeId(vec3 p) {
    float C = getContinentalness(p.xz);
    float tC = C * 0.5 + 0.5;
    
    #ifdef IS_FRAGMENT_SHADER
        float terrainHeight = texture(uHeightSpline, tC).r + float(WATER_LEVEL);
    #else
        float terrainHeight = getHeight(p.xz);
    #endif
    
    // PHASE 1: Ocean Check
    bool isUnderwater = terrainHeight <= float(WATER_LEVEL);
    if (isUnderwater) {
        return OCEAN_BIOME_ID;
    }
    
    // PHASE 1.5: Beach Detection (NEW!)
    // Beach appears at the water's edge, not based on climate
    // Check if we're at water level AND near actual water
    float heightAboveWater = terrainHeight - float(WATER_LEVEL);
    
    if (heightAboveWater <= 3.0) {  // Within 3 blocks of water level
        // Check if we're in a coastal area (low continentalness)
        // AND actually near the water transition
        if (tC < params.uCoastRange) {  // Coastal region
            // Beach biome for areas just above water
            return 1u;  // BEACH_BIOME_ID
        }
    }
    
    // PHASE 2: Climate-Based Land Biomes
    float t = getTemperature(p);
    float h = getHumidity(p);
    
    uint baseBiomeId = texture(uBiomeLUT, vec2(t, h)).r;
    
    // Skip Beach from LUT - it's handled above
    if (baseBiomeId == 1u) {  // If LUT returns Beach
        baseBiomeId = DEFAULT_FALLBACK_BIOME_ID;  // Use Plains instead
    }
    
    if (baseBiomeId == OCEAN_BIOME_ID) {
        baseBiomeId = DEFAULT_FALLBACK_BIOME_ID;
    }
    
    // PHASE 3: Alpine...
    // (rest unchanged)
}
```

### Better Approach: Remove Beach from Biome LUT Entirely

**The real fix**: Beach shouldn't be in the climate-based biome LUT at all!

**C# Side** - `BiomeDefinition.DefaultSet()`:

Remove or modify Beach biome definition so it's not returned by the LUT.

**Shader Side** - Handle Beach as a special case like Ocean:

```glsl
// After ocean check:
if (isUnderwater) {
    return OCEAN_BIOME_ID;
}

// NEW: Beach detection (elevation + coast proximity)
float heightAboveWater = terrainHeight - float(WATER_LEVEL);
bool isCoastal = (tC < params.uCoastRange);  // Use coast threshold

if (isCoastal && heightAboveWater > 0.0 && heightAboveWater <= 3.0) {
    return 1u;  // BEACH_BIOME_ID
}

// Then continue with climate LUT (which no longer contains Beach)
```

## Implementation Plan

###Step 1: Fix Beach Biome Detection

1. Add beach detection to `getBiomeId()` based on:
   - Terrain height just above water (0-3 blocks)
   - Coastal continentalness (< CoastRange threshold)

2. Make LUT skip Beach biome or return Plains instead

### Step 2: Fix Water Curtains (If Still Present)

1. Debug: Log what `proceduralVoxel()` returns at chunk borders
2. Ensure height calculation is deterministic
3. If needed, add tolerance for water-water boundaries

## Testing

### Test 1: Beach Biome
- **Before**: Yellow blocks appear far inland
- **After**: Yellow only at shorelines, within 3 blocks of water

### Test 2: Water Curtains
- **Before**: Dark seams visible underwater at chunk borders
- **After**: Seamless water rendering

### Expected Visual:
```
Ocean (Blue) → Beach (Yellow, 1-3 blocks) → Land Biomes (Green/etc)
     Y<35          Y=36-38                      Y>38
```

## Quick Fix to Try First

Add this to `getBiomeId()` right after the ocean check:

```glsl
// Quick beach fix
float heightAboveWater = terrainHeight - float(WATER_LEVEL);
if (heightAboveWater >= 0.0 && heightAboveWater <= 3.0 && tC < 0.5) {
    return 1u;  // Beach
}

// Skip beach from LUT
uint baseBiomeId = texture(uBiomeLUT, vec2(t, h)).r;
if (baseBiomeId == 1u) baseBiomeId = 2u;  // Beach→Plains
```

This should immediately fix the yellow blocks issue!
