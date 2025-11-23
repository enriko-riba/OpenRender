# Phase 3: Visibility & Compaction Pipeline - Architectural Design

**Version**: 1.0  
**Date**: 2025-01-14  
**Status**: Design Phase  

---

## Table of Contents

1. [Overview](#overview)
2. [Core Principles](#core-principles)
3. [Pipeline Architecture](#pipeline-architecture)
4. [Buffer Design](#buffer-design)
5. [Shader Specifications](#shader-specifications)
6. [Memory Barriers & Synchronization](#memory-barriers--synchronization)
7. [Error Prevention Strategy](#error-prevention-strategy)
8. [Implementation Checklist](#implementation-checklist)

---

## Overview

Phase 3 implements the **visibility determination** and **mesh compaction** stages of the GPU terrain pipeline. This phase converts sparse voxel data into a compact, renderable mesh by:

1. **Visibility Pass**: Determine which voxel faces are visible (not occluded by neighbors)
2. **Compaction Pass**: Build a compact vertex buffer containing only visible faces
3. **Prefix Sum**: Calculate per-chunk offsets for multi-draw indirect rendering

### Key Goals

✅ **Correctness First**: Every buffer operation must be explicitly initialized and validated  
✅ **Explicit Synchronization**: All GPU-CPU and GPU-GPU dependencies tracked with barriers  
✅ **Predictable Memory Layout**: Fixed-size allocations, no dynamic resizing mid-frame  
✅ **Debug-Friendly**: Every stage outputs intermediate buffers for validation  

---

## Core Principles

### Principle 1: **Explicit Initialization**

**Problem (Old System)**: Buffers contained garbage data, causing unpredictable behavior  
**Solution**: Every buffer must be explicitly cleared or filled before first use  

```csharp
// CORRECT: Explicit initialization
GL.NamedBufferStorage(buffer, size, IntPtr.Zero, flags);
GL.ClearNamedBufferSubData(buffer, SizedInternalFormat.R32ui, 
    IntPtr.Zero, size, PixelFormat.RedInteger, PixelType.UnsignedInt, ref zero);

// WRONG: Assume buffer is zeroed
GL.NamedBufferStorage(buffer, size, IntPtr.Zero, flags);
// ❌ Buffer may contain garbage!
```

### Principle 2: **Explicit Synchronization**

**Problem (Old System)**: Missing memory barriers caused race conditions  
**Solution**: Insert barriers at every stage boundary  

```csharp
// Stage 1: Visibility
GL.DispatchCompute(chunkCount, 1, 1);
GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

// Stage 2: Count visible faces
GL.DispatchCompute(chunkCount, 1, 1);
GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

// Stage 3: CPU reads results
GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
```

### Principle 3: **Predictable Allocations**

**Problem (Old System)**: Buffer sizes unpredictable, leading to overflow/underflow  
**Solution**: Pre-allocate worst-case sizes, use atomic counters for actual usage  

```csharp
// Worst-case: every voxel has 3 visible faces (average is ~1.2)
int maxVertices = chunkCount * CHUNK_VOXEL_COUNT * 3 * 4; // 3 faces * 4 verts
GL.NamedBufferStorage(vertexBuffer, maxVertices * vertexStride, IntPtr.Zero, flags);
```

### Principle 4: **Single Responsibility**

**Problem (Old System)**: Shaders tried to do too much in one pass  
**Solution**: Each shader has ONE clear job  

- `compute-visibility.comp`: Mark visible faces (**nothing else**)
- `compute-count.comp`: Count visible faces per chunk (**nothing else**)
- `compute-compact.comp`: Build vertex buffer (**nothing else**)

---

## Pipeline Architecture

### Stage Overview

```
┌─────────────────────────────────────────────────────────────────┐
│ Phase 2: Generation (COMPLETE)                                  │
│ Output: voxelData[chunkCount * CHUNK_VOXEL_COUNT]              │
└────────────────────┬────────────────────────────────────────────┘
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ Stage 3.1: Visibility Determination (NEW)                       │
│                                                                  │
│ Shader: compute-visibility.comp                                 │
│ Input:  voxelData[] (uint packed)                              │
│ Output: visibilityMask[] (uint bitfield, 6 bits per voxel)    │
│                                                                  │
│ Job: For each voxel, check 6 neighbors:                        │
│      - Bit 0: +X face visible                                   │
│      - Bit 1: -X face visible                                   │
│      - Bit 2: +Y face visible                                   │
│      - Bit 3: -Y face visible                                   │
│      - Bit 4: +Z face visible                                   │
│      - Bit 5: -Z face visible                                   │
│                                                                  │
│ ⚠️  BARRIER: GL.MemoryBarrier(ShaderStorageBarrierBit)         │
└────────────────────┬────────────────────────────────────────────┘
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ Stage 3.2: Count Visible Faces (NEW)                            │
│                                                                  │
│ Shader: compute-count.comp                                      │
│ Input:  visibilityMask[] (from Stage 3.1)                      │
│ Output: visibleCounts[] (uint per chunk)                       │
│                                                                  │
│ Job: For each chunk, count total visible faces                 │
│      count = sum(popcount(visibilityMask[i]))                  │
│                                                                  │
│ ⚠️  BARRIER: GL.MemoryBarrier(ShaderStorageBarrierBit)         │
└────────────────────┬────────────────────────────────────────────┘
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ Stage 3.3: CPU Prefix Sum (NEW)                                 │
│                                                                  │
│ CPU Code: ChunkStreamingManager.ComputePrefixSum()             │
│ Input:  visibleCounts[] (download from GPU)                    │
│ Output: baseOffsets[] (uint per chunk)                         │
│         totalVertices (uint)                                    │
│                                                                  │
│ Job: Calculate base offset for each chunk in vertex buffer     │
│      baseOffsets[i] = sum(visibleCounts[0..i-1]) * 4          │
│                                                                  │
│ ⚠️  BARRIER: GL.MemoryBarrier(BufferUpdateBarrierBit)          │
└────────────────────┬────────────────────────────────────────────┘
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ Stage 3.4: Mesh Compaction (NEW)                                │
│                                                                  │
│ Shader: compute-compact.comp                                    │
│ Input:  voxelData[] (from Phase 2)                             │
│         visibilityMask[] (from Stage 3.1)                      │
│         baseOffsets[] (from Stage 3.3)                         │
│ Output: compactedVertices[] (vec4: position, texCoord, AO)    │
│         compactedIndices[] (uint)                              │
│                                                                  │
│ Job: For each visible face, emit 4 vertices and 6 indices      │
│      Use atomic counter to allocate vertex/index slots         │
│      Calculate ambient occlusion for each vertex               │
│                                                                  │
│ ⚠️  BARRIER: GL.MemoryBarrier(VertexAttribArrayBarrierBit)     │
└────────────────────┬────────────────────────────────────────────┘
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ Phase 4: Rendering (NEXT)                                       │
│ Input: compactedVertices[], compactedIndices[]                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## Buffer Design

### Buffer Naming Convention

All buffers follow this pattern: `{stage}_{purpose}_{dataType}`

Examples:
- `gen_voxelData_ssbo` - Generation output (voxel data)
- `vis_mask_ssbo` - Visibility mask
- `count_visible_ssbo` - Visible face counts
- `compact_vertices_vbo` - Compacted vertex buffer

### Buffer Lifecycle

| Buffer Name | Size | Initialized | Producer | Consumer | Lifetime |
|-------------|------|-------------|----------|----------|----------|
| `gen_voxelData_ssbo` | `chunkCount * CHUNK_VOXEL_COUNT * 4` | ✅ Phase 2 | `compute-generate.comp` | `compute-visibility.comp` | Persistent |
| `vis_mask_ssbo` | `chunkCount * CHUNK_VOXEL_COUNT * 1` | ✅ Stage 3.1 | `compute-visibility.comp` | `compute-count.comp` | Transient |
| `count_visible_ssbo` | `chunkCount * 4` | ✅ Stage 3.2 | `compute-count.comp` | CPU | Transient |
| `offset_base_ssbo` | `chunkCount * 4` | ✅ CPU | CPU | `compute-compact.comp` | Transient |
| `compact_vertices_vbo` | `maxVertices * 32` | ✅ Stage 3.4 | `compute-compact.comp` | Renderer | Persistent |
| `compact_indices_ibo` | `maxIndices * 4` | ✅ Stage 3.4 | `compute-compact.comp` | Renderer | Persistent |

### Buffer Initialization Strategy

```csharp
public class Phase3BufferManager
{
    // Allocate all buffers upfront during initialization
    public void AllocateBuffers(int maxChunks)
    {
        var voxelCount = maxChunks * VoxelHelper.CHUNK_VOXEL_COUNT;
        
        // Visibility mask (1 byte per voxel, stores 6 face bits)
        visMaskBuffer = GL.GenBuffer();
        GL.NamedBufferStorage(visMaskBuffer, voxelCount, IntPtr.Zero, 
            BufferStorageFlags.DynamicStorageBit);
        ClearBuffer(visMaskBuffer, voxelCount, 0); // Explicit zero
        
        // Visible counts (1 uint per chunk)
        countBuffer = GL.GenBuffer();
        GL.NamedBufferStorage(countBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapReadBit);
        ClearBuffer(countBuffer, maxChunks * sizeof(uint), 0);
        
        // Base offsets (1 uint per chunk)
        offsetBuffer = GL.GenBuffer();
        GL.NamedBufferStorage(offsetBuffer, maxChunks * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        
        // Compacted vertices (worst-case: 3 faces * 4 verts per voxel)
        var maxVerts = voxelCount * 3 * 4;
        vertexBuffer = GL.GenBuffer();
        GL.NamedBufferStorage(vertexBuffer, maxVerts * VERTEX_STRIDE, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        
        // Compacted indices (6 per quad)
        var maxIndices = voxelCount * 3 * 6;
        indexBuffer = GL.GenBuffer();
        GL.NamedBufferStorage(indexBuffer, maxIndices * sizeof(uint), IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
    }
    
    // Clear buffer to known value (solves garbage data problem)
    private void ClearBuffer(uint buffer, int sizeBytes, byte value)
    {
        var data = new byte[sizeBytes];
        Array.Fill(data, value);
        GL.NamedBufferSubData(buffer, IntPtr.Zero, sizeBytes, data);
    }
}
```

---

## Shader Specifications

### Shader 1: `compute-visibility.comp`

**Purpose**: Determine which faces of each voxel are visible (not occluded)

**Work Group Size**: `16x8x2` (256 threads = good occupancy)

**Inputs**:
- `voxelData[]` (SSBO binding 0): Packed voxel data from Phase 2
- Chunk metadata (uniforms): chunk indices, world dimensions

**Outputs**:
- `visibilityMask[]` (SSBO binding 1): 1 byte per voxel (6 bits used)

**Algorithm**:
```glsl
layout(local_size_x = 16, local_size_y = 8, local_size_z = 2) in;

layout(std430, binding = 0) readonly buffer VoxelData {
    uint voxelData[];  // From Phase 2
};

layout(std430, binding = 1) writeonly buffer VisibilityMask {
    uint visibilityMask[];  // 6 bits per voxel
};

uniform uint uChunkCount;
uniform uint uChunkIndices[64];  // Max batch size

void main() {
    uint chunkIdx = gl_WorkGroupID.x;
    if (chunkIdx >= uChunkCount) return;
    
    // Local voxel coordinates within chunk
    uint lx = gl_LocalInvocationID.x;  // 0-15
    uint ly = gl_LocalInvocationID.y;  // 0-127 (8 invocations * 16 dispatches)
    uint lz = gl_LocalInvocationID.z;  // 0-15
    
    uint voxelIdx = chunkIdx * CHUNK_VOXEL_COUNT + 
                    lz * CHUNK_SIDE_SIZE_SQUARED + 
                    ly * CHUNK_SIDE_SIZE + lx;
    
    uint voxel = voxelData[voxelIdx];
    uint blockType = voxel & 0xFFu;
    
    // Air blocks have no visible faces
    if (blockType == 0u) {
        visibilityMask[voxelIdx] = 0u;
        return;
    }
    
    uint mask = 0u;
    
    // Check +X neighbor
    if (lx < CHUNK_SIDE_SIZE - 1) {
        uint neighbor = voxelData[voxelIdx + 1];
        if (isTransparent(neighbor)) mask |= (1u << 0);
    } else {
        // Boundary: assume visible (TODO: cross-chunk check)
        mask |= (1u << 0);
    }
    
    // Check -X neighbor
    if (lx > 0) {
        uint neighbor = voxelData[voxelIdx - 1];
        if (isTransparent(neighbor)) mask |= (1u << 1);
    } else {
        mask |= (1u << 1);
    }
    
    // Repeat for +Y, -Y, +Z, -Z...
    // (Implementation detail: see full shader)
    
    visibilityMask[voxelIdx] = mask;
}

bool isTransparent(uint voxel) {
    uint blockType = voxel & 0xFFu;
    return blockType == 0u || blockType == 7u;  // Air or Water
}
```

**Key Points**:
- ✅ No atomic operations (pure compute)
- ✅ No shared memory needed (neighbors are direct SSBO reads)
- ✅ Boundary chunks mark edges as visible (Phase 5 will add cross-chunk checks)
- ⚠️  **Barrier Required**: After this shader completes

---

### Shader 2: `compute-count.comp`

**Purpose**: Count total visible faces per chunk

**Work Group Size**: `256x1x1` (one thread per chunk region)

**Inputs**:
- `visibilityMask[]` (SSBO binding 0): From visibility pass

**Outputs**:
- `visibleCounts[]` (SSBO binding 1): 1 uint per chunk

**Algorithm**:
```glsl
layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

layout(std430, binding = 0) readonly buffer VisibilityMask {
    uint visibilityMask[];
};

layout(std430, binding = 1) writeonly buffer VisibleCounts {
    uint visibleCounts[];
};

uniform uint uChunkCount;

void main() {
    uint chunkIdx = gl_GlobalInvocationID.x;
    if (chunkIdx >= uChunkCount) return;
    
    uint baseIdx = chunkIdx * CHUNK_VOXEL_COUNT;
    uint count = 0u;
    
    // Count visible faces in this chunk
    for (uint i = 0u; i < CHUNK_VOXEL_COUNT; i++) {
        uint mask = visibilityMask[baseIdx + i];
        count += bitCount(mask);  // popcount: count set bits
    }
    
    visibleCounts[chunkIdx] = count;
}
```

**Key Points**:
- ✅ Simple reduction (no atomics needed)
- ✅ CPU reads this buffer next
- ⚠️  **Barrier Required**: Before CPU reads

---

### CPU Stage: Prefix Sum

**Purpose**: Calculate base vertex offset for each chunk

**Implementation**:
```csharp
public class ChunkStreamingManager
{
    public (uint[] baseOffsets, uint totalVertices) ComputePrefixSum(uint[] visibleCounts)
    {
        var baseOffsets = new uint[visibleCounts.Length];
        uint runningTotal = 0;
        
        for (int i = 0; i < visibleCounts.Length; i++)
        {
            baseOffsets[i] = runningTotal;
            runningTotal += visibleCounts[i] * 4;  // 4 vertices per face
        }
        
        // Upload offsets back to GPU
        GL.NamedBufferSubData(offsetBuffer, IntPtr.Zero, 
            baseOffsets.Length * sizeof(uint), baseOffsets);
        
        // Insert barrier before next shader reads offsets
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
        
        return (baseOffsets, runningTotal);
    }
}
```

---

### Shader 3: `compute-compact.comp`

**Purpose**: Build compact vertex and index buffers

**Work Group Size**: `16x8x2` (same as visibility)

**Inputs**:
- `voxelData[]` (SSBO binding 0): From Phase 2
- `visibilityMask[]` (SSBO binding 1): From visibility pass
- `baseOffsets[]` (SSBO binding 2): From CPU prefix sum

**Outputs**:
- `compactVertices[]` (SSBO binding 3): Vertex buffer
- `compactIndices[]` (SSBO binding 4): Index buffer

**Algorithm**:
```glsl
layout(local_size_x = 16, local_size_y = 8, local_size_z = 2) in;

// ... inputs/outputs ...

layout(std430, binding = 5) buffer AtomicCounters {
    uint vertexCounter;
    uint indexCounter;
};

void main() {
    // ... compute voxel index ...
    
    uint voxel = voxelData[voxelIdx];
    uint mask = visibilityMask[voxelIdx];
    
    if (mask == 0u) return;  // No visible faces
    
    // Get base offset for this chunk
    uint baseOffset = baseOffsets[chunkIdx];
    
    // For each visible face...
    for (uint face = 0u; face < 6u; face++) {
        if ((mask & (1u << face)) == 0u) continue;
        
        // Atomically allocate 4 vertex slots and 6 index slots
        uint vertBase = atomicAdd(vertexCounter, 4u);
        uint idxBase = atomicAdd(indexCounter, 6u);
        
        // Emit quad vertices
        vec3 pos = getVoxelPosition(lx, ly, lz);
        vec3[4] corners = getFaceCorners(face, pos);
        float ao[4] = calculateAO(face, corners);
        
        for (uint v = 0u; v < 4u; v++) {
            compactVertices[vertBase + v] = packVertex(
                corners[v],
                getFaceNormal(face),
                getFaceUV(v),
                ao[v]
            );
        }
        
        // Emit quad indices (two triangles)
        compactIndices[idxBase + 0] = vertBase + 0;
        compactIndices[idxBase + 1] = vertBase + 1;
        compactIndices[idxBase + 2] = vertBase + 2;
        compactIndices[idxBase + 3] = vertBase + 0;
        compactIndices[idxBase + 4] = vertBase + 2;
        compactIndices[idxBase + 5] = vertBase + 3;
    }
}

float calculateAO(uint face, vec3[4] corners) {
    // Simple AO: check 3x3 neighborhood of each corner
    // Return [0,1] where 0=full occlusion, 1=no occlusion
    // (Implementation detail: see ambient occlusion docs)
}
```

**Key Points**:
- ✅ Atomic counters ensure thread-safe vertex allocation
- ✅ AO calculated per-vertex for smooth shading
- ⚠️  **Barrier Required**: Before rendering

---

## Memory Barriers & Synchronization

### Barrier Placement Rules

**Rule 1**: After every compute dispatch that writes to a buffer  
**Rule 2**: Before CPU reads GPU buffer  
**Rule 3**: Before rendering from compute-written buffers  

### Complete Barrier Sequence

```csharp
public void ExecutePhase3(int[] chunkIndices)
{
    // Stage 3.1: Visibility
    BindBuffer(0, gen_voxelData_ssbo);
    BindBuffer(1, vis_mask_ssbo);
    visibilityShader.Use();
    GL.DispatchCompute(chunkIndices.Length, 16, 1);  // 16 Y slices
    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
    
    // Stage 3.2: Count
    BindBuffer(0, vis_mask_ssbo);
    BindBuffer(1, count_visible_ssbo);
    countShader.Use();
    GL.DispatchCompute((chunkIndices.Length + 255) / 256, 1, 1);
    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
    
    // Stage 3.3: Download counts (CPU)
    GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
    var counts = new uint[chunkIndices.Length];
    GL.GetNamedBufferSubData(count_visible_ssbo, IntPtr.Zero, 
        counts.Length * sizeof(uint), counts);
    
    // Stage 3.3b: Prefix sum (CPU)
    var (offsets, totalVerts) = ComputePrefixSum(counts);
    GL.NamedBufferSubData(offset_base_ssbo, IntPtr.Zero, 
        offsets.Length * sizeof(uint), offsets);
    GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
    
    // Stage 3.4: Compaction
    BindBuffer(0, gen_voxelData_ssbo);
    BindBuffer(1, vis_mask_ssbo);
    BindBuffer(2, offset_base_ssbo);
    BindBuffer(3, compact_vertices_vbo);
    BindBuffer(4, compact_indices_ibo);
    BindBuffer(5, atomic_counters_ssbo);
    compactShader.Use();
    GL.DispatchCompute(chunkIndices.Length, 16, 1);
    GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit | 
                      MemoryBarrierFlags.ElementArrayBarrierBit);
    
    // Now safe to render!
}
```

---

## Error Prevention Strategy

### Problem 1: Garbage Data in Buffers

**Root Cause**: Buffers not explicitly initialized  
**Solution**: Zero-fill every buffer before first use  

```csharp
// ALWAYS do this after buffer creation
GL.NamedBufferStorage(buffer, size, IntPtr.Zero, flags);
var zero = 0u;
GL.ClearNamedBufferSubData(buffer, SizedInternalFormat.R32ui,
    IntPtr.Zero, size, PixelFormat.RedInteger, PixelType.UnsignedInt, ref zero);
```

### Problem 2: Race Conditions

**Root Cause**: Missing or incorrect memory barriers  
**Solution**: Insert barriers at every stage boundary  

Use this checklist:
- [ ] After compute dispatch? → `ShaderStorageBarrierBit`
- [ ] Before CPU read? → `BufferUpdateBarrierBit`
- [ ] Before rendering? → `VertexAttribArrayBarrierBit`

### Problem 3: Buffer Size Mismatches

**Root Cause**: Dynamic sizes calculated incorrectly  
**Solution**: Pre-allocate worst-case, track actual usage with atomics  

```glsl
// Worst-case: every voxel has 3 visible faces
layout(std430, binding = 5) buffer Counters {
    uint actualVertexCount;  // Track real usage
    uint actualIndexCount;
};

void main() {
    if (mask != 0u) {
        uint slot = atomicAdd(actualVertexCount, 4u);
        // Use slot...
    }
}
```

### Problem 4: Incorrect SSBO Bindings

**Root Cause**: Binding points mismatched between C# and GLSL  
**Solution**: Use constants and validation  

```csharp
public static class BufferBindings
{
    public const int VOXEL_DATA = 0;
    public const int VISIBILITY_MASK = 1;
    public const int VISIBLE_COUNTS = 2;
    public const int BASE_OFFSETS = 3;
    public const int COMPACT_VERTICES = 4;
    public const int COMPACT_INDICES = 5;
    public const int ATOMIC_COUNTERS = 6;
}

// In shader:
// layout(std430, binding = 1) buffer VisibilityMask { ... };

// In C#:
GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 
    BufferBindings.VISIBILITY_MASK, vis_mask_ssbo);
```

---

## Implementation Checklist

### Phase 3.1: Visibility Shader

- [ ] Create `compute-visibility.comp` with work group size 16x8x2
- [ ] Implement 6-neighbor checking logic
- [ ] Add boundary condition handling (edges = visible)
- [ ] Test with single chunk (verify output bits)
- [ ] Test with 64-chunk batch
- [ ] Validate barrier placement

### Phase 3.2: Count Shader

- [ ] Create `compute-count.comp` with work group size 256x1x1
- [ ] Implement bitCount reduction
- [ ] Test count output matches manual count
- [ ] Verify CPU can read counts buffer

### Phase 3.3: CPU Prefix Sum

- [ ] Implement `ComputePrefixSum()` method
- [ ] Add buffer upload for offsets
- [ ] Insert correct memory barrier
- [ ] Test offset calculation correctness

### Phase 3.4: Compaction Shader

- [ ] Create `compute-compact.comp` with atomic allocation
- [ ] Implement vertex emission for each face type
- [ ] Add AO calculation (simple 3x3 neighborhood)
- [ ] Test vertex/index output format
- [ ] Verify no buffer overflows
- [ ] Validate final vertex count matches expected

### Integration

- [ ] Add Phase3BufferManager class
- [ ] Update ChunkStreamingManager with phase 3 methods
- [ ] Add diagnostic logging for each stage
- [ ] Create unit test for single-chunk pipeline
- [ ] Create stress test for 64-chunk batch
- [ ] Profile GPU timing for each stage

---

## Success Criteria

✅ **Correctness**:
- [ ] Visibility mask matches manual calculation for test chunk
- [ ] Vertex count matches expected (no overflow/underflow)
- [ ] All buffers initialized before use
- [ ] No garbage data in any output buffer

✅ **Performance**:
- [ ] Visibility pass: <0.5ms for 64 chunks
- [ ] Count pass: <0.1ms for 64 chunks
- [ ] Compaction pass: <1ms for 64 chunks
- [ ] Total Phase 3: <2ms for 64 chunks (meets target)

✅ **Robustness**:
- [ ] No driver-specific behavior differences
- [ ] Deterministic output (same input → same output)
- [ ] Graceful handling of edge cases (empty chunks, all-solid chunks)

---

## Next Steps

After Phase 3 is complete:

1. **Phase 4**: Implement MDI setup and rendering shaders
2. **Phase 5**: Add streaming and edit support
3. **Phase 6**: Profile and optimize

**Ready to implement!** 🚀
