# AO Chunk Boundary Fix

## Problem
Blocks at chunk boundaries (local position 15) appear lighter than interior blocks because AO calculations don't properly sample neighboring chunks.

## Root Cause
In `compute-compact.comp`, the `calculateAO()` function computes AO for face corners by sampling 3 neighboring blocks. However, when a face corner is exactly on a chunk boundary:

1. The corner world position might be at 16.0 (next chunk's position 0)
2. But we pass the block's local position `ivec3(lx, ly, lz)` to `isSolid()`
3. When `isSolid()` adds the AO offsets, it samples from wrong positions

### Example:
- Block at local position (15, 50, 10)
- Top face, corner at world position (16.0, 51.0, 10.0) 
- AO needs to check offset (-1, 1, 0) → position (15.0, 51.0, 10.0)
- But we're passing local (15, 50, 10) + offset → checks (14, 51, 10) ❌

## Solution
Change line 212 in `compute-compact.comp`:

### Before:
```glsl
float ao=calculateAO(face,v,chunkIdx,chunkWorldOffset,ivec3(lx,ly,lz));
```

### After:
```glsl
// Use the corner's actual position for AO sampling
ivec3 cornerLocalPos = ivec3(round(localPosFloat));
float ao=calculateAO(face,v,chunkIdx,chunkWorldOffset,cornerLocalPos);
```

This ensures AO samples are taken relative to the corner's actual position, correctly handling chunk boundaries when corners are at position 16 (next chunk's 0).

## Impact
- Fixes lighting discontinuities at chunk boundaries
- No performance impact (just uses already-calculated variable)
- Maintains correct AO for interior blocks
