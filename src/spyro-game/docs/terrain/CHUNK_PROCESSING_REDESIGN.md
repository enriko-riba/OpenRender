# Chunk Processing Redesign Plan

**Status: IMPLEMENTED ✅**

## Goals
1. **Simple design** - clear step-by-step processing, no complex notification systems ✅
2. **Minimize re-processing** - only process chunks that actually need it ✅
3. **Fast processing** - all heavy work on background threads ✅
4. **Correct visuals** - no invisible walls at chunk borders ✅
5. **Fast light source changes** - immediate processing for player actions ✅
6. **Cross-chunk support** - correct calculations across chunk boundaries ✅
7. **Metrics** - timing data for mesh and light propagation ✅
8. **Robust streaming** - handles fast movement (ghost mode) without stalling ✅

## Problems Solved
- ~~Overly complex notification/mask systems~~ → Simplified batch-based processing
- ~~Cascading remesh causing infinite loops or stop & go~~ → Batch isolation prevents cascades
- ~~Light not propagating across chunk borders during streaming~~ → Light propagation on background threads
- ~~Main thread blocking during dirty chunk processing~~ → All heavy work on background threads
- ~~Fast movement breaks streaming~~ → Pending batch system handles fast movement

## Implemented Design

### Core Concept
**Two-pass batch processing with pending queue:**

1. **Pass 1 (Terrain Generation)**: Generate voxel data for chunks in current batch
2. **Pass 2 (Mesh + Light)**: When batch complete, propagate light and mesh all chunks
3. **Pending Queue**: New chunks during batch processing wait in pending queue

### Chunk States (Simplified)
```
Pending → Generating → HasTerrain → Processing → Ready
                           ↑                        │
                           └── (mark dirty) ←───────┘
```

- `Pending`: Queued for terrain generation
- `Generating`: Background terrain generation in progress
- `HasTerrain`: Has voxel data, needs mesh/light calculation
- `Processing`: Background mesh/light calculation in progress  
- `Ready`: Fully processed, renderable

### Processing Flow

#### Streaming Scenario (Player Movement)
```
Frame N:
1. Identify NEW chunks entering view distance
2. If batch processing in progress:
   - Add NEW chunks to pendingStreamingBatch
3. Else:
   - Add NEW chunks to currentStreamingBatch
4. Queue all for terrain generation (state = Pending → Generating)

Frame N+1...N+X:
5. Background: Generate terrain for chunks → HasTerrain
6. Check if ALL chunks in currentStreamingBatch have terrain:
   - Skip chunks that were unloaded (no longer in activeChunks)
   - If any still generating, wait (batchProcessingInProgress = true)

Frame N+Y (batch complete):
7. Mark cardinal neighbors of new chunks for reprocessing
8. Queue ALL HasTerrain chunks for meshing with light propagation
9. Clear currentStreamingBatch
10. Promote pendingStreamingBatch → currentStreamingBatch

Frame N+Z:
11. Background mesh results arrive → Ready
```

#### Light Source Change (Player Action)
```
Immediate (same frame):
1. Update voxel data in cache (place/break light source)
2. Recalculate lighting for affected chunk
3. Find all chunks within light radius (up to 8 chunks for torch)
4. Mark all as HasTerrain and add to chunksNeedingReprocess
5. Immediately queue for meshing (processed in ProcessReprocessChunks)

Next frames:
6. Background mesh results arrive → Ready
```

#### Fast Movement Handling (Ghost Mode)
```
Frame N (batch in progress):
1. NEW chunks arrive during fast movement
2. Added to pendingStreamingBatch (NOT currentStreamingBatch)
3. Current batch continues processing uninterrupted

Frame N+X (chunk unloaded while generating):
4. Unloaded chunks removed from currentStreamingBatch
5. Remaining chunks in batch can still complete
6. Batch completion doesn't wait for unloaded chunks

Frame N+Y (batch complete):
7. pendingStreamingBatch promoted to currentStreamingBatch
8. Process continues with next batch
```

### Key Design Decisions

#### 1. No PlaceholderMask
- Removed - chunks process when batch is ready
- Edge-of-world chunks don't wait for out-of-bounds neighbors
- Edge-of-view chunks remesh when neighbor loads

#### 2. Batch Processing with Pending Queue
- `currentStreamingBatch`: Chunks being actively processed
- `pendingStreamingBatch`: Chunks waiting for next batch (added during processing)
- `batchProcessingInProgress`: Flag to route new chunks to pending queue
- Prevents batch from growing indefinitely during fast movement

#### 3. Robust Unload Handling
- Unloaded chunks removed from ALL batch tracking sets
- Batch completion skips chunks that no longer exist in activeChunks
- Prevents stale references from blocking batch completion

