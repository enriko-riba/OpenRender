# Collision, Block Interaction, and Persistence Plan

## 1. Overview
The goal is to implement player collision, block breaking/placing, block picking, and data persistence in a GPU-driven voxel terrain system. The core challenge is bridging the gap between GPU-generated data and CPU-side logic (physics, game logic) without introducing stalls.

## 2. Collision Detection
Since the full voxel data resides on the GPU, we cannot efficiently transfer the entire 3D grid to the CPU. Instead, we will use a simplified "2.5D" representation: **Column Spans**.

### 2.1. Column Spans
For each (x, z) column in a chunk, we store a list of vertical spans of solid blocks.
Structure: `Start(y), End(y), Type`.
This reduces memory usage and bandwidth significantly compared to a full 3D grid.

### 2.2. Pipeline Integration
1.  **Generation**: After the voxel generation phase (Phase 2), we run a new compute shader `compute-column-spans.comp`.
2.  **Readback**: We use a dedicated buffer for column spans. We issue an asynchronous readback (PBO or `glGetBufferSubData` with fences) to fetch this data to the CPU.
3.  **Latency Handling**: The readback will have a latency of 1-3 frames. The physics system must handle cases where collision data is pending (e.g., treat as empty or extrapolate).
4.  **Storage**: CPU stores `Dictionary<Vector2i, ChunkCollisionData>`. `ChunkCollisionData` contains the column spans.

### 2.3. Player Collision
*   The player's physics update queries the `ChunkCollisionData` for the chunks surrounding the player.
*   Collision checks are performed against the spans (AABB vs. Spans).
*   **Pre-fetching**: We prioritize reading back collision data for the chunk the player is in and its immediate neighbors.

## 3. Block Interaction (Edits)

### 3.1. Data Structure
We store modifications (deltas) rather than the full chunk state to save memory and bandwidth.
*   `Dictionary<Vector2i, Dictionary<int, BlockType>> ChunkEdits`: Maps Chunk Coordinate -> (Encoded Local Index -> Block Type).
*   Local Index = `x + z * 16 + y * 256`.

### 3.2. Applying Edits
When a chunk is generated or updated:
1.  **Upload**: Upload the edits for the batch of chunks being processed to a `SSBO`.
2.  **Apply**: A new compute shader `compute-apply-edits.comp` runs after `compute-generate.comp` (or as part of it) to overwrite voxels based on the edits.
3.  **Re-mesh**: The standard meshing pipeline (Phase 3-5) runs on the modified voxel data.

### 3.3. Breaking/Placing
1.  **Input**: User clicks.
2.  **Raycast**: Perform raycast on CPU using the **Column Spans** (see Section 4).
3.  **Update**:
    *   Update `ChunkEdits` on CPU.
    *   Mark the chunk (and neighbors if on border) as "Dirty".
    *   Trigger re-generation of the chunk in the `ChunkStreamingManager`.

## 4. Block Picking
Raycasting is performed on the CPU against the `ChunkCollisionData` (Column Spans).
*   Since spans represent the exact geometry of the chunk (run-length encoded columns), raycasting is precise.
*   Algorithm: 3D DDA (Digital Differential Analyzer) or simple stepping through the ray, checking the column span at each (x, z).

## 5. Persistence (Saving/Loading)
We only save the **Edits**.
*   **Format**: Binary file per region or a single database (SQLite/custom).
*   **Content**: List of modified blocks (Position, Type).
*   **Loading**: When a chunk is requested, check if there are saved edits. Load them into `ChunkEdits` before generation.

## 6. Implementation Steps
1.  **Fix Shader**: Fix syntax errors in `compute-column-spans.comp`. (Done)
2.  **Collision Pipeline**:
    *   Integrate `compute-column-spans.comp` into `ChunkStreamingManager`.
    *   Implement async readback of spans.
    *   Create `CollisionManager` to store spans and provide query API.
3.  **Physics Integration**:
    *   Update `Player` to use `CollisionManager` for movement.
4.  **Edits System**:
    *   Implement `ChunkEdits` storage.
    *   Create `compute-apply-edits.comp`.
    *   Update `ChunkStreamingManager` to upload and apply edits.
5.  **Interaction**:
    *   Implement `BlockPickingService` using `CollisionManager`.
    *   Handle mouse input to modify `ChunkEdits` and trigger updates.
6.  **Persistence**:
    *   Implement `TerrainSerializer` to save/load `ChunkEdits`.

