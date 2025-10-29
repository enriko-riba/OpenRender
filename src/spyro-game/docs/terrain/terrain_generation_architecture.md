# Terrain Generation Architecture and Implementation

## 1. High-Level Architecture

### Overview

The terrain generation system creates a procedural voxel-based world made of **chunks**, each containing a 3D grid of blocks. The generation pipeline uses layered noise functions to simulate geological processes such as continental drift, erosion, and biome differentiation.

**World composition:**

* World size: 300x300 chunks (X and Z)
* Chunk size: 32x32 blocks (X/Z) × 128 height (Y)

### Core Concepts

* **Chunk:** 3D array of blocks representing a small part of the world.
* **Block:** Fundamental terrain unit (e.g., Rock, Dirt, Grass, Sand).
* **Biome:** Defines surface characteristics (e.g., Desert, Taiga, Alpine) used for texture/visual differentiation.
* **Height Data:** Base elevation of terrain, derived from domain-warped Perlin noise.
* **Continentalness, Erosion, Temperature, Humidity:** Low-frequency global fields controlling terrain shape and biome assignment.
* **Ridge Data:** Secondary noise layer emphasizing sharp cliffs and ridges.

### Generation Phases

1. **Global Field Generation**

   * Compute large-scale noise fields for continentalness (C), erosion (E), temperature (T), humidity (H).
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

   * Biomes selected based on continentalness, erosion, temperature, and humidity.
   * Used later to choose appropriate block textures.
7. **Block Type Generation**

   * Block material decided based on relative altitude vs. terrain height.
   * Water, sand, rock, or dirt applied according to depth and biome context.

## 2. Noise System

### Domain Warped Noise

Terrain uses domain-warped 2D Perlin noise for realistic variation.

```csharp
heightData = NoiseData.CreateDomainWarped(
    0, 0, worldSize,
    baseFreq: 1f / 180f,
    warpFreq: 1f / 900f,
    warpAmp: 8f,
    seedBase: seed ^ 0x12345, out var range);
```

#### Parameters:

* **baseFreq:** Controls hill/mountain feature size. Higher = more detail.
* **warpFreq:** Controls warp bending scale. Lower = broader continents.
* **warpAmp:** Controls warp strength. Too high produces bowl artifacts.

A second ridged noise (`ridgeData`) layer creates sharp mountain features:

```csharp
ridgeData = NoiseData.CreateDomainWarped(
    0, 0, worldSize,
    baseFreq: 1f / 220f,
    warpFreq: 1f / 550f,
    warpAmp: 14f,
    seedBase: seed ^ 0x5A17C3, out var range);
```

### Normalization

All noise fields are normalized once globally to `[-1,1]` to keep terrain math consistent.

```csharp
for (int i = 0; i < heightData.Length; i++)
    heightData[i] = (heightData[i] - min) / (max - min) * 2f - 1f;
```

## 3. Height Calculation

### Core Function

`Height01At(int wx, int wz, float baseVal)`

Steps:

1. Sample continentalness `C` and erosion `E`.
2. Compute blended height:

   ```csharp
   var fbm = 0.5f * (baseVal + 1f);
   var rid = 1f - MathF.Abs(ridgeData[idx]);
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

## 6. Rendering and Visibility

### Hidden Tile Issue Fix

Invisible water-bottom blocks occur when visibility culling ignores submerged blocks. Fixed by:

1. Recalculating transparency per `BlockType` instead of cached flags.
2. Extending visibility check up to `max(height, WaterLevel + 2)`.

```csharp
private bool IsBlockTransparent(int x,int y,int z)
{
    var t = Blocks[idx].BlockType;
    return t == BlockType.None || t == BlockType.WaterLevel;
}
```

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