#### 4. Immediate Block Edit Processing
- `chunksNeedingReprocess`: High-priority queue for block edits
- Processed in `ProcessReprocessChunks()` before batch check
- Not affected by batch processing state

#### 5. Background Thread Work
ALL heavy operations on background threads:
- Terrain generation (`ChunkGenerationJobSystem`)
- Light calculation (in generation job)
- Light propagation (in meshing job, before mesh build)
- Mesh building (`ChunkMeshingJobSystem`)

Main thread only:
- State management
- Queue management  
- GPU uploads

### Data Structures

#### ChunkDescriptor (Implemented)
```csharp
public struct ChunkDescriptor
{
    public int ChunkIndex;
    public TerrainChunkState State;  // 5-state enum
    public int CommandSlot;
    public int AtlasOffset;
    public int IndexOffset;
    public int VisibleVoxelCount;
    public IntPtr Fence;              // GPU sync fence
}

public enum TerrainChunkState : byte
{
    Pending = 0,      // Waiting for terrain generation
    Generating = 1,   // Terrain generation in progress
    HasTerrain = 2,   // Has voxel data, needs mesh/light
    Processing = 3,   // Mesh/light calculation in progress
    Ready = 4,        // Fully processed, renderable
}
```

#### Batch Tracking (Implemented)
```csharp
// Chunks being actively processed (waiting for terrain, then meshing)
private readonly HashSet<int> currentStreamingBatch = [];

// Chunks queued while batch is processing (promoted when batch completes)
private readonly HashSet<int> pendingStreamingBatch = [];

// Flag to route new chunks to pending queue
private bool batchProcessingInProgress;

// High-priority queue for block edits (processed immediately)
private readonly HashSet<int> chunksNeedingReprocess = [];
```

### Algorithm Details (Implemented)

#### QueueNewChunks() - Batch Isolation
```csharp
private void QueueNewChunks(HashSet<int> visibleChunks)
{
    var newChunks = visibleChunks.Except(activeChunks.Keys).ToList();
    if (newChunks.Count == 0) return;

    // KEY: Route to pending batch if processing is in progress
    var targetBatch = batchProcessingInProgress 
        ? pendingStreamingBatch 
        : currentStreamingBatch;

    foreach (var chunkIdx in newChunks)
    {
        // Create descriptor, add to targetBatch, queue for generation
        activeChunks[chunkIdx] = new ChunkDescriptor { State = Pending, ... };
        targetBatch.Add(chunkIdx);
        EnqueueForGeneration(chunkIdx);
    }
}
```

#### ProcessTerrainBatch() - Robust Completion
```csharp
private void ProcessTerrainBatch()
{
    ProcessReprocessChunks();  // Block edits first (immediate)
    
    if (currentStreamingBatch.Count == 0)
    {
        // Promote pending batch if any
        if (pendingStreamingBatch.Count > 0)
        {
            foreach (var idx in pendingStreamingBatch)
                currentStreamingBatch.Add(idx);
            pendingStreamingBatch.Clear();
            batchProcessingInProgress = false;
        }
        return;
    }

    // Check if all chunks have terrain (skip unloaded chunks)
    var stillWaiting = false;
    foreach (var idx in currentStreamingBatch)
    {
        if (activeChunks.TryGetValue(idx, out var d))
        {
            if (!d.HasVoxelData())
            {
                stillWaiting = true;
                break;
            }
        }
        // Unloaded chunks are considered "done"
    }

    if (stillWaiting)
    {
        batchProcessingInProgress = true;
        return;
    }

    // Process all HasTerrain chunks + their neighbors
    var toProcess = currentStreamingBatch
        .Where(idx => activeChunks.TryGetValue(idx, out var d) && 
                      d.State == HasTerrain)
        .ToList();
    
    // Mark cardinal neighbors for reprocess
    foreach (var chunkIdx in toProcess)
        MarkCardinalNeighborsForReprocess(chunkIdx);

    // Queue all for meshing with light propagation
    foreach (var chunkIdx in toProcess)
        ScheduleCpuMeshing(chunkIdx, propagateLight: true);

    // Clear and promote
    currentStreamingBatch.Clear();
    batchProcessingInProgress = false;
    
    if (pendingStreamingBatch.Count > 0)
    {
        foreach (var idx in pendingStreamingBatch)
            currentStreamingBatch.Add(idx);
        pendingStreamingBatch.Clear();
    }
}
```

