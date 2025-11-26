# Shader Cache Issue - Line 750 Error

## Issue

Build error references line 750 in `compute-greedy-merge.comp`, but the file is only ~340 lines. This indicates the OpenGL/Nvidia driver is reading a **cached version** of the shader files.

## Root Cause

The Nvidia shader compiler caches compiled shaders. When we removed the `unpackQuad1/unpackQuad2` functions from `terrain-greedy-meshing.glsl`, the cache wasn't invalidated, so the driver is still trying to compile the OLD version with those functions.

## Solution

### Option 1: Clear Shader Cache (Recommended)

**Windows**:
```
Delete: C:\Users\<YourName>\AppData\Local\NVIDIA\GLCache
```

**Steps**:
1. Close Visual Studio
2. Navigate to `%LOCALAPPDATA%\NVIDIA\GLCache`
3. Delete all files in that folder
4. Restart Visual Studio
5. Rebuild

### Option 2: Restart Computer

A full restart will clear the shader cache automatically.

### Option 3: Add Version Comment

Force shader recompilation by changing version comment:

```glsl
// compute-greedy-merge.comp - Phase GM-1: Greedy Meshing
// Version: 1.1 (Force recompile)
```

### Option 4: Touch the File

Update the file's timestamp to force recompilation:

**PowerShell**:
```powershell
(Get-Item "src/spyro-game/Shaders/terrain-greedy-meshing.glsl").LastWriteTime = Get-Date
```

## Verification

After clearing cache, you should see:
- Build successful
- No line 750 error
- Shader compiles with only pack functions (no unpack)

## Prevention

To avoid this in future:

1. **Clear cache between major shader refactors**
2. **Change version numbers** in shader comments
3. **Use `#pragma once`** alternative (add timestamp comment)
4. **Rebuild clean** after modular refactoring

---

**Date**: 2026-01-26  
**Status**: Shader cache invalidation needed
