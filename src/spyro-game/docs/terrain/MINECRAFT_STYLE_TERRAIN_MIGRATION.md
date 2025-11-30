# Minecraft-Style Terrain Generation Migration Plan

## Core Principles (CRITICAL)

### 1. Biome Height Blending at Borders
**Problem:** Discrete biome lookup causes chunk boundary discontinuities - chunks at different elevations with vertical cliffs.

**Solution - Minecraft's Approach:**
- Each `BiomeDefinition` has `base_height` and `height_variation` attributes
- At biome transitions, **blend the height attributes** of adjacent biomes, NOT just pick one discrete biome
- Use continuous noise values (sampled at world coordinates) to weight biome contributions
- This guarantees smooth terrain across chunk boundaries because the input is continuous

```csharp
// BAD: Discrete biome lookup causes discontinuities
var biome = GetBiomeAt(localX, localZ);  // Discrete!
var height = biome.BaseHeight;            // Jump at border!

// GOOD: Blend height attributes from all matching biomes
foreach (var biome in AllBiomes)
{
    float weight = CalculateClimateMatch(biome, continentalness, temp, humidity);
    blendedBaseHeight += biome.BaseHeight * weight;
    blendedHeightVar += biome.HeightVariation * weight;
}
// Result is continuous because climate values are continuous
```

### 2. Climate Parameters Determine Biome, Biome Attributes Shape Terrain
- **C/T/H/E/PV** (Continentalness, Temperature, Humidity, Erosion, Peaks/Valleys) select which biome
- **BiomeDefinition** contains `BaseHeight`, `HeightVariation`, `PeaksInfluence`, `ErosionSensitivity`
- Terrain height uses the biome's attributes, not just the climate values directly
- This creates distinct biome characters: deserts are flatter, mountains are peakier

### 3. Continuous Noise for Height, Discrete Biome for Display/Textures
- **Height calculation:** Use continuous climate noise → blend biome height attributes → smooth terrain
- **Biome display/textures:** Use discrete biome lookup → correct textures per biome cell
- These are SEPARATE concerns - don't conflate them

### 4. Ocean Shape Avoidance (Atoll Problem)
**Problem:** Circular ocean patterns (atoll-like) appear when continentalness noise creates ring patterns.

**Solution:**
- Use **domain warping** on continentalness noise to break circular patterns
- Apply different noise frequencies for ocean depth vs land height
- Ensure continentalness gradients are gradual, not oscillating

---

## Current System Problems

1. **Biome computed in shader** - No CPU access, can't debug, can't affect gameplay
2. **BlockDescriptor is geological** - Surface/Subsurface/etc., not actual block types
3. **Single-pass generation** - Height + block type in one pass, no separation
4. **No temperature/humidity storage** - Recomputed every time
5. **Uniform terrain** - Same patterns repeat everywhere
6. **No palette system** - Full voxel data stored per block

## Target Architecture (Minecraft-Style)

### Stage 1: Biome Pre-computation (NEW)
```
Input: Chunk coordinates
Output: BiomeId[4][4] grid per chunk (coarse, like Minecraft)
        + Climate data cache (temperature, humidity, continentalness, erosion, PV)
```

**Why 4x4?** Biomes don't need per-block resolution. 4x4 = 16 biome samples per chunk, interpolated for block queries.

### Stage 2: Base Terrain (Noise)
```
Input: Climate data from Stage 1
Output: Height map + 3D density field
        Fills with: Stone (below surface), Water (below sea level), Air (above)
```

**Key change:** Height is influenced by biome parameters:
- Ocean biome → deep water
- Plains → flat terrain (low PV)
- Mountains → high terrain (high PV + low erosion)
- Amplified by continentalness

### Stage 3: Surface Decoration
```
Input: Base terrain + Biome grid
Output: Surface blocks replaced based on biome
```

**Biome → Surface mapping:**
| Biome | Surface | Subsurface | Deep |
|-------|---------|------------|------|
| Plains | Grass | Dirt | Stone |
| Desert | Sand | Sand | Sandstone |
| Alpine | Snow | SnowDirt | Stone |
| Ocean | Bedrock | Gravel | Stone |
| Beach | Sand | Sand | Stone |
| Taiga | Grass | Dirt | Stone |
| Tundra | SnowGrass | Dirt | Stone |

