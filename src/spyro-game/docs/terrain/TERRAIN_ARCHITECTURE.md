# Terrain Generation Architecture

This document describes the target architecture for terrain generation in spyro-game, aligned with modern Minecraft (1.18+) terrain generation principles.

## Overview

The terrain generation pipeline follows Minecraft's sequential approach where **biome assignment happens FIRST** using global noise parameters, and then biome-specific properties drive terrain shaping. This ensures:

1. **Seamless biome transitions** - Biomes are determined by continuous global noise, not chunk boundaries
2. **Consistent terrain-biome matching** - Terrain shape is derived FROM biome data, not vice versa
3. **Deterministic water levels** - Aquifer system uses coordinate-based noise, no runtime flood-fill
4. **Performance as priority #1** - Every stage must be profiled and optimized for real-time streaming

## Generation Pipeline Stages

### Stage 1: Biome Assignment (First)

**Purpose**: Determine the biome for every column using global climate noise parameters.

**Inputs**: World position (X, Z)

**Process**:
1. Sample 5 global noise parameters at each column:
   - **Continentalness (C)**: Controls ocean vs land distribution
   - **Temperature (T)**: Hot vs cold climate zones
   - **Humidity (H)**: Wet vs dry regions
   - **Erosion (E)**: Terrain roughness/smoothness
   - **Peaks/Valleys (PV)**: Ridge noise for mountain peaks

2. Use these parameters to look up a biome from a multi-dimensional biome table

**Output**: 
- BiomeId per column (stored in `ChunkBiomeData`)
- **Raw climate values preserved** for Stage 2 (especially PV and E which have dual purpose)

**Key Principle**: The terrain is still conceptually flat at this point. Biomes exist as data assignments, not physical shapes.

**Important**: The PV (Peaks/Valleys) and E (Erosion) noise values serve a **dual purpose**:
1. Input to biome lookup table (determines biome type)
2. Raw values passed through to Stage 2 (modulates terrain height within that biome)

Both uses must read from the same cached noise sample to ensure consistency.

### Stage 2: Terrain Density Generation (Shapes Land Using Biome Data)

**Purpose**: Build the 3D world using density functions parameterized by biome properties.

**Inputs**:
- BiomeId from Stage 1
- Biome's `base_height` and `height_variation` attributes
- **Raw PV and E values** from Stage 1 climate cache (not re-sampled)
- 3D position (X, Y, Z)

**Process**:
1. Look up biome properties:
   - `base_height`: Base elevation offset for this biome
   - `height_variation`: How much terrain height varies
   - `peaks_influence`: How much PV noise affects terrain
   - `erosion_sensitivity`: How much erosion smooths terrain

2. Calculate 3D density at each voxel:
   ```
   density(x, y, z) = 
       biome.base_height 
       + biome.height_variation * terrain_noise(x, z)
       - y
       + 3D_modifiers(overhangs, caves)
   ```
   
   The `-y` term is the fundamental trick: it creates a horizontal "surface" where density transitions from positive (solid) to negative (air). The biome's `base_height` shifts this surface up or down.

3. Density > 0 = solid, Density < 0 = air (or water if in aquifer)

**Output**: Voxel density values determining solid vs air

**Key Principle**: The biome's height parameters DRIVE the density function. Ocean biomes have low `base_height`, producing terrain below water level. Mountain biomes have high `height_variation`, producing dramatic peaks.

**3D Modifiers**: For Minecraft 1.18-style terrain with dramatic overhangs and varied shapes, the `3D_modifiers` should use continuous 3D noise (not just 2D heightmap + overlay). This is addressed in Phase 4 of the migration.

### Stage 3: Aquifer System (Water Level Determination)

**Purpose**: Determine water levels using deterministic noise, not flood-fill.

**Process**:
1. For each voxel, calculate a "local water level" (LWL) using 3D noise
2. If a voxel's density < 0 (air) AND Y <= LWL, the voxel becomes water
3. The aquifer noise is continuous across the entire world, so adjacent water bodies naturally connect at matching levels

**Key Principle**: Water containment is automatic because:
- Solid terrain (density > 0) ALREADY surrounds the water
- The aquifer only fills air spaces that the density function created
- No runtime flood-fill needed - it's all deterministic coordinate-based lookup

**Ocean vs Lake Distinction**:
- **Ocean biomes**: Have very low `base_height` (e.g., -25 to -30 blocks relative to sea level). The density function naturally produces terrain BELOW the global sea level. The aquifer fills all air blocks up to Y=35 (global sea level).
- **Inland water bodies**: Use local aquifer noise that can vary. The aquifer system samples 3D noise to determine if a particular air pocket should contain water and at what level.
- The biome's `base_height` property is what causes oceans to exist - it's not a special case, just a biome with terrain below water level.

### Stage 4: Surface Block Assignment

**Purpose**: Convert generic "solid" into specific block types based on biome.

**Process**:
1. For each solid voxel adjacent to air:
   - Look up biome at that column
   - Assign biome's `SurfaceBlock` (grass, sand, snow)
   
2. For subsurface voxels (2-4 blocks below surface):
   - Assign biome's `SubsurfaceBlock` (dirt, sand)
   
3. For deep voxels:
   - Assign biome's `DeepBlock` (stone, sandstone)

**Special Cases**:
- Beach: Sand where land meets ocean
- Underwater: Different surface/subsurface blocks (gravel, clay)

### Stage 5: Carvers (Caves and Ravines)

**Purpose**: Cut caves through the already-generated solid terrain.

**Process**:
1. Use 3D noise functions to identify cave regions
2. "Cheese" caves: Large irregular chambers using fbm noise
3. "Spaghetti" caves: Long winding tunnels using distance-from-2-noise-fields
4. Carvers respect biome configuration for density/frequency

**Key Principle**: Carvers MODIFY existing terrain, they don't generate it. The solid terrain must exist first.

### Stage 6: Decoration (Vegetation, Structures)

**Purpose**: Place biome-specific features on the surface.

**Process**:
1. Trees, flowers, grass based on biome's `VegetationRules`
2. Structures (future): Villages, dungeons, etc.

---

## Performance Requirements

**Performance is Priority #1.** The terrain system must support real-time chunk streaming without frame drops. Every stage must be profiled and optimized.

### Known Performance Hotspots

| Stage | Operation | Current Cost | Target | Mitigation |
|-------|-----------|--------------|--------|------------|
| Stage 1 | 2D Noise Sampling (6 params × 256 columns) | ~1.5ms | <1ms | SIMD batching via `ChunkClimateCache` |
| Stage 2 | 3D Density Evaluation (256 × 384 voxels) | ~3-5ms | <2ms | Sparse sampling + trilinear interpolation |
| Stage 3 | Aquifer Lookup | ~0.5ms | <0.3ms | 2D aquifer with Y-clamped regions |
| Stage 5 | Cave Carving (3D noise) | ~2ms | <1ms | Reuse sparse 3D grid from Stage 2 |
| Meshing | Face generation | ~2-4ms | <2ms | Greedy meshing, neighbor caching |

### Optimization Strategies

1. **SIMD Batching**: All 2D noise operations should use `NoiseDotNet` with `Vector256<float>` internally. The `ChunkClimateCache` already does this - ensure no regression.

2. **Sparse 3D Sampling**: Don't evaluate 3D noise at every voxel. Sample on a coarse grid (every 4 blocks) and trilinear interpolate. Current implementation uses 5×5×97 sparse grid = 2,425 samples vs 98,304 full samples (~40× reduction).

3. **Early-Out Optimizations**:
   - Skip density calculation for Y > estimated_surface + max_overhang_range
   - Skip cave carving for ocean biome columns
   - Skip surface block assignment for deep underground voxels

4. **Cache Reuse**:
   - Climate values sampled once, used by both biome selection AND height calculation
   - Sparse 3D noise grid reused for both overhangs AND caves
   - Neighbor chunk data cached for border meshing

5. **Parallel Generation**:
   - Use `BlockingCollection` + dedicated worker threads (current `ChunkGenerationJobSystem` pattern)
   - Keep GL thread free - generation happens on background threads
   - Throttle GPU uploads to avoid frame spikes

### Performance Budget per Chunk

