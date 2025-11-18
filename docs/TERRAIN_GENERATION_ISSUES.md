# Terrain Generation Issues - Analysis & Fix

**Date**: 2025-01-16  
**Issues**: Flat terrain at Y~26, visible chunk boundary walls

---

## Issue 1: Flat Terrain at ~26 Blocks High

### Problem
The terrain appears as a giant flat cube approximately 26 blocks high, with no procedural variation.

### Root Cause Analysis

**Elevation mapping formula in shader** (`compute-generate.comp` line 212):
```glsl
float h01 = height01At(wx, wz, baseVal, fields);
height = int(h01 * float(CHUNK_Y_SIZE - 1));
```

Where `height01At` returns a normalized height `[0, 1]` that gets scaled to `[0, 127]`.

**Inside `height01At`** (lines 198-208):
```glsl
float h = heightRaw(wx, wz, baseVal, fields);  // Returns ~[-2, 2] range
float h01 = clamp((h - uElevOffset) * uElevScale, 0.0, 1.0);
```

**Current values** (`TerrainLoadingScene.cs` line 90):
```csharp
elevOffset: 20f, elevScale: 40f
```

### The Math

If `heightRaw()` returns values in range `[-1.5, 1.5]` (typical for domain-warped noise):

```
h01 = clamp((h - 20.0) * 40.0, 0.0, 1.0)

// For h = -1.5:
h01 = clamp((-1.5 - 20.0) * 40.0, 0.0, 1.0) 
    = clamp(-860, 0.0, 1.0) 
    = 0.0

// For h = 0.0:
h01 = clamp((0.0 - 20.0) * 40.0, 0.0, 1.0)
    = clamp(-800, 0.0, 1.0)
    = 0.0

// For h = 1.5:
h01 = clamp((1.5 - 20.0) * 40.0, 0.0, 1.0)
    = clamp(-740, 0.0, 1.0)
    = 0.0
```

**❌ ALL noise values map to 0.0!**

The problem is `elevOffset: 20f` is WAY too large for noise in range `~[-2, 2]`.

### Expected Values

For noise in range `[-1.5, 1.5]` to map to terrain heights `[0, 127]`:

```glsl
// Want to map [-1.5, 1.5] → [0.0, 1.0]
// Formula: h01 = (h - min) / (max - min)
//        = (h - (-1.5)) / (1.5 - (-1.5))
//        = (h + 1.5) / 3.0

// Convert to shader parameters:
// h01 = (h - offset) * scale
// Matching: h01 = (h + 1.5) / 3.0 = (h - (-1.5)) * (1/3.0)

elevOffset = -1.5f
elevScale = 1.0f / 3.0f = 0.333f
```

**But wait!** The shader also applies terrain modifications (rivers, ridges, continentalness bias), so the actual range might be wider, like `[-2.5, 2.5]`:

```csharp
elevOffset = -2.5f   // Minimum expected height
elevScale = 0.2f     // 1 / (2.5 - (-2.5)) = 1/5
```

### Recommended Fix

Update `TerrainLoadingScene.cs` line 90:

```csharp
// OLD (produces flat terrain):
streamingManager!.InitializeGpuGeneration(world.Seed, 20f, 40f, testMode: false, maxChunks: surroundingChunkCount);

// NEW (calibrated for noise range ~[-2.5, 2.5]):
streamingManager!.InitializeGpuGeneration(world.Seed, 
    elevOffset: -2.0f,    // Adjust if you see terrain clipping at min
    elevScale: 0.25f,      // 1 / (max - min) = 1 / 4
    testMode: false, 
    maxChunks: surroundingChunkCount);
```

### Debugging Strategy

To find the correct values, add logging in the shader (not possible in GLSL) or:

1. **Temporary test mode**: Set `testMode: true` to verify rendering works
2. **Incremental adjustment**: Start with `elevOffset: 0f, elevScale: 0.5f` and adjust
3. **Add CPU logging**: Sample `heightRaw()` on CPU for a few points and log the range

---

## Issue 2: Visible Chunk Boundary Walls

### Problem
All chunks show visible "outer edge walls" from bottom to surface block. These are interior faces between adjacent chunks that should be culled.

### Root Cause

**Visibility shader** (`compute-visibility.comp`) only checks **within-chunk** neighbor voxels:

```glsl
// Check -X neighbor (simplified)
if (lx > 0) {
    uint neighborVoxel = voxelData[voxelIdx - 1];
    if ((neighborVoxel & 0xFFu) != 0u) {
        mask &= ~(1u << FACE_NEG_X);  // Cull face
    }
}
// No check for lx == 0 (chunk boundary!)
```

**What's missing**: When `lx == 0`, the neighbor is in the **previous chunk** (chunkX - 1), but we never check it!

### Why It Happens

1. **Each chunk processes independently** - no knowledge of neighboring chunks
2. **Boundary voxels** at `lx=0, lx=15, lz=0, lz=15` don't have neighbors checked
3. **Result**: Faces on chunk boundaries are always marked visible, even if adjacent chunk has a solid block

### Visual Impact

```
Chunk (0,0):          Chunk (1,0):
+---+---+---+         +---+---+---+
| A | B |15 |← Wall →|0  | D | E |
+---+---+---+         +---+---+---+

Voxel at (0,0)[15] and (1,0)[0] are adjacent but:
- (0,0)[15]: Face +X marked VISIBLE (no neighbor check)
- (1,0)[0]:  Face -X marked VISIBLE (no neighbor check)
Result: Redundant double-faced wall!
```

### Solution Options

#### **Option 1: Pass Neighbor Chunk Data** (Best for GPU)

