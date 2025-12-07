# Water System Redesign Plan

## Overview

This document outlines the plan to replace the global water level system with a more realistic ocean-only water system, with future support for local water bodies (lakes, rivers).

### Design Philosophy: Global Sea Level vs. Local Water Bodies

**`VoxelHelper.WaterLevel` (Y=35) serves TWO distinct purposes:**

1. **Ocean Sea Level (GLOBAL)** - All oceans must be at the same Y level for consistency. This is intentional and required.
2. **Height Spline Offset (TECHNICAL)** - The height spline outputs relative heights; WaterLevel translates them to absolute Y coordinates.

**Lakes use TERRAIN-RELATIVE water levels:**
- Lake water level = `baseHeight + offset` (based on smooth regional terrain, not global sea level)
- This allows lakes at any elevation (mountain lakes, highland lakes, etc.)
- Water fills where actual terrain dips below the regional baseHeight level

## Current Problems (Before Implementation)

1. **Global Water Level**: `VoxelHelper.WaterLevel = 35` floods all depressions below Y=35
2. **Inland Ponds**: Small circular water bodies appear everywhere in low-lying terrain
3. **No Mountain Lakes**: Water can only exist below a fixed Y level
4. **No Rivers**: No flowing water features possible
5. **Beach Rings**: Beach biome incorrectly assigned to inland low areas

---

## Implementation Phases

### Phase 1: Ocean-Only Water ✅ COMPLETED
Remove automatic inland water filling. Water only appears in ocean biomes.

### Phase 2: Local Water Bodies (Lakes) ✅ COMPLETED
Add sparse lake detection using noise + local basin analysis.

### Phase 3: Rivers 🔲 NOT STARTED
Carve river channels from highlands to ocean.

---

## Progress Tracking

### Phase 1: Ocean-Only Water

| Task | Status | Notes |
|------|--------|-------|
| Modify `GenerateBlock` water placement | ✅ Done | Water only placed when `continentalness01 < oceanThreshold` |
| Update underwater detection | ✅ Done | Simplified to `isOceanArea && y <= WaterLevel` |
| Remove `IsNearWater` helper | ✅ Done | No longer needed |
| Fix `BiomeSelector` coastal detection | ✅ Done | Coast requires BOTH continentalness proximity AND low elevation |
| Update `SelectWithBlend` coastal logic | ✅ Done | Same fix as SelectPrimary |
| Build verification | ✅ Done | Compiles successfully |
| Runtime testing | ✅ Done | No inland ponds, beaches only near ocean |

### Phase 2: Local Water Bodies (Lakes)

| Task | Status | Notes |
|------|--------|-------|
| Add `LakeNoise` config to TerrainConfig | ✅ Done | Sparse noise layer with 1/600 scale |
| Add `LakeThreshold` config | ✅ Done | Default 0.75 (~25% of land can have lakes) |
| Add lake noise sampling to ChunkClimateCache | ✅ Done | LakeNoise01 array with SIMD sampling |
| Implement lake level computation | ✅ Done | Noise-only approach with variable depth |
| Modify `GenerateBlock` for lakes | ✅ Done | Lake water fills depressions above terrain surface |
| Add `BiomeId.Lake` biome | ✅ Done | Sand/gravel surface, priority 85 |
| Update `BiomeSelector` for lake detection | ✅ Done | Lake biome SKIPPED - handled at block level |
| Cross-chunk lake continuity | ✅ Done | Noise-based approach ensures continuity |
| Build verification | ✅ Done | Compiles successfully |
| Runtime testing | 🔲 Pending | Verify lakes appear at various elevations |

### Phase 3: Rivers

| Task | Status | Notes |
|------|--------|-------|
| Design river source detection | 🔲 Pending | High-altitude + humidity threshold |
| Design flow direction algorithm | 🔲 Pending | Follow terrain gradient |
| Design channel carving | 🔲 Pending | Lower terrain along river path |
| Implement river biome | 🔲 Pending | `BiomeId.River` |
| Handle waterfalls | 🔲 Pending | Special case for vertical drops |

---

## Detailed Implementation

### Phase 1 Changes (Completed)

#### CpuTerrainGenerator.cs

**Water placement now checks ocean area:**
```csharp
var isOceanArea = continentalness01 < terrainParams.OceanThreshold;

// Only place water in ocean areas
if (density < 0f)
{
    if (y <= VoxelHelper.WaterLevel && isOceanArea)
        return BlockId.Water;
    return BlockId.Air;
}
```

