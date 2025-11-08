# Terrain Generation Architecture and Implementation

## 1. High-Level Architecture

### Overview

The terrain generation system creates a procedural voxel-based world made of **chunks**, each containing a 3D grid of blocks. The generation pipeline uses layered noise functions to simulate geological processes such as continental drift, erosion, and biome differentiation.

**World composition:**

* World size: 300x300 chunks (X and Z)
* Chunk size: `VoxelHelper.ChunkSideSize` × `VoxelHelper.ChunkSideSize` blocks (X/Z) × `VoxelHelper.ChunkYSize` height (Y)

### Core Concepts

* **Chunk:** 3D array of blocks representing a small part of the world.
* **Block:** Fundamental terrain unit (e.g., Rock, Dirt, Grass, Sand).
* **Biome:** Defines surface characteristics (e.g., Swamp, Desert, Savanna, Rainforest, Taiga, Alpine) used for texture/visual differentiation.
* **Height Data:** Base elevation of terrain, derived from domain-warped Perlin noise.
* **Continentalness, Erosion, Temperature, Humidity:** Low-frequency global fields controlling terrain shape and biome assignment.
* **Ridge Data:** Secondary noise layer emphasizing sharp cliffs and ridges.

### Generation Phases

1. **Global Field Generation**

   * Compute large-scale noise fields for continentalness (C), erosion (E), temperature (T), humidity (H).
   * Precompute additional higher-frequency domain-warped detail layers for temperature and humidity that are blended in at sample time to increase climate variety.
2. **Base Terrain Heightmap**

   * Generate domain-warped base height noise.
   * Normalize to range `[-1,1]`.
3. **Ridge Data Generation**

   * Generate a separate ridged noise layer for mountains and cliffs.
   * Normalize independently to `[-1,1]`.
4. **Height Sampling (Height01At)**

   * Combine base height, ridge, and field influences to produce normalized height [0..1].
   * Apply domain warping, erosion weighting, and peak boosting.
5. **Quantile Calibration**

   * Dynamically compute global height distribution and normalize to [0..1].
   * Ensures oceans and mountains occupy consistent world-scale proportions.
6. **Biome Assignment**

   * Biomes selected using continentalness, erosion, temperature, humidity, local slope, and altitude relative to sea level.
   * Classification pipeline:
     * Below-sea samples become deep- or coastal-water biomes (`Ocean`, `Beach`, or `Swamp`) depending on moisture and temperature.
     * Gentle lowlands blend dryness and warmth into `Desert`, `Savanna`, `Plains`, or humid `Rainforest` variants.
     * Cold or moderately dry climates resolve to `Taiga` and `Tundra`, while steep or high-altitude areas escalate to `Highlands` or `Alpine`.
     * When no explicit rule triggers, the system falls back to the closest biome centroid across the C/E/T/H fields for smooth transitions.
   * Biome selection feeds into later texture/material assignment for surface blocks.
7. **Block Type Generation**

   * Block material decided based on relative altitude vs. terrain height.
   * Water, sand, rock, or dirt applied according to depth and biome context.

## 2. Noise System

### Domain Warped Noise

Terrain uses domain-warped 2D Perlin noise for realistic variation.

```csharp
float baseNoise = NoiseData.SampleDomainWarped(
    wx, wz,
    baseFreq: 1f / 180f,
    warpFreq: 1f / 900f,
    warpAmp: 8f,
    seedBase: seed ^ 0x12345);
```

#### Parameters:

* **baseFreq:** Controls hill/mountain feature size. Higher = more detail.
* **warpFreq:** Controls warp bending scale. Lower = broader continents.
* **warpAmp:** Controls warp strength. Too high produces bowl artifacts.

A second ridged noise layer, sampled with a different frequency/seed set, creates sharp mountain features:

```csharp
float ridgeNoise = NoiseData.SampleDomainWarped(
    wx, wz,
    baseFreq: 1f / 220f,
    warpFreq: 1f / 550f,
    warpAmp: 14f,
    seedBase: seed ^ 0x5A17C3);
```

### Normalization

