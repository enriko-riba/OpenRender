# Greedy Meshing Implementation Plan for Voxel Terrain

## Date: 2025-01-25

## Executive Summary

Greedy meshing will merge adjacent co-planar faces of the same type into larger quads, reducing vertex count by 50-80% for typical terrain. Based on analysis of previous attempts and current architecture, this plan provides a compute-shader-based approach that works within the existing visibility-compaction pipeline.

---

## Why Previous Attempts Failed

### Analysis of Current System

Looking at `compute-compact.comp` (Version 4.0), the current system:

1. ✅ **Works correctly**: Generates individual quads for each visible face
2. ✅ **Has good infrastructure**: Visibility masks, prefix sums, per-chunk allocation
3. ❌ **No merging**: Each face = separate quad (worst case for vertex count)
4. ❌ **Thread-per-voxel model**: Hard to coordinate merging across multiple threads

### Why Greedy Meshing is Hard in Compute Shaders

**Challenge 1: Coordination**
- Current: Each thread processes one voxel independently
- Greedy: Need to scan rows/columns and mark "consumed" voxels
- **Issue**: Atomics and barriers create race conditions

**Challenge 2: Non-uniform Output**
- Current: Fixed 4 vertices per face
- Greedy: Variable-size quads (1×1 to 16×16 or larger)
- **Issue**: Dynamic allocation more complex

**Challenge 3: Correctness**
- Greedy meshing requires:
  - Identifying mergeable faces
  - Marking consumed voxels
  - Generating correct UVs for merged quads
  - Maintaining AO per-vertex (not per-face!)

---

## Recommended Approach: Two-Phase Greedy Meshing

### Core Insight

**Don't try to do greedy meshing in the existing `compute-compact` shader!**

Instead, add a **new intermediate stage** between visibility and compaction:

```
Phase 3.1: Visibility     → visibilityMask[]
Phase 3.2: Count          → visibleCounts[]
Phase 3.3: Greedy Merge   → mergedQuads[] (NEW!)
Phase 3.4: Compact        → vertices/indices (modified to read quads)
```

This approach:
- ✅ Reuses existing visibility/count infrastructure
- ✅ Separates concerns (merge logic separate from vertex building)
- ✅ Avoids race conditions (one thread per layer/axis)
- ✅ Easier to debug (intermediate quad buffer is inspectable)

---

## Architecture

### New Data Structure: MergedQuad

```glsl
// Packed representation of a merged quad
struct MergedQuad {
    // Uint 0: Start position (X:5, Y:9, Z:5) | Face:3 | ExtentX:5 | ExtentZ:5
    uint packed1;
    
    // Uint 1: BlockType:8 | AO corners (4×3 bits = 12) | padding:12
    uint packed2;
};

// Total: 8 bytes per quad (vs 28 bytes × 4 vertices = 112 bytes)
// Savings: 93% storage in intermediate buffer!
```

### New Buffer: `mergedQuads[]`

```csharp
// Worst-case: same as current (one quad per face)
int maxQuads = chunkCount * CHUNK_VOXEL_COUNT * 3; // 3 faces max per voxel
GL.NamedBufferStorage(mergedQuadsBuffer, maxQuads * sizeof(MergedQuad), ...);

// Actual usage: 50-80% less due to merging
```

### New Shader: `compute-greedy-merge.comp`

**Dispatch**: `(chunkCount, CHUNK_Y_SIZE, 3)` — one workgroup per chunk-layer-axis

- X dimension: chunk index
- Y dimension: layer (0..CHUNK_Y_SIZE-1)
- Z dimension: axis (0=X faces, 1=Y faces, 2=Z faces)

**Algorithm**: Slice-based greedy meshing (industry standard)

---

## Greedy Meshing Algorithm

### Conceptual Overview

