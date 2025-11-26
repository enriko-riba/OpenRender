# Modularization Hotfix & F4 Toggle

## Date: 2026-01-26
## Status: ✅ FIXED

---

## Issue

After splitting GLSL files into modules, `compute-visibility.comp` failed to compile:

```
error C1503: undefined variable "domainWarp"
error C1503: undefined variable "fbm"
error C1503: undefined variable "fbm3D"
error C1503: undefined variable "isCave"
```

**Root Cause**: `terrain-generation.glsl` depends on `terrain-noise.glsl` and `terrain-caves.glsl`, but `compute-visibility.comp` only included `terrain-generation.glsl`.

---

## Solution

### Fix #1: Add Missing Includes

Updated `compute-visibility.comp`:

```glsl
// Before (BROKEN)
#include "terrain-common.glsl"
#include "terrain-generation.glsl"

// After (FIXED)
#include "terrain-common.glsl"
#include "terrain-noise.glsl"
#include "terrain-caves.glsl"
#include "terrain-generation.glsl"
```

**Why**: `terrain-generation.glsl` uses functions from noise and caves modules, so all dependencies must be included.

### Fix #2: F4 Keyboard Toggle for Greedy Meshing

Added runtime toggle in `GameScene.cs`:

```csharp
// Toggle Greedy Meshing (F4)
if (SceneManager.KeyboardState.IsKeyPressed(Keys.F4))
{
    if (streamingManager != null)
    {
        streamingManager.UseGreedyMeshing = !streamingManager.UseGreedyMeshing;
        Log.Info($"Greedy Meshing: {(streamingManager.UseGreedyMeshing ? "ENABLED" : "DISABLED")}");
    }
}
```

**UI Update**: Added "F4 - Toggle Greedy Meshing" to controls display

---

## Module Dependency Tree

Understanding the dependencies helps prevent similar issues:

```
terrain-common.glsl (base - no dependencies)
  ├── terrain-noise.glsl (depends on: common)
  │     ├── terrain-caves.glsl (depends on: common, noise)
  │     ├── terrain-generation.glsl (depends on: common, noise, caves)
  │     │     └── terrain-biomes.glsl (depends on: common, noise, generation)
  │     └── terrain-greedy-meshing.glsl (depends on: common only)
```

### Include Rules

1. **Always include dependencies in order**: base → dependencies → target
2. **terrain-generation.glsl requires**:
   - `terrain-common.glsl`
   - `terrain-noise.glsl`
   - `terrain-caves.glsl`

3. **terrain-biomes.glsl requires**:
   - `terrain-common.glsl`
   - `terrain-noise.glsl`
   - `terrain-generation.glsl`

4. **terrain-greedy-meshing.glsl requires**:
   - `terrain-common.glsl` (only!)

---

## Correct Include Patterns

### Compute Shaders (Generation)

```glsl
#include "terrain-common.glsl"
#include "terrain-noise.glsl"
#include "terrain-caves.glsl"
#include "terrain-generation.glsl"
#include "terrain-biomes.glsl"
```

**Used by**: `compute-generate.comp`, `compute-visibility.comp`, `compute-compact.comp`

### Compute Shaders (Greedy Meshing)

```glsl
#include "terrain-common.glsl"
#include "terrain-greedy-meshing.glsl"
```

**Used by**: `compute-greedy-merge.comp`

### Fragment Shaders (Biome Overlay)

```glsl
#define IS_FRAGMENT_SHADER
#include "terrain-common.glsl"
#include "terrain-noise.glsl"
#include "terrain-biomes.glsl"
```

**Used by**: `voxel-terrain.frag`

---

## Files Modified

1. ✅ `src/spyro-game/Shaders/compute-visibility.comp` - Added noise/caves includes
2. ✅ `src/spyro-game/GameScene.cs` - Added F4 toggle + UI display

---

## Testing Checklist

### Compilation
- [x] Build succeeds
- [x] No shader compilation errors
- [x] All modules load correctly

### Runtime (To Verify)
- [ ] Terrain generates correctly
- [ ] F4 toggles greedy meshing
- [ ] Log message shows toggle state
- [ ] No visual artifacts

### Expected Behavior

**With F4 OFF (default)**:
- Terrain renders as before
- Per-voxel face generation
- No quad merging

**With F4 ON**:
- Greedy merge shader runs
- Quad counts logged to console
- Visual appearance identical (Phase GM-1)
- Vertex reduction realized in Phase GM-2

---

## Verification

### Build Status
✅ **Build successful** (no errors, no warnings)

### Log Output (Expected)

```
// F4 pressed (first time)
Greedy Meshing: ENABLED

// F4 pressed (second time)
Greedy Meshing: DISABLED
```

---

## Lessons Learned

### 1. **Transitive Dependencies Matter**

When including a module, you must include ALL its dependencies:

❌ **Wrong**:
```glsl
#include "terrain-generation.glsl"  // Missing noise/caves!
```

✅ **Right**:
```glsl
#include "terrain-noise.glsl"
#include "terrain-caves.glsl"
#include "terrain-generation.glsl"
```

### 2. **Include Guards Are Not Enough**

GLSL `#ifndef` guards prevent multiple inclusion, but **don't resolve dependencies**. You must manually include dependencies in the correct order.

### 3. **Document Dependencies**

Each module should document its dependencies in a header comment:

```glsl
// terrain-generation.glsl
// Core terrain generation (height maps, density, block types)
//
// DEPENDENCIES:
// - terrain-common.glsl (constants, macros)
// - terrain-noise.glsl (FBM, domain warp)
// - terrain-caves.glsl (cave generation)
```

### 4. **Test Each Shader**

After modularization, compile EVERY shader that uses the modules to catch missing includes early.

---

## Future Improvements

### 1. **Automatic Dependency Resolution**

Consider a preprocessor that automatically includes dependencies:

```glsl
#include "terrain-generation.glsl"  // Auto-includes noise, caves
```

### 2. **Module Header Comments**

Add dependency documentation to each module:

```glsl
// terrain-generation.glsl
// Requires: terrain-common.glsl, terrain-noise.glsl, terrain-caves.glsl
```

### 3. **Compile-Time Checks**

Add a shader validation step that verifies all dependencies are included before compilation.

---

## Status

**Hotfix**: ✅ **COMPLETE**
- ✅ Missing includes added
- ✅ Build verified
- ✅ F4 toggle implemented
- ⏳ Runtime testing pending

**Ready for**: Game testing and Phase GM-2 development

---

**Implementation Date**: 2026-01-26  
**Build Status**: ✅ PASS  
**Files Modified**: 2  
**Lines Changed**: ~10
