# Phase 5: Terrain Streaming & Edit Support - Design Document

**Status**: 🟡 In Progress  
**Branch**: `feature/terrain-streaming`  
**Last Updated**: 2025-01-15

---

## Overview

Phase 5 completes the terrain streaming system by adding:
1. **Incremental buffer updates** - Update only changed chunks without full regeneration
2. **Buffer reuse optimization** - Recycle freed buffer regions instead of reallocating
3. **Edit mask persistence** - Save/load player edits across sessions
4. **Priority queue system** - Load closer chunks first
5. **Smooth streaming transitions** - Avoid visible pop-in/pop-out

---

## Architecture

### Current State (Phase 4 Complete)

```
┌──────────────────┐
│ VoxelWorld      │  Camera position tracking
└────┬─────────────┘
     │
┌────▼──────────────────┐
│ ChunkStreamingManager │  Load/unload orchestration
├───────────────────────┤
│ • DetermineVisibleChunks()    │
│ • QueueNewChunks()            │
│ • UnloadDistantChunks() ✅    │
│ • ApplyBlockEdit() ✅         │
└────┬──────────────────┘
     │
┌────▼──────────────────┐
│ Phase3BufferManager   │  GPU buffer management
├───────────────────────┤
│ • VertexBuffer        │
│ • SharedIndexBuffer   │
│ • AtomicCounters      │
└───────────────────────┘
```

### Terrain Streaming Data Flow (CPU-first pipeline)

```
Camera / Player Motion
    │      world-space focus (Vec3)
    ▼
ChunkStreamingManager.Update()
    │  chunk indices + priority budget
    ├────► DetermineVisibleChunks / QueueNewChunks
    │          │   populate high/low priority queues
    │          ▼
    │    ChunkGenerationJobSystem
    │          │  CpuTerrainGenerator fills spans + voxels
    │          ├────► ChunkVoxelDataCache (versioned buffers)
    │          └────► CollisionManager & Height Cache upload
    │
    ├────► ChunkMeshingJobSystem
    │          │  ChunkMeshBuilder pulls cached voxels
    │          ▼
    │    CpuChunkMesh results (face counts, vertex/index data, placeholder masks)
    │          │
    │          ▼
    ├────► Phase3BufferManager (Allocate/Free vertex, index, command slots)
    │          │
    │          ▼
    ├────► VoxelTerrainRenderer + Frustum Culling buffers
    │          │  ready chunk descriptors + visibility flags
    │          ▼
    └────► GameScene HUD / TerrainLoadingScene progress (GetStats / GetStreamingProgress)
```

#### Component responsibilities & data contracts

| Component | Produces | Consumes | Notes/Key Interactions |
| --- | --- | --- | --- |
| `ChunkStreamingManager` (`World/ChunkStreamingManager.cs`) | Chunk life-cycle state, placeholder masks, chunk stats, GPU upload instructions | Camera pose, `VoxelWorld` container, job system completions, frustum feedback | `SubmitPendingBatches()` gates priority queues; `PollCompletedBatches()` hands results to meshing and height cache; `UnloadChunk()` also frees GPU + cache resources to prevent churn. |
| `ChunkGenerationJobSystem` + `CpuTerrainGenerator` | Column spans (`SpanPairs`, `SpanCounts`, `SpanTypes`), voxel arrays (via `ChunkVoxelDataCache`), collision payloads | Chunk indices + edit descriptors from streaming manager | Writes into pooled buffers and immediately stores them in `ChunkVoxelDataCache` with monotonically increasing cache versions used later by meshing to detect stale data. |
| `ChunkVoxelDataCache` (`World/ChunkVoxelDataCache.cs`) | Versioned voxel buffers exposed as read-only spans; cache statistics | Generation workers rent/write buffers; meshing workers read them | Acts as the synchronization point between CPU generation threads and CPU meshing threads, ensuring placeholder-aware meshes can be built without GPU readbacks. |
| `ChunkMeshingJobSystem` + `ChunkMeshBuilder` | `CpuChunkMesh` records (vertex/index arrays, visible face counts, placeholder mask echo, cache version) | Chunk indices scheduled by streaming manager, voxel cache views | Emits CPU-ready mesh payloads that `ProcessCpuMeshResults()` uploads into Phase 3 buffers; also signals seam refresh triggers when placeholder dependencies resolve. |
| `Phase3BufferManager` (`World/Phase3BufferManager.cs`) | GPU buffer offsets, compacted vertex/index/indirect command regions, allocation stats | CPU mesh uploads, unload notifications, dirty chunk frees | `TryUploadCpuMesh()` allocates/frees regions per chunk; `FreeVertexRegion`/`FreeIndexRegion` make unloaded space immediately available, keeping memory stable while exploring. |
| `VoxelTerrainRenderer` + frustum culling shaders | Draw commands bound to VAO, per-chunk visibility flags, renderer stats | Phase 3 buffers, camera matrices, chunk descriptors | Consumes `WriteIndirectCommands()` output and frustum flags to render ready chunks; exposes stats to HUD along with `VisibleChunkCount`/`CulledChunkCount`. |
| Height cache subsystem (`EnsureHeightCacheSlot`, `UploadHeightCacheFromSpans`) | Packed 10-bit column heights + water/edit bits stored in SSBO slots | CPU column span output, `VoxelWorld` chunk data on restore | Shared with GPU sampling to skip recomputing moisture/height lookups; slots are reclaimed when chunks unload via `InvalidateHeightCache()`. |
| `TerrainLoadingScene` / `GameScene` overlays | Player-facing progress text, HUD chunk totals | `ChunkStreamingManager.GetStreamingProgress()` / `.GetStats()` | Loading scene drives staged progress (init → stream) and transitions once `ready >= target`; HUD in `GameScene` mirrors totals so players can see churn vs target radius. |

