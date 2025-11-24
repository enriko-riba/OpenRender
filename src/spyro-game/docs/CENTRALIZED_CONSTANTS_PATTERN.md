# Centralized Constants Pattern

**Date**: 2025-01-16  
**Status**: ✅ **IMPLEMENTED**

## Problem

Hardcoded "magic numbers" scattered throughout the codebase:
- ❌ `maxChunks = 64` in ChunkStreamingManager
- ❌ `const int VERTEX_STRIDE = 36` in Phase3BufferManager
- ❌ Workgroup sizes `(16, 1, 16)` duplicated in C# dispatch calls
- ❌ **No validation** that C# constants match GLSL shader constants

**Result**: 
- Maintenance nightmare - changing chunk size requires hunting down all hardcoded values
- **Runtime errors** if C# and GLSL get out of sync (e.g., buffer overflow if dispatch uses wrong workgroup size)

---

## Solution: VoxelHelper as Single Source of Truth

All voxel-related constants are now centralized in `VoxelHelper.cs`:

### World & Chunk Dimensions
```csharp
public const int ChunkSideSize = 16;         // Chunk width/depth
public const int ChunkYSize = 128;           // Chunk height
public const int WorldChunksXZ = 600;        // World size in chunks
public const int TotalChunks = WorldChunksXZ * WorldChunksXZ; // 360,000 chunks
```

### GPU Pipeline Configuration
```csharp
public const int DEFAULT_MAX_CHUNKS_PER_BATCH = 64;  // Default batch size
public const int COMPUTE_WORKGROUP_X = 16;           // MUST match shader layout
public const int COMPUTE_WORKGROUP_Y = 1;
public const int COMPUTE_WORKGROUP_Z = 16;
public const int VERTEX_STRIDE_BYTES = 36;           // Vertex output format
```

### Shared Index Buffer (Phase 5.1)
```csharp
public static readonly uint[] SHARED_QUAD_INDICES = { 0, 1, 2, 0, 2, 3 };
```

---

## Shader Validation

**NEW**: `VoxelHelper.ValidateShaderConstants()` ensures C# and GLSL are in sync!

```csharp
public static void ValidateShaderConstants()
{
    // Validates that COMPUTE_WORKGROUP_* match shader layout(local_size_x/y/z)
    // Throws InvalidOperationException if mismatch detected
}
```

Called during `ChunkStreamingManager.InitializeGpuGeneration()` to catch errors early.

### Why This Matters

**Before**: If you changed workgroup size in shader but forgot to update C# dispatch:
```csharp
// Shader: layout(local_size_x = 32, ...) 
GL.DispatchCompute(chunks, 1, 1);  // Still using old assumption of 16!
// Result: Wrong number of threads, silent data corruption ❌
```

**After**: Validation catches this at startup:
```
CRITICAL: VoxelHelper workgroup constants (16, 1, 16) do not match expected values (32, 1, 16)!
Compute shaders require layout(local_size_x = 16, local_size_y = 1, local_size_z = 16).
```

---

## Usage Pattern

### ✅ **DO**: Reference VoxelHelper constants
```csharp
// ChunkStreamingManager.cs
private const int MAX_CHUNKS_PER_BATCH = VoxelHelper.DEFAULT_MAX_CHUNKS_PER_BATCH;

// Phase3BufferManager.cs
private const int VERTEX_STRIDE = VoxelHelper.VERTEX_STRIDE_BYTES;

// Dispatch calls
GL.DispatchCompute(chunkCount, 1, 1);  // Workgroup layout handled by shader
```

### ❌ **DON'T**: Hardcode magic numbers
```csharp
var maxChunks = 64;  // ❌ What if we want to change batch size?
const int VERTEX_STRIDE = 36;  // ❌ What if vertex format changes?
```

---

## Files Modified

| File | Change |
|------|--------|
| `VoxelHelper.cs` | Added GPU pipeline constants + validation |
| `ChunkStreamingManager.cs` | Uses `DEFAULT_MAX_CHUNKS_PER_BATCH` |
| `Phase3BufferManager.cs` | Uses `VERTEX_STRIDE_BYTES` |
| `TerrainLoadingScene.cs` | Passes chunk count to `InitializeGpuGeneration()` |

---

## Shader Constants (MUST Match C#)

All compute shaders **MUST** use:
```glsl
layout(local_size_x = 16, local_size_y = 1, local_size_z = 16) in;
```

**Files with this layout**:
- `compute-generate.comp`
- `compute-visibility.comp`
- `compute-compact.comp`

**Validation ensures** these match `VoxelHelper.COMPUTE_WORKGROUP_*` constants.

---

## Benefits

✅ **Single source of truth** - change constants in one place  
✅ **Runtime validation** - catch C#/GLSL mismatches at startup  
✅ **Self-documenting** - constants have clear names and XML docs  
✅ **Type-safe** - compiler catches references to removed constants  
✅ **Easier refactoring** - IDE "Find All References" works  

---

## Future Improvements

### Phase 5.2: Make Constants Configurable
Instead of compile-time constants, allow runtime configuration:
```csharp
public class VoxelConfig
{
    public int ChunkSideSize { get; init; } = 16;
    public int MaxChunksPerBatch { get; init; } = 64;
    // ... etc
}
```

**Benefits**: 
- Support different terrain scales without recompiling
- Allow GPU-specific tuning (smaller batches for low-VRAM GPUs)

**Challenges**: 
- Shaders still need compile-time constants
- Need to generate/select shaders based on config

---

**Author**: GitHub Copilot  
**Pattern**: Single Source of Truth (SSOT)  
**Impact**: Eliminates hardcoded magic numbers, prevents C#/GLSL sync bugs