### Stage 4: Carvers (Caves)
```
Input: Decorated terrain
Output: Caves carved, some flooded
```

### Stage 5: Features (Future)
```
Input: Carved terrain
Output: Trees, ores, structures
```

---

## New Data Structures

### ChunkBiomeData (NEW)
```csharp
public struct ChunkBiomeData
{
    // 4x4 biome grid (16 cells covering 16x16 blocks)
    public byte[] BiomeIds;           // [16] - one per 4x4 area
    
    // Climate parameters (for debugging/gameplay)
    public float[] Temperature;       // [16]
    public float[] Humidity;          // [16]
    public float[] Continentalness;   // [16]
    public float[] Erosion;           // [16]
    public float[] PeaksValleys;      // [16]
}
```

### BlockId Architecture (Flags Enum + Per-Chunk Palette)

**Note:** `BlockId` (in `World/BlockId.cs`) is the new block type enum. The old `BlockType` enum (`World/BlockType.cs`) is obsolete and will be deleted.

`BlockId` is a `[Flags]` enum where **bits encode both the unique ID and properties directly**.
This eliminates the need for separate `BlockDescriptor` or lookup tables.

**Bit Layout (ushort = 16 bits):**
```
Bits 0-9:   Unique block ID (1024 possible blocks)
Bits 10-15: Property flags (Solid, Opaque, Liquid, Translucent, Replaceable, reserved)
```

**Solution: Per-Chunk Palette System**
```
Global Registry: BlockId (ushort with embedded flags)
                          ↓
Chunk Palette:   BlockId[] Palette (max 128 unique values per chunk)
                          ↓  
Voxel Storage:   byte paletteIndex into chunk palette
                          ↓
Lookup:          chunk.Palette[paletteIndex] → BlockId (with flags)
```

**Why 128 max palette entries?**
- Typical chunks use 5-15 block types (Air, Water, Stone, Dirt, Grass, maybe Sand)
- Even complex chunks rarely exceed 50 types
- 7 bits = 128 entries is generous while leaving 1 bit for future use

**Global Block Registry (BlockId enum - ushort with flags):**
```csharp
[Flags]
public enum BlockId : ushort
{
    // === Property Flags (upper 6 bits) ===
    Solid       = 1 << 10,    // Blocks movement
    Opaque      = 1 << 11,    // Blocks light, culls neighbors
    Liquid      = 1 << 12,    // Water/lava behavior
    Translucent = 1 << 13,    // Partial transparency
    Replaceable = 1 << 14,    // Can be overwritten
    
    // === Block IDs with flags (lower 10 bits = ID) ===
    Air         = 0 | Replaceable,
    Water       = 1 | Liquid | Translucent | Replaceable,
    Stone       = 2 | Solid | Opaque,
    Bedrock     = 3 | Solid | Opaque,
    Dirt        = 10 | Solid | Opaque,
    Grass       = 11 | Solid | Opaque,
    Sand        = 20 | Solid | Opaque,
    Ice         = 42 | Solid | Translucent,
    OakLeaves   = 140 | Solid | Translucent,
    // ... up to 1024 unique blocks
}

// Extract parts
public static ushort GetId(this BlockId b) => (ushort)((ushort)b & 0x03FF);
public static bool IsSolid(this BlockId b) => (b & BlockId.Solid) != 0;
public static bool IsOpaque(this BlockId b) => (b & BlockId.Opaque) != 0;
```

**Per-Chunk Storage:**
```csharp
public class ChunkData
{
    // Palette: maps byte index → BlockId (with embedded flags)
    public BlockId[] Palette;      // Max 128 entries typically
    
    // Voxel data: palette index per block
    public byte[] VoxelData;       // 16×16×384 = 98304 bytes
    
    // Get block at position
    public BlockId GetBlock(int x, int y, int z)
    {
        byte idx = VoxelData[y * 256 + z * 16 + x];
        return Palette[idx];  // Returns BlockId with flags!
    }
}
```