**Cross-system notes**
- Placeholder masks flow from `ComputePlaceholderMask()` → meshing job → upload, guaranteeing seams stay hidden until neighbors finish. Masks live in `placeholderMasksInFlight` until `TryUploadCpuMesh()` resolves dependencies.
- The same chunk events update multiple consumers: CPU generation completion triggers collision refresh + height cache upload, while CPU meshing completion drives GPU uploads and optional seam refresh scheduling. Documenting this fan-out keeps churn investigations grounded in real data paths.
- Streaming throttles are distance-aware: `CalculatePriority()` sorts candidates, `GetUnloadDistanceChunks()` maintains a guard band so reactive unloading does not outpace generation, and TerrainLoadingScene’s progress uses `VoxelHelper.CalculateCircularChunkCount(GetActiveLoadDistance())` so UX reflects real budgets.

### Issues to Fix

#### 1. **No Incremental Updates** ❌
**Problem**: When a chunk becomes dirty (player edit), we mark it as `Pending` and regenerate it, but we DON'T update the renderer buffers incrementally.

```csharp
// Current code in MarkChunkDirty():
desc.State = TerrainChunkState.Pending;
pendingGeneration.Enqueue(chunkIdx);
// ❌ Missing: Update Phase3BufferManager to replace old mesh data
```

**Impact**: 
- Player edits don't appear until full terrain reload
- Streaming broken - can only load initial terrain set

#### 2. **No Buffer Reuse** ❌
**Problem**: When chunks unload, their buffer regions are freed but not tracked for reuse.

```csharp
// Current code in UnloadChunk():
activeChunks.Remove(chunkIndex);
// ❌ Missing: Track freed vertex/index buffer regions
// ❌ Missing: Reuse freed regions for new chunks
```

**Impact**:
- Memory fragmentation over time
- Buffer grows indefinitely as player explores

#### 3. **No Edit Persistence** ❌
**Problem**: Player edits are not saved to disk.

```csharp
// Current code in MarkVoxelEdited():
Log.Debug($"Voxel edit: chunk={chunkIdx} voxel={voxelIdx}");
// ❌ Missing: Write edit mask to GPU buffer
// ❌ Missing: Save/load edit masks from disk
```

**Impact**:
- Player changes lost on restart
- Can't modify terrain permanently

---

## Phase 5 Tasks

### 5.1: Incremental Buffer Updates ⬜

**Goal**: Update renderer buffers when chunks change without full regeneration.

#### 5.1.1: Add Incremental Update Methods
Create methods to update specific buffer regions:

