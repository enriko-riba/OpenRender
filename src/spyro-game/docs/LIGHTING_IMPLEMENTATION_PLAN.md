# Minecraft-Style Voxel Lighting Implementation Plan

## Overview
This document outlines the plan to implement Minecraft-style flood-fill lighting (levels 0-15) to solve light bleeding issues in caves and enclosed structures. The system will utilize the existing unused 8-bit light channel in the voxel data structure.

## Core Concept
Instead of relying solely on global directional lighting (which causes bleeding through walls), we will calculate a per-voxel "Sunlight" value.
- **Sunlight (0-15):** Represents direct exposure to the sun and its propagation.
- **Integration:** This value will act as a visibility factor for the Directional Light in the fragment shader.
  - Level 15: Full sunlight (1.0 multiplier).
  - Level 0: Complete darkness/shadow (0.0 multiplier).

## 1. Data Structure
**Current State:** `voxelData` is a packed `uint`.
- Bits 0-7: Block ID
- Bits 8-15: Biome ID
- **Bits 16-23: Light Level (Currently unused/hardcoded to 15)**
- Bits 24-31: Flags

**Action:** No structural changes needed. We will start writing meaningful data to bits 16-23.

## 2. Compute Shader Pipeline Updates

### A. New Shader: `compute-light.comp`
A new compute shader pass is required between **Generation** and **Meshing**.

**Stage 1: Initialization (Column-based)**
- For each X,Z column in the chunk:
  - Cast a ray from Top (Y=384) down.
  - **Sky Logic:** Mark voxels as "Sun Source" (Level 15) until the first solid block is hit.
  - **Solid Blocks:** Set Light = 0.
  - **Below Solid:** Set Light = 0 (Shadow).

**Stage 2: Propagation (BFS Flood Fill)**
- Iterative pass (or single pass with work queue if using advanced compute features, but iterative is simpler for starters).
- For each voxel with Light > 0:
  - Spread to 6 neighbors (Up, Down, Left, Right, Forward, Back).
  - Neighbor Light = `Current Light - 1`.
  - **Constraint:** Only propagate if `Neighbor Light < Current Light - 1` and Neighbor is not solid.
  - **Opacity:** Water reduces light by 2 or 3 levels instead of 1 to create depth darkness.

### B. Pipeline Integration
1. `compute-generate.comp`: Generates blocks.
2. **`compute-light.comp`**: Calculates light levels (NEW).
3. `compute-visibility.comp`: (Existing) Calculates face visibility.
4. `compute-compact.comp`: (Existing) Packs vertices. **Update needed:** Read the light level from `voxelData` and pack it into the vertex data.

## 3. Vertex Shader Updates (`voxel-terrain.vert`)
- **Input:** The packed vertex data now contains the Light Level.
- **Logic:** Unpack the light level (0-15).
- **Output:** Pass `vSunLight` (float 0.0 - 1.0) to the fragment shader.
  - `vSunLight = float(lightLevel) / 15.0;`

## 4. Fragment Shader Updates (`voxel-terrain.frag`)
Combine the new Voxel Light with the existing Directional Light.

```glsl
// Current
vec3 diffuse = dirLight.diffuse * texColor * NdotL * aoStrength;

// New
float sunFactor = vSunLight; // 0.0 to 1.0 derived from voxel light
// Optional: Non-linear curve for better aesthetics (e.g., pow(sunFactor, 1.4))

// Apply to Directional Light (Sun)
// If sunFactor is 0 (Cave), the directional light is effectively blocked.
vec3 finalDiffuse = dirLight.diffuse * texColor * NdotL * aoStrength * sunFactor;

// Apply to Ambient
// Ambient should also be darkened, but maybe keep a tiny minimum for gameplay visibility
vec3 finalAmbient = dirLight.ambient * texColor * aoStrength * max(sunFactor, 0.05);
```

## 5. Handling Chunk Borders (The Hard Part)
Light propagation must cross chunk boundaries.
- **Solution:** The `compute-light.comp` must run on a 3x3 chunk area or have a "border exchange" step.
- **Simplification (Phase 1):** Run lighting *after* generation but *before* meshing. When a chunk is generated, it initializes its light. When neighbors are present, a "Light Update" pass propagates light across borders.
- **Optimization:** Use a "Light Map" texture or buffer if voxel traversal is too slow, but direct voxel writing is preferred for the current architecture.

## Implementation Steps
1.  **Modify `compute-generate.comp`**: Ensure bits 16-23 are initialized to 0 (not 15).
2.  **Create `compute-light.comp`**: Implement the Top-Down Sky check + Propagation.
3.  **Update `VoxelTerrainRenderer.cs`**: Dispatch the new compute shader.
4.  **Update `compute-compact.comp`**: Extract light bits and pack into vertex.
5.  **Update Shaders**: Visualize the light level to debug.
6.  **Refine**: Tune propagation (water absorption, etc.).
