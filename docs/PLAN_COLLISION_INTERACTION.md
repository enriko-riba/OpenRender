# Plan: Collision, Interaction, and Persistence for GPU-Based Voxel Engine

## 1. Architecture Overview

The engine currently generates and meshes terrain entirely on the GPU. The CPU has minimal knowledge of the terrain geometry, which poses a challenge for physics (collision) and interaction (block picking).

**Core Philosophy:**
*   **GPU Authority:** The GPU holds the "truth" of the terrain geometry.
*   **CPU Cache:** The CPU maintains a lightweight, read-only cache of collision data for the immediate vicinity of the player.
*   **Sparse Edits:** Terrain modifications are stored as sparse deltas on the CPU and applied on the GPU during generation.
*   **Async Communication:** All data transfer from GPU to CPU must be asynchronous to prevent pipeline stalls.

---

## 2. Collision System (Column Spans Approach)

To enable CPU-side physics without transferring massive voxel buffers, we implemented a **Column Spans** approach.

### 2.1. Data Structure
Instead of full voxel data, we store vertical runs of solid blocks (spans) for each (x, z) column in a chunk.
*   **Chunk Size:** 16 (X) * 128 (Y) * 16 (Z).
*   **Data:** For each of the 256 columns, we store a count and a list of `(StartY, EndY, BlockType)` tuples.
*   **Compactness:** Most columns have 1-2 spans (ground, maybe a tree). Very efficient storage.

### 2.2. Pipeline
1.  **Generation (GPU):** Standard procedural generation creates the `voxel_data` buffer.
2.  **Span Extraction (GPU):** `compute-column-spans.comp` scans each column and writes packed span data to a `column_spans_ssbo`.
3.  **Readback (CPU):**
    *   **Prioritization:** Chunks within a 2-chunk radius (current + neighbors) are prioritized for generation and readback.
    *   **Immediate Readback:** For high-priority chunks, we read back the span data immediately after generation completes (using `glMapBufferRange` with `GL_MAP_READ_BIT`).
    *   **Latency:** This ensures collision data is available *before* the player physically enters the chunk (assuming reasonable movement speed).
4.  **Management (ChunkStreamingManager):**
    *   `ChunkStreamingManager` maintains `highPriorityPending` and `lowPriorityPending` queues.
    *   `SubmitPendingBatches` prioritizes the high-priority queue.
    *   `PollCompletedBatches` triggers the readback as soon as the generation fence is signaled.

### 2.3. CPU Physics
*   The `Player` queries `VoxelWorld`, which delegates to `Chunk`.
*   `Chunk` uses the cached `ColumnSpan` data to perform precise AABB collision checks.
*   **Optimization:** `GetBlockByPosition` uses the span data to return `BlockType` without needing the full voxel grid.

---

## 3. Block Picking (Raycasting)

For selecting blocks (highlighting), we need high precision. Since the full geometry is on the GPU, we should perform the raycast there.

### 3.1. GPU Raycasting
*   **Shader:** `compute-raycast.comp`.
*   **Inputs:** Camera Position, Forward Vector, Max Distance.
*   **Algorithm:** 3D DDA (Digital Differential Analyzer) through the voxel grid.
*   **Output:** `SelectedBlockInfo` struct (IVec3 Coordinate, IVec3 Normal, bool Hit).

### 3.2. Execution
*   Run this compute shader once per frame (or every few frames).
*   Read back the single `SelectedBlockInfo` struct (16-32 bytes).
*   **Latency:** 1-2 frames latency is acceptable for a selection highlight cursor. It will trail slightly during fast camera movement but settle instantly.

---

## 4. Block Breaking & Building (Edits)

Since the terrain is procedural, we cannot simply "change" the source array. We must apply edits as a layer on top of the procedural generation.

### 4.1. Data Structure: `ChunkDelta`
*   **CPU:** A `Dictionary<int, BlockType>` or `List<BlockEdit>` per chunk. Stores only changed blocks.
*   **Persistence:** This is the data that gets saved/loaded to disk.

### 4.2. GPU Application
1.  **Upload:** Before the meshing phase, upload the `ChunkDelta` for the chunk being generated.
    *   Use a `delta_buffer_ssbo` (Index, BlockID).
2.  **Apply Shader:** `compute-apply-edits.comp`.
    *   Runs after `compute-generate.comp`.
    *   Reads `delta_buffer_ssbo`.
    *   Writes directly into the `voxel_data` buffer on the GPU.
3.  **Mesh:** The standard meshing pipeline runs on the modified voxel data.

### 4.3. Real-time Updates
*   When player breaks a block:
    1.  Update CPU `ChunkDelta`.
    2.  Mark chunk as "Dirty".
    3.  `ChunkStreamingManager` re-queues the chunk for the GPU pipeline (Generate -> Apply Edits -> Mesh).

---

## 5. Saving & Loading

### 5.1. File Format
*   **Header:** Chunk Coords, Version.
*   **Data:** Serialized `ChunkDelta` (Count, [Index, BlockID]...).
*   **Compression:** Simple RLE or Deflate if deltas get large (unlikely for typical gameplay).

### 5.2. Integration
*   **Load:** When `ChunkStreamingManager` activates a chunk, check for a save file.
    *   If exists: Load `ChunkDelta` into memory.
    *   If not: Create empty `ChunkDelta`.
*   **Save:** On unload or periodic auto-save, serialize `ChunkDelta` to disk.

---

## 6. Implementation Steps

### Phase 1: Collision Infrastructure
1.  Create `compute-collision-pack.comp`.
2.  Implement `CollisionManager` in `ChunkStreamingManager`.
3.  Implement Async Readback (PBO/Fence) for collision buffers.
4.  Visualize collision data (debug lines) to verify alignment.

### Phase 2: Physics Integration
1.  Update `PlayerController` to use `CollisionManager` instead of direct voxel access.
2.  Implement the 5x5 active region logic to ensure data is ready before the player touches it.

### Phase 3: Interaction (Picking & Edits)
1.  Implement `compute-raycast.comp` and readback.
2.  Create `ChunkDelta` class and `EditManager`.
3.  Implement `compute-apply-edits.comp`.
4.  Wire up Mouse Click -> Update Delta -> Re-mesh Chunk.

### Phase 4: Persistence
1.  Implement `ChunkSerializer`.
2.  Hook into `LoadChunk` / `UnloadChunk` events.
