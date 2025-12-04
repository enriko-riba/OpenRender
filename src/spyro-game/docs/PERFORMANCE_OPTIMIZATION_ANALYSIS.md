# Performance Optimization Analysis

## 🔴 Direct Performance Improvements

### 1. Lighting Calculation - Full BFS Every Chunk (HIGH IMPACT)
**File:** `LightingCalculator.cs`

**Issue:** `CalculateLighting()` clears ALL light data and performs full BFS flood-fill for every new chunk. This is ~100k+ voxels per chunk.

**Recommendation:**
- Implement incremental lighting: only recalculate columns that changed
- Use height maps to skip underground air columns that won't receive sky light
- Consider lazy propagation: only propagate light when mesh is requested

### 2. ChunkMeshBuilder - Triple Neighbor Sampling per Vertex (HIGH IMPACT)
**File:** `ChunkMeshBuilder.cs` lines 480-580

**Issue:** `ComputeSmoothLight()` and `ComputeAmbientOcclusion()` each sample 4 neighbors (base + 2 sides + corner). For a face with 4 vertices, this means 8 neighbor block samples + 8 opacity checks + 8 light reads per face.

**Recommendation:**
- Cache neighbor lookups per face (not per vertex) since many vertices share neighbors
- Pre-compute light values per block once instead of per-vertex
- Use lookup tables for AO patterns instead of runtime calculation

### 3. Dictionary Allocations in ChunkVoxelSampler (MEDIUM IMPACT) ✅ COMPLETED
**File:** `ChunkMeshBuilder.cs` line 398

**Issue:** `neighborCache = new Dictionary<int, ChunkVoxelDataView>()` allocates a new dictionary per chunk mesh build.

**Recommendation:** Pool the sampler objects or use `[ThreadStatic]` dictionary that gets cleared instead of recreated.

**Resolution:** Added `[ThreadStatic] private static Dictionary<int, ChunkVoxelDataView>? t_neighborCache` that gets `.Clear()` and reused instead of allocating a new dictionary per chunk.

### 4. BlockRegistry.GetProperties() Hot Path (MEDIUM IMPACT)
**File:** `ChunkMeshBuilder.cs` line 213, `LightingCalculator.cs` multiple

**Issue:** Called frequently (every block evaluation) - should be a direct array lookup.

**Recommendation:** Ensure this is a simple array index lookup, not dictionary access. Consider caching in BlockId struct directly.

### 5. Palette Lookup in Hot Loop (MEDIUM IMPACT) ✅ COMPLETED
**File:** `ChunkData.cs` lines 74-84

**Issue:** `GetOrAddPaletteEntry()` does linear search for every block read/write. With 20+ block types, this adds up.

**Recommendation:**
- Use a reverse lookup array (BlockId → paletteIndex) that's rebuilt when palette changes
- Pre-populate common blocks (Air, Stone, Dirt, Grass, Water) at known indices

**Resolution:** Added `reversePalette` Dictionary<BlockId, byte> for O(1) block→index lookup. Both forward and reverse mappings are maintained in `GetOrAddPaletteEntry()`.

### 6. Sparse 3D Noise Interpolation (LOW-MEDIUM IMPACT)
**File:** `CpuTerrainGenerator.cs` lines 16-24

**Issue:** Currently samples at 4-block intervals. Good, but trilinear interpolation could use SIMD.

**Recommendation:** Use `Vector256<float>` for trilinear interpolation batches (8 values at once).

### 7. Light Propagation Creates New Collections (MEDIUM IMPACT) ✅ COMPLETED
**File:** `LightingCalculator.cs` `RemoveBlockLight()`

**Issue:** Returns `HashSet<int>` with `[.. modifiedChunks]` spread syntax, creating allocations.

**Recommendation:** Pass in a pooled/reused `HashSet<int>` instead of returning a new one.

**Resolution:** `RemoveBlockLight()`, `RemoveSkyLight()`, and `RecalculateLightingAroundBlock()` now return the static `ModifiedChunks` HashSet directly instead of creating a copy with spread syntax. Callers must iterate immediately or copy if needed.

### 8. ConcurrentDictionary in ChunkVoxelDataCache (LOW IMPACT)
**File:** `ChunkVoxelDataCache.cs`

**Issue:** Every `TryGetReadOnly` and `TryGetChunkData` goes through ConcurrentDictionary overhead.

**Recommendation:** Since most reads are from the same thread, consider a thread-local cache layer that falls back to the concurrent dictionary.

### 9. ToList()/ToArray() in Update Loop (MEDIUM IMPACT) ✅ COMPLETED
**File:** `ChunkStreamingManager.cs` line 439

**Issue:** `visibleChunks.Except(activeChunks.Keys).ToList()` allocates every frame.

**Recommendation:** Reuse a pooled list and manually filter instead of LINQ.

**Resolution:** Added `reusableChunkList` and `reusableChunkList2` as pooled lists. Replaced LINQ `.ToList()` allocations in `QueueNewChunks()`, `ProcessBatchedChunks()`, and `ProcessReprocessChunks()` with manual loops using the reusable lists.

### 10. BFS Queue Size (LOW IMPACT) ✅ COMPLETED
**File:** `LightingCalculator.cs` line 20

**Issue:** Initial queue capacity of 4096 may cause reallocation for large cave systems.

**Recommendation:** Size based on chunk volume (16×384×16 ÷ 8 = ~12k might be better).

