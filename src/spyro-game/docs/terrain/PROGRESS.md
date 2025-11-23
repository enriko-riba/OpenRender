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
- Current milestone: M1
- Risks: none
- Next actions: implement GPU bindings stubs and UI for spline editing

Milestones

M1: Parameter bucket + GPU bindings + height spline only
- [x] Design plan document (PLAN_TERRAIN_AND_BIOMES.md)
- [x] CPU parameter bucket types scaffolded (`TerrainConfig`, `Spline1D`, biomes/regions/caves)
- [ ] Bake 1D height spline LUT and upload as texture
- [ ] Create 2D biome-id LUT baking and upload path
- [ ] Bind SSBO/UBO for `TerrainParams`
- [ ] In-game debug UI to edit `TerrainConfig` and hot-reload

M2: Macro fields shaping + domain warp
- [ ] Implement continentalness/erosion/ridge/warp fields in compute shader
- [ ] Sample height via 1D LUT and compute base density

M3: Caves (cheese + spaghetti)
- [ ] Add 3D noises and subtract from density
- [ ] Param-drive thresholds and amplitudes from `CaveParams`

M4: Climate + biome LUT + regionization
- [ ] Compute temperature/humidity fields
- [ ] Sample biome LUT for base biome id
- [ ] Regionization with Worley-style mix across K neighbors
- [ ] Fragment-side recomputation of biome weights (preferred)

M5: Shading with biome textures
- [ ] Tri-planar sampling per geology layer
- [ ] Blend by biome weights
- [ ] Texture array/atlas hookup and indices from `BiomeTextureSet`

M6: Polish, debug, perf
- [ ] Add debug views (C/E/R/T/Hm/biome)
- [ ] Validate transitions along region borders
- [ ] Profile GPU and reduce memory traffic

Changelog
- v0.1: Plan and CPU config scaffolding added

Notes
- Keep all fields world-space deterministic.
- Avoid CPU generation; treat this document as the canonical checklist.