For each axis (X, Y, Z):
1. **Slice** the chunk perpendicular to axis (e.g., X-axis → scan YZ planes)
2. **Scan** each row in the slice
3. **Merge** adjacent faces of same type into quads
4. **Mark** consumed voxels to prevent double-processing

### Pseudocode

```glsl
// Process one layer (e.g., Y=5) for one axis (e.g., +X faces)
// Each thread processes one row in the layer

layout(local_size_x = 16, local_size_y = 1, local_size_z = 1) in;

// Shared memory for layer data (16×16 layer = 256 voxels)
shared uint s_mask[256];      // Visibility masks
shared uint s_consumed[256];  // Marks which faces are consumed
shared uint s_quadCount;      // Number of quads generated

void main() {
    uint chunkIdx = gl_WorkGroupID.x;
    uint ly = gl_WorkGroupID.y;
    uint axis = gl_WorkGroupID.z;
    
    if (axis == 0) processFacesX(chunkIdx, ly);
    else if (axis == 1) processFacesY(chunkIdx, ly);
    else processFacesZ(chunkIdx, ly);
}

void processFacesX(uint chunkIdx, uint ly) {
    uint rowIdx = gl_LocalInvocationID.x; // 0..15 (one row per thread)
    
    // Load layer data into shared memory
    for (uint lz = 0; lz < 16; lz++) {
        uint voxelIdx = getVoxelIndex(chunkIdx, 0, ly, lz);
        uint idx = lz * 16 + rowIdx;
        s_mask[idx] = visibilityMask[voxelIdx];
        s_consumed[idx] = 0;
    }
    
    if (gl_LocalInvocationIndex == 0) s_quadCount = 0;
    barrier();
    
    // Each thread scans one row (Z direction)
    for (uint lx = 0; lx < 16; lx++) {
        uint idx = rowIdx * 16 + lx;
        
        // Skip if no +X face visible or already consumed
        if ((s_mask[idx] & FACE_POS_X_BIT) == 0 || (s_consumed[idx] & FACE_POS_X_BIT) != 0)
            continue;
        
        // Found start of a potential quad
        uint startX = lx;
        uint startZ = rowIdx;
        
        // Expand along X axis (greedy!)
        uint extentX = 1;
        while (startX + extentX < 16) {
            uint nextIdx = rowIdx * 16 + (startX + extentX);
            if ((s_mask[nextIdx] & FACE_POS_X_BIT) == 0) break;
            if ((s_consumed[nextIdx] & FACE_POS_X_BIT) != 0) break;
            if (!isSameType(idx, nextIdx)) break;
            extentX++;
        }
        
        // Try to expand along Z axis (greedy in 2D!)
        uint extentZ = 1;
        bool canExpand = true;
        while (startZ + extentZ < 16 && canExpand) {
            // Check if entire row [startX..startX+extentX) is uniform
            for (uint x = 0; x < extentX; x++) {
                uint checkIdx = (startZ + extentZ) * 16 + (startX + x);
                if ((s_mask[checkIdx] & FACE_POS_X_BIT) == 0) { canExpand = false; break; }
                if ((s_consumed[checkIdx] & FACE_POS_X_BIT) != 0) { canExpand = false; break; }
                if (!isSameType(idx, checkIdx)) { canExpand = false; break; }
            }
            if (canExpand) extentZ++;
        }
        
        // We found a quad of size extentX × extentZ!
        // Mark all covered voxels as consumed
        for (uint z = 0; z < extentZ; z++) {
            for (uint x = 0; x < extentX; x++) {
                uint consumeIdx = (startZ + z) * 16 + (startX + x);
                atomicOr(s_consumed[consumeIdx], FACE_POS_X_BIT);
            }
        }
        
        // Emit quad
        uint quadSlot = atomicAdd(s_quadCount, 1);
        emitQuad(chunkIdx, startX, ly, startZ, extentX, extentZ, FACE_POS_X);
    }
}
```

### Key Features

