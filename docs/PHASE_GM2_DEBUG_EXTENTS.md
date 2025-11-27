# Phase GM-2 Debugging: Extent Mapping Issue

## Date: 2026-01-26
## Status: 🔧 DEBUGGING

---

## Problem

Greedy meshing terrain is broken: missing chunks, vertical face stripes, floating panels.

**Root Cause**: Incorrect interpretation of `extentX` and `extentZ` in `getMergedFaceCorners`.

---

## The Core Issue

The greedy merge shader processes **one Y-layer at a time** (16×16 XZ grid at Y=ly).

For each layer, it merges adjacent visible faces in the XZ plane.

### Question: How can vertical faces span HEIGHT if we process one layer at a time?

**Answer**: They CAN'T! Each layer generates quads that are **1 block high**.

But then how does greedy meshing help? We're just merging faces HORIZONTALLY within each layer!

### Current Interpretation (Attempted Fix)

```glsl
// For Y faces (horizontal):
- extentX = width (X direction)
- extentZ = depth (Z direction)

// For X faces (vertical, perpendicular to X):
- extentX = depth (Z direction)
- extentZ = height (Y direction) ← WRONG! Can't span Y in single layer!

// For Z faces (vertical, perpendicular to Z):
- extentX = width (X direction)
- extentZ = height (Y direction) ← WRONG! Can't span Y in single layer!
```

**This interpretation is INCORRECT** because vertical quads can't span multiple Y-layers when processing one layer at a time!

---

## Correct Interpretation (Hypothesis)

Since each layer processes independently, vertical quads must be **1 block high** (the layer).

The extents tell us how the quad spans in the **XZ plane** (horizontal dimensions).

### For +X Face (perpendicular to X, in YZ plane):
- Face is at X=startX+1 (on +X side of voxel)
- Quad is 1 block wide (X direction = constant)
- Quad spans **Z direction** (one horizontal dimension)
- Quad is **1 block high** (Y=ly to Y=ly+1, the layer)
- **extentX** = number of blocks in Z direction (depth on the face)
- **extentZ** = ??? (should be 1, or maybe unused?)

### For +Z Face (perpendicular to Z, in XY plane):
- Face is at Z=startZ+1 (on +Z side of voxel)
- Quad is 1 block deep (Z direction = constant)
- Quad spans **X direction** (one horizontal dimension)
- Quad is **1 block high** (Y=ly to Y=ly+1, the layer)
- **extentX** = number of blocks in X direction (width on the face)
- **extentZ** = ??? (should be 1, or maybe unused?)

### For +Y Face (perpendicular to Y, in XZ plane):
- Face is at Y=ly+1 (on +Y side of voxel)
- Quad spans **X direction** (width)
- Quad spans **Z direction** (depth)
- **extentX** = number of blocks in X direction
- **extentZ** = number of blocks in Z direction

---

## The Real Question

If vertical quads are only 1 block high, **what's the point of greedy meshing?**

**Answer**: Horizontal merging still reduces vertex count!

Example: A 16×16 flat surface with per-voxel:
- **256 faces** (one per voxel)
- **1024 vertices** (4 per face)

With greedy meshing (single-layer):
- **1 merged quad** (16×16)
- **4 vertices** total
- **256× reduction!**

Even for vertical faces, merging horizontally helps:
- A 16-block-wide wall at Y=64:
  - Per-voxel: 16 faces = 64 vertices
  - Merged: 1 quad = 4 vertices
  - **16× reduction!**

---

## What About Multi-Layer Merging?

To merge vertical quads across Y-layers, we'd need a **second pass**:

1. **Phase GM-1**: Merge within each Y-layer (current implementation)
2. **Phase GM-1.5** (future): Merge quads across Y-layers (vertical stacking)

Example:
- Layer Y=64 generates quad at (0,64,5) size (16×1) for +Z face
- Layer Y=65 generates quad at (0,65,5) size (16×1) for +Z face
- **GM-1.5** merges them into quad at (0,64,5) size (16×2)

This would require:
- Reading quads from all layers
- Finding vertically stackable quads (same X/Z position and extent)
- Merging them into taller quads

**Complexity**: Significant! Would need another compute pass.

**Benefit**: Further vertex reduction for vertical surfaces.

---

## Current Fix Attempt

```glsl
void getMergedFaceCorners(uint face, vec3 pos, uint extentX, uint extentZ, out vec3 corners[4]) {
    float ex = float(extentX);
    float ez = float(extentZ);
    
    if (face == FACE_POS_Y || face == FACE_NEG_Y) {
        // Horizontal faces: extentX=width, extentZ=depth
        // (correct as-is)
    }
    else if (face == FACE_POS_X || face == FACE_NEG_X) {
        // Vertical X faces: 1 block wide (X), extentX blocks deep (Z), 1 block high (Y)
        // ??? How to interpret extents?
    }
    else { // FACE_POS_Z or FACE_NEG_Z
        // Vertical Z faces: extentX blocks wide (X), 1 block deep (Z), 1 block high (Y)
        // ??? How to interpret extents?
    }
}
```

**Current Guess**: For vertical faces, one extent is horizontal span, the other is ignored (or =1).

---

## Debugging Strategy

1. **Log quad data** from greedy merge:
   - startX, startY, startZ, extentX, extentZ, face
   - Check if extentZ is always 1 for vertical faces

2. **Visualize quads**:
   - Render each quad as a different color
   - Check if they're positioned correctly

3. **Compare with per-voxel**:
   - Toggle F4 ON/OFF
   - Verify same faces are rendered (just merged)

4. **Check greedy merge algorithm**:
   - For X faces: does it expand along X or Z?
   - For Z faces: does it expand along X or Z?

---

## Expected Quad Dimensions

### Horizontal Faces (Y)
- Width: 1-16 blocks (X direction)
- Depth: 1-16 blocks (Z direction)
- Height: 0 (flat)

### Vertical Faces (X/Z) - Single Layer
- Width OR Depth: 1-16 blocks (one horizontal dimension)
- Height: **1 block** (the layer)
- Other dimension: 0 (perpendicular to face)

### Observed Behavior (Broken)
- "stripes of vertical faces" → quads too thin?
- "single side panels high in the air" → wrong position?
- "missing chunks" → quads not emitted?

---

## Next Steps

1. **Add debug visualization** to see quad extents
2. **Read greedy merge output** to verify extent values
3. **Fix `getMergedFaceCorners`** based on actual data
4. **Consider future GM-1.5** for vertical merging across layers

---

**Status**: Build compiles, but rendering is broken  
**Blocker**: Incorrect extent interpretation for vertical faces  
**Need**: Empirical data from greedy merge shader output
