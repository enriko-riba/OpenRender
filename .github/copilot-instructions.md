# OpenRender Copilot Guide

## Repo Map
- Root solution `OpenRenderer.sln` includes `OpenRender`, `TextRendering`, and `spyro-game`; `src/src.sln` mirrors the same projects for inner-loop work.
- `src/OpenRender` is the reusable renderer (SceneManager, components, GL helpers) targeted by every sample; it ships shaders under `src/OpenRender/Shaders` and copies them via the csproj metadata.
- `src/TextRendering` houses the glyph atlas and text shaders (SixLabors ImageSharp + Fonts) that power HUD and loading scenes.
- `src/spyro-game` is the active voxel game; `World/` contains streaming, meshing, and GPU interop, while `docs/terrain` tracks the terrain redesign.
- Legacy GPU compute experiments live in `docs/*.md` and `tmp/` (e.g., `compute-compact.full.glsl`); they are reference-only and excluded from builds.

## Build & Run
- Restore/build all projects with `dotnet build OpenRenderer.sln -c Debug` (use Release when collecting perf traces).
- Run the game via `dotnet run --project src/spyro-game/spyro-game.csproj -c Debug`; `dotnet watch run --project src/spyro-game` is the preferred hot-reload loop.
- Program.cs forces an OpenGL 4.6 context (debug context in DEBUG builds), so keep graphics drivers up to date and watch the GL debug output.
- New shaders or resources are copied automatically because the csproj includes `Resources\**\*.*` and `Shaders\**\*.*`, but add explicit `<Content>` entries when linking shared assets (see the consola.ttf link example).
- Validation is manual: launch into `TerrainLoadingScene`, wait for progress to reach 100%, then ensure `GameScene` streams chunks smoothly (target 60 FPS, ≤4 pending chunks at transition).

## Terrain Pipeline (CPU+GPU)
- `TerrainLoadingScene` queues initialization steps (Streaming Manager → GPU generation → Phase 3 buffers → Terrain renderer → Frustum culling → Streaming) before activating `GameScene`.
- `ChunkStreamingManager` (`src/spyro-game/World/ChunkStreamingManager.cs`) seeds `TerrainConfig`, starts `ChunkGenerationJobSystem`, handles GPU readback, and `Update(cameraPosition)` must be called every frame.
- `ChunkVoxelDataCache` + `ChunkMeshingJobSystem` → `ChunkMeshBuilder` produce CPU meshes that `Phase3BufferManager` (`World/Phase3BufferManager.cs`) uploads via persistent-mapped staging buffers and multi-draw command slots.
- `VoxelTerrainRenderer` (`World/VoxelTerrainRenderer.cs`) binds `TerrainParamsSSBO` + biome LUTs, draws via a single multi-draw-indirect call, and exposes visibility stats consumed by HUD/debug overlays.
- `GameScene.SetupCpuTerrain` wires the renderer into the scene graph, reinstates `LoadDistance = VoxelHelper.MaxDistanceInChunks`, and throttles GPU frustum culling by calling `ChunkStreamingManager.ExecuteFrustumCulling` every ~166 ms.

## Key Systems & Patterns
- `VoxelHelper` defines world math (chunk size 16×16×384, far plane 600, chunk indexing helpers); always use it for coordinate math so streaming budgets stay in sync.
- Chunk placeholder masks (bits for ±X/±Z) guard seams while neighbors are missing; `ChunkMeshBuilder` respects those masks, so new face logic must as well.
- `TerrainConfig` loads/saves `terrain_config.json` and exposes spline/LUT baking; call `ChunkGenerationJobSystem.UpdateConfig` after edits so workers pick up new seeds/biomes.
- Height caching and visibility budgets depend on `ChunkStreamingManager.LoadDistance`/`SetPrefetchMargin`; adjust those first so `UpdateVisibilityBudgetCapacity` can resize SSBOs without thrashing GL memory.
- Gameplay debugging lives in `GameScene` (F3 biomes, F5 wireframe, F6 `FlushVoxelCache`); `BlockPickingService`, `Player`, and collision logic rely on `ChunkStreamingManager.World` plus CPU caches—keep them render-thread safe.

## Conventions & Docs
- Follow `src/spyro-game/docs/coding_conventions.md`: .NET 10, C# 14, file-scoped namespaces, modern language features, and XML doc comments on every public/internal method touched.
- `docs/GPU_TO_CPU_MIGRATION_PLAN.md` now serves as historical context only; it captured the one-time migration plan and should not drive current implementation decisions.
- Terrain-specific planning lives in `src/spyro-game/docs/terrain/` (`PLAN_TERRAIN_AND_BIOMES.md`, `PROGRESS.md`); keep them in sync when altering streaming budgets or KPIs.
- Shader code must honor the attribute/uniform layout documented in README.md (locations 0–3, `camera` UBO binding 0, etc.); document any new bindings directly in the shader header.
- Keep new textures/fonts under each project’s `Resources/` tree so the existing `<Content CopyToOutputDirectory>` rules pick them up automatically.
- Spyro-game must maintain smooth streaming performance (no “stop & go” hitches when crossing chunk borders); profile chunk loads and keep frame time within 16.6 ms even with active streaming.

## Workflow Tips
- Before tackling any larger code implementation or refactor, ensure the current worktree is committed so new changes apply on top of a clean state.
- Always call `ChunkStreamingManager.Update(camera.Position)` and use `GetStats`, `GetMemoryStats`, plus `FlushVoxelCache("reason")` when diagnosing stalls or invalid meshes.
- Watch `Phase3BufferManager.CommandSlotCapacity`, `VoxelTerrainRenderer.VisibleDraws`, and the face explosion warning inside `ChunkMeshBuilder` (>5× voxel count) to validate mesh health.
- New background work should reuse the job system pattern (`BlockingCollection` + long-running tasks) so the GL thread never blocks on CPU generation/meshing.
- UI overlays (loading bars, HUD text) go through `TextRenderer` with atlases built via `FontAtlasGenerator`; follow the measure-before-render pattern used in `TerrainLoadingScene.RenderUI` for centering.
- Treat everything under `tmp/` (e.g., `ChunkStreamingManager.corrupted.cs`) as read-only reference files; do not reintroduce them into any csproj without aligning with the migration docs.