1. **Row-based processing**: Each thread scans one row independently
2. **Greedy expansion**: Extend X first, then try Z (maximizes quad size)
3. **Shared memory**: Layer fits in shared memory (16×16 = 256 bytes)
4. **Atomic marking**: `atomicOr` prevents double-consumption
5. **Type checking**: Only merge faces of same block type

---

## Implementation Phases

### Phase GM-1: Add Greedy Merge Stage (Est. 8-12 hours)

#### Step 1.1: Define MergedQuad Structure

```csharp
// C# side
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MergedQuad
{
    public uint Packed1; // Position + Face + Extents
    public uint Packed2; // BlockType + AO
    
    public const int SizeBytes = 8;
}
```

```glsl
// GLSL side (in terrain-common.glsl)
uint packQuad1(ivec3 start, uint face, uint extentX, uint extentZ) {
    return uint(start.x) | (uint(start.y) << 5) | (uint(start.z) << 14) |
           ((face & 0x7u) << 19) | ((extentX & 0x1Fu) << 22) | ((extentZ & 0x1Fu) << 27);
}

uint packQuad2(uint blockType, uint ao0, uint ao1, uint ao2, uint ao3) {
    return (blockType & 0xFFu) | ((ao0 & 0x7u) << 8) | ((ao1 & 0x7u) << 11) |
           ((ao2 & 0x7u) << 14) | ((ao3 & 0x7u) << 17);
}
```

#### Step 1.2: Create `compute-greedy-merge.comp`

```glsl
#version 460
#include "terrain-common.glsl"

layout(local_size_x = 16, local_size_y = 1, local_size_z = 1) in;

// Inputs
layout(std430, binding = 0) readonly buffer VoxelData { uint voxelData[]; };
layout(std430, binding = 1) readonly buffer VisibilityMask { uint visibilityMask[]; };

// Outputs
layout(std430, binding = 10) writeonly buffer MergedQuads { uint mergedQuads[]; };
layout(std430, binding = 11) buffer QuadCounts { uint quadCounts[]; };

// Shared memory for one layer
shared uint s_mask[256];
shared uint s_types[256];
shared uint s_consumed[256];
shared uint s_quadCount;

uniform uint uChunkCount;

void main() {
    uint chunkIdx = gl_WorkGroupID.x;
    uint ly = gl_WorkGroupID.y;
    uint axis = gl_WorkGroupID.z;
    
    if (chunkIdx >= uChunkCount) return;
    
    // Initialize shared memory
    if (gl_LocalInvocationIndex == 0) s_quadCount = 0;
    
    // Load layer into shared memory
    uint rowIdx = gl_LocalInvocationID.x;
    for (uint col = 0; col < 16; col++) {
        uint lx, ly, lz;
        if (axis == 0) { lx = col; lz = rowIdx; }       // X faces: vary X, fix Z
        else if (axis == 1) { lx = col; lz = rowIdx; }  // Y faces: vary X, fix Z
        else { lx = rowIdx; lz = col; }                  // Z faces: vary Z, fix X
        
        uint voxelIdx = chunkIdx * CHUNK_VOXEL_COUNT + lx + lz * CHUNK_SIDE_SIZE + ly * CHUNK_SIDE_SIZE_SQUARED;
        uint idx = rowIdx * 16 + col;
        
        s_mask[idx] = getPackedVisMask(voxelIdx, visibilityMask);
        s_types[idx] = voxelData[voxelIdx] & 0xFFu;
        s_consumed[idx] = 0;
    }
    barrier();
    
    // Greedy merge (axis-specific)
    if (axis == 0) greedyMergeFacesX(chunkIdx, ly, rowIdx);
    else if (axis == 1) greedyMergeFacesY(chunkIdx, ly, rowIdx);
    else greedyMergeFacesZ(chunkIdx, ly, rowIdx);
    
    barrier();
    
    // Leader thread updates count
    if (gl_LocalInvocationIndex == 0) {
        atomicAdd(quadCounts[chunkIdx], s_quadCount);
    }
}

void greedyMergeFacesX(uint chunkIdx, uint ly, uint rowIdx) {
    // Scan row for +X and -X faces
    // ... (implementation as shown in pseudocode)
}

// Similar for Y and Z axes
```

