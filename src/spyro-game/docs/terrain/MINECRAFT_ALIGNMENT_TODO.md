# Minecraft Terrain Architecture Alignment - TODO List

This document tracks all items needed to align the current implementation with Minecraft 1.18+ style terrain generation as documented in `MINECRAFT_TERRAIN_ARCHITECTURE.md`.

**Legend:**
- ✅ DONE - Implemented and working
- 🔧 PARTIAL - Partially implemented, needs refinement
- ⬜ TODO - Not yet implemented
- ❓ UNCLEAR - Needs investigation/design decision

---

## 1. Biome System

### 1.1 Biome Selection (Climate Parameters)
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| 6-parameter climate system (C, T, H, E, PV, W) | ✅ DONE | `BiomeGenerator.cs` | All 6 parameters computed and stored |
| Continentalness-only ocean selection | ✅ DONE | `BiomeGenerator.cs:SelectBiome()` | Fixed - uses `cont01 < OceanThreshold` not altitude |
| Per-cell jitter for organic borders | ✅ DONE | `BiomeGenerator.cs:53-55` | Added ±0.5 block jitter using gradient noise |
| Domain warp for continentalness | ✅ DONE | `BiomeGenerator.cs:57-60` | Uses `WarpScale` and `WarpStrength` from config |
| Temperature lapse rate (altitude cooling) | ✅ DONE | `BiomeGenerator.cs:79-82` | Uses `config.LapseRate` |
| Continental drying effect | ✅ DONE | `BiomeGenerator.cs:85-90` | Uses `config.CoastDrying` |

### 1.2 Biome Data Storage
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| 4×4 horizontal grid per chunk | ✅ DONE | `ChunkBiomeData.cs` | 16 cells (4×4), 4 blocks per cell |
| Climate values stored per cell | ✅ DONE | `ChunkBiomeData.cs` | C, E, PV, W, T, H all stored |
| Biome ID per cell | ✅ DONE | `ChunkBiomeData.cs` | `BiomeIds[16]` array |
| 3D cave biomes (Y-axis sampling) | ✅ DONE | `ChunkBiomeData.cs`, `BiomeGenerator.cs` | 4×4×24 grid with CaveBiomeId enum |

### 1.3 Biome Definitions
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Temperature/Humidity ranges per biome | ✅ DONE | `TerrainConfig.cs:BiomeDefinition` | Uses `Range` type |
| Surface/Subsurface/Deep blocks per biome | ✅ DONE | `TerrainConfig.cs:723-743` | `SurfaceBlock`, `SubsurfaceBlock`, `DeepBlock`, etc. |
| Per-biome height variation parameters | 🔧 PARTIAL | - | Fields exist but not fully utilized |
| Biome blend weights (Worley regionization) | ✅ DONE | `ChunkBiomeData.cs` | GetBiomeBlendWeights() with K neighbors |

---

## 2. Terrain Generation

### 2.1 Height/Density System
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Continentalness noise → height spline | ✅ DONE | `CpuTerrainGenerator.cs` | Uses `heightSpline` array |
| Sparse 3D sampling (4-block step) | ✅ DONE | `CpuTerrainGenerator.cs:18-26` | Trilinear interpolation, ~40x speedup |
| Erosion modulates peaks | ✅ DONE | `CpuTerrainGenerator.cs` | Low erosion = jagged peaks |
| Peaks/valleys ridge noise | ✅ DONE | `CpuTerrainGenerator.cs` | Uses `columnPeaks` |
| Cliff noise for overhangs | ✅ DONE | `CpuTerrainGenerator.cs` | Uses `columnCliff` |
| Domain warp for organic shapes | ✅ DONE | `CpuTerrainGenerator.cs` | Uses `columnWarpX/Z` |

### 2.2 Cave Generation
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Cheese caves (3D noise threshold) | ✅ DONE | `CpuTerrainGenerator.cs` | Uses `cheeseVolume` |
| Spaghetti caves (ridged tunnels) | ✅ DONE | `CpuTerrainGenerator.cs` | Uses `spaghettiVolume` |
| Cave depth attenuation | 🔧 PARTIAL | - | Needs tuning for cave-free high mountains |
| Aquifer caves (flooded caves) | ⬜ TODO | - | Minecraft 1.18+ has underground lakes |
| Deep dark / sculk generation | ⬜ TODO | - | Low priority, optional |

### 2.3 Block Assignment
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Biome-based surface blocks | ✅ DONE | `CpuTerrainGenerator.cs:1071-1115` | Uses `BiomeDefinition.SurfaceBlock` etc. |
| Depth-based layer assignment | ✅ DONE | `CpuTerrainGenerator.cs` | Surface → Subsurface → Deep |
| Underwater surface blocks | ✅ DONE | `TerrainConfig.cs:738-743` | `UnderwaterSurfaceBlock`, `UnderwaterSubsurfaceBlock` |
| Ore generation | ✅ DONE | `CpuTerrainGenerator.cs`, `TerrainConfig.cs` | OreParams with depth distribution |
| Bedrock floor (Y=0-5) | ✅ DONE | `BlockId.cs` | `Bedrock` block exists |
| Structure integration points | ⬜ TODO | - | Villages, dungeons, mineshafts, etc. |

---

## 3. Block System

### 3.1 BlockId Enum
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Flags enum with embedded properties | ✅ DONE | `BlockId.cs` | Upper 6 bits = flags, lower 10 = ID |
| Solid, Opaque, Liquid, Translucent flags | ✅ DONE | `BlockId.cs:18-26` | All flags implemented |
| Extension methods (IsSolid, IsOpaque, etc.) | ✅ DONE | `BlockId.cs:152-180` | No lookup tables needed |
| Block variants (all Minecraft blocks) | 🔧 PARTIAL | `BlockId.cs` | Basic blocks present, many missing |

### 3.2 Palette System
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Per-chunk block palette | ⬜ TODO | - | Target: `BlockId[] Palette` per chunk |
| Voxel data as palette indices | ⬜ TODO | - | Target: `byte[]` (98KB per chunk) |
| `GetOrAddPaletteEntry()` method | ⬜ TODO | - | Returns palette index for BlockId |
| Current: Direct uint per voxel | ❓ CURRENT | `ChunkVoxelDataCache.cs` | Uses `uint[]`, needs migration |

---

## 4. Meshing Pipeline

### 4.1 Vertex Format
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Compressed 8-byte vertex | ✅ DONE | `VoxelHelper.cs:50` | `VERTEX_STRIDE_BYTES = 8` |
| Packed position (x, y, z, face, corner) | ✅ DONE | `ChunkMeshBuilder.cs` | Bit-packed in first uint |
| Block descriptor in vertex | ✅ DONE | `voxel-terrain.vert:42-43` | `vBlockDescriptor = p2 & 0xFFu` |
| AO per vertex | ✅ DONE | `voxel-terrain.vert:30-36` | 4 AO levels |
| Light level per vertex | ✅ DONE | `voxel-terrain.vert:45-46` | Sky + block light packed |
| Biome ID per vertex | ✅ DONE | `voxel-terrain.vert:44` | For future biome-based coloring |

### 4.2 Face Culling
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Opaque block neighbor culling | ✅ DONE | `ChunkMeshBuilder.cs` | Uses `IsOpaque()` |
| Translucent pass (water, ice, leaves) | ✅ DONE | `ChunkMeshBuilder.cs` | Separate buffers |
| Water top-faces only | ✅ DONE | `ChunkMeshBuilder.cs` | No side/bottom water faces |
| Chunk boundary placeholder masks | ✅ DONE | `ChunkMeshBuilder.cs:16-19` | Handles missing neighbors |

---

## 5. Rendering / Textures

### 5.1 Texture System
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| 150×50 atlas format (top|bottom|side) | ✅ DONE | `BlockTextureManager.cs`, shaders | Implemented as documented |
| 2D texture array (layer = BlockId) | ✅ DONE | `BlockTextureManager.cs` | Each block type = one layer |
| Face-based UV offset in shader | ✅ DONE | `voxel-terrain.frag:52+` | Top/Bottom/Side column offsets |
| All block textures created | 🔧 PARTIAL | `Resources/Textures/` | Many blocks still missing textures |

### 5.2 Water Rendering
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Flat water surface (top faces only) | ✅ DONE | `ChunkMeshBuilder.cs` | No side water curtains |
| Translucent pass (after opaque) | ✅ DONE | `VoxelTerrainRenderer.cs` | Separate draw calls |
| Animated water UVs | ✅ DONE | `voxel-terrain.frag` | Multi-layer scrolling UV animation |
| Underwater fog / tint | 🔧 PARTIAL | Shader | Basic underwater detection exists |
| Water depth fog | ✅ DONE | `voxel-terrain.frag`, `water.frag` | Depth-based darkening and tint |

### 5.3 Lighting
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Sky light propagation | 🔧 PARTIAL | - | Basic values in vertex, no flood-fill |
| Block light propagation | 🔧 PARTIAL | - | Basic values, no proper light sources |
| AO baking per vertex | ✅ DONE | `ChunkMeshBuilder.cs` | 4-level AO system |
| Day/night cycle | ✅ DONE | `DayNightCycle.cs` | Affects sky light |

---

## 6. GPU Buffer Management

### 6.1 Multi-Draw Indirect
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Multi-draw command buffer | ✅ DONE | `Phase3BufferManager.cs` | Single draw call for all chunks |
| Per-chunk slot allocation | ✅ DONE | `Phase3BufferManager.cs` | Dynamic slot management |
| Frustum culling on GPU | ✅ DONE | `ChunkStreamingManager.cs` | GPU-based visibility |

### 6.2 Palette SSBO (Future)
| Item | Status | File(s) | Notes |
|------|--------|---------|-------|
| Per-chunk palette SSBO | ⬜ TODO | - | Upload chunk palettes to GPU |
| Shader palette lookup | ⬜ TODO | - | `palette[paletteIndex] → BlockId` |
| Current: Direct block ID in vertex | ❓ CURRENT | Shaders | Works but less memory-efficient |

---

## 7. Files to Modify/Remove (Post-Migration)

### 7.1 Files to Remove/Simplify
| File | Action | Status | Notes |
|------|--------|--------|-------|
| `terrain-climate.glsl` | DELETE | ⬜ TODO | Biomes now on CPU |
| `getBiomeId()` in shaders | DELETE | ⬜ TODO | No shader biome calculation |
| `World/BlockType.cs` | DELETE | ❓ CHECK | If exists, replace with BlockId |
| `World/BlockDescriptor.cs` | DELETE | ❓ CHECK | Replaced by BlockId flags |
| GPU voxel generation code | DELETE | ✅ DONE | Moved to CPU (`CpuTerrainGenerator`) |

### 7.2 Shader Cleanup
| Item | Status | Notes |
|------|--------|-------|
| Remove climate noise from frag shader | 🔧 PARTIAL | Some unused code may remain |
| Simplify `terrain-common.glsl` | ⬜ TODO | Keep only constants |
| Remove unused uniforms | ⬜ TODO | Climate parameters no longer needed |

---

## 8. Performance & Polish

### 8.1 Memory Optimization
| Item | Status | Priority | Notes |
|------|--------|----------|-------|
| Palette system (50% voxel memory reduction) | ⬜ TODO | HIGH | Current: 4 bytes/voxel, Target: 1 byte |
| Vertex memory (78% reduction achieved) | ✅ DONE | - | 8 bytes/vertex down from 36 |

### 8.2 Quality Improvements
| Item | Status | Priority | Notes |
|------|--------|----------|-------|
| Biome border blending | ⬜ TODO | MEDIUM | Smooth transitions between biomes |
| Height blending at biome transitions | ⬜ TODO | MEDIUM | Avoid cliff walls at borders |
| More natural beach generation | ⬜ TODO | LOW | Use erosion parameter |
| River generation | ⬜ TODO | LOW | Carve river channels |
| Biome-specific decorations (trees, flowers) | ✅ DONE | LOW | Post-terrain features |

### 8.3 Debug Tools
| Item | Status | Notes |
|------|--------|-------|
| F3 biome color overlay | ✅ DONE | `GameScene.cs` |
| F5 wireframe mode | ✅ DONE | `GameScene.cs` |
| Biome HUD display | ✅ DONE | Shows current biome name |
| Climate parameter visualization | ⬜ TODO | Show C/E/T/H per location |

---

## Priority Order for Implementation

### Phase 1: Quick Wins (No Architecture Changes)
1. ✅ Biome selection fix (continentalness-only for oceans) - DONE
2. ✅ Cell jitter for organic biome borders - DONE
3. ⬜ Clean up unused shader code (climate noise)
4. ⬜ Add missing block textures
5. ⬜ Water animation (UV scrolling)

### Phase 2: Palette System (Major Refactor)
1. ⬜ Create `ChunkData` class with palette + byte indices
2. ⬜ Update `CpuTerrainGenerator` to output palette data
3. ⬜ Update `ChunkMeshBuilder` to read from palette
4. ⬜ Add palette SSBO to shader pipeline
5. ⬜ Update `ChunkVoxelDataCache` to use new format

### Phase 3: Visual Polish
1. ⬜ Biome border blending (Worley regionization)
2. ⬜ Height transition smoothing
3. ⬜ Proper light propagation
4. ✅ Underwater depth fog - DONE
5. ⬜ Cave ambient occlusion improvements

### Phase 4: Features
1. ✅ Cave biomes (3D biome grid) - DONE
2. ⬜ Aquifer caves
3. ✅ Ore generation - DONE
4. ✅ Decorations (trees, flowers, grass) - DONE
5. ⬜ Structures (villages, dungeons)

---

## Summary Statistics

| Category | Done | Partial | Todo | Total |
|----------|------|---------|------|-------|
| Biome System | 10 | 1 | 0 | 11 |
| Terrain Generation | 9 | 2 | 2 | 13 |
| Block System | 4 | 1 | 3 | 8 |
| Meshing Pipeline | 8 | 0 | 0 | 8 |
| Rendering/Textures | 8 | 2 | 2 | 12 |
| GPU Buffers | 3 | 0 | 2 | 5 |
| **TOTAL** | **42** | **6** | **9** | **57** |

**Completion: ~74% core functionality, ~84% with partials**

---

*Last Updated: December 2024*
*Cross-referenced with: MINECRAFT_TERRAIN_ARCHITECTURE.md, PROGRESS.md, PLAN_TERRAIN_AND_BIOMES.md*