Each height sample is calibrated on the fly through quantile-based normalization (`CalibrateElevation`), so we avoid allocating a full world-sized height map while still maintaining consistent sea-level and mountain coverage.

## 3. Height Calculation

### Core Function

`Height01At(int wx, int wz, float baseVal)`

Steps:

1. Sample continentalness `C` and erosion `E`.
2. Compute blended height:

   ```csharp
   var fbm = 0.5f * (baseVal + 1f);
   var rid = 1f - MathF.Abs(SampleRidgeNoise(wx, wz));
   var relief = Lerp(MathF.Pow(fbm, P.SmoothPow), rid, ruggedWeight);
   ```
3. Apply mountain/sea scaling:

   ```csharp
   var h = seaBias + (relief - 0.5f) * mountainK;
   ```
4. Peak boost and continental basins:

   ```csharp
   h += (rid - 0.5f) * (C*C) * 0.45f;
   h -= (1f - C)*(1f - C)*1.5f;
   ```
5. Normalize to [0..1] using quantile calibration.

### Calibration

A quantile-based calibration ensures terrain always fills the vertical range:

* 10th percentile → 0.0 (deep ocean)
* 98th percentile → 1.0 (highest peaks)

This keeps oceans, coasts, and mountains proportional across worlds.

## 4. Terrain Refinements

### Rivers and Shoreline Flattening

To improve connectivity and natural coastlines:

* Carve elongated depressions using a low-frequency directional noise band.
* Flatten height gradients near the water level to form beaches.

```csharp
float r = NoiseData.SampleGradientNoise2D(wx * 0.0009f, wz * 0.00045f, 1f, 1f, seed ^ 0xBEEF);
float band = 1f - Smooth01(MathF.Abs(r));
h -= band * Lerp(0.02f, 0.14f, C);
```

Shoreline flattening:

```csharp
if (h01 < wl01 + 0.08f)
{
    float k = Smooth01((h01 - wl01 + 0.08f)/0.08f);
    h01 = wl01 + (h01 - wl01) * Lerp(0.2f, 1f, k);
}
```

## 5. Block Assignment

### BlockType Logic

Determines material layers per (x,y,z):

```csharp
if (y > maxHeight) None;
else if (y == maxHeight) GrassDirt;
else if (y >= maxHeight - 2) Dirt;
else Rock;
```

Special handling for water and sand:

```csharp
if (y <= WaterLevel)
{
    if (y == WaterLevel) WaterLevel;
    else if (y >= WaterLevel - 1) Sand;
    else BedRock;
}
```

### Biome-based Texture Substitution

BlockType stays generic (`GrassDirt`, `Snow`, etc.), but biomes map those to biome-specific textures, e.g.:

* Desert + GrassDirt → DesertSand
* Swamp + Dirt → SwampMud

## 6. Rendering, Visibility & Ambient Occlusion

### GPU-First Chunk Initialization

All chunk data now flows through `ChunkInitializer`, which drives the `compute-chunk.comp` shader.\
Each dispatch processes one chunk and writes into three SSBOs:

| Binding | Buffer            | Purpose                                                     |
| ------- | ----------------- | ----------------------------------------------------------- |
| 0       | `heightSSBO`      | Normalized height map (input from `TerrainBuilder`)         |
| 1       | `blockTypeSSBO`   | Block type per voxel (output)                               |
| 3       | `columnHeightsSSBO` | Quantized column tops for gameplay / biome stats         |
| 4       | `blockAttribSSBO` | Packed visibility bit + 6×3-bit ambient occlusion samples   |

`chunkIndicesSSBO` (binding 2) simply enumerates the world chunk indices requested for the batch.

The shader performs three passes inside the same workgroup:

1. **Block materialisation** – replicates the CPU `CalcBlockType` logic using the normalized column height.
2. **Visibility test** – checks the six orthogonal neighbours and sets bit `0` when at least one face is exposed.
3. **Ambient occlusion** – samples four diagonals around each face, clamps the sample count to `[0,3]`, then packs the six faces into the upper bits of `blockAttrib`.

