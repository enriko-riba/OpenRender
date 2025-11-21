# Collision and Interaction Plan

## Overview
This document outlines the plan for implementing collision detection, block picking, and terrain editing in the GPU-based voxel terrain system.

## Current State
- Terrain generation is fully GPU-based.
- `Chunk` objects on CPU do not contain block data (`Blocks` array is null or empty).
- Collision logic in `Player.cs` and `VoxelWorld.cs` relies on `Chunk` data.
- `CollisionManager` exists but was not fully integrated.

## Problem
The player falls through the terrain because the CPU-side `Chunk` objects lack the block data required for collision checks. Since generation happens on the GPU, we need a way to provide collision data to the CPU without reading back the entire voxel volume (which would cause stalls).

## Solution: Column Spans
Instead of reading back all voxels, we generate "Column Spans" on the GPU. A column span represents a run of solid blocks in a vertical column (x, z).
- **Shader**: `compute-column-spans.comp` scans each column and outputs a list of solid spans (Start Y, End Y, Block Type).
- **Readback**: `ChunkStreamingManager` reads back these spans asynchronously.
- **Storage**: `Chunk` objects store these spans in a compact format.
- **Collision**: `VoxelWorld` uses these spans to determine if a position is solid.

## Implementation Steps

### 1. Fix Shader Compilation
The `compute-column-spans.comp` shader had syntax errors.
- **Status**: Fixed. Variable declarations moved to correct scope, explicit casting added.

### 2. Implement Readback and Population
`ChunkStreamingManager` needs to read the span data from the GPU buffer and populate the `Chunk` objects.
- **Status**: Implemented. `ReadbackColumnSpans` now converts the packed GPU format into `ChunkCollisionData` and calls `chunk.ApplyColumnSpansForCollision`.

### 3. Integrate with VoxelWorld
`VoxelWorld.GetBlockByPositionGlobalSafe` already has logic to use `HasGpuSpans` and `IsSolidBySpans`.
- **Status**: Verified. Logic exists and should work once `HasGpuSpans` is true.

### 4. Block Picking (Raycasting)
Block picking currently uses a DDA algorithm on the CPU (`VoxelWorld.PickBlock`).
- **Issue**: It relies on `GetBlockByPositionGlobalSafe`.
- **Resolution**: With spans, `GetBlockByPositionGlobalSafe` returns a valid `BlockState` (with `BlockType.Rock` for solid spans). This allows picking to work for solid blocks.
- **Limitation**: We lose specific block types (everything is Rock) unless we store block types in spans. The current implementation stores block type in `ChunkCollisionData` but `Chunk.cs` ignores it for collision. For picking, we might want the type.
- **Future Improvement**: Update `Chunk.cs` to store block types in spans if needed for picking correct materials.

### 5. Terrain Editing (Block Breaking/Placing)
- **Block Breaking**: `VoxelWorld.BreakBlock` updates the `breakMasks3D` (bitmask).
- **GPU Update**: The break mask is uploaded to the GPU, and the mesh is regenerated.
- **Collision Update**: The column spans need to be re-generated after an edit.
- **Plan**:
    1.  When a block is broken, update the CPU-side break mask.
    2.  Trigger a GPU dispatch to re-generate the chunk mesh AND column spans.
    3.  Read back the new spans to update CPU collision.

## Next Steps
- Verify collision works in-game.
- Test block picking.
- Test block breaking and ensure collision updates (player shouldn't collide with broken blocks).
