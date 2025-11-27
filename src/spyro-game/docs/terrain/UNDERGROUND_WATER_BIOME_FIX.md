# Critical Fix: Underground Water vs Ocean Biome Detection

## The Problem You Identified 🎯

### Scenario: Underground Water Cave with Land Above
```
Y = 100: [Grass] [Grass] [Grass]  ← Land surface (should be Forest/Plains biome)
Y = 99:  [Dirt]  [Dirt]  [Dirt]
Y = 98:  [Stone] [Stone] [Stone]
...
Y = 30:  [Stone] [Water] [Stone]  ← Underground cave with water
Y = 29:  [Stone] [Water] [Stone]
```

### Initial Broken Fix Attempt ❌
```glsl
// My original "fix":
float terrainHeight = p.y;  // Use fragment Y position
bool isUnderwater = terrainHeight <= float(WATER_LEVEL);
```

**Problem**: Would have caused issues because:
1. Water blocks at Y=30: `30 <= 35` → Ocean biome ✅ (correct)
2. BUT if we checked land blocks: `100 <= 35` → FALSE → Land biome ✅ (also correct!)

**Wait, that actually works?** 🤔

Actually YES for **different blocks**, but NO for **water surface faces**!

## The Real Issue: Water Surface Top Faces

### Water Block at Exactly WATER_LEVEL (Y=35)
```
Y = 36: [Air]    ← Top face of water block renders here
Y = 35: [Water]  ← Water block at water level
```

**Water Top Face Rendering**:
- Face vertices at Y = 36
- With shift-down fix: Y = 35.5
- Biome check: `35.5 <= 35` → **FALSE** ❌
- Result: Water surface gets **land biome** instead of ocean!

## The Correct Solution

**Key Insight**: Biome should be determined by **terrain surface height**, not fragment position!

### For Ocean Floors (Underwater Terrain)
```
Terrain surface at Y = 20 (below water level 35)
→ isUnderwater = TRUE
→ Ocean biome everywhere on this terrain column
→ Ocean floor gets bedrock texture ✅
```

### For Land (Above Water)
```
Terrain surface at Y = 60 (above water level 35)
→ isUnderwater = FALSE
→ Land biome (Forest/Plains/etc.)
→ Surface gets grass/stone texture ✅
```

### For Underground Water (Cave)
```
Terrain surface at Y = 100 (above water level)
Water cave at Y = 30 (below surface)

Surface blocks at Y=100:
→ Biome check uses terrain height = 100
→ 100 <= 35? FALSE
→ Land biome ✅

Water in cave at Y=30:
→ Biome check uses terrain height = 100 (same XZ column!)
→ 100 <= 35? FALSE
→ Land biome for that column
→ But block type is WATER → Uses water texture ✅
```

**Key**: Biome is about the **terrain type at that location**, not individual block types!

## Implementation

### Fragment Shader Approach

```glsl
#ifdef IS_FRAGMENT_SHADER
    // Use terrain surface height approximation from spline
    // This gives us the HEIGHT OF THE TERRAIN SURFACE at this XZ
    float terrainHeight = texture(uHeightSpline, tC).r + float(WATER_LEVEL);
#else
    // Compute shader: Full terrain generation
    float terrainHeight = getHeight(p.xz);
#endif

// Check if TERRAIN SURFACE is underwater (not if fragment is underwater)
bool isUnderwater = terrainHeight <= float(WATER_LEVEL);
```

### Why This Works

**Ocean Floor** (terrain surface at Y=20):
```
terrainHeight = 20 (from spline at this XZ)
20 <= 35 → TRUE
→ Ocean biome
→ All blocks in this column get ocean context
→ Surface layer (Y=20) uses BD_UNDERWATER_SURFACE → sand/bedrock texture
```

**Land with Underground Water** (terrain surface at Y=100, water at Y=30):
```
terrainHeight = 100 (from spline at this XZ)
100 <= 35 → FALSE
→ Land biome (Forest/Plains/etc.)
→ All blocks in this column get land biome context

Block at Y=100 (surface):
→ BD_SURFACE + Forest biome → grass texture ✅

Block at Y=30 (cave water):
→ BD_WATER + Forest biome → water texture ✅
```

**Water Surface** (terrain surface at Y=35):
```
terrainHeight = 35 (from spline)
35 <= 35 → TRUE
→ Ocean biome
→ Top face uses water texture with ocean biome ✅
```

## Block Descriptor vs Biome

This fix clarifies the distinction:

**Block Descriptor** (geology):
- `BD_SURFACE` - Surface layer
- `BD_WATER` - Water block
- `BD_UNDERWATER_SURFACE` - Ocean floor
- Determined by **actual block type at Y position**

**Biome** (climate/region):
- `OCEAN_BIOME_ID` - Underwater terrain
- `PLAINS_BIOME_ID` - Temperate grassland
- `ALPINE_BIOME_ID` - High altitude/cold
- Determined by **terrain surface characteristics at XZ**

**Texture Selection** = `biomeTextures[biomeId][blockDescriptor]`

### Examples

| Terrain Height | Block Y | Block Type | Biome | Texture |
|----------------|---------|------------|-------|---------|
| 20 (ocean) | 20 | BD_UNDERWATER_SURFACE | Ocean | Sand/Bedrock |
| 20 (ocean) | 35 | BD_WATER | Ocean | Water (blue) |
| 60 (land) | 60 | BD_SURFACE | Plains | Grass |
| 60 (land) | 30 | BD_WATER | Plains | Water (blue) |
| 35 (coast) | 35 | BD_WATER | Ocean | Water (blue) |

## Performance Note

Using `texture(uHeightSpline, tC)` in fragment shader is **very cheap**:
- 1D texture lookup
- Already calculated `tC` from continentalness
- No terrain generation needed
- Result is cached by GPU

This is **much faster** than the previous attempt to use `getHeight(p.xz)` which would do full terrain generation!

## Summary

✅ **Ocean floors**: Terrain surface below water → Ocean biome → Bedrock texture  
✅ **Land surfaces**: Terrain surface above water → Land biome → Grass/stone texture  
✅ **Underground water**: Terrain surface above water → Land biome for column, water texture for water blocks  
✅ **Water surface**: Terrain height = 35 → Ocean biome → Water texture  

The fix correctly distinguishes between:
- **Where you are** (fragment Y) - used for nothing
- **What you're rendering** (block descriptor) - determines texture type
- **What terrain type** (surface height at XZ) - determines biome/climate

Perfect! 🎉