```csharp
// Phase3BufferManager.cs
public class Phase3BufferManager
{
    // ... existing code ...

    /// <summary>
    /// Update a single chunk's mesh data in the compacted vertex buffer
    /// </summary>
    public void UpdateChunkMesh(int chunkIndex, uint baseOffset, uint vertexCount, 
                                ReadOnlySpan<CompactedVertex> vertices)
    {
        // 1. Validate offset and count
        if (baseOffset + vertexCount > VertexBufferCapacity)
            throw new ArgumentException("Buffer overflow");

        // 2. Upload new vertex data to GPU
        var byteOffset = baseOffset * VoxelHelper.VERTEX_STRIDE_BYTES;
        var byteSize = vertexCount * VoxelHelper.VERTEX_STRIDE_BYTES;
        
        GL.NamedBufferSubData(VertexBuffer, (IntPtr)byteOffset, (int)byteSize, vertices);
        
        Log.Debug($"Updated chunk {chunkIndex} mesh: {vertexCount} vertices at offset {baseOffset}");
    }
    
    /// <summary>
    /// Mark a buffer region as free for reuse (Phase 5.2)
    /// </summary>
    public void FreeRegion(uint baseOffset, uint vertexCount)
    {
        // Add to free list for reuse
        freeRegions.Add(new BufferRegion { Offset = baseOffset, Size = vertexCount });
        freeRegions = freeRegions.OrderBy(r => r.Offset).ToList();
        
        // Try to merge adjacent free regions
        MergeFreeRegions();
    }
}
```

#### 5.1.2: Update PollCompletedBatches() to Handle Updates

```csharp
// ChunkStreamingManager.cs
private void PollCompletedBatches()
{
    while (inFlightBatches.Count > 0)
    {
        var batch = inFlightBatches.Peek();
        
        // Check fence...
        
        // ✅ NEW: Incremental update instead of full reload
        foreach (var chunkIdx in batch.ChunkIndices)
        {
            if (activeChunks.TryGetValue(chunkIdx, out var desc))
            {
                // Update the chunk's mesh data incrementally
                if (desc.AtlasOffset >= 0 && desc.VisibleVoxelCount > 0)
                {
                    // Read generated vertex data from Phase 3 output
                    var vertices = phase3Buffers.ReadVertices(desc.AtlasOffset, desc.VisibleVoxelCount * 4);
                    
                    // Update existing buffer region
                    phase3Buffers.UpdateChunkMesh(chunkIdx, desc.AtlasOffset, desc.VisibleVoxelCount * 4, vertices);
                }
                
                desc.State = TerrainChunkState.Ready;
                activeChunks[chunkIdx] = desc;
            }
        }
    }
}
```

**Acceptance Criteria**:
- [ ] Player edits visible immediately (< 16ms)
- [ ] No full terrain regeneration required
- [ ] Streaming works while player moves

---

### 5.2: Buffer Reuse Optimization ⬜

**Goal**: Recycle freed buffer regions to avoid fragmentation.

#### 5.2.1: Add Free Region Tracker

```csharp
// Phase3BufferManager.cs
private struct BufferRegion
{
    public uint Offset;
    public uint Size;
}

private List<BufferRegion> freeRegions = new();

/// <summary>
/// Allocate a region from the buffer, reusing free space if available
/// </summary>
public uint AllocateRegion(uint requestedSize)
{
    // Try to find a free region that fits
    for (var i = 0; i < freeRegions.Count; i++)
    {
        var region = freeRegions[i];
        if (region.Size >= requestedSize)
        {
            // Use this region
            var offset = region.Offset;
            
            // Update free region (shrink or remove)
            if (region.Size == requestedSize)
            {
                freeRegions.RemoveAt(i);
            }
            else
            {
                freeRegions[i] = new BufferRegion
                {
                    Offset = region.Offset + requestedSize,
                    Size = region.Size - requestedSize
                };
            }
            
            Log.Debug($"Reused buffer region: offset={offset}, size={requestedSize}");
            return offset;
        }
    }
    
    // No free region found - allocate at end
    var newOffset = currentBufferEnd;
    currentBufferEnd += requestedSize;
    
    // Check if we need to resize
    if (currentBufferEnd > VertexBufferCapacity)
    {
        ResizeVertexBuffer(currentBufferEnd * 2);
    }
    
    return newOffset;
}
```

#### 5.2.2: Update UnloadChunk() to Track Freed Regions

```csharp
// ChunkStreamingManager.cs
private void UnloadChunk(int chunkIndex)
{
    if (!activeChunks.TryGetValue(chunkIndex, out var desc))
        return;
    
    // ✅ NEW: Free buffer region for reuse
    if (desc.AtlasOffset >= 0 && desc.VisibleVoxelCount > 0)
    {
        phase3Buffers?.FreeRegion((uint)desc.AtlasOffset, (uint)(desc.VisibleVoxelCount * 4));
    }
    
    // Cleanup fence...
    activeChunks.Remove(chunkIndex);
    
    Log.Debug($"Unloaded chunk {chunkIndex}, freed {desc.VisibleVoxelCount * 4} vertices");
}
```

