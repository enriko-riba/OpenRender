# Optimization Plan for Spyro Game Voxel Engine

## Executive Summary
This document outlines a series of optimizations for the voxel engine, focusing on memory bandwidth reduction and compute shader efficiency. The primary recommendations are to pack the visibility mask (75% reduction) and compress the vertex format (40%+ reduction).

## 1. Bandwidth Optimizations

### 1.1. Visibility Mask Packing (High Priority)
**Current State:**
The visibility mask uses one `uint` (32 bits) per voxel to store ~7 bits of information (6 faces + 1 water bit).
*   Buffer Size: ~384 KB per chunk.
*   Total for 64 chunks: 24 MB.

**Recommendation:**
Pack 4 voxels into a single `uint` (8 bits per voxel).
*   New Buffer Size: ~96 KB per chunk.
*   Total for 64 chunks: 6 MB.
*   **Benefit:** significantly reduces memory bandwidth during the `compute-visibility` and `compute-count`/`compute-compact` passes.

**Implementation Details:**
*   **Buffer Allocation:** Reduce `visMaskBuffer` size by factor of 4.
*   **Compute Visibility:** Use atomic operations or careful indexing to write 8-bit segments. Since threads process columns, we can have one thread write a packed `uint` for 4 vertical voxels, or use `imageStore` with `r8ui` format (if bound as image), or just use bitwise operations on `uint` array.
    *   *Note:* `compute-visibility` runs 1 thread per voxel. Writing to packed `uint`s from multiple threads requires atomics or changing the thread mapping.
    *   *Alternative:* Change `compute-visibility` to process 4 voxels per thread (vertical strip). This eliminates atomics and improves instruction cache usage.
*   **Compute Count/Compact:** Update read logic to extract 8-bit mask.

### 1.2. Vertex Format Compression (Medium Priority)
**Current State:**
28 bytes per vertex: `Pos(12) + UV(8) + AO(4) + Face(4)`.

**Recommendation:**
Compress to 16 bytes per vertex.
*   **Position:** Use local chunk coordinates (packed `uint` or `ushort`s) + `gl_DrawID` lookup for chunk offset. (Savings: 8 bytes)
*   **UV:** Use `ushort` normalized (4 bytes). (Savings: 4 bytes)
*   **AO:** Use `unorm8` (1 byte). (Savings: 3 bytes)
*   **Face/Data:** Keep as `ubyte` or `ushort`.

**Benefit:** Reduces VRAM usage and vertex fetch bandwidth by ~43%.

### 1.3. Greedy Meshing (High Impact / High Effort)
**Current State:**
Faces are generated individually. A flat wall of 16x16 blocks generates 256 separate quads (1024 vertices).

**Recommendation:**
Implement Greedy Meshing in the compute shader to merge adjacent faces of the same type/texture into larger quads.
*   **Benefit:** Can reduce vertex and index count by 50-80% for typical terrain. Drastically reduces vertex shader invocations.
*   **Implementation:** Requires a more complex compute kernel (likely slice-based) to scan and merge runs of identical faces. Texture coordinates will need to be adjusted to tile correctly across the larger quad.

## 2. Performance Optimizations

### 2.1. Compute Shader Workgroup Optimization
**Current State:**
`compute-visibility` uses `16x1x16` (256 threads). Each thread processes 1 voxel.
`compute-count` uses `64x1x1` and processes 4 voxels per thread.

**Recommendation:**
*   Align `compute-visibility` to process vertical strips (e.g., 4 voxels per thread). This synergizes with Visibility Mask Packing (allows non-atomic writes).
*   Ensure memory accesses are coalesced.

### 2.2. Indirect Draw Batching
**Current State:**
Uses `MultiDrawElementsIndirect`.
**Recommendation:**
Ensure `gl_DrawID` is utilized to fetch per-chunk data (transform, offset) to avoid updating uniforms or pushing constants.

### 2.3. GPU Occlusion Culling (Advanced)
**Current State:**
Frustum culling is implemented in `compute-frustum.comp`. However, chunks hidden behind mountains or underground are still processed by the vertex shader (though depth testing discards fragments).

**Recommendation:**
Implement Compute-based Occlusion Culling (Hi-Z Culling).
*   **Technique:** Generate a hierarchical depth buffer (Hi-Z pyramid) from the previous frame's depth buffer.
*   **Implementation:** In `compute-frustum.comp`, after passing frustum check, sample the Hi-Z buffer to check if the chunk's AABB is occluded. If occluded, set `instanceCount` to 0.
*   **Benefit:** Massive performance gain for large view distances and complex terrain (caves, valleys).

## 3. Implementation Plan & Effort

### Phase 1: Visibility Mask Packing (Completed 2025-11-25)
1.  Modify `Phase3BufferManager.cs` to allocate 1/4 size buffer. (Done)
2.  Modify `compute-visibility.comp`: (Done)
    *   Change dispatch to process 4 voxels per thread (reduce Y dimension of dispatch or handle loop inside).
    *   Pack 4 masks into one `uint`.
    *   Write to buffer.
3.  Modify `compute-count.comp` & `compute-compact.comp`: (Done)
    *   Update reading logic to unpack mask.

### Phase 2: Vertex Compression (Est. 8-12 hours)
1.  Define new vertex struct/layout.
2.  Update `compute-compact.comp` to pack data.
3.  Update `voxel-terrain.vert` to unpack data.
4.  Implement `gl_DrawID` based chunk offset lookup (requires binding `ChunkInfoBuffer` to vertex shader).

### Phase 3: Greedy Meshing (Est. 16-24 hours)
1.  Prototype greedy meshing logic (likely on CPU first or simple compute shader).
2.  Replace `compute-compact.comp` with greedy version.
3.  Update texture coordinate handling in vertex shader.

### Phase 4: Occlusion Culling (Est. 12-16 hours)
1.  Implement depth pyramid generation (mip-mapping depth buffer).
2.  Update `compute-frustum.comp` to sample depth map.
3.  Handle frame latency (reprojection) if necessary.

### Phase 5: Code Cleanup (Est. 2 hours)
1.  Remove unused uniforms/buffers.
2.  Standardize binding points.

## 4. Immediate Next Steps
Start with **Phase 2 (Vertex Compression)** to further reduce memory usage and bandwidth.