The CPU performs a single GPU → CPU copy per buffer (persistently mapped), wraps the raw arrays into `TerrainBuilder.ChunkGenerationData`, and pushes them to `Chunk.ApplyGenerationData`. The chunk no longer recomputes lighting when the GPU already produced the attributes.

### Rendering Path

* `ChunkRenderer` uploads the packed block state array to an instanced SSBO (`ssbo_blocks`).
* `instancedChunk.vert` selects the proper face AO (`packedAO >> face * 3`) and passes a brightness scalar to the fragment shader.
* `instancedChunk.frag` multiplies the lit color by `aoBrightness`, preserving the bindless texture / material pipeline.

### CPU Fallback

`Chunk.RecomputeLighting(force: true)` is still available for edge cases (runtime edits or missing GPU data).\
`VoxelWorld.ProcessWorkItem` only invokes it on chunks that did not arrive with GPU-produced attributes.

### Instrumentation

`ChunkInitializer.DispatchAndReadback` logs two timings: shader dispatch and CPU post-processing.\
These metrics should stay below ~1 ms per chunk on development hardware; use them to catch regressions.

---

## 7. Parameter Reference

| Parameter       | Typical Range  | Description                |
| --------------- | -------------- | -------------------------- |
| baseFreq        | 1/50 – 1/300   | Hill/mountain size         |
| warpFreq        | 1/600 – 1/1000 | Warp curvature scale       |
| warpAmp         | 4 – 12         | Warp strength              |
| SeaMin          | -0.6 – -0.3    | Ocean depth bias           |
| SeaMax          | 0.3 – 0.4      | Continental elevation bias |
| MtnLow          | 0.3            | Mountain base factor       |
| MtnHigh         | 1.5 – 2.5      | Peak multiplier            |
| RidgePow        | 1.5 – 2.0      | Ridge sharpness            |
| TerraceStep     | 0.02 – 0.04    | Terracing step height      |
| TerraceStrength | 0.2 – 0.4      | Terrace prominence         |

---

## 8. Future Improvements

* **Hydrology simulation:** connect river bands dynamically to lowest ocean basins.
* **Multi-octave erosion noise:** simulate realistic mountain drainage.
* **Biome edge blending:** smooth transitions between biome tilesets.
* **Thermal/altitude-based snow placement.**
* **Volcanic and canyon landforms** via layered 3D noise masks.

---

## 9. Summary

This architecture provides a high-performance, modular, noise-driven world generation system suitable for voxel terrain. Each terrain layer is independent yet compositionally consistent through normalization and calibration, yielding natural-looking continents, ridges, and coastlines while maintaining performance across large (300×300) chunk worlds.
## TODO: Incremental GPU Compaction & Streaming

Goal: Eliminate full-atlas rebuilds on every tile change; only process new tiles and their border dependencies to reduce churn and jitter.

- Track per-tile sets
  - Keep `prevSet` and `newSet` of surrounding chunks (by camera tile).
  - Compute `added = newSet − prevSet`, `removed = prevSet − newSet`.

- Border AO dependencies
  - For `added`, build a padded subset `added ∪ 4‑neighbors` (neighbors must be in `newSet`).
  - Dispatch `compute-chunk` + `compute-borders` for this subset only.

- Incremental compaction
  - Append compacted data for `added` into the existing atlas; maintain `chunkIndex -> (base,count)`.
  - For `removed`, mark counts=0 and push regions to a free‑list (soft delete).
  - Periodic defrag: when fragmentation > threshold (e.g., 25%) or on a coarse timer, do a full rebuild.

- Draw arrays maintenance
  - Keep `CompactedChunkIndices/Bases/Counts` synced; update only touched chunks.
  - Optionally re‑emit a compact index array to keep draw index stable.

- Publish policy
  - Publish only on tile change with non‑empty delta; rate limit (≥150–200 ms) to remove jitter.
  - Edits: update only the edited chunk (plus borders if needed) and patch its draw in place — no full rebuild.

- Synchronization & swap
  - Retain fences after count/write; swap atlas only after issuing draws for the current frame.