**Benefits (No Separate Lookup Table):**
- Flags are embedded in BlockId itself - just use bitmask operations
- `block.IsSolid()` is a single AND operation, no array lookup
- Meshing: `if (!neighbor.IsOpaque()) EmitFace();`
- Shader receives only the 10-bit ID (extracted from palette), flags used on CPU only

---

## Migration Phases

### Phase 1: Biome Pre-computation ✅ COMPLETED
**Goal:** Compute and store biome + climate data per chunk on CPU

1. ✅ Create `ChunkBiomeData` structure - `World/ChunkBiomeData.cs`
2. ✅ Create `BiomeGenerator` class with Minecraft-style climate parameters - `World/Generation/BiomeGenerator.cs`
3. ✅ Compute biome grid during chunk generation (before terrain)
4. ✅ Store in `ChunkVoxelDataCache` alongside voxel data
5. ✅ Update debug UI to show actual biome from stored data - `GameScene.GetBiomeNameForBlock()`

**Files modified:**
- `ChunkVoxelDataCache.cs` - Added biome storage (`StoreBiomeData`, `TryGetBiomeData`, `GetBiomeAtWorldPos`)
- `CpuTerrainGenerator.cs` - Added biome pre-computation, `GetLastChunkBiomeData()`
- `ChunkGenerationJobSystem.cs` - Store biome data after generation
- `ChunkStreamingManager.cs` - Expose `GetBiomeAtWorldPos()` and `TryGetChunkBiomeData()`
- `GameScene.cs` - Updated `GetBiomeNameForBlock()` to use stored biome

**New files:**
- `World/BiomeId.cs` - Biome enum
- `World/ChunkBiomeData.cs` - 4x4 biome grid storage
- `World/Generation/BiomeGenerator.cs` - Climate-based biome selection

### Phase 2: Terrain Variety via Climate 🔄 IN PROGRESS
**Goal:** Height generation uses climate parameters properly

1. Continentalness controls ocean/land/mountain base height
2. Erosion controls terrain smoothness (high erosion = flat)
3. PeaksValleys (PV) controls local height variation
4. Temperature affects snow line
5. Dramatic transitions: use "weirdness" noise for sudden changes

**Key insight from Minecraft:**
- PV (Peaks/Valleys) is separate from continentalness
- You can have a "mountain peak" biome at low continentalness (isolated mountain)
- You can have "plains" at high continentalness (plateau)

### Phase 3: Surface Decoration ✅ Priority: MEDIUM
**Goal:** Biome determines actual block types

1. Create biome → surface block mapping table
2. Replace generic "Surface/Subsurface" with actual BlockId
3. Surface stage runs after base terrain
4. Shore detection uses actual water proximity

### Phase 4: Storage Optimization (Palette) ⚪ Priority: LOW
**Goal:** Reduce memory usage

1. Implement palette system per chunk
2. Bit-pack block indices
3. Only store unique block types

### Phase 5: Cleanup & Shader Simplification ⏸️ DEFERRED
**Goal:** Remove legacy code and unused data structures

**BlockDescriptor Removal:**
`BlockDescriptor` is obsolete. Block properties (solid, opaque, etc.) are derived from `BlockId` via the flags system described above. The shader will use `BlockId` directly for texture lookup via the palette.

**Deferred Work (Low Priority - Current System Works):**
1. Pass biome ID from CPU via vertex attribute instead of recomputing in shader
2. Remove `getBiomeId()` from `terrain-climate.glsl` once biome comes from CPU
3. Simplify `terrain-common.glsl` to only needed constants
4. Remove `BlockDescriptor` enum and all usages
5. Remove old `BlockType` enum (`World/BlockType.cs`) - replaced by `BlockId`
6. Implement `BlockFlags` lookup table

**Files to Review Later:**
- `voxel-terrain.frag` - Currently recomputes biome from noise, could use CPU biome
- `terrain-climate.glsl` - Climate sampling only needed if shader computes biome
- `terrain-common.glsl` - Still needed for constants and UBO bindings

---

## Immediate Action Items

### Step 1: Add Biome Storage to Cache
```csharp
// In ChunkVoxelDataCache
public class ChunkData
{
    public uint[] Voxels;
    public ChunkBiomeData Biome;  // NEW
    public int SurfaceY;          // NEW - cached surface height per column
}
```

