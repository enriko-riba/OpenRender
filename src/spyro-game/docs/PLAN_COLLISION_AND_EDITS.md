# Plan: Collision, Interaction, and Persistence for GPU Terrain

## 1. Architecture Overview
The terrain is generated on the GPU to ensure high performance and avoid CPU bottlenecks. However, game logic (physics, collision, interaction) runs on the CPU. This requires a hybrid approach where essential collision data is extracted from the GPU and synchronized to the CPU without causing pipeline stalls.

## 2. Collision System (CPU-Side)
To avoid transferring full voxel data (which is heavy), we will use **Column Spans**.
*   **Column Span:** A run-length encoded representation of solid blocks in a vertical column (e.g., "Solid from Y=0 to Y=10", "Air from Y=11 to Y=128").
*   **Data Structure:** `struct ColumnSpan { byte startY; byte endY; byte blockType; }`.

### 2.1. Generation Pipeline Update
1.  **Phase 2.5 (New):** After `compute-generate` (Phase 2), dispatch a new shader `compute-column-spans.comp`.
2.  **Compute Shader:**
    *   Input: Voxel data (SSBO).
    *   Process: For each (x, z) column in the chunk, iterate Y to find solid intervals.
    *   Output: `ColumnSpansSSBO` (Packed buffer of spans).

### 2.2. Async Readback Strategy
To prevent FPS drops (stalls), we must not block the render thread waiting for GPU data.
1.  **Fence Sync:** When a chunk generation batch is submitted, insert a `GLFence`.
2.  **Polling:** In `Update()`, check `glClientWaitSync` with `GL_TIMEOUT_IGNORED` (non-blocking).
3.  **Readback:** Once the fence signals completion:
    *   Map the `ColumnSpansSSBO` using `glMapBufferRange` with `GL_MAP_READ_BIT`.
    *   Copy data to CPU `ChunkCollisionData` object.
    *   Unmap immediately.
4.  **Prioritization:**
    *   **Current Chunk:** High priority. If missing, physics might need to fallback (e.g., hover) or block (undesirable).
    *   **Neighbors:** Pre-fetch 3x3 or 5x5 area around the player asynchronously.

## 3. Interaction (Block Picking)
*   **Raycasting:** Perform raycasting on the CPU against the `ChunkCollisionData` (Column Spans).
*   **Accuracy:** Since spans represent the actual geometry, raycasting will be accurate for block selection.

## 4. Block Breaking & Building (Edits)
Edits cannot be purely CPU-side because the mesh is generated on the GPU.

### 4.1. Data Storage
*   **CPU:** `Dictionary<Vector3Int, BlockType>` per chunk to store deltas (changes from procedural base).
*   **GPU:** `EditsSSBO` containing a flat list or hash map of changes for the chunk being generated.

### 4.2. Pipeline Integration
1.  **User Action:** Player breaks a block.
2.  **CPU Update:** Update `ChunkCollisionData` immediately (for instant physics response) and add to `Edits` dictionary.
3.  **GPU Upload:** Upload the updated `Edits` list to the GPU `EditsSSBO`.
4.  **Re-Generation:** Mark the chunk for "Re-mesh".
    *   Dispatch `compute-generate`.
    *   Shader Logic: `voxel = procedural_noise(); if (HasEdit(pos)) voxel = edit_value;`
    *   Dispatch `compute-mesh` to update visuals.

## 5. Persistence (Save/Load)
*   **Save Format:**
    *   World Seed (long).
    *   Player Position/Rotation.
    *   Per Chunk: List of Edits (Position + BlockType).
*   **Loading:**
    *   Load Seed.
    *   Load Edits into CPU dictionaries.
    *   When streaming a chunk, upload its edits to GPU before generation.

## 6. Implementation Steps
1.  **Shader:** Create `compute-column-spans.comp`.
2.  **C# Structs:** Define `ColumnSpan` and `CollisionData`.
3.  **Manager:** Implement `CollisionManager` to handle async readbacks.
4.  **Integration:** Hook into `ChunkStreamingManager` to trigger span generation and readback.
5.  **Physics:** Update `Player` to use `CollisionManager`.
6.  **Edits:** Implement `ChunkEditManager` and update `compute-generate.comp`.
