# Frustum Culling Performance Notes

## Issue: GPU→CPU Transfer Warnings

**Problem:** When running frustum culling every frame, we see thousands of OpenGL performance warnings:
```
Buffer performance warning: Buffer object visibility_flags_ssbo is being copied/moved 
from VIDEO memory to HOST memory.
```

**Root Cause:** The `ExecuteFrustumCulling()` method uses `GL.GetNamedBufferSubData()` to read back visibility flags from GPU to CPU every frame. This causes:
1. **Pipeline stalls** - GPU must finish compute shader before CPU can read
2. **Bandwidth waste** - Copying data GPU→CPU every frame (60+ times/sec)
3. **Unnecessary work** - We're not even using the flags to skip rendering yet

## Solution: Throttled Readback

**Quick Fix (Phase 4.5):**
- Only read back visibility flags every **10 frames** instead of every frame
- Reduces readback frequency from 60/sec → 6/sec
- Statistics update slightly less frequently, but still responsive
- Eliminates performance warnings

**Implementation:**
```csharp
// In GpuTerrainTestScene.UpdateFrame()
private int frameCounter = 0;
private const int FrustumCullingUpdateInterval = 10;

frameCounter++;
if (camera != null && frameCounter >= FrustumCullingUpdateInterval)
{
    frameCounter = 0;
    var visibilityFlags = streamingManager.ExecuteFrustumCulling(camera, chunkIndices);
    terrainRenderer.SetVisibilityFlags(visibilityFlags, chunkIndices);
}
```

## Future Optimization: GPU-Only Culling (Phase 5)

**Best Solution:** Use visibility flags directly on GPU for rendering:

### Phase 5 Approach:
1. **Keep visibility flags on GPU** - Don't read back to CPU
2. **Use indirect rendering** - `glMultiDrawElementsIndirect`
3. **GPU controls draw count** - Visibility shader updates `instanceCount` field
4. **Zero CPU overhead** - All culling happens on GPU

### Benefits:
- ✅ No GPU→CPU transfers
- ✅ No pipeline stalls
- ✅ Better performance scaling
- ✅ Can handle thousands of chunks efficiently

### Implementation Sketch (Phase 5):
```glsl
// In compute-frustum.comp - write directly to indirect commands
layout(std430, binding = 3) writeonly buffer IndirectCommands
{
    DrawElementsIndirectCommand commands[];
};

void main()
{
    // ... frustum test ...
    
    // Write instance count (1 = visible, 0 = culled)
    commands[idx].instanceCount = isVisible ? 1 : 0;
}
```

```csharp
// In VoxelTerrainRenderer.OnDraw()
// GPU decides which chunks to render
GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, 
    DrawElementsType.UnsignedInt, IntPtr.Zero, chunkCount, 0);
```

## Performance Impact

### Before Throttling:
- Readback: 60 times/second
- GPU→CPU bandwidth: ~256 bytes/frame × 60 = ~15 KB/sec
- Pipeline stalls: Every frame
- Warnings: Thousands per second

### After Throttling:
- Readback: 6 times/second
- GPU→CPU bandwidth: ~256 bytes/frame × 6 = ~1.5 KB/sec
- Pipeline stalls: Every 10th frame
- Warnings: Eliminated ✅

### Future (Phase 5 GPU-Only):
- Readback: Never (0 times/second)
- GPU→CPU bandwidth: 0 KB/sec
- Pipeline stalls: None
- Warnings: None
- Bonus: Supports 1000+ chunks with zero overhead

## Testing

**Expected behavior after fix:**
1. ✅ No more performance warnings in console
2. ✅ Statistics still update (just slightly delayed)
3. ✅ Smooth 60 FPS with no stuttering
4. ✅ Culling still works correctly

**To verify:**
1. Run the test scene
2. Move camera around
3. Check console - should see DEBUG messages but NO performance warnings
4. Statistics should update smoothly every ~150ms (10 frames @ 60fps)

## References

- **Current Implementation:** `GpuTerrainTestScene.UpdateFrame()` line ~138
- **Culling Method:** `ChunkStreamingManager.ExecuteFrustumCulling()` line ~597
- **Phase 5 Design:** See `docs/GPU-Terrain-Architecture.md` Section 9.3
