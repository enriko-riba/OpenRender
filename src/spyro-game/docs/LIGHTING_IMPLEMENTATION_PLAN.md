# Minecraft-Style Voxel Lighting Implementation Plan (Dual Channel)

## Overview
This document outlines the plan to implement a dual-channel flood-fill lighting system (Sky Light + Block Light) to solve light bleeding in caves while supporting local light sources (torches, lava).

## Core Concept
We will split the lighting data into two separate channels to handle their distinct behaviors:
1.  **Sky Light (0-15):** Represents sunlight. Masks the global Directional Light.
2.  **Block Light (0-15):** Represents artificial light (torches). Adds local illumination independent of the sun.

## 1. Data Structure
**Current State:** `voxelData` is a packed `uint`.
- Bits 0-7: Block ID
- Bits 8-15: Biome ID
- **Bits 16-23: Light Data (Split)**
  - **Bits 16-19: Sky Light (0-15)**
  - **Bits 20-23: Block Light (0-15)**
- Bits 24-31: Flags

## 2. Compute Shader Pipeline Updates

### A. New Shader: `compute-light.comp`
This shader will handle propagation for *both* channels.

**Stage 1: Initialization**
- **Sky Light:**
  - Raycast from Top (Y=384).
  - If Sky: Set SkyLight = 15.
  - If Solid/Below: Set SkyLight = 0.
- **Block Light:**
  - Check Block ID.
  - If Emissive (Torch, Lava): Set BlockLight = 15 (or specific emission level).
  - Else: Set BlockLight = 0.

**Stage 2: Propagation (BFS Flood Fill)**
- Propagate both channels independently in the same pass.
- **Rule:** `Neighbor.Light = max(Neighbor.Light, Current.Light - Decay)`
- **Decay:**
  - Air: -1
  - Water: -2 or -3 (darkens faster)
  - Solid: Blocks propagation.

### B. Pipeline Integration
1. `compute-generate.comp`: Generates blocks.
2. **`compute-light.comp`**: Calculates Sky and Block light levels.
3. `compute-visibility.comp`: Calculates face visibility.
4. `compute-compact.comp`: Packs `(SkyLight << 4 | BlockLight)` into the vertex data.

## 3. Vertex Shader Updates (`voxel-terrain.vert`)
- **Input:** Packed light byte.
- **Logic:** Unpack two floats.
  - `vSkyLight = float(packed & 0xF) / 15.0;`
  - `vBlockLight = float((packed >> 4) & 0xF) / 15.0;`
- **Output:** Pass both to fragment shader.

## 4. Fragment Shader Updates (`voxel-terrain.frag`)
Combine the two light channels with the Directional Light.

```glsl
// 1. Sun/Directional Contribution (Masked by Sky Light)
// Only visible if Sky Light is present.
float skyFactor = vSkyLight;
vec3 sunLight = dirLight.diffuse * NdotL * skyFactor;

// 2. Block/Torch Contribution (Additive)
// Independent of the sun. Always visible if Block Light is present.
// Use a warm color for torch light.
vec3 torchColor = vec3(1.0, 0.8, 0.6); 
vec3 localLight = torchColor * vBlockLight; 

// 3. Ambient (Base)
// Ambient is usually sky-dependent.
vec3 ambient = dirLight.ambient * max(skyFactor, 0.05);

// Final Combination
// (Sun + Local + Ambient) * Texture * AO
vec3 finalLight = (sunLight + localLight + ambient);
vec3 finalColor = finalLight * texColor * aoStrength;
```

## 5. Handling Chunk Borders
- The propagation logic must handle chunk boundaries.
- **Phase 1:** Simple intra-chunk lighting (borders might be dark).
- **Phase 2:** Multi-pass or 3x3 chunk neighborhood for correct border propagation.

## Implementation Steps
1.  **Modify `compute-generate.comp`**: Initialize bits 16-23 to 0.
2.  **Create `compute-light.comp`**: Implement dual-channel propagation.
3.  **Update `VoxelTerrainRenderer.cs`**: Dispatch lighting pass.
4.  **Update `compute-compact.comp`**: Pack split light data.
5.  **Update Shaders**: Implement the combined lighting formula.