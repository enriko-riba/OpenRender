# Terrain Generation & Biomes — Progress

Status: active
Owner: terrain/biomes
Location: `src/spyro-game/docs/terrain/PROGRESS.md`
Related: `PLAN_TERRAIN_AND_BIOMES.md`

How to read
- Milestones break down the work into phases.
- Each task has a status and brief notes.
- Update frequently; keep it source-of-truth for current state.

Legend
- [ ] Pending
- [~] In progress
- [x] Done
- [-] Skipped/Not applicable

Summary
- Current milestone: M6
- Risks: none
- Next actions: Polish, debug, perf
- Recent fix: Fixed texture filtering consistency (enable mipmaps for all textures)

Recent Updates (M5)
- Implemented biome texture shading with blending.
- Updated `VoxelTerrainRenderer` to use bindless textures for unlimited biome texture support.
- Updated `voxel-terrain.frag` to blend textures based on biome weights and geology layer.
- Refactored texture loading to support dynamic biome configuration.

Milestones

M1: Parameter bucket + GPU bindings + height spline only
- [x] Design plan document (PLAN_TERRAIN_AND_BIOMES.md)
- [x] CPU parameter bucket types scaffolded (`TerrainConfig`, `Spline1D`, biomes/regions/caves)
- [x] Bake 1D height spline LUT and upload as texture
- [x] Create 2D biome-id LUT baking and upload path
- [x] Bind SSBO/UBO for `TerrainParams`
- [x] In-game debug UI to edit `TerrainConfig` and hot-reload (Implemented via JSON hot-reload)

M2: Macro fields shaping + domain warp
- [x] Implement continentalness/erosion/ridge/warp fields in compute shader
- [x] Sample height via 1D LUT and compute base density
- [x] Implement visibility culling and mesh compaction shaders.
- [x] Integrate with `ChunkStreamingManager` and `VoxelTerrainRenderer`.
- [x] Fix terrain flatness and water rendering issues.
- [x] Refactor save system to use per-chunk files.
- [ ] **Note:** Auto-saving of `TerrainConfig` is currently disabled to prevent overwriting with defaults. Re-enable in `ChunkStreamingManager.InitializeGpuGeneration` once pipeline is stable.

M3: Caves (cheese + spaghetti)
- [x] Add 3D noises and subtract from density
- [x] Param-drive thresholds and amplitudes from `CaveParams`

M4: Climate + biome LUT + regionization
- [x] Compute temperature/humidity fields
- [x] Sample biome LUT for base biome id
- [x] Regionization with Worley-style mix across K neighbors
- [x] Fragment-side recomputation of biome weights (preferred)
- [x] Refactor `TerrainConfig` to support per-biome texture paths (List<string>)

M5: Shading with biome textures
- [x] Tri-planar sampling per geology layer (Implemented via bindless texture array and biome blending)
- [x] Blend by biome weights (Implemented in fragment shader)
- [x] Texture array/atlas hookup and indices from `BiomeTextureSet` (Implemented via `uBiomeTextures` uniform array)
- [x] Fixed `GL_INVALID_OPERATION` with `uBiomeTextures` uniform array setting.
- [x] Optimized fragment shader performance by simplifying noise for visuals (approx. 6x fewer noise calls).
- [x] Fixed F3 biome visualization (variable shadowing issue and missing uniform update).
- [x] Fixed blue tint in F3 mode by binding `TerrainParams` SSBO (binding 10) in `VoxelTerrainRenderer`.
- [x] Fixed underwater rendering regression (fog/tint) by updating `uIsUnderwater` uniform and shader logic.
- [x] Fixed underwater block breaking rendering (glass block effect) by replacing broken blocks with Water instead of Air.
- [x] Fixed texture filtering consistency (enable mipmaps for all textures)

M6: Polish, debug, perf
- [ ] Add debug views (C/E/R/T/Hm/biome)
- [ ] Validate transitions along region borders
- [ ] Profile GPU and reduce memory traffic

Changelog
- v0.1: Plan and CPU config scaffolding added

Notes
- Keep all fields world-space deterministic.
- Avoid CPU generation; treat this document as the canonical checklist.