### Step 2: Create Climate-Based Biome Selection
```csharp
public static BiomeId SelectBiome(float temperature, float humidity, 
    float continentalness, float erosion, float pv)
{
    // Ocean check (continentalness < threshold)
    if (continentalness < 0.3f) 
        return continentalness < 0.1f ? BiomeId.DeepOcean : BiomeId.Ocean;
    
    // Beach check (near ocean + low elevation)
    if (continentalness < 0.4f && erosion > 0.5f)
        return BiomeId.Beach;
    
    // Alpine check (cold OR high elevation from PV)
    if (temperature < 0.2f || (pv > 0.8f && erosion < 0.3f))
        return BiomeId.Alpine;
    
    // Climate-based land biomes
    if (temperature > 0.7f)
        return humidity < 0.3f ? BiomeId.Desert : 
               humidity < 0.6f ? BiomeId.Savanna : BiomeId.Rainforest;
    
    if (temperature < 0.4f)
        return humidity > 0.5f ? BiomeId.Taiga : BiomeId.Tundra;
    
    // Temperate
    return humidity > 0.6f ? BiomeId.Forest : BiomeId.Plains;
}
```

### Step 3: Height from Multi-Parameter System
```csharp
float GetTerrainHeight(float continentalness, float erosion, float pv, float weirdness)
{
    // Base height from continentalness (ocean to mountain base)
    float baseHeight = SampleContinentalnessSpline(continentalness);
    
    // Erosion flattens terrain (high erosion = flat)
    float erosionFactor = 1.0f - erosion * 0.7f;
    
    // PV adds peaks and valleys
    float pvHeight = pv * 80f * erosionFactor;  // Up to 80 blocks of variation
    
    // Weirdness creates dramatic transitions
    float weirdFactor = weirdness > 0.7f ? (weirdness - 0.7f) * 3f : 0f;
    float weirdHeight = weirdFactor * 60f;  // Sudden 60-block cliffs
    
    return baseHeight + pvHeight + weirdHeight;
}
```

---

## Temperature/Height Relationship for Alpine

The current system checks `elevation > AlpineElevation` but this doesn't account for temperature.

**Minecraft approach:**
- Temperature decreases with altitude (lapse rate)
- Alpine biome is selected when `adjusted_temperature < 0.2`
- This naturally creates snow caps on mountains

```csharp
float GetAdjustedTemperature(float baseTemp, float elevation)
{
    // Lapse rate: ~0.6°C per 100m in real world
    // In game: 0.003 per block (300 blocks = full temp range)
    float altitudeAboveSeaLevel = Math.Max(0, elevation - WaterLevel);
    return baseTemp - altitudeAboveSeaLevel * 0.003f;
}
```

---

## Debug UI Improvements

After migration, the debug UI can show:
```
Block: Grass @ (100, 65, 200)
Biome: Plains (ID: 2)
Climate: T=0.55 H=0.45 C=0.62 E=0.38 PV=0.22
Surface Y: 65
Chunk: (6, 12) Biome Grid: [Plains, Plains, Forest, Plains...]
```

This gives full visibility into terrain generation decisions.

---

## File Structure After Migration

```
World/
├── Generation/
│   ├── BiomeGenerator.cs         # NEW: Climate → Biome selection
│   ├── TerrainGenerator.cs       # Renamed from CpuTerrainGenerator
│   ├── SurfaceDecorator.cs       # NEW: Biome → Block type
│   ├── CaveCarver.cs             # Extracted from TerrainGenerator
│   └── ClimateNoise.cs           # NEW: All climate noise functions
├── BlockId.cs                    # Block type registry (replaces old BlockType.cs)
├── BiomeId.cs                    # Biome enumeration
├── ChunkBiomeData.cs             # Per-chunk biome storage
└── ... existing files
```

---

## Success Criteria

1. ✅ Debug UI shows correct biome name from stored data
2. ✅ Alpine biome appears on cold/high areas
3. ✅ Ocean/land distribution controlled by continentalness
4. ✅ Varied terrain: some areas flat, some mountainous, some with cliffs
5. ✅ Temperature varies with altitude (snow caps)
6. ✅ No repeating patterns - each area feels unique