**Simplified underwater detection:**
```csharp
// Before: Complex check with multiple conditions
var isUnderwater = y <= VoxelHelper.WaterLevel && (isOceanBiome || height < VoxelHelper.WaterLevel);

// After: Simple ocean-area check
var isUnderwater = isOceanArea && y <= VoxelHelper.WaterLevel;
```

#### BiomeSelector.cs

**Fixed coastal detection to require ocean proximity:**
```csharp
// Before (broken): Any low terrain = beach
var isCoastal = !isUnderwater && isNearWaterHeight;

// After (fixed): Only near actual ocean = beach
var isNearOceanByContinentalness = contDistance <= _coastRange;
var isNearWaterHeight = altitudeAboveWater <= _shorelineRange;
var isCoastal = !isUnderwater && !isOceanic && isNearOceanByContinentalness && isNearWaterHeight;
```

---

### Phase 2 Changes (Completed)

#### TerrainConfig.cs
Added lake configuration parameters:
```csharp
public NoiseLayer LakeNoise { get; set; } = new()
{
    BaseScale = 1f / 600f,    // Large-scale lake distribution
    Octaves = 2,
    Persistence = 0.5f,
    Lacunarity = 2.0f,
    DomainWarpScale = 1f / 400f,
    DomainWarpStrength = 40f
};

public float LakeThreshold { get; set; } = 0.75f;  // ~25% of land can have lakes
public float LakeMaxDepth { get; set; } = 5f;      // Maximum lake depth in blocks
public float LakeMinContinentalness { get; set; } = 0.45f; // Lakes only on land
```

**Lake biome definition (Sand shores, not Gravel):**
```csharp
new ((int)BiomeId.Lake, ...,
    surfaceBlock: BlockId.Sand, subsurfaceBlock: BlockId.Sand, deepBlock: BlockId.Stone,
    underwaterSurfaceBlock: BlockId.Sand, underwaterSubsurfaceBlock: BlockId.Gravel),
```

#### ChunkClimateCache.cs
Added lake noise sampling:
```csharp
private readonly float[] _lakeNoise = new float[ColumnCount];
private readonly float[] _lakeNoise01 = new float[ColumnCount];
public ReadOnlySpan<float> LakeNoise01 => _lakeNoise01;
```

#### BiomeId.cs
Added Lake biome:
```csharp
Lake = 13,
```

#### BiomeSelector.cs
**Lake biome detection DISABLED** - Lakes are handled at BLOCK level, not biome level:
```csharp
public BiomeId SelectPrimary(..., float lakeNoise01 = 0f)
{
    // NOTE: Lake biome selection is DISABLED here.
    // Lakes are handled at the BLOCK level in CpuTerrainGenerator.GenerateBlock().
    // The Lake biome was incorrectly being applied to entire regions (including hills)
    // just because lakeNoise01 exceeded threshold, when lakes should only affect
    // the actual water-filled depressions at the block level.
    _ = lakeNoise01; // Suppress unused parameter warning
    
    // Skip Lake biome in iteration - it's handled at block level
    if (biome.Id == (int)BiomeId.Lake)
        continue;
    // ... rest of biome selection uses climate-based matching
}
```

**Coastal detection fix** - Include terrain at exact water level:
```csharp
var isNearWaterHeight = altitudeAboveWater >= -1 && altitudeAboveWater <= _shorelineRange;
var isCoastal = !isUnderwater && !isOceanic && isNearOceanByContinentalness && isNearWaterHeight;
```

#### CpuTerrainGenerator.cs
**Lake water placement logic (terrain-relative):**
```csharp
// Lakes are TERRAIN-RELATIVE - water level based on baseHeight (smooth regional terrain)
// NOT tied to VoxelHelper.WaterLevel - allows lakes at any elevation
if (!isOceanArea && continentalness01 >= config.LakeMinContinentalness)
{
    var lakeNoise01 = climateCache.LakeNoise01[columnIndex];
    if (lakeNoise01 > config.LakeThreshold)
    {
        isLakeArea = true;
        var depthFactor = (lakeNoise01 - config.LakeThreshold) / (1f - config.LakeThreshold);
        // Lake surface = baseHeight (regional) + small offset
        // Water fills where actual terrain height < this level
        lakeWaterLevel = baseHeight + 1f + depthFactor * config.LakeMaxDepth;
    }
}

// Water fills where terrain dips below the lake level
if (isLakeArea && y <= lakeWaterLevel && y > height)
    return BlockId.Water;
```