#### Step 1.3: Update Phase3BufferManager

```csharp
public class Phase3BufferManager
{
    private uint mergedQuadsBuffer;
    private uint quadCountsBuffer;
    
    public void AllocateBuffers(int maxChunks)
    {
        // ... existing buffers ...
        
        // Merged quads (worst-case: one quad per face)
        int maxQuads = maxChunks * VoxelHelper.CHUNK_VOXEL_COUNT * 3;
        mergedQuadsBuffer = GL.GenBuffer();
        GL.NamedBufferStorage(mergedQuadsBuffer, maxQuads * 8, IntPtr.Zero, 
            BufferStorageFlags.DynamicStorageBit);
        
        // Quad counts per chunk
        quadCountsBuffer = GL.GenBuffer();
        GL.NamedBufferStorage(quadCountsBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
    }
}
```

#### Step 1.4: Update Pipeline Execution

```csharp
public void ExecutePhase3WithGreedy(int[] chunkIndices)
{
    // Stage 3.1: Visibility (unchanged)
    visibilityShader.Use();
    GL.DispatchCompute(chunkIndices.Length, CHUNK_Y_SIZE / 4, 1);
    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
    
    // Stage 3.2: Greedy Merge (NEW!)
    greedyMergeShader.Use();
    GL.DispatchCompute(chunkIndices.Length, CHUNK_Y_SIZE, 3); // 3 axes
    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
    
    // Stage 3.3: Count quads (modified to read mergedQuads)
    countShader.Use();
    GL.DispatchCompute(chunkIndices.Length, 1, 1);
    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
    
    // Stage 3.4: Download counts, prefix sum (unchanged)
    var counts = DownloadQuadCounts(chunkIndices.Length);
    var (offsets, totalVerts) = ComputePrefixSum(counts);
    GL.NamedBufferSubData(offsetBuffer, IntPtr.Zero, offsets.Length * sizeof(uint), offsets);
    GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
    
    // Stage 3.5: Compact (modified to read mergedQuads)
    compactShader.Use();
    GL.DispatchCompute(chunkIndices.Length, 1, 1); // Changed: one workgroup per chunk!
    GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit);
}
```

---

### Phase GM-2: Modify Compaction Shader (Est. 4-6 hours)

Update `compute-compact.comp` to read `mergedQuads[]` instead of per-voxel visibility:

```glsl
layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

// Input: Merged quads (instead of visibility mask!)
layout(std430, binding = 10) readonly buffer MergedQuads { 
    uint mergedQuads[];  // Array of packed MergedQuad structs
};

layout(std430, binding = 11) readonly buffer QuadCounts { 
    uint quadCounts[]; 
};

void main() {
    uint chunkIdx = gl_WorkGroupID.x;
    uint threadIdx = gl_LocalInvocationID.x;
    
    if (chunkIdx >= uChunkCount) return;
    
    uint quadCount = quadCounts[chunkIdx];
    uint quadBase = baseOffsets[chunkIdx]; // From prefix sum
    
    // Each thread processes multiple quads
    for (uint q = threadIdx; q < quadCount; q += 256) {
        uint quadIdx = quadBase + q;
        
        // Unpack quad
        uint packed1 = mergedQuads[quadIdx * 2 + 0];
        uint packed2 = mergedQuads[quadIdx * 2 + 1];
        
        uint startX = packed1 & 0x1Fu;
        uint startY = (packed1 >> 5) & 0x1FFu;
        uint startZ = (packed1 >> 14) & 0x1Fu;
        uint face = (packed1 >> 19) & 0x7u;
        uint extentX = (packed1 >> 22) & 0x1Fu;
        uint extentZ = (packed1 >> 27) & 0x1Fu;
        
        uint blockType = packed2 & 0xFFu;
        uint ao0 = (packed2 >> 8) & 0x7u;
        uint ao1 = (packed2 >> 11) & 0x7u;
        uint ao2 = (packed2 >> 14) & 0x7u;
        uint ao3 = (packed2 >> 17) & 0x7u;
        
        // Emit 4 vertices for the merged quad
        // NOTE: UVs must be scaled by extent!
        vec2 uvScale = vec2(extentX, extentZ);
        
        uint vertBase = atomicAdd(vertexCounter, 4);
        uint idxBase = atomicAdd(indexCounter, 6);
        
        // Emit vertices with scaled UVs
        for (uint v = 0; v < 4; v++) {
            vec3 pos = getQuadCorner(face, startX, startY, startZ, extentX, extentZ, v);
            vec2 uv = getQuadUV(face, v, uvScale);
            uint ao = getCornerAO(v, ao0, ao1, ao2, ao3);
            
            writeVertex(vertBase + v, pos, uv, ao, face, blockType);
        }
        
        // Emit indices
        writeIndices(idxBase, vertBase);
    }
}
```

