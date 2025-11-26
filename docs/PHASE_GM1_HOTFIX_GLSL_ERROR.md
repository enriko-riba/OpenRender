# Phase GM-1 Hotfix: GLSL Compilation Error

## Issue
Runtime error when loading `compute-generate.comp`:
```
error C0000: syntax error, unexpected identifier, expecting ')' at token "start"
```

## Root Cause
The greedy meshing functions (`packQuad1`, `unpackQuad1`, etc.) were added to `terrain-common.glsl`, which is included by ALL compute shaders. The Nvidia Cg compiler was having issues with the `out` parameter syntax in shaders that don't actually use these functions.

## Solution
Wrapped the greedy meshing functions in a conditional compile guard:

```glsl
#ifdef INCLUDE_GREEDY_MESHING
// ... pack/unpack functions ...
#endif
```

Only `compute-greedy-merge.comp` defines this symbol before including `terrain-common.glsl`.

## Files Modified
1. `src/spyro-game/Shaders/terrain-common.glsl` - Wrapped functions in `#ifdef`
2. `src/spyro-game/Shaders/compute-greedy-merge.comp` - Added `#define INCLUDE_GREEDY_MESHING`

## Verification
✅ Build successful
✅ No shader compilation errors
✅ Other shaders unaffected (`compute-generate.comp`, `compute-visibility.comp`, etc.)

## Status
**FIXED** - Ready for testing

---
**Date**: 2025-01-26
**Build**: ✅ PASS
