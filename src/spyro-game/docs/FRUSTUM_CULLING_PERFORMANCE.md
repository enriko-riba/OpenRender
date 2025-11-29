# Frustum Culling Performance Notes

## State After GPU Removal

The compute-based frustum culling path has been fully removed. `ChunkStreamingManager.ExecuteFrustumCulling()` now keeps the entire operation on the CPU using the `CameraFrustum` helper and chunk AABBs from `ChunkWorld`. No GL buffers are read back, and the visibility flags written into the indirect draw commands come straight from CPU logic.

### Why the Change?
1. **Eliminate stalls:** The previous compute shader forced a GPU→CPU sync so the CPU could decide which commands to submit. This created the warning spam shown below and stalled every frame.
2. **Match CPU pipeline:** Chunk generation/meshing already happens on worker threads. Keeping culling on the CPU simplifies coordination and keeps phase-three command buffers coherent.
3. **Asset cleanup:** Removing compute shaders (e.g., `compute-frustum.comp`) means this doc should no longer direct engineers to assets that no longer exist.

```
Old warning for historical context:
Buffer performance warning: Buffer object visibility_flags_ssbo is being copied/moved
from VIDEO memory to HOST memory.
```

## Current Implementation (Phase 5+)
- `ChunkStreamingManager.ExecuteFrustumCulling()` iterates the active chunk indices, runs `CameraFrustum.IsBoxVisible`, and toggles the matching indirect command's `instanceCount` between 0/1.
- `VoxelTerrainRenderer.SetVisibilityFlags()` simply mirrors that data into the command buffer without any GPU readback.
- Visibility updates run every frame without throttling because the work is just a few hundred AABB tests on the CPU.

### Performance Impact
- ✅ No GPU→CPU transfers or pipeline stalls.
- ✅ Visibility reflects the latest camera transform every frame.
- ✅ Indirect draw buffer stays GPU-resident; only CPU-side copies write to it when culling masks change.

### Future Ideas
If we revisit GPU-side culling later, it would be to reduce CPU cost for extremely large scenes. That would require a fresh compute (or mesh shader) implementation that **writes indirect commands directly** without any CPU readback. Until then, the CPU path is the canonical implementation.

## Testing Checklist
1. Run `spyro-game` and move the camera past chunk boundaries.
2. The debug overlay should report consistent chunk counts with no GL warnings.
3. Breakpoints in `ChunkStreamingManager.ExecuteFrustumCulling()` should fire each frame, confirming CPU execution.

## References
- `src/spyro-game/ChunkStreamingManager.cs` — look at `ExecuteFrustumCulling()` and `UpdateCommandVisibility()`.
- `src/spyro-game/VoxelTerrainRenderer.cs` — applies CPU visibility results to indirect command buffers.
