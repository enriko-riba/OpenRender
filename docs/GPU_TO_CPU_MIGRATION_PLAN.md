# GPU ➜ CPU Terrain Pipeline Migration Plan

## Goals
- Restore deterministic seam handling by moving the fragile Phase 3 (visibility + compaction) logic back to CPU code.
- Keep the high-throughput wins from GPU generation and lighting while simplifying placeholder/neighbor handling.
- Retire obsolete compute shaders / GLSL includes once their responsibilities are owned by CPU systems.
- Ensure every new/updated CPU component ships with XML-style function docs and targeted code comments explaining complex blocks.
- Enforce the guidance in `src/spyro-game/docs/coding_conventions.md` (latest C# features, file-scoped namespaces, etc.) for all migration work.

## Code Quality Requirements
- **Function documentation**: Each public/internal method introduced or touched during the migration must include XML doc comments describing purpose, parameters, and return semantics.
- **Inline context**: Non-trivial logic (meshing loops, neighbor sampling, buffer uploads) requires concise explanatory comments so future GPU/CPU shifts remain understandable.
- **Convention adherence**: Apply the project rules captured in `src/spyro-game/docs/coding_conventions.md`—latest C# features, file-scoped namespaces, and idiomatic patterns.
- **Review checklist**: Treat documentation + convention compliance as a blocking checklist item before deleting the legacy compute shaders.

## Target Architecture Overview
```
[GPU: generation + lighting]
        │ (voxel SSBO per chunk → readback)
        ▼
[CPU: visibility + meshing + edge refresh]
        │ (CPU-built vertex/index buffers)
        ▼
[GPU: upload to Phase3 buffers + render]
```

### Responsibilities Split
| Stage | Owner | Responsibilities | Threading Notes |
|-------|-------|------------------|-----------------|
|Phase 2 Generation|GPU (`compute-generate.comp`) | Procedural voxel population, biome/material tags, column metadata | Lives fully on GPU compute queue.
|Phase 2 Lighting|GPU (`compute-light.comp`) | Sky/block light propagation inside chunk SSBO | Lives fully on GPU compute queue.
|Voxel Readback|CPU (`ChunkStreamingManager`) | Map / copy generated voxels & light data into CPU buffers (per chunk) once GPU fence signals | **[GL thread]** Map/Unmap buffers + fence polling. **[Worker threads]** memcpy + chunk cache formatting once pointer acquired.
|Visibility + Meshing|CPU (new module) | - Determine visible faces per voxel using neighbor data already in RAM.<br>- Apply greedy meshing or quad batching.<br>- Generate final vertex + index buffers (reuse Phase3 buffer manager uploads). | **[Worker threads]** Entire pass can fan out (chunk-level parallelism); only final buffer submission waits for GL thread.
|Edge Refresh + Placeholder Handling|CPU | Track neighbor readiness and rerun border meshing when missing neighbors arrive; placeholders become simple CPU flags (no SSBO). | **[Worker threads]** State tracking + remesh scheduling. **[GL thread]** only re-upload commands.
|Upload to GPU|CPU + existing Phase3 buffer manager | Copy CPU-built mesh data into shared vertex/index buffers; rebuild indirect draw commands. | **[GL thread]** Buffer orphan/upload + indirect command writes; prep staging data on workers.

## Migration Flow
1. **Data Readback Layer**
   - **[GL thread]** After `Compute` fence in Phase 2, map voxel SSBO (or use PBO) and obtain pointer ranges.
   - **[Worker threads]** Copy chunk data into CPU-side arrays (voxels + lights + biome flags) and store per-chunk cache keyed by chunk index.

2. **CPU Visibility Pass**
   - **[Worker threads]** Reuse existing CPU-friendly utilities (from the pre-GPU path) or write a new iterator:
     - For each voxel, sample 6 neighbors via chunk cache; treat missing neighbors as air but mark seam dirty.
     - Produce face masks + optional AO/light metadata.

3. **CPU Meshing**
   - **[Worker threads]** Apply greedy meshing or the previous instanced mesher to convert visible faces into vertex/index lists.
   - Output format matches current Phase3 vertex compression to avoid renderer changes; only final upload waits for GL thread.

4. **Edge Refresh Logic**
   - **[Worker threads]** When a chunk finishes meshing, evaluate neighbor availability:
     - If neighbor missing ➜ store placeholder edge state, queue chunk for refresh when neighbor arrives.
     - When neighbor becomes ready, re-run CPU visibility/meshing for affected chunks (cheap compared to GPU dispatch).

5. **Upload / Build Indirect**
   - **[Worker threads]** Prepare packed vertex/index blobs + indirect structs per chunk.
   - **[GL thread]** Feed data into `Phase3BufferManager` (reuse current allocator, but remove reliance on GPU count/scan results) and write indirect draw commands directly in C# (per chunk) instead of `compute-build-indirect.comp`.

6. **Cleanup GPU Assets**
   - Remove obsolete compute shaders + includes:
     - `compute-visibility.comp`
     - `compute-count.comp`
     - `compute-scan.comp`
     - `compute-compact.comp`
     - `compute-build-indirect.comp`
     - `terrain-bindings.glsl` (once no remaining shaders consume it)
     - Placeholder helper GLSL files (`terrain-noise`, etc.) stay because generation still needs them.

7. **Codebase Adjustments**
   - Strip placeholder mask SSBO plumbing; replace with CPU-side bitfields stored directly in `ChunkDescriptor`.
   - Simplify `ChunkStreamingManager` state machine: Phase3 becomes synchronous CPU job per completed generation batch.
   - Update `VoxelHelper.SSBOBindings` to remove unused bindings.
   - Remove shader compilation/loading for the deleted compute passes.

8. **Incremental Rollout Strategy**
   - Step 1: Implement CPU mesher but keep GPU pipeline; compare outputs offline.
   - Step 2: Gate enabling flag to switch a subset of chunks to CPU path for validation.
   - Step 3: Remove GPU passes + bindings once CPU path is stable.

## Expected Removals / Additions
- **Remove**: SSBOs & GL bindings for visibility mask, counts, scan totals, compact vertices/indices, placeholder mask buffer, atomic counters, etc.
- **Add**: CPU data caches (chunk voxel arrays), job system hooks for meshing, serializer for vertex/index uploads.
- **Keep**: existing rendering shaders (voxel terrain, water, etc.), GPU lighting/generation assets, frustum culling compute (still useful for draw calls).

## Next Steps
1. Prototype CPU visibility/meshing using a single chunk readback to verify mesh parity with current GPU output.
2. Design CPU job pipeline (thread pool or existing task runner) to process completed batches.
3. Plan API changes for `Phase3BufferManager` so it accepts CPU-provided counts/offsets.
4. Once validated, delete the obsolete compute shaders and update project files.