---

### Phase GM-3: UV Scaling for Tiling (Est. 2-3 hours)

**Challenge**: Merged quads have variable size, but texture must tile correctly.

**Solution**: Scale UVs by quad extent:

```glsl
vec2 getQuadUV(uint face, uint corner, vec2 extent) {
    vec2 baseUV = getBaseUV(corner);  // (0,0), (0,1), (1,1), (1,0)
    
    // Scale by extent so texture tiles
    vec2 scaledUV = baseUV * extent;
    
    // Map to atlas tile
    ivec2 tile = faceToTile(face);
    vec2 offset = vec2(float(tile.x) / ATLAS_COLS, float(tile.y) / ATLAS_ROWS);
    
    // Apply tiling within tile bounds
    vec2 tiledUV = fract(scaledUV) * TILE_SIZE + offset;
    
    return flipV(tiledUV);
}
```

**Fragment shader** must handle tiled UVs:
```glsl
// Already handled! fract() in vertex shader ensures UVs wrap within tile
```

---

## Testing Strategy

### Test 1: Single Flat Layer

```csharp
// Generate chunk with Y=64 all solid, rest air
// Expected: ONE large quad (16×16)
// Actual vertices: 4 (vs 1024 without greedy)
```

### Test 2: Checkerboard Pattern

```csharp
// Alternate grass/stone in checkerboard
// Expected: 256 quads (1×1 each, no merging possible)
// Actual vertices: 1024 (same as before)
```

### Test 3: Real Terrain

```csharp
// Generate hilly terrain
// Expected: 50-80% reduction in vertices
// Measure: visibleCounts before/after greedy
```

### Test 4: Cross-Chunk Boundaries

```csharp
// Generate two adjacent chunks with matching flat surfaces
// Expected: Per-chunk greedy works (no cross-chunk merging needed for now)
// Future: Phase GM-4 adds cross-chunk merging
```

---

## Performance Targets

| Metric | Before Greedy | After Greedy | Target |
|--------|---------------|--------------|--------|
| Vertices (flat terrain) | 1,024 per layer | 4 per layer | **99.6% reduction** |
| Vertices (hills) | ~600 per chunk | ~150 per chunk | **75% reduction** |
| Greedy merge time | 0ms | <0.5ms | <10% of compaction |
| Total Phase 3 time | 2ms | 2.5ms | <25% overhead |

---

## Risks & Mitigation

### Risk 1: Shared Memory Limits

**Issue**: 16×16 layer = 256 × 3 bytes = 768 bytes per layer × 3 arrays = 2.3 KB

**Limit**: Most GPUs have 48 KB shared memory per SM

**Mitigation**: We're well below limit. If needed, process 8×16 slices instead.