Extend the visibility shader to receive pointers to neighboring chunk voxel data:

```glsl
// New uniforms
uniform uint uChunkNeighborOffsets[6];  // Offsets to neighbor chunk data
// [0] = -X neighbor, [1] = +X neighbor, [2] = -Z neighbor, 
// [3] = +Z neighbor, [4] = -Y neighbor, [5] = +Y neighbor

// Check -X neighbor across chunk boundary
if (lx == 0 && uChunkNeighborOffsets[0] != 0xFFFFFFFFu) {
    uint neighborChunkOffset = uChunkNeighborOffsets[0];
    uint neighborVoxelIdx = neighborChunkOffset + (15) + lz * 16 + ly * 256;
    uint neighborVoxel = voxelData[neighborVoxelIdx];
    if ((neighborVoxel & 0xFFu) != 0u) {
        mask &= ~(1u << FACE_NEG_X);
    }
}
```

**Requires**:
- CPU pre-calculates which chunks are neighbors
- Chunks must be generated in correct order (neighbors first)
- More complex dispatch logic

#### **Option 2: Two-Pass Visibility** (Simpler, works with any order)

**Pass 1**: Mark all faces visible (current behavior)

**Pass 2**: Post-process boundary faces by checking neighbors:
```glsl
// New compute shader: compute-visibility-boundaries.comp
// For each chunk boundary voxel:
//   - Check if neighbor chunk has been generated
//   - If yes, check neighbor voxel and cull face if solid
//   - Update visibility mask
```

**Requires**:
- Small additional shader (~50 lines)
- Extra dispatch after main visibility pass
- Minimal CPU overhead

#### **Option 3: Stitch Chunks at Compaction Time** (Lazy approach)

Instead of culling at visibility time, skip redundant faces during compaction:
```glsl
// In compute-compact.comp, before writing vertex:
if (isBoundaryFace(lx, lz, face)) {
    // Check neighbor chunk (requires neighbor data access)
    if (neighborHasSolidBlock(wx, wy, wz, face)) {
        continue;  // Skip this face
    }
}
```

**Pros**: Works with current visibility shader  
**Cons**: Still generates visibility masks for redundant faces (wastes bandwidth)

---

### Recommended Fix: Option 2 (Two-Pass Visibility)

**Why**: 
- ✅ No ordering constraints on chunk generation
- ✅ Minimal code changes (new shader + small CPU dispatch)
- ✅ Clear separation of concerns (intra-chunk vs inter-chunk culling)

**Implementation**:

1. **Add new shader** `compute-visibility-boundaries.comp`:
```glsl
layout(local_size_x = 64) in;  // One thread per boundary voxel

// For each chunk, check 4 edges × height (16×2 + 16×2 = 64 boundary columns)
// Thread processes one boundary column, iterates over Y

void main() {
    uint chunkIdx = gl_WorkGroupID.x;
    uint boundaryColumn = gl_LocalInvocationID.x;
    
    // Decode which edge this thread handles (0-15 = -X edge, 16-31 = +X edge, etc.)
    // For each Y level, check neighbor chunk and update visibility mask
}
```

2. **Update `ChunkStreamingManager.ExecutePhase3()`**:
```csharp
// After main visibility pass:
GL.DispatchCompute((int)chunkCount, 1, 1);  // Existing visibility
GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

// NEW: Boundary visibility pass
boundaryVisibilityShader.Use();
GL.Uniform1(boundaryVisibilityShader.GetUniformLocation("uChunkCount"), chunkCount);
GL.DispatchCompute((int)chunkCount, 1, 1);  // One workgroup per chunk
GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
```

3. **Handle neighbor lookups**:
- Require chunks in contiguous XZ layout (already true for our `chunkIndices[]`)
- Neighbor offset calculation:
  ```glsl
  int neighborChunkIdx = chunkIdx - 1;  // -X neighbor
  if (neighborChunkIdx >= 0 && chunkX > 0) {
      // Access neighbor voxel data
  }
  ```

---

## Quick Fix Testing Plan

### Step 1: Fix Flat Terrain

Update line 90 in `TerrainLoadingScene.cs`:
```csharp
streamingManager!.InitializeGpuGeneration(world.Seed, 
    elevOffset: -2.0f, 
    elevScale: 0.25f, 
    testMode: false, 
    maxChunks: surroundingChunkCount);
```

**Expected result**: Terrain with hills/valleys visible

### Step 2: Verify Chunk Boundaries

Look at chunk edges - you should still see walls.

### Step 3: Implement Boundary Culling

Follow Option 2 implementation above.

**Expected result**: Smooth terrain across chunks, no visible boundaries

---

## Alternative: CPU Pre-Processing (Not Recommended)

Generate all chunks first, then run a CPU pass to update visibility masks for boundary faces. This would require:
- Downloading all voxel data to CPU (slow)
- Processing boundaries (slow)
- Re-uploading visibility masks (slow)

**❌ Defeats the purpose of GPU terrain generation!**

---

## Files to Modify

### For Flat Terrain Fix:
1. `src/spyro-game/TerrainLoadingScene.cs` line 90

### For Chunk Boundary Fix (Option 2):
1. Create `src/spyro-game/Shaders/compute-visibility-boundaries.comp`
2. Update `src/spyro-game/World/ChunkStreamingManager.cs`:
   - Add `boundaryVisibilityShader` field
   - Initialize in `InitializePhase3()`
   - Dispatch in `ExecutePhase3()` after main visibility pass

---

**Author**: GitHub Copilot  
**Priority**: High (blocks gameplay)  
**Estimated Time**: 
- Flat terrain fix: 2 minutes
- Boundary culling: 1-2 hours