| Operation | Budget | Notes |
|-----------|--------|-------|
| Climate + Biome | 1.5ms | Stage 1 |
| Density + Blocks | 3.0ms | Stages 2-4 |
| Cave Carving | 1.0ms | Stage 5 |
| Vegetation | 0.5ms | Stage 6 |
| Meshing | 2.0ms | Face generation |
| **Total CPU** | **8.0ms** | Per chunk on worker thread |
| GPU Upload | 0.5ms | Staging buffer copy |

Target: Generate + mesh a chunk in under 8ms to support streaming at 60 FPS with multiple chunks in flight.

---

## Debug & Statistics System

The terrain system must expose comprehensive statistics for performance monitoring and debugging. These are displayed in the debug HUD (F3 overlay).

### Required Metrics

#### Generation Pipeline Stats
```csharp
public record struct TerrainGenerationStats
{
    // Timing (milliseconds)
    public double ClimateNoiseMs;      // Stage 1: 2D noise sampling
    public double BiomeSelectionMs;     // Stage 1: Biome lookup
    public double HeightCalculationMs;  // Stage 2: Density/height
    public double BlockGenerationMs;    // Stage 2-4: Voxel assignment
    public double CaveCarverMs;         // Stage 5: Cave carving
    public double VegetationMs;         // Stage 6: Decoration
    public double TotalGenerationMs;    // Sum of above
    
    // Counts
    public int ChunksGenerated;         // Total since startup
    public int ChunksGeneratedLastSecond;
    public int VoxelsGenerated;         // Non-air voxels this chunk
    public int CavesCarved;             // Cave voxels removed
}
```

#### Streaming Stats
```csharp
public record struct StreamingStats
{
    public int TotalChunks;             // In world
    public int PendingChunks;           // Queued for generation
    public int GeneratingChunks;        // Currently generating
    public int MeshingChunks;           // Awaiting mesh build
    public int ReadyChunks;             // Fully loaded + meshed
    public int VisibleChunks;           // After frustum cull
    
    public long VoxelCacheBytes;        // ChunkVoxelDataCache memory
    public long MeshBufferBytes;        // GPU mesh memory
    public int CommandSlotCapacity;     // Multi-draw slots
}
```

#### Per-Frame Stats
```csharp
public record struct FrameTerrainStats
{
    public double StreamingUpdateMs;    // ChunkStreamingManager.Update()
    public double FrustumCullMs;        // GPU frustum culling
    public double RenderMs;             // VoxelTerrainRenderer.Draw()
    public int DrawCalls;               // Multi-draw indirect calls
    public int TrianglesRendered;       // Visible faces × 2
}
```

### Debug HUD Display

The F3 overlay should show (existing system to be adapted):

```
=== Terrain Generation ===
Generation: 4.2ms (Climate: 1.1 | Height: 1.8 | Blocks: 0.8 | Caves: 0.5)
Chunks: 847 total | 3 pending | 1 generating | 2 meshing | 841 ready
Visible: 312 / 841 (37%)
Memory: Voxel Cache 128MB | GPU Mesh 64MB

=== Frame Stats ===
Streaming: 0.3ms | Cull: 0.1ms | Render: 2.1ms
Draw calls: 1 | Triangles: 1.2M
```

### Profiler Integration

The `TerrainGenerationProfiler` class must track all stages:

```csharp
public enum GenerationStep
{
    Total,
    ClimateSampling,    // Stage 1: Noise
    BiomeSelection,     // Stage 1: Lookup
    HeightCalculation,  // Stage 2: Density
    BlockGeneration,    // Stage 2-4: Voxels
    AquiferLookup,      // Stage 3: Water (new)
    CaveCarving,        // Stage 5
    Vegetation,         // Stage 6
    CollisionGeneration // Collision spans
}
```

Each step uses `BeginStep()` / `EndStep()` bracketing with `Stopwatch` precision. Stats are aggregated per-chunk and exposed via `GetAverageStats()` for the HUD.

### Performance Alerts

The debug system should flag performance regressions:
- **Yellow warning**: Any stage exceeds 2× its budget
- **Red alert**: Total generation exceeds 12ms (50% over budget)
- Log warnings when streaming falls behind camera movement

---

## Why Current Implementation Differs

The current `CpuTerrainGenerator` implementation has these issues:

