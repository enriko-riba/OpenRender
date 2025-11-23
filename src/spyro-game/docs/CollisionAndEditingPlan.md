# Collision, Block Editing, and Persistence Plan

## 1. Collision Detection
**Goal:** Enable player collision with GPU-generated terrain.

**Current Status:**
- Terrain is generated on GPU (compute shaders).
- Column spans (start Y, end Y, block type) are generated in `compute-column-spans.comp`.
- Spans are read back to CPU in `ChunkStreamingManager`.
- `Chunk` object stores spans (`HasGpuSpans`, `columnSpanPairs`).
- `VoxelWorld` queries `Chunk` for collision.

**Implementation Plan:**
1.  **GPU Generation:** Ensure `compute-column-spans.comp` correctly identifies solid runs and writes them to SSBO.
2.  **Readback:** `ChunkStreamingManager` reads back the SSBO asynchronously (using fences) to avoid stalling.
3.  **Data Storage:** Populate `Chunk.columnSpanPairs` with the readback data.
4.  **Querying:** Update `VoxelWorld.GetBlockByPositionGlobalSafe` to prioritize `HasGpuSpans` and use `Chunk.GetBlockTypeFromSpans`.
5.  **Fallback:** Maintain `HasGpuColumns` (heightmap) as a fallback or for simple queries.

## 2. Block Picking (Raycasting)
**Goal:** Allow the player to select blocks for breaking/placing.

**Implementation Plan:**
1.  **Raycasting:** Use `VoxelWorld.PickBlock` which performs DDA (Digital Differential Analyzer) traversal.
2.  **Data Source:** `PickBlock` calls `GetBlockByPositionGlobalSafe`. Since collision is fixed (step 1), picking should work automatically.
3.  **Optimization:** Ensure `GetBlockByPositionGlobalSafe` is fast. Spans lookup is efficient (linear scan of small array).

## 3. Block Breaking/Building (Editing)
**Goal:** Allow modifying the terrain and updating the GPU representation.

**Implementation Plan:**
1.  **CPU Update:**
    - When a block is broken/placed, update the `Chunk` data on CPU.
    - If `Chunk.Blocks` (full voxel array) is null (GPU-only mode), we might need to:
        - Option A: Initialize `Chunk.Blocks` from GPU data (expensive readback of full voxels).
        - Option B: Maintain a "delta" list of changed blocks per chunk.
        - Option C: Use the column spans to reconstruct a sparse representation.
    - **Decision:** For now, assume we can initialize `Chunk.Blocks` lazily or use a delta list.
    - Update `Chunk.columnSpanPairs` locally to reflect the change immediately for collision.

2.  **GPU Update:**
    - We need to update the voxel data on the GPU so the mesh can be rebuilt.
    - **Approach:**
        - Upload the changed block(s) to a "pending edits" buffer.
        - Re-run the generation/meshing pipeline for the affected chunk(s).
        - Since the pipeline is procedural, we need a way to "inject" edits.
        - **Solution:** Use a 3D texture or SSBO for "World Edits" that the generation shader samples.
        - OR: Re-upload the full chunk data if we switch to CPU-authoritative for edited chunks.

    - **Proposed Hybrid Approach:**
        - Keep procedural generation for unmodified chunks.
        - For modified chunks, mark them as "Dirty".
        - When regenerating a dirty chunk, pass the modified voxel data (or delta) to the compute shader.
        - Alternatively, use `ChunkInitializer.UpdateChunksInPlace` which seems to exist in `VoxelWorld.cs`.

3.  **Mesh Rebuilding:**
    - Trigger `ChunkStreamingManager` to re-mesh the chunk.
    - This might require invalidating the cached mesh and re-submitting the chunk to the pipeline.

## 4. Saving/Loading
**Goal:** Persist terrain changes.

**Implementation Plan:**
1.  **Format:** Use the existing RLE-compressed binary format (`.bin` files).
2.  **Saving:**
    - Iterate over `VoxelWorld.loadedChunks`.
    - Identify chunks with modifications.
    - Serialize the modified state.
3.  **Loading:**
    - When streaming a chunk, check if a save file exists.
    - If yes, load the data.
    - **Integration with GPU:**
        - If a chunk is loaded from disk, we cannot use the procedural generation shader alone.
        - We must upload the loaded voxel data to the GPU.
        - **Pipeline Modification:**
            - Add a flag `IsLoadedFromDisk`.
            - If true, skip `compute-generate.comp` (procedural noise).
            - Instead, upload the voxel data to the `generation_buffer` directly.
            - Then run the rest of the pipeline (visibility, compaction, meshing).

## Next Steps
1.  Verify collision fix (completed).
2.  Implement Block Breaking:
    - Update `VoxelWorld.BreakBlock` to handle GPU-only chunks (update spans locally).
    - Trigger mesh update.
3.  Implement Saving/Loading integration with GPU pipeline.