**Acceptance Criteria**:
- [ ] Freed buffer regions reused for new chunks
- [ ] Buffer size stays stable during exploration
- [ ] No memory fragmentation over time

---

### 5.3: Edit Mask Persistence ⬜

**Goal**: Save and load player edits across sessions.

#### 5.3.1: Create EditMaskManager

```csharp
// World/EditMaskManager.cs
public class EditMaskManager
{
    private readonly Dictionary<int, EditMask> editMasks = new();
    private readonly string saveDirectory;
    
    public EditMaskManager(string saveDirectory)
    {
        this.saveDirectory = saveDirectory;
        Directory.CreateDirectory(saveDirectory);
    }
    
    /// <summary>
    /// Mark a voxel as edited
    /// </summary>
    public void SetVoxel(int chunkIndex, int voxelIndex, BlockType newType, bool isBreaking)
    {
        if (!editMasks.TryGetValue(chunkIndex, out var mask))
        {
            mask = new EditMask();
            editMasks[chunkIndex] = mask;
        }
        
        mask.SetVoxel(voxelIndex, newType, isBreaking);
    }
    
    /// <summary>
    /// Save all edit masks to disk
    /// </summary>
    public void SaveAll(int worldSeed)
    {
        foreach (var kvp in editMasks)
        {
            var chunkIndex = kvp.Key;
            var mask = kvp.Value;
            
            var filePath = Path.Combine(saveDirectory, $"edit_{worldSeed}_{chunkIndex}.bin");
            mask.SaveToFile(filePath);
        }
        
        Log.Info($"Saved {editMasks.Count} edit masks");
    }
    
    /// <summary>
    /// Load edit masks for a chunk
    /// </summary>
    public EditMask? LoadChunkMask(int worldSeed, int chunkIndex)
    {
        var filePath = Path.Combine(saveDirectory, $"edit_{worldSeed}_{chunkIndex}.bin");
        if (!File.Exists(filePath))
            return null;
        
        return EditMask.LoadFromFile(filePath);
    }
}
```

#### 5.3.2: Create EditMask Structure

```csharp
// World/EditMask.cs
public struct EditMask
{
    // 3D bitset: 1 bit per voxel (32×32×128 = 131,072 bits = 16,384 bytes)
    private byte[] hiddenMask;  // 1 = voxel removed
    private byte[] modifiedMask; // 1 = voxel type changed
    private BlockType[] typeOverrides; // Sparse array of type overrides
    
    public void SetVoxel(int voxelIndex, BlockType newType, bool isBreaking)
    {
        // Set appropriate mask bit
        var byteIndex = voxelIndex / 8;
        var bitIndex = voxelIndex % 8;
        
        if (isBreaking)
        {
            hiddenMask[byteIndex] |= (byte)(1 << bitIndex);
        }
        else
        {
            modifiedMask[byteIndex] |= (byte)(1 << bitIndex);
            typeOverrides[voxelIndex] = newType;
        }
    }
    
    public void SaveToFile(string filePath) { /* Binary serialization */ }
    public static EditMask LoadFromFile(string filePath) { /* Binary deserialization */ }
}
```

**Acceptance Criteria**:
- [ ] Player edits saved to disk automatically
- [ ] Edits loaded when chunks regenerate
- [ ] No data loss on game restart

---

### 5.4: Priority Queue System ⬜

**Goal**: Load closest chunks first for better player experience.

#### 5.4.1: Update QueueNewChunks() to Use Priority

```csharp
// ChunkStreamingManager.cs - ALREADY IMPLEMENTED! ✅
private void QueueNewChunks(HashSet<int> visibleChunks)
{
    var newChunks = visibleChunks.Except(activeChunks.Keys).ToList();
    if (newChunks.Count == 0)
        return;
    
    // ✅ Already sorts by priority (distance to camera)
    newChunks.Sort((a, b) => CalculatePriority(a).CompareTo(CalculatePriority(b)));
    
    foreach (var chunkIdx in newChunks)
    {
        // ... queue chunks ...
    }
}
```

**Status**: ✅ Already implemented correctly!

---

### 5.5: Smooth Streaming Transitions ⬜

**Goal**: Avoid visible pop-in/pop-out when chunks load/unload.

#### 5.5.1: Add Fade-in Effect