### Problem 1: Height-First, Biome-Second
Current flow:
```
Climate Noise → Height Calculation → Biome Selection (using height)
```

Correct flow:
```
Climate Noise → Biome Selection → Height Calculation (using biome properties)
```

**Impact**: Biomes can't truly control terrain shape because terrain is already calculated.

### Problem 2: No True Density Functions
Current approach:
```
2D height map + 3D overhang modifications
```

Correct approach:
```
True 3D density function evaluated at every voxel
```

**Impact**: Limited ability to create complex 3D terrain features.

### Problem 3: Broken Water System
Current approach:
```
- Global `WaterLevel` constant
- `columnWaterBody[]` array as a patch
- Lakes disabled due to per-column noise issues
```

Correct approach:
```
- Deterministic aquifer noise sampled at each (x, y, z)
- Water level is a coordinate-based lookup, not per-column
- Water "contained" by existing solid density terrain
```

---

## Migration Strategy

### Phase 1: Reorder Biome Selection (Critical Fix)

**Goal**: Fix the fundamental ordering issue - biomes must be selected BEFORE height calculation.

1. Move biome selection to happen BEFORE height calculation in `CpuTerrainGenerator`
2. Sample climate noise (C/T/H/E/PV) first via `ChunkClimateCache` (already exists)
3. Use climate values to select biome via `BiomeSelector`
4. **Preserve raw PV and E values** - pass them to height calculation (don't re-sample)
5. Pass biome properties to height calculation

**Performance Impact**: Minimal - reordering existing operations, no new computation.

### Phase 2: Biome-Driven Height Calculation

**Goal**: Let biome properties control terrain shape instead of hardcoded checks.

1. Ensure `BiomeDefinition` has terrain shaping attributes (already exists):
   - `BaseHeight`: Elevation offset from sea level
   - `HeightVariation`: Terrain amplitude
   - `PeaksInfluence`: How much PV noise affects height
   - `ErosionSensitivity`: How much erosion smooths terrain

2. Update height calculation to use these biome properties:
   ```csharp
   float CalculateHeight(int columnIndex, BiomeDefinition biome)
   {
       // Reuse cached values - NO new noise sampling
       var pv = climateCache.PeaksValleys[columnIndex];
       var erosion = climateCache.Erosion01[columnIndex];
       
       var variation = biome.HeightVariation * pv * biome.PeaksInfluence;
       var smoothing = erosion * biome.ErosionSensitivity;
       
       return biome.BaseHeight + variation * (1 - smoothing) + WaterLevel;
   }
   ```

3. Remove hardcoded height spline - biome `BaseHeight` replaces it

**Performance Impact**: Slight improvement - removes spline evaluation, uses direct biome lookup.

### Phase 3: Implement Aquifer System

**Goal**: Replace broken lake system with deterministic aquifer.

1. Add aquifer noise layer to `ChunkClimateCache` (2D base level):
   ```csharp
   // 2D aquifer base noise - sampled once per column
   private readonly float[] _aquiferBase = new float[ColumnCount];
   
   public float GetLocalWaterLevel(int columnIndex, int y)
   {
       var biome = GetBiomeAt(columnIndex);
       if (biome.IsOcean) return SeaLevel; // Ocean uses global level
       
       // Inland: local variation
       var aquiferNoise = _aquiferBase[columnIndex];
       return SeaLevel + aquiferNoise * AquiferVariation;
   }
   ```

2. Update block generation to use aquifer:
   ```csharp
   if (density < 0) // Air space
   {
       var lwl = GetLocalWaterLevel(columnIndex, y);
       return y <= lwl ? BlockId.Water : BlockId.Air;
   }
   ```

3. Water is automatically contained by solid terrain (no flood-fill needed)

**Performance Impact**: 
- Adds one 2D noise layer (~0.2ms per chunk)
- Removes `columnWaterBody[]` computation and adjacency checks
- Net: roughly neutral

### Phase 4: True 3D Density Functions (Optional Enhancement)

**Goal**: Full Minecraft 1.18-style terrain with dramatic 3D features.

For fully Minecraft-style terrain, convert to pure density evaluation:
```csharp
float GetDensity(int x, int y, int z, BiomeDefinition biome)
{
    // Base density from biome height
    var baseDensity = biome.BaseHeight + WaterLevel - y;
    
    // Add 3D terrain noise (sparse sampled + interpolated)
    var terrainNoise = SampleSparse3DNoise(x, y, z) * biome.HeightVariation;
    
    // Apply squeeze factor based on height above/below surface
    var squeeze = GetSqueezeFunction(y, biome);
    
    return baseDensity + terrainNoise * squeeze;
}
```

**Performance Impact**: 
- Higher cost than 2D heightmap approach
- Mitigated by sparse 3D sampling (already implemented)
- Only enable for biomes that need it (mountains, weird terrain)

### Phase 5: Update Debug Stats System

**Goal**: Adapt existing HUD to show new pipeline stages.

1. Update `TerrainGenerationProfiler.Step` enum with new stages
2. Add aquifer timing to profiler
3. Ensure all stats exposed via `ChunkStreamingManager.GetStats()` / `GetMemoryStats()`
4. Update `GameScene` F3 overlay to display new metrics

---

## Biome Transition Handling

### Why Transitions Are Seamless

1. **Global Noise**: Climate parameters (C/T/H/E/PV) use continuous noise functions based on global X/Z coordinates
2. **No Chunk Boundaries**: Noise doesn't know about chunks - it produces smooth gradients everywhere
3. **Height Blending**: Because terrain height is derived from climate values, transitions are automatic:
   - Ocean (C < 0.4) produces low terrain
   - Plains (C ~ 0.5-0.7) produces medium terrain
   - Mountains (C > 0.85) produces high terrain
   - The transition zone smoothly interpolates

### Beach/Coastline Handling

The beach biome appears where:
1. Continentalness is just above ocean threshold (C ~ 0.40-0.45)
2. Terrain height is near water level
3. No need for explicit "adjacent ocean check" - the noise naturally creates this zone

---

## File Structure

```
World/Generation/
├── ChunkClimateCache.cs       # SIMD-batched climate noise sampling (Stage 1)
├── BiomeSelector.cs           # Multi-parameter biome lookup table (Stage 1)
├── TerrainDensityEvaluator.cs # Biome-driven height/density calculation (Stage 2) [new]
├── AquiferSystem.cs           # Water level determination (Stage 3) [new]
├── CpuTerrainGenerator.cs     # Main generation orchestrator
├── ChunkGenerationJobSystem.cs# Background worker threads
├── VegetationGenerator.cs     # Decoration placement (Stage 6)
├── TerrainGenerationProfiler.cs # Performance timing for all stages
└── TerrainConfig.cs           # All configuration parameters

World/
├── ChunkStreamingManager.cs   # Coordinates generation + meshing + rendering
├── ChunkMeshBuilder.cs        # Voxel to mesh conversion
├── ChunkMeshingJobSystem.cs   # Background mesh generation
├── VoxelTerrainRenderer.cs    # Multi-draw indirect rendering
└── TerrainMeshBufferManager.cs # GPU buffer management
```

---

## Key Invariants

### Pipeline Ordering
1. **Biome assignment must complete before terrain density calculation begins**
2. **Climate noise is sampled ONCE and cached** - both biome selection and height calculation read from cache
3. **Carvers only modify terrain that already exists; they cannot create terrain**

### Data Flow
4. **Terrain height is always derived from biome properties + noise, never hardcoded per-biome checks**
5. **Water placement uses coordinate-based aquifer lookup, never runtime simulation**
6. **All noise functions are deterministic - same seed + coordinates = same result**

### Performance
7. **No 2D noise re-sampling** - all climate values come from `ChunkClimateCache`
8. **3D noise uses sparse sampling + interpolation** - never full 98K voxel evaluation
9. **Generation stays under 8ms per chunk** - enables smooth streaming at 60 FPS
10. **GPU uploads are batched and throttled** - avoid frame spikes from buffer transfers

### Debugging
11. **All generation stages are individually timed** - exposed via `TerrainGenerationProfiler`
12. **Stats are available every frame** - streaming manager exposes current state
13. **Performance regressions trigger visible warnings** - debug HUD alerts

---

## References

- Minecraft 1.18+ world generation documentation
- Density functions and noise routers
- Multi-noise biome source
- Aquifer and cave carver systems