### Risk 2: Thread Divergence

**Issue**: Greedy algorithm has variable-length loops (while expanding)

**Mitigation**: Acceptable for geometry generation (not real-time rendering). Profile and optimize if needed.

### Risk 3: Incorrect Merging

**Issue**: Bug in greedy logic could merge incompatible faces

**Mitigation**: 
- Unit tests with known patterns
- Visual debugging: color quads by size
- Fallback: Disable greedy (keep per-face path)

---

## Implementation Checklist

### Phase GM-1: Greedy Merge Shader
- [ ] Create `compute-greedy-merge.comp` with shared memory layer processing
- [ ] Implement row-based greedy expansion (X then Z)
- [ ] Add atomic marking to prevent double-consumption
- [ ] Test with flat layer (expect one 16×16 quad)
- [ ] Test with checkerboard (expect no merging)
- [ ] Validate quad counts match visibility counts (or less)

### Phase GM-2: Modified Compaction
- [ ] Update `compute-compact.comp` to read `mergedQuads[]`
- [ ] Unpack quad extent and position
- [ ] Generate 4 vertices per quad (not per voxel face)
- [ ] Scale UVs by extent for tiling
- [ ] Test vertex output format
- [ ] Verify no buffer overflows

### Phase GM-3: Pipeline Integration
- [ ] Add `mergedQuadsBuffer` and `quadCountsBuffer` to Phase3BufferManager
- [ ] Update execution order: visibility → greedy → count → prefix → compact
- [ ] Insert correct memory barriers between stages
- [ ] Test end-to-end pipeline with single chunk
- [ ] Test with 64-chunk batch

### Phase GM-4: Validation & Profiling
- [ ] Add diagnostic logging for quad counts
- [ ] Profile GPU timing for greedy merge stage
- [ ] Compare vertex counts before/after greedy
- [ ] Visual test: verify no cracks or gaps
- [ ] Stress test: 64 chunks at view distance

---

## Future Enhancements (Post-GM)

### GM-5: Cross-Chunk Merging

Currently, greedy merging stops at chunk boundaries. Future work:

1. Add "boundary face buffer" for chunk edges
2. Second pass merges adjacent chunks' boundary faces
3. Expected: Additional 10-20% vertex reduction

### GM-6: Optimal Face Orientation

Currently, we expand X then Z arbitrarily. Optimal approach:

1. Try both X-first and Z-first
2. Choose orientation that yields larger quads
3. Expected: 5-10% additional reduction

### GM-7: Adaptive Greedy

For distant chunks, use more aggressive merging (allow slight visual differences). Expected: LOD-based vertex reduction.

---

## Success Criteria

✅ **Correctness**:
- [ ] Merged quads render identically to per-face quads
- [ ] No cracks, gaps, or Z-fighting
- [ ] UVs tile correctly on merged quads
- [ ] AO preserved at quad corners

✅ **Performance**:
- [ ] 50-80% vertex reduction for typical terrain
- [ ] Greedy merge adds <0.5ms per 64 chunks
- [ ] Total Phase 3 time remains <3ms

✅ **Maintainability**:
- [ ] Greedy merge can be toggled on/off (for debugging)
- [ ] Intermediate quad buffer is inspectable
- [ ] Clear separation between merge and compaction logic

---

## Conclusion

This plan provides a **compute-shader-based greedy meshing** solution that:

1. **Fits existing pipeline**: Adds one stage, minimal changes to others
2. **Avoids race conditions**: Row-based processing with shared memory
3. **Maximizes merging**: 2D greedy expansion (X×Z for horizontal faces)
4. **Maintains quality**: Per-vertex AO, correct UV tiling
5. **Debuggable**: Intermediate quad buffer for validation

**Ready to implement Phase GM-1!** 🚀

**Estimated Total Time**: 16-24 hours across 4 phases

**Expected Result**: 50-80% vertex reduction, <0.5ms overhead, no visual artifacts
