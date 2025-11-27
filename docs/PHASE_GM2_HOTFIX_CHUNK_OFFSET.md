# Phase GM-2 Hotfix: Missing chunkWorldOffset Variable

## Date: 2026-01-26
## Status: ✅ FIXED

---

## Issue

Shader compilation error in `compute-compact.comp`:

```
0(1079) : error C1503: undefined variable "chunkWorldOffset"
0(1109) : error C1503: undefined variable "chunkWorldOffset"
0(1112) : error C1503: undefined variable "chunkWorldOffset"
```

---

## Root Cause

The `main()` function structure had the greedy meshing path calculating `chunkWorldOffset` AFTER the per-voxel path tried to use it. The code was structured like:

```glsl
void main() {
    if (uUseGreedyMeshing == 0u) {
        // Per-voxel path uses chunkWorldOffset
        vec3 voxelPos = chunkWorldOffset + ...;  // ERROR: undefined!
    }
    
    // Greedy meshing path defines chunkWorldOffset (TOO LATE!)
    uint chunkIdx = gl_WorkGroupID.x;
    vec3 chunkWorldOffset = ...;
}
```

**Both paths need `chunkWorldOffset`**, but it was only defined at the end.

---

## Solution

Moved the `chunkWorldOffset` calculation to the **beginning** of `main()` before the conditional branch:

```glsl
void main() {
    // CRITICAL: Calculate chunk world offset FIRST (needed by both paths)
    uint chunkIdx = gl_WorkGroupID.x;
    if (chunkIdx >= uChunkCount) return;
    
    uint actualChunkIdx = chunkIndices[chunkIdx];
    uint chunkX = actualChunkIdx % uWorldChunksXZ;
    uint chunkZ = actualChunkIdx / uWorldChunksXZ;
    vec3 chunkWorldOffset = vec3(float(chunkX * CHUNK_SIDE_SIZE), 0.0, float(chunkZ * CHUNK_SIDE_SIZE));
    
    // Branch based on mode
    if (uUseGreedyMeshing == 0u) {
        // Per-voxel path - can now use chunkWorldOffset!
        vec3 voxelPos = chunkWorldOffset + vec3(float(lx), float(ly), float(lz));
        // ...
    }
    else {
        // Greedy meshing path - also uses chunkWorldOffset
        vec3 quadWorldPos = chunkWorldOffset + vec3(startPos);
        // ...
    }
}
```

---

## Files Modified

- ✅ `src/spyro-game/Shaders/compute-compact.comp` - Restructured main() function

---

## Verification

**Build Status**: ✅ **SUCCESSFUL**

```bash
Build successful
```

---

## Why This Works

1. **Single calculation**: `chunkWorldOffset` is calculated once at the start
2. **Shared scope**: Both conditional branches can access it
3. **Early exit**: Bounds check happens before expensive calculations
4. **Clean structure**: Clear separation between shared setup and mode-specific logic

---

## Lesson Learned

When refactoring code with multiple paths (per-voxel vs greedy meshing), ensure:
1. **Shared variables** are declared in outer scope before conditionals
2. **Both paths** can access all required variables
3. **Scope boundaries** are clear and well-documented

This is a common pitfall when converting linear code to conditional branches!

---

**Implementation Date**: 2026-01-26  
**Build Status**: ✅ **PASS**  
**Issue**: Missing variable scope  
**Solution**: Hoist shared calculation to outer scope
