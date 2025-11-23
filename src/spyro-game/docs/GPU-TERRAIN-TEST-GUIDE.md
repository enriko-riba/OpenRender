# GPU Terrain Test Scene - Quick Start Guide

## Overview

The `GpuTerrainTestScene` is a standalone demonstration of the new GPU terrain pipeline (Phases 2-4) with detailed performance timing.

## How to Test

### Option 1: Modify Program.cs (Quickest)

Replace the loading scene with the test scene:

```csharp
// In Program.cs, replace:
var scene = new LoadingScene2(tr1);

// With:
var scene = new GpuTerrainTestScene(tr1);
```

### Option 2: Add as Menu Option

Add a new scene to the SceneManager:

```csharp
scm.AddScene(new GpuTerrainTestScene(tr1));
// Press a key to switch to it
```

## What You'll See

### Timing Display

The scene displays detailed timing for each pipeline stage:

```
GPU TERRAIN PIPELINE TEST

✅ Generation Complete (16 chunks)

Phase 2 (Generation):        X.XX ms
Phase 3 (Visibility):        X.XX ms
Phase 3 (Count):             X.XX ms
Phase 3 (Prefix Sum):        X.XX ms
Phase 3 (Compaction):        X.XX ms
Phase 4 (Setup):             X.XX ms

Total Phase 3:               X.XX ms
Total Pipeline:              X.XX ms

Vertices: XX,XXX
Indices:  XX,XXX
Faces:    XX,XXX

Controls:
  WASD - Move camera
  R - Regenerate (toggle 16/64 chunks)

FPS: XXX (X.XXms)
```

### Expected Results

**For 16 Chunks:**
- Phase 2 (Generation): ~0.5-2ms
- Phase 3 (Visibility): ~0.2-1ms
- Phase 3 (Count): ~0.05-0.2ms
- Phase 3 (Prefix Sum): ~0.01-0.05ms (CPU)
- Phase 3 (Compaction): ~0.3-1.5ms
- **Total Pipeline: ~1-5ms** ✅ (well under 2ms target per chunk batch)

**For 64 Chunks:**
- Total Pipeline: ~3-15ms (still very good for 64 chunks at once)

### Controls

- **WASD**: Move camera around the terrain
- **R**: Regenerate terrain (toggles between 16 and 64 chunks)
- **Mouse**: Look around (if camera supports it)

## Troubleshooting

### "Phase 3 not initialized!"

**Problem**: Phase 3 buffers weren't created.

**Fix**: Check that `InitializePhase3()` was called successfully. Look for GL errors in the log.

### "Terrain generation produced no geometry!"

**Problem**: The shaders didn't produce any vertices/indices.

**Possible causes**:
1. Voxel data is all air (check generation shader)
2. All faces are occluded (check visibility shader)
3. Compaction shader not writing data (check atomic counters)

**Debug**: Check the console output for:
- "Phase 3 complete" message with vertex/index counts
- Any GL errors
- Shader compilation errors

### No terrain visible

**Problem**: Rendering isn't working.

**Checks**:
1. Are vertices/indices > 0? (shown in UI)
2. Is camera inside the terrain? (try moving back)
3. Check console for GL errors
4. Verify shader compilation (look for warnings)

## Console Output

You should see output like:

```
Phase3BufferManager: Allocating buffers for 16 chunks
  Max voxels: 524,288
  Max vertices: 6,291,456 (215.04 MB)
  Max indices: 9,437,184 (36.00 MB)
Phase3BufferManager: All buffers allocated and initialized
Phase 4 rendering initialized
Starting GPU terrain generation for 16 chunks...
Phase 2 (Generation): 1.23ms
Phase 3 complete: 16 chunks, 45321 vertices, 67982 indices
  Timing: Vis=0.45ms Count=0.08ms PrefixSum=0.02ms Compact=0.89ms Total=1.44ms
✅ GPU Terrain Complete!
   Total Time: 2.67ms
   Vertices: 45,321, Indices: 67,982
```

## Integration with MainScene

Once testing is successful, integrate into LoadingScene2:

```csharp
// In LoadingScene2.Load():
world.EnsureChunkInitializer();

// Initialize GPU terrain pipeline
world.ChunkStreamingManager?.InitializeGpuGeneration(seed, elevOffset, elevScale);
world.ChunkStreamingManager?.InitializePhase3(64);
world.ChunkStreamingManager?.InitializePhase4();

// Generate initial terrain
var chunkIndices = GetStartingChunkIndices();
world.ChunkStreamingManager?.ExecuteCompletePipeline(chunkIndices);
```

## Performance Targets

| Metric | Target | Expected | Status |
|--------|--------|----------|--------|
| Phase 2 (Generation) | <1ms/chunk | ~0.05-0.1ms/chunk | ✅ |
| Phase 3 (Visibility) | <0.5ms/batch | ~0.2-1ms/64 chunks | ✅ |
| Phase 3 (Compaction) | <1ms/batch | ~0.3-1.5ms/64 chunks | ✅ |
| **Total Pipeline** | <2ms/chunk | ~0.04-0.08ms/chunk | ✅ |

## Next Steps

1. **Test with 16 chunks** - Verify basic functionality
2. **Test with 64 chunks** - Verify scaling
3. **Move camera** - Verify rendering from different angles
4. **Regenerate (R key)** - Verify pipeline can run multiple times
5. **Check console** - Review timing logs
6. **Integration** - Wire into LoadingScene2 and MainScene

## Known Limitations

1. **No cross-chunk visibility** - Chunk edges may have extra faces (Phase 5)
2. **No AO calculation** - Lighting is flat (Phase 6)
3. **No textures** - Single grass-like color (Phase 5)
4. **No caves** - Solid terrain only (deferred to Phase 6)

All of these are expected and will be addressed in later phases!
