# Phase GM-2: Greedy Meshing Consumption Implementation

## Date: 2026-01-26
## Status: 🚧 IN PROGRESS

---

## Overview

Phase GM-2 modifies the compaction shader to **consume merged quads** instead of reading per-voxel visibility masks. This realizes the actual vertex reduction benefits of greedy meshing.

### Phase Transition

**Phase GM-1 (Infrastructure - COMPLETE)**:
```
Visibility → Greedy Merge → Generate Quads → [stored but not used]
          ↓
Count (per-voxel) → Scan → Compact (per-voxel) → Vertices
```

**Phase GM-2 (Consumption - THIS PHASE)**:
```
Visibility → Greedy Merge → Generate Quads
                                    ↓
Count (quad-based) → Scan → Compact (READS QUADS) → Vertices
```

---

## Expected Benefits

Based on Phase GM-1 statistics:
- **Quad reduction**: 147-187 quads/chunk vs 677 faces/chunk
- **Reduction ratio**: **78% fewer faces**
- **Vertex reduction**: **78% fewer vertices** (4 vertices per quad/face)
- **Index reduction**: **78% fewer indices** (6 indices per quad/face)

### Performance Impact

| Metric | Before GM-2 | After GM-2 | Improvement |
|--------|-------------|------------|-------------|
| **Vertices/chunk** | ~2,708 | ~588 | **78% reduction** |
| **Indices/chunk** | ~4,062 | ~882 | **78% reduction** |
| **Memory bandwidth** | 100% | 22% | **78% savings** |
| **Cache efficiency** | Baseline | 4.5x better | **Fewer cache misses** |

---

## Implementation Changes

### 1. Modify `compute-compact.comp`

**Current behavior** (Phase GM-1):
- Reads from `visibilityMask[]` (per-voxel, 8 bits each)
- Iterates all voxels (16³×384 = 983,040 per chunk)
- Generates 4 vertices + 6 indices per visible face

**New behavior** (Phase GM-2):
- Reads from `mergedQuads[]` buffer (2 uints per quad)
- Iterates only merged quads (147-187 per chunk)
- Generates 4 vertices + 6 indices per merged quad
- **Uses extent scaling** for quad dimensions

#### Key Code Changes

**Before** (per-voxel iteration):
```glsl
layout(local_size_x = 16, local_size_y = 1, local_size_z = 1) in;

void main() {
    uint chunkIdx = gl_WorkGroupID.x;
    uint ly = gl_WorkGroupID.y; // Y coordinate
    uint localIdx = gl_LocalInvocationID.x;
    
    // Iterate all voxels in XZ plane
    for (uint lz = 0; lz < 16; lz++) {
        uint lx = localIdx; // 0..15
        uint voxelIdx = ly * 256 + lz * 16 + lx;
        
        // Check visibility mask
        uint mask = getPackedVisMask(voxelIdx, visibilityMask);
        
        // Generate faces for each visible side
        for (uint face = 0; face < 6; face++) {
            if ((mask & (1u << face)) != 0u) {
                emitFace(chunkIdx, lx, ly, lz, face, blockType);
            }
        }
    }
}
```

