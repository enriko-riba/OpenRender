# AO Chunk Border Fix

## Problem
Visible dark lines (incorrect ambient occlusion) appearing at chunk boundaries in the voxel terrain, affecting **all biomes** including Alpine snow at high altitudes. The issue manifested as:
- Dark edges on chunk borders (especially visible at local coordinates x=0, x=15, z=0, z=15)
- Ocean biome appearing deep inland (incorrect terrain type assumptions)
- Snow textures in Alpine biomes getting darkened at chunk edges

## Root Cause
The `isSolid()` function in `compute-compact.comp` had an **incomplete procedural fallback** for neighbor chunks not in the current batch. The function was calling:
- `generateHeight()` - to get terrain height
- `isCave()` - to check for cave carving

But it was **NOT calling `generateBlockType()`**, which contains the complete terrain generation logic including:
- Ocean vs Land distinction based on continentalness
- 3D density overhangs for mountainous terrain  
- Surface/subsurface/shoreline block type determination
- Flooded cave logic

This meant the AO calculation was making **incorrect assumptions** about what blocks existed at neighbor positions, treating all terrain below height as solid without accounting for:
- Carved overhangs in mountains
- Ocean floor vs land terrain
- Different block types (air, water, solid)

The result: AO samples at chunk boundaries saw "phantom solid blocks" that weren't actually there, darkening the edges.

## Solution
Replace the incomplete procedural logic with a **call to `generateBlockType()`**, which executes the **exact same terrain generation** used to create the chunks.

### Before (Broken):
```glsl
// Incomplete - only checks height and caves
int height = generateHeight(wx, wz);

if (wy > height) {
    if (wy <= WATER_LEVEL) return false; // Water
    return false; // Air
}

if (wy > 0) {
    int depth = height - wy;
    float slope = 0.0;
    if (depth < 15) {
        slope = getSlope(wx, wz);
    }
    if (isCave(wx, wy, wz, depth, slope)) {
        // ... cave flooding logic
        return false;
    }
}

return true; // Assume solid
```

### After (Fixed):
```glsl
// Complete - uses full terrain generation
int height = generateHeight(wx, wz);
uint blockType = generateBlockType(height, wy, wx, wz);
return blockType > BD_WATER;
```

The `generateBlockType()` function handles:
✅ Height-based air/water determination  
✅ Continentalness check for ocean vs land  
✅ 3D density overhangs in mountains  
✅ Cave carving with proper depth/slope logic  
✅ Flooded cave detection  
✅ Surface/subsurface/shoreline block types  

## Code Structure
```glsl
bool isSolid(ivec3 pos, uint chunkIdx, vec3 chunkWorldOffset) {
    // 1. Quick bounds checks (y < 0 or y >= CHUNK_Y_SIZE)
    
    // 2. Check if inside current chunk (fast path - read from buffer)
    
    // 3. Try to find neighbor chunk in current batch
    //    - Calculate neighbor chunk coordinates
    //    - Search batch for matching chunk
    //    - Read from neighbor's buffer if found (with bounds check)
    
    // 4. Procedural fallback (neighbor not in batch):
    //    a. Check world edits
    //    b. Generate terrain height
    //    c. Call generateBlockType() - CRITICAL FIX
    //    d. Return solid if blockType > BD_WATER
}
```

## Related Files
- `src/spyro-game/Shaders/compute-compact.comp` - Fixed AO calculation
- `src/spyro-game/Shaders/terrain-common.glsl` - Terrain generation functions (generateBlockType, generateHeight, isCave)
- `src/spyro-game/Shaders/compute-visibility.comp` - Reference implementation for neighbor handling

## Testing
The fix should eliminate:
1. ✅ Dark edges at chunk boundaries in all biomes
2. ✅ Ocean biome appearing inland (was caused by missing continentalness check)
3. ✅ Incorrect AO on Alpine snow at chunk borders
4. ✅ Missing AO for carved overhangs/caves at boundaries
5. ✅ Flooded cave artifacts at chunk edges

## Performance Impact
**Slightly increased** - `generateBlockType()` is more expensive than the old logic, but:
- Only affects AO samples crossing chunk boundaries (edge blocks only)
- Only when neighbor chunk is not in current batch
- Most AO samples remain fast path (current chunk buffer reads)
- The procedural generation is already cached via texture/buffer lookups where possible

## Key Insight
**AO calculation MUST use identical terrain generation logic as chunk creation.** Any divergence causes visual artifacts at boundaries. The fix ensures the procedural fallback matches `generateBlockType()` exactly, preventing phantom solid blocks.