**Underwater detection for surface blocks (CORRECTED):**
```csharp
// Underwater detection for surface block selection:
// A block is "underwater" if the TERRAIN SURFACE at this column is below water level,
// meaning there's water above this solid block. This is NOT based on the current block's Y.
// 
// - Ocean areas: terrain surface (height) < WaterLevel means water fills above
// - Lake areas: terrain surface (height) < lakeWaterLevel means lake water fills above
// 
// IMPORTANT: Do NOT use (y <= WaterLevel) - that incorrectly marks subsurface blocks
// on land at Y=35 as underwater when the terrain surface is actually at Y=36+.
var isUnderwater = (isOceanArea && height < VoxelHelper.WaterLevel) || 
                   (isLakeArea && height < lakeWaterLevel);
```

---

### Phase 3 Design (Rivers)

Rivers are more complex and require:
1. **Global terrain analysis** to find flow paths
2. **Pre-computed river network** per world seed
3. **Special biome type** (`BiomeId.River`)
4. **Channel carving** into terrain height

This phase may be deferred or implemented as a separate feature.

---

## Component Interaction Diagram

```
TerrainConfig
    ├── OceanThreshold (existing)
    ├── LakeNoise (Phase 2)
    └── LakeThreshold (Phase 2)
           │
           ▼
ChunkClimateCache
    ├── SampleForChunk() - add lake noise sampling (Phase 2)
    └── LakeLevels[] - per-column lake water level (Phase 2)
           │
           ▼
CpuTerrainGenerator
    ├── GenerateBlock() - ocean-only water (Phase 1 ✅)
    └── Lake water logic (Phase 2)
           │
           ▼
BiomeSelector
    ├── SelectPrimary() - fixed coastal detection (Phase 1 ✅)
    └── Lake biome detection (Phase 2)
           │
           ▼
BiomeDefinition
    └── Add BiomeId.Lake (Phase 2)
```

---

## Questions & Decisions

### Resolved
- ✅ **Water only in oceans**: Yes, removes inland pond problem
- ✅ **Beach detection**: Requires both continentalness AND elevation proximity

### Open (Phase 2)
- **Lake frequency**: How common should lakes be? (~5-10% of land area?)
- **Lake depth**: Fixed (3-5 blocks) or variable based on noise?
- **Lake biome**: Should lakes have their own biome?
- **Mountain lakes**: Allow lakes up to what elevation?

---

## Files Modified

### Phase 1
- `src/spyro-game/World/Generation/CpuTerrainGenerator.cs` - Water placement logic
- `src/spyro-game/World/Generation/BiomeSelector.cs` - Coastal detection fix

### Phase 2
- `src/spyro-game/World/TerrainConfig.cs` - Lake noise config (LakeNoise, LakeThreshold, LakeMaxDepth, LakeMinContinentalness)
- `src/spyro-game/World/Generation/ChunkClimateCache.cs` - Lake noise sampling
- `src/spyro-game/World/Generation/CpuTerrainGenerator.cs` - Lake water placement in GenerateBlock
- `src/spyro-game/World/Generation/BiomeSelector.cs` - Lake biome detection (lakeNoise01 parameter)
- `src/spyro-game/World/BiomeId.cs` - Added Lake = 13

### Phase 3 (Planned)
- `src/spyro-game/World/TerrainConfig.cs` - River config
- `src/spyro-game/World/Generation/RiverGenerator.cs` - New class for river carving
- `src/spyro-game/World/BiomeId.cs` - Already has River = 11

---

## Testing Checklist

### Phase 1
- [x] No water in inland depressions
- [x] Ocean water still works correctly
- [x] Beach biome only appears near ocean
- [x] No circular beach patterns inland
- [x] Underwater surface blocks (gravel) only in ocean

### Phase 2
- [ ] Lakes appear at various elevations (including mountains)
- [ ] Lakes are sparse (not everywhere)
- [ ] Lake water level is consistent across chunks
- [ ] Lake shores have sand/gravel biome blocks
- [ ] Lake depth varies based on noise

### Phase 3
- [ ] Rivers flow from highlands to ocean
- [ ] River channels are carved into terrain
- [ ] Waterfalls render correctly
- [ ] Rivers connect to lakes appropriately

---

*Last Updated: 2024-12-06*
*Status: Phase 1 Complete, Phase 2 Complete, Phase 3 Not Started*