**After** (quad iteration):
```glsl
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// Inline unpacking (no 'out' parameters to avoid Nvidia compiler issues)
ivec3 unpackQuadStart(uint packed) {
    return ivec3(
        int(packed & 0x1Fu),           // X: bits 0-4
        int((packed >> 5) & 0x1FFu),   // Y: bits 5-13
        int((packed >> 14) & 0x1Fu)    // Z: bits 14-18
    );
}

uint unpackQuadFace(uint packed) {
    return (packed >> 19) & 0x7u; // Face: bits 19-21
}

uvec2 unpackQuadExtents(uint packed) {
    return uvec2(
        (packed >> 22) & 0x1Fu,  // ExtentX: bits 22-26
        (packed >> 27) & 0x1Fu   // ExtentZ: bits 27-31
    );
}

void main() {
    uint chunkIdx = gl_WorkGroupID.x;
    uint localIdx = gl_LocalInvocationID.x;
    
    // Read quad count for this chunk
    uint quadCount = quadCounts[chunkIdx];
    
    // Each thread processes multiple quads
    uint quadsPerThread = (quadCount + 63) / 64;
    
    for (uint i = 0; i < quadsPerThread; i++) {
        uint quadIdx = localIdx + i * 64;
        if (quadIdx >= quadCount) break;
        
        // Calculate global quad index
        uint maxQuadsPerChunk = CHUNK_VOXEL_COUNT * 3u;
        uint globalQuadIdx = chunkIdx * maxQuadsPerChunk + quadIdx;
        
        // Read packed quad data
        uint packed1 = mergedQuads[globalQuadIdx * 2u + 0u];
        uint packed2 = mergedQuads[globalQuadIdx * 2u + 1u];
        
        // Unpack inline
        ivec3 startPos = unpackQuadStart(packed1);
        uint face = unpackQuadFace(packed1);
        uvec2 extents = unpackQuadExtents(packed1);
        
        uint blockType = packed2 & 0xFFu;
        uint ao0 = (packed2 >> 8) & 0x7u;
        uint ao1 = (packed2 >> 11) & 0x7u;
        uint ao2 = (packed2 >> 14) & 0x7u;
        uint ao3 = (packed2 >> 17) & 0x7u;
        
        // Emit merged quad with extent scaling
        emitMergedQuad(chunkIdx, startPos, face, extents, blockType, ao0, ao1, ao2, ao3);
    }
}
```

### 2. Update Count Shader

**Current** (Phase GM-1):
- Counts visible faces per chunk
- Reads from `visibilityMask[]`

**New** (Phase GM-2):
- Reads `quadCounts[]` directly
- No need to iterate voxels

```glsl
void main() {
    uint chunkIdx = gl_WorkGroupID.x;
    
    // Simply read the quad count from Phase GM-1
    uint quadCount = quadCounts[chunkIdx];
    
    // Write to count buffer (used by scan)
    visibleCounts[chunkIdx] = quadCount;
    
    // Update opaque counts (assume all quads are opaque for now)
    opaqueCounts[chunkIdx] = quadCount;
}
```

### 3. Update ChunkStreamingManager

**Changes in `ExecutePhase3_Part1`**:
- Pass `useGreedyMeshing` flag to shaders
- Skip visibility shader if greedy meshing is enabled (already have quads)
- Adjust count shader to read quad counts

**Changes in `ExecutePhase3_Part2`**:
- Adjust vertex/index allocation based on quad counts (not face counts)
- Update logging to show vertex reduction statistics

---

## Vertex Generation Logic

### Standard Face (1×1)
```
4 vertices, 6 indices (2 triangles)

v0 ---- v1
|     / |
|   /   |
| /     |
v2 ---- v3
```

### Merged Quad (extentX × extentZ)
```
4 vertices, 6 indices (2 triangles)
BUT: vertex positions scaled by extents

v0 -------------------- v1
|                    /  |
|                  /    |
|                /      |  extentZ blocks high
|              /        |
|            /          |
|          /            |
|        /              |
|      /                |
|    /                  |
v2 -------------------- v3
      extentX blocks wide

Position scaling:
- v0: (startX, startY, startZ)
- v1: (startX + extentX, startY, startZ)
- v2: (startX, startY, startZ + extentZ)  [for +Y face]
- v3: (startX + extentX, startY, startZ + extentZ)

Texture coordinates scaled:
- u: 0.0 to extentX
- v: 0.0 to extentZ
```

### Face-Specific Vertex Calculation

Each face orientation needs different vertex position calculations based on extents:

**+Y Face (top)**: Extends in X and Z
```glsl
vec3 v0 = vec3(startPos.x, startPos.y, startPos.z);
vec3 v1 = vec3(startPos.x + extentX, startPos.y, startPos.z);
vec3 v2 = vec3(startPos.x, startPos.y, startPos.z + extentZ);
vec3 v3 = vec3(startPos.x + extentX, startPos.y, startPos.z + extentZ);
```