```csharp
// VoxelTerrainRenderer.cs
public class VoxelTerrainRenderer
{
    // ... existing code ...
    
    private Dictionary<int, float> chunkFadeTimers = new();
    private const float FADE_IN_DURATION = 0.3f; // 300ms
    
    /// <summary>
    /// Register a new chunk for fade-in
    /// </summary>
    public void RegisterNewChunk(int chunkIndex)
    {
        chunkFadeTimers[chunkIndex] = 0f;
    }
    
    /// <summary>
    /// Update fade timers (call once per frame)
    /// </summary>
    public void UpdateFadeTimers(float deltaTime)
    {
        var keysToRemove = new List<int>();
        
        foreach (var kvp in chunkFadeTimers)
        {
            var chunkIndex = kvp.Key;
            var timer = kvp.Value + deltaTime;
            
            if (timer >= FADE_IN_DURATION)
            {
                keysToRemove.Add(chunkIndex);
            }
            else
            {
                chunkFadeTimers[chunkIndex] = timer;
            }
        }
        
        foreach (var key in keysToRemove)
        {
            chunkFadeTimers.Remove(key);
        }
    }
    
    /// <summary>
    /// Get fade alpha for a chunk (0.0 = invisible, 1.0 = fully visible)
    /// </summary>
    public float GetChunkFadeAlpha(int chunkIndex)
    {
        if (!chunkFadeTimers.TryGetValue(chunkIndex, out var timer))
            return 1.0f; // Fully visible (old chunk)
        
        return Math.Clamp(timer / FADE_IN_DURATION, 0f, 1f);
    }
}
```

#### 5.5.2: Update Vertex Shader for Fading

```glsl
// voxel-terrain.vert
#version 460

// ... existing inputs/outputs ...

// Per-chunk fade alpha (passed via drawData SSBO)
layout(std430, binding = 5) readonly buffer drawData {
    mat4 chunkTransforms[];
    float chunkFadeAlphas[];  // NEW: Alpha per chunk
};

void main() {
    // ... existing position/normal calculation ...
    
    // NEW: Pass fade alpha to fragment shader
    vFadeAlpha = chunkFadeAlphas[gl_DrawID];
}
```

```glsl
// voxel-terrain.frag
#version 460

// ... existing inputs ...
in float vFadeAlpha;  // NEW: Fade alpha from vertex shader

void main() {
    // ... existing lighting calculation ...
    
    // NEW: Apply fade alpha
    FragColor = vec4(finalColor, vFadeAlpha);
}
```

**Acceptance Criteria**:
- [ ] New chunks fade in over 300ms
- [ ] No visible "pop-in" when chunks load
- [ ] Smooth visual transitions during streaming

---

## Implementation Order

1. ✅ **Priority queue** (already done!)
2. ⬜ **Incremental buffer updates** (Task 5.1) - Critical for streaming
3. ⬜ **Buffer reuse** (Task 5.2) - Prevents memory growth
4. ⬜ **Edit persistence** (Task 5.3) - Player experience
5. ⬜ **Fade-in effects** (Task 5.5) - Polish

---

## Testing Strategy

### Unit Tests
- [ ] Buffer region allocation/reuse
- [ ] Edit mask serialization/deserialization
- [ ] Priority queue ordering

### Integration Tests
- [ ] Player edits visible immediately
- [ ] Streaming works while moving
- [ ] Memory stable during exploration
- [ ] Edits persist across restarts

### Performance Tests
- [ ] Update latency < 16ms per chunk
- [ ] Memory usage < 500MB for 289 chunks
- [ ] No frame drops during streaming

---

## Success Criteria

Phase 5 is complete when:
- [x] Chunks unload when player moves away
- [ ] Player edits update terrain immediately
- [ ] Buffer memory stable during exploration
- [ ] Edits persist across game restarts
- [ ] Streaming works smoothly (no stutter)
- [ ] All tests passing

---

## Next Steps

1. **Step 1**: Implement incremental buffer updates (Task 5.1)
2. **Step 2**: Add buffer reuse optimization (Task 5.2)
3. **Step 3**: Implement edit mask persistence (Task 5.3)
4. **Step 4**: Test streaming with player movement
5. **Step 5**: Add fade-in effects (Task 5.5)
6. **Step 6**: Update TERRAIN_PROGRESS.md

---

## References

- Phase 3 Design: `docs/Phase3-Visibility-Compaction-Design.md`
- Progress Tracking: `src/spyro-game/docs/TERRAIN_PROGRESS.md`
- Buffer Management: `src/spyro-game/World/Phase3BufferManager.cs`
- Streaming Manager: `src/spyro-game/World/ChunkStreamingManager.cs`