#### UnloadChunk() - Clean Batch Tracking
```csharp
private void UnloadChunk(int chunkIndex)
{
    // CRITICAL: Remove from ALL batch tracking
    currentStreamingBatch.Remove(chunkIndex);
    pendingStreamingBatch.Remove(chunkIndex);
    chunksNeedingReprocess.Remove(chunkIndex);
    
    // ... rest of cleanup (GPU resources, cache, etc.)
    activeChunks.Remove(chunkIndex);
}
```

#### PropagateBoundaryLight() - Background Thread
```csharp
// In ChunkMeshingJobSystem.ProcessMeshItem()
private void ProcessMeshItem(ChunkMeshWorkItem item)
{
    if (item.PropagateLight)
    {
        var sw = Stopwatch.StartNew();
        PropagateBoundaryLight(item.ChunkIndex);  // Cardinal neighbors
        sw.Stop();
        metrics?.RecordLightPropagation(sw.Elapsed.TotalMilliseconds);
    }

    var meshSw = Stopwatch.StartNew();
    if (ChunkMeshBuilder.TryBuild(item, voxelCache, out var mesh))
    {
        meshSw.Stop();
        metrics?.RecordMeshBuild(meshSw.Elapsed.TotalMilliseconds);
        completedMeshes.Enqueue(mesh);
    }
}
```
### Metrics System (Implemented)
```csharp
public sealed class ChunkProcessingMetrics
{
    // Rolling averages (100 sample window)
    public double AvgTerrainGenerationMs { get; }  // Background thread
    public double AvgLightCalculationMs { get; }   // Background thread
    public double AvgLightPropagationMs { get; }   // Background thread (meshing)
    public double AvgMeshBuildMs { get; }          // Background thread
    
    // Per-second counters
    public int ChunksGeneratedPerSecond { get; }
    public int ChunksMeshedPerSecond { get; }
    public int ChunksReprocessedPerSecond { get; }
    
    // Recording methods (called from background threads)
    public void RecordTerrainGeneration(double ms);
    public void RecordLightCalculation(double ms);
    public void RecordLightPropagation(double ms);
    public void RecordMeshBuild(double ms);
    public void RecordReprocess();
    
    // Update per-second counters
    public void Update(double currentTimeSeconds);
}
```

### HUD Display (Implemented)
```
Chunks:
  GPU Ready: 1,009 | Queued: 37 | Target: 1,009
  Visible: 432
  Frustum Culled: 577
  Indices: 2,847,234 / 5,123,456
  Batch: 0 | Pending: 0 | Processing: False

Processing:
  Terrain Gen: 8.2ms | Light Calc: 2.1ms
  Light Prop: 1.5ms | Mesh Build: 3.4ms
  Gen/s: 12 | Mesh/s: 24 | Reproc/s: 0
```

### Implementation Status

#### Phase 1: Simplify State Machine ✅
- [x] 5-state `TerrainChunkState` enum
- [x] Updated `ChunkDescriptor` with Fence for GPU sync
- [x] All state transitions updated in `ChunkStreamingManager`

#### Phase 2: Implement Batch Tracking ✅
- [x] `currentStreamingBatch` and `pendingStreamingBatch` collections
- [x] `batchProcessingInProgress` flag for batch isolation
- [x] `ProcessTerrainBatch()` with robust completion check
- [x] Removed old notification systems

#### Phase 3: Move Light Calculation Off Main Thread ✅
- [x] Light calculation in `ChunkGenerationJobSystem` (background)
- [x] Light propagation in `ChunkMeshingJobSystem.PropagateBoundaryLight()` (background)
- [x] Thread-safe chunk data access via `ChunkVoxelDataCache`

#### Phase 4: Implement Light Source Change Handler ✅
- [x] `HandleBlockEdit()` for block placement/breaking
- [x] `ProcessReprocessChunks()` for immediate processing
- [x] Direct voxel cache update for instant feedback

#### Phase 5: Add Metrics ✅
- [x] `ChunkProcessingMetrics` class with rolling averages
- [x] Timing instrumentation in generation and meshing jobs
- [x] HUD display of all metrics and batch status

#### Phase 6: Robust Fast Movement Handling ✅
- [x] Pending batch queue for new chunks during processing
- [x] Unload removes chunks from all batch tracking
- [x] Batch completion skips unloaded chunks
- [x] Tested with extended ghost mode flight

### Success Criteria - ALL MET ✅
- [x] No visible walls at chunk borders after streaming
- [x] Torch light propagates correctly across chunk borders
- [x] No stop & go when crossing chunk borders  
- [x] Torch placement/removal updates all affected chunks immediately
- [x] Average mesh time visible in debug overlay
- [x] Average light propagation time visible in debug overlay
- [x] Code is significantly simpler than previous implementation
- [x] Fast ghost mode movement doesn't break streaming