**+X Face (right)**: Extends in Y and Z
```glsl
vec3 v0 = vec3(startPos.x, startPos.y, startPos.z);
vec3 v1 = vec3(startPos.x, startPos.y + extentX, startPos.z);  // extentX used for height
vec3 v2 = vec3(startPos.x, startPos.y, startPos.z + extentZ);
vec3 v3 = vec3(startPos.x, startPos.y + extentX, startPos.z + extentZ);
```

**+Z Face (front)**: Extends in X and Y
```glsl
vec3 v0 = vec3(startPos.x, startPos.y, startPos.z);
vec3 v1 = vec3(startPos.x + extentX, startPos.y, startPos.z);
vec3 v2 = vec3(startPos.x, startPos.y + extentZ, startPos.z);  // extentZ used for height
vec3 v3 = vec3(startPos.x + extentX, startPos.y + extentZ, startPos.z);
```

---

## Testing Strategy

### Phase 1: Verify Compilation
1. Build with new compact shader
2. Check for GL errors
3. Ensure no crashes

### Phase 2: Verify Correctness
1. Enable greedy meshing (F4)
2. Verify terrain renders identically
3. Check for visual artifacts (holes, missing faces)
4. Test in various biomes (ocean, hills, mountains, caves)

### Phase 3: Measure Performance
1. Compare vertex counts before/after
2. Measure FPS improvement
3. Check GPU memory usage
4. Profile vertex shader performance

### Expected Results

**Vertex Reduction**:
```
Before: 2,708 vertices/chunk × 842 chunks = 2,280,136 vertices
After:  588 vertices/chunk × 842 chunks = 495,096 vertices
Reduction: 78% (1,785,040 fewer vertices)
```

**Memory Savings**:
```
Before: 2,280,136 vertices × 8 bytes/vertex = 17.4 MB
After:  495,096 vertices × 8 bytes/vertex = 3.8 MB
Savings: 13.6 MB (78%)
```

**Performance Impact**:
- **Vertex shader invocations**: 78% reduction
- **Fragment shader invocations**: Similar (depends on screen coverage)
- **Memory bandwidth**: 78% reduction
- **Cache efficiency**: ~4.5x improvement (fewer cache lines)

---

## Potential Issues & Solutions

### Issue 1: Texture Coordinate Stretching

**Problem**: Large merged quads may have distorted textures.

**Solution**: Scale texture coordinates by extent:
```glsl
vec2 texCoord = baseTexCoord * vec2(extentX, extentZ);
```

### Issue 2: Ambient Occlusion Interpolation

**Problem**: AO values are per-corner, but quads span multiple blocks.

**Solution** (Phase GM-2.1):
- For now, use placeholder AO (all corners = 4 = no occlusion)
- Future: Calculate proper AO for merged quad corners

### Issue 3: Mixed Block Types

**Problem**: Greedy merge should only merge same block type.

**Solution**: Already handled in Phase GM-1 shader (`canMerge` checks `s_types`)

### Issue 4: Water vs Opaque Separation

**Problem**: Rendering order requires separate opaque/transparent passes.

**Solution**: Phase GM-1 already generates separate counts (`opaqueCounts`, `waterEmit`)

---

## Rollback Plan

If Phase GM-2 causes issues:

1. **Disable greedy meshing**: Press F4 to toggle off
2. **Revert compact shader**: Restore from backup
3. **Keep Phase GM-1**: Infrastructure remains useful for future work

The feature flag (`UseGreedyMeshing`) allows safe testing without breaking existing functionality.

---

## Success Criteria

- ✅ Build compiles without errors
- ✅ Terrain renders identically (no visual artifacts)
- ✅ Vertex count reduced by ~78%
- ✅ No performance regression (should improve)
- ✅ F4 toggle works correctly (can switch modes)
- ✅ All biomes render correctly (ocean, hills, mountains, caves)

---

## Next Steps (Future Phases)

**Phase GM-2.1**: Proper AO calculation for merged quads
**Phase GM-2.2**: Transparency sorting for merged water quads
**Phase GM-2.3**: Texture atlas support (tiling for large quads)
**Phase GM-3**: Optimize greedy merge algorithm (better merging)

---

**Implementation Date**: 2026-01-26  
**Status**: 🚧 IN PROGRESS  
**Expected Completion**: Today  
**Risk Level**: Medium (requires careful shader changes)
