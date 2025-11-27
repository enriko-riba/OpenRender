# BlockDescriptor Renaming Refactor

## Overview
Renamed all shader constants and variables from `BLOCK_*` naming to `BD_*` (BlockDescriptor) to match the C# `BlockDescriptor` enum, improving consistency across the codebase.

## Changes Made

### 1. terrain-common.glsl
**Old Block Constants:**
```glsl
const uint BLOCK_NONE = 0u;
const uint BLOCK_WATER_LEVEL = 1u;
const uint BLOCK_ROCK = 2u;
const uint BLOCK_SAND = 3u;
const uint BLOCK_DIRT = 4u;
const uint BLOCK_GRASS_DIRT = 5u;
const uint BLOCK_BEDROCK = 8u;
```

**New BlockDescriptor Constants:**
```glsl
const uint BD_AIR = 0u;                    // Air (empty space)
const uint BD_WATER = 1u;                  // Water
const uint BD_SURFACE = 2u;                // Surface layer (grass, snow, sand at surface)
const uint BD_SUBSURFACE = 3u;             // Subsurface layer (dirt below surface)
const uint BD_DEEP_SUBSURFACE = 4u;        // Deep subsurface (rock/stone)
const uint BD_UNDERWATER_SURFACE = 5u;     // Underwater surface (ocean floor)
const uint BD_UNDERWATER_SUBSURFACE = 6u;  // Underwater subsurface (bedrock)
const uint BD_SHORELINE = 7u;              // Shoreline (beach sand)
```

**Updated `generateBlockType()` function:**
- Replaced all `BLOCK_NONE` → `BD_AIR`
- Replaced all `BLOCK_WATER_LEVEL` → `BD_WATER`
- Replaced `BLOCK_GRASS_DIRT` → `BD_SURFACE`
- Replaced `BLOCK_DIRT` → `BD_SUBSURFACE`
- Replaced `BLOCK_ROCK` → `BD_DEEP_SUBSURFACE`
- Replaced `BLOCK_SAND` → `BD_SHORELINE`
- Replaced `BLOCK_BEDROCK` → `BD_UNDERWATER_SUBSURFACE`

### 2. voxel-terrain.frag
**Renamed Variables:**
- `flat in uint vBlockType` → `flat in uint vBlockDescriptor`

**Renamed Functions:**
- `int getGeologyLayer(uint blockType)` → `uint getBlockDescriptor(uint blockDescriptor)`
- New function is now a simple passthrough since BD_* values map directly to texture indices

**Updated References:**
- All `vBlockType` → `vBlockDescriptor`
- All `BLOCK_WATER_LEVEL` → `BD_WATER`
- `int layer = getGeologyLayer(vBlockType)` → `uint layer = getBlockDescriptor(vBlockDescriptor)`

### 3. voxel-terrain.vert
**Renamed Variables:**
- `flat out uint vBlockType` → `flat out uint vBlockDescriptor`

**Updated Comments:**
- Updated comment to reflect "block descriptor (geology layer)" instead of "block type"

## Benefits

1. **Consistency**: Shader code now matches C# enum exactly
2. **Clarity**: `BD_*` prefix makes it clear these are geology layer descriptors, not in-game block types
3. **Simplified Mapping**: BlockDescriptor values (0-7) map directly to texture array indices
4. **Better Documentation**: Comments now explain each descriptor's purpose in the biome texture system

## C# BlockDescriptor Enum (Reference)
```csharp
public enum BlockDescriptor : byte
{
    Air = 0,
    Water = 1,
    Surface = 2,
    Subsurface = 3,
    DeepSubsurface = 4,
    UnderwaterSurface = 5,
    UnderwaterSubsurface = 6,
    ShoreLine = 7,
}
```

## Testing Notes

### What to Test:
1. **Texture Mapping**: Verify each biome's textures still appear correctly:
   - Surface blocks (grass/snow) → Index 2
   - Subsurface blocks (dirt) → Index 3
   - Deep blocks (stone/rock) → Index 4
   - Water blocks → Index 1
   - Beach sand → Index 7

2. **Biome Transitions**: Check that biome boundaries show correct textures without artifacts

3. **Water Rendering**: Ensure water blocks still have wave animation and proper transparency

4. **Alpine Biome**: Verify the original issue (rock at surface) to see if naming fixes any mapping issues

### Known Issues (Unrelated to Renaming):
- **Overhang System**: User reports no overhangs appearing despite system being in place
- **Surface Layer Removal**: Overhang carving may be removing surface/subsurface layers in mountains, exposing deep subsurface (rock) at the top
  - This causes surface blocks to incorrectly map to BD_DEEP_SUBSURFACE (4) instead of BD_SURFACE (2)
  - Suggested fix: Implement Option 1 (disable overhang near surface) or Option 2 (recalculate surface after carving)

## Files Modified
1. `src/spyro-game/Shaders/terrain-common.glsl`
2. `src/spyro-game/Shaders/voxel-terrain.frag`
3. `src/spyro-game/Shaders/voxel-terrain.vert`
4. `src/spyro-game/Shaders/compute-visibility.comp`
5. `src/spyro-game/Shaders/compute-count.comp`
6. `src/spyro-game/Shaders/compute-compact.comp`

## Build Status
✅ Build successful - no compilation errors
