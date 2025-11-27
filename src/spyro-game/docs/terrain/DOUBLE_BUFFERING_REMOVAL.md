# Double-Buffering Removal

## Background

The original design used double-buffering to allow 2 batches to process in parallel:
- Batch N uses `voxelDataBuffers[0]` 
- Batch N+1 uses `voxelDataBuffers[1]`

This was intended to improve throughput by overlapping generation with visibility passes.

## Why It Was Removed

### The Problem

The visibility shader performs **neighbor lookups** across chunks to determine face visibility. The lookup process:

1. Find neighbor chunk's **global index** in the world
2. Search for that index in the `chunkIndices` buffer
3. If found, read voxel data from `voxelData` buffer

**The bug:** Steps 2 and 3 used **different buffers**!

```glsl
// In compute-visibility.comp (simplified)
for (uint i = 0; i < uChunkCount; i++) {
    if (chunkIndices[i] == neighborGlobalIdx) {  // Search in current buffer
        // Found it! Now read voxel data...
        uint voxelIdx = ...;
        uint voxel = voxelData[voxelIdx];  // ❌ READS FROM CURRENT BUFFER!
                                           // But neighbor might be in OTHER buffer!
        break;
    }
}
```

**Example failure:**
```
Batch 1 (chunks 0-63):  bufferIndex=0, chunkIndices[0], voxelData[0]
Batch 2 (chunks 64-127): bufferIndex=1, chunkIndices[1], voxelData[1]

Visibility pass for Batch 1:
- Processing Chunk 63
- Needs neighbor Chunk 64 (in Batch 2)
- Finds "64" in chunkIndices[1] ✅
- Reads from voxelData[0] ❌ (wrong buffer!)
- Gets stale/uninitialized data
- Missing faces appear
```

### Attempted Fixes (Failed)

#### Attempt 1: Procedural Fallback
**Idea:** If neighbor not found in buffer, generate it procedurally  
**Result:** ❌ Failed - procedural generation had subtle differences from shader generation

#### Attempt 2: Wait-for-All
**Idea:** Wait for all batches to complete generation before starting visibility  
**Result:** ❌ Failed - still read from wrong buffer even when data was ready

#### Attempt 3: Per-Chunk Buffer Tracking
**Idea:** Track which buffer each chunk uses, pass to shader  
**Result:** ❌ Too complex, requires major shader changes

### The Solution: Remove Double-Buffering

**Implementation:**
```csharp
// Always use buffer 0
const int bufferIndex = 0;

// Only 1 batch in-flight at a time
if (totalBatchesInPipeline >= 1) return;
```

**Why it works:**
- All chunks are in the same buffer (0)
- Neighbor lookups always read correct data
- Sequential processing prevents buffer overwrites

## Performance Impact

### Before (Broken)
- 2 batches in parallel
- ~5-6 seconds for 1089 chunks
- **Missing faces, broken chunks**

### After (Fixed)
- 1 batch at a time
- ~6-7 seconds for 1089 chunks
- **Perfect terrain, every time**

**Trade-off:** +15-20% load time for 100% correctness

## Why Not Fix Double-Buffering Instead?

### Option 1: Buffer Index Tracking
**Requires:**
- Add `bufferIndex` field to ChunkDescriptor
- Pass buffer indices to shader via SSBO
- Modify all shaders to use dynamic buffer indexing:
  ```glsl
  uint neighborBuffer = getChunkBuffer(neighborIdx);
  uint voxel = voxelData[neighborBuffer][voxelIdx];
  ```

**Problems:**
- Complex shader changes
- Extra memory (1 int per chunk)
- Dynamic indexing may hurt performance
- More places for bugs

### Option 2: Smart Batching
**Requires:**
- Detect spatial adjacency
- Group neighbors into same batch
- Handle uneven batch sizes

**Problems:**
- Complex batching logic
- May not always work (diagonal neighbors, etc.)
- Unpredictable batch sizes
- Still needs buffer tracking for edge cases

### Option 3: Unified Buffer Pool
**Requires:**
- Single large buffer for all chunks
- Dynamic offset tracking per chunk
- Upload to specific offsets

**Problems:**
- Larger memory footprint
- Complex offset management
- No real benefit over single-buffer approach

## Current Design: Single Buffer, Sequential

**Advantages:**
- ✅ Simple and understandable
- ✅ No buffer confusion
- ✅ Guaranteed correctness
- ✅ Easy to debug
- ✅ Minimal code changes

**Disadvantages:**
- ⚠️ Can't overlap generation with visibility
- ⚠️ Slower initial load (~1 second)

**Verdict:** The simplicity and correctness gains far outweigh the minor performance cost.

## Code Changes Summary

### Removed
- `nextBufferIndex` field (was: toggles 0/1)
- Buffer index toggle logic
- Double-buffering comments/infrastructure

### Modified
- `SubmitPendingBatches()`: Always uses `bufferIndex = 0`
- Pipeline limit: `MAX_IN_FLIGHT_BATCHES` → 1 (effectively)

### Added
- Documentation explaining the removal
- Comments pointing to bug fix document

## Future Considerations

If performance becomes critical:

1. **Profile first** - is initial load time actually a problem?
2. **Consider spatial batching** - group neighbors together
3. **Optimize other areas** - shader improvements, better culling, etc.
4. **If still needed** - implement proper buffer tracking with extensive testing

But for now, **simple and correct wins**.

## References

- Main bug fix document: `docs/terrain/BUG_FIX_MISSING_FACES.md`
- Streaming manager: `World/ChunkStreamingManager.cs`
- Visibility shader: `Shaders/compute-visibility.comp`
- Initial issue: Appeared after CHUNK_Y_SIZE upgrade 128 → 384

## Testing

When modifying chunk streaming, always verify:
- No missing faces at chunk boundaries
- Consistent terrain between runs
- Chunk reload doesn't "fix" issues
- Test with various CHUNK_Y_SIZE values

The single-buffer approach makes these guarantees much easier to maintain.