**Resolution:** Added `QueueInitialCapacity = 12288` constant and updated all three BFS queues (`LightQueue`, `RemovalQueue`, `RepropagateQueue`) to use this larger capacity.

---

## 🔵 Maintainability & Simplification Improvements

### 1. Consolidate Light Type Handling
**File:** `LightingCalculator.cs`

**Issue:** Sky light and block light use nearly identical propagation logic with boolean parameter switching.

**Recommendation:** Extract shared BFS logic into a generic method, parameterized by get/set delegates.

### 2. ChunkDescriptor is a Mutable Struct
**File:** `ChunkDescriptor.cs`

**Issue:** Mutable structs are error-prone; every modification requires re-assignment to dictionary.

**Recommendation:** Convert to a `record struct` or use a class for `activeChunks` values.

### 3. Magic Numbers in Terrain Generation
**File:** `CpuTerrainGenerator.cs` lines 300-400

**Issue:** Many hardcoded values like `0.35f`, `0.45f`, `0.65f` for biome thresholds.

**Recommendation:** Move these to `TerrainConfig` or named constants.

### 4. Separate Concerns in ChunkStreamingManager
**File:** `ChunkStreamingManager.cs` (2000+ lines)

**Issue:** This class handles: streaming, batching, queuing, unloading, GPU uploads, collision, biome queries, config loading, metrics.

**Recommendation:** Extract:
- `ChunkBatchProcessor` - batch state machine
- `ChunkLifecycleManager` - load/unload/retention logic
- `TerrainQueryService` - biome/climate queries

### 5. Duplicate Neighbor Offset Arrays
**Files:** `ChunkStreamingManager.cs`, `LightingCalculator.cs`, `ChunkMeshBuilder.cs`

**Issue:** Each file defines its own neighbor offset arrays.

**Recommendation:** Centralize in `VoxelHelper` as `CardinalOffsets` and `AllNeighborOffsets`.

### 6. Missing XML Documentation
**Files:** Multiple

**Issue:** Many public methods lack documentation, especially parameters.

**Recommendation:** Add `<param>` and `<returns>` tags for public API surface.

### 7. Inconsistent Error Handling
**File:** `ChunkGenerationJobSystem.cs` line 179

**Issue:** `// TODO: Add ReturnWritable...` comment indicates incomplete cleanup path.

**Recommendation:** Implement proper buffer return on failure to prevent memory leaks.

### 8. ThreadLocal Disposal Pattern
**File:** `ChunkMeshBuilder.cs` lines 25-28

**Issue:** `[ThreadStatic]` lists are never explicitly cleared on thread exit.

**Recommendation:** Use `ThreadLocal<T>` with `trackAllValues: true` and proper disposal, or document that these intentionally persist.

### 9. Biome Weight Calculation Complexity
**File:** `CpuTerrainGenerator.cs` `CalculateBiomeWeight()`

**Issue:** Complex nested logic with multiple smoothstep calls per biome per column.

**Recommendation:** Pre-compute biome weight lookup tables (256×256 climate grid) at config load time.

### 10. LINQ in Hot Paths
**File:** `ChunkStreamingManager.cs` various

**Issue:** Methods like `GetStats()` use `.Count(predicate)` which allocates.

**Recommendation:** Use manual loops or cache stats that update incrementally.

---

## 📊 Performance Impact Summary

| Improvement | Estimated Impact | Effort |
|-------------|------------------|--------|
| Incremental lighting | 30-50% faster gen | High |
| Cache neighbor lookups in meshing | 20-30% faster mesh | Medium |
| Pool ChunkVoxelSampler dictionary | 10-15% less GC | Low |
| Reverse palette lookup | 5-10% faster gen | Low |
| Avoid LINQ allocations in Update | 5-10% less GC | Low |
| SIMD trilinear interpolation | 10-20% faster noise | Medium |

---

## 🎯 Top 3 Quick Wins

1. ✅ **Replace neighborCache dictionary with `[ThreadStatic]` pooled dictionary** (5 min fix, immediate GC reduction)
2. ✅ **Remove `.ToList()` in QueueNewChunks** - use manual loop (5 min fix)
3. ✅ **Add reverse palette array in ChunkData** for O(1) block→index lookup (30 min fix)

---

## 🎯 Top 3 High-Impact Changes

1. **Incremental sky lighting** - only propagate from column tops, skip solid blocks
2. **Per-face light caching** - compute light once per face, not 4× per vertex
3. **Biome weight LUT** - pre-compute 256×256 climate→weight grid

---

## Implementation Progress

- [x] Phase 1: Quick Wins (Low effort, immediate impact) - **COMPLETED Dec 4, 2025**
  - [x] ThreadStatic neighborCache in ChunkMeshBuilder
  - [x] Remove .ToList() allocations in ChunkStreamingManager
  - [x] Reverse palette lookup in ChunkData
- [x] Phase 2: Medium Impact Changes - **COMPLETED Dec 4, 2025**
  - [x] Return static HashSet directly from light removal methods
  - [x] Increase BFS queue capacity to 12288
  - [x] Thread-local cache for ChunkVoxelDataCache (SKIPPED - low impact, stale data risks)
- [ ] Phase 3: High Impact Changes
  - [ ] Incremental sky lighting
  - [ ] Per-face light caching
  - [ ] Biome weight LUT
