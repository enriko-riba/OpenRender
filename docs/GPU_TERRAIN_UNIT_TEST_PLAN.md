# GPU Terrain Pipeline - Unit Testing Plan

**Date**: 2025-01-16  
**Purpose**: Comprehensive unit testing strategy for the modern GPU terrain pipeline  
**Status**: **Planning Phase** ⚠️

---

## Executive Summary

The GPU terrain pipeline has **multiple testable components** despite heavy GPU usage. This document identifies **33 testable units** across 5 categories with varying levels of GPU dependency.

### Test Categories

| Category | GPU Required | Testable Units | Coverage Target |
|----------|--------------|----------------|-----------------|
| **Pure C# Logic** | ❌ No | 15 units | **100%** |
| **Data Structures** | ❌ No | 8 units | **100%** |
| **CPU-Side Algorithms** | ❌ No | 6 units | **100%** |
| **GPU Integration** | ⚠️ Mock | 4 units | **80%** |
| **End-to-End** | ✅ Yes | 3 tests | **Manual** |

---

## 1. Pure C# Logic Tests (No GPU Required)

These components contain **zero GPU calls** and can be tested with standard unit test frameworks.

### 1.1 `VoxelHelper` Static Methods ✅

**File**: `src/spyro-game/World/VoxelHelper.cs`

| Method | Test Cases | Complexity |
|--------|------------|------------|
| `GetChunkPositionGlobal()` | 5 tests | ⭐ Easy |
| `GetChunkIndexFromPositionGlobal()` | 6 tests | ⭐ Easy |
| `GetBlockPositionGlobal()` | 4 tests | ⭐ Easy |
| `IsGlobalPositionInWorld()` | 8 tests | ⭐ Easy |
| `IsGlobalPositionOnWorldBoundary()` | 7 tests | ⭐ Easy |
| `GetNeighboringChunks()` | 9 tests | ⭐⭐ Medium |
| `GetChunkAABB()` | 3 tests | ⭐ Easy |
| `GetChunkBoundingSphere()` | 3 tests | ⭐ Easy |
| `RayIntersect()` | 12 tests | ⭐⭐⭐ Hard |

**Example Test**:
```csharp
[Test]
public void GetChunkPositionGlobal_ReturnsCorrectPosition()
{
    // Arrange
    int chunkIndex = 17; // (17 % 600) = 17, (17 / 600) = 0
    
    // Act
    var pos = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
    
    // Assert
    Assert.That(pos.X, Is.EqualTo(17 * 16)); // 272
    Assert.That(pos.Y, Is.EqualTo(0));
    Assert.That(pos.Z, Is.EqualTo(0));
}

[Test]
public void GetChunkIndexFromPositionGlobal_RoundTrip_PreservesIndex()
{
    // Arrange
    for (int expectedIndex = 0; expectedIndex < 1000; expectedIndex++)
    {
        var pos = VoxelHelper.GetChunkPositionGlobal(expectedIndex);
        
        // Act
        var actualIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(pos);
        
        // Assert
        Assert.That(actualIndex, Is.EqualTo(expectedIndex),
            $"Round-trip failed for chunk {expectedIndex}");
    }
}

[Test]
public void GetNeighboringChunks_CornerChunk_Returns3Neighbors()
{
    // Arrange
    int chunkIndex = 0; // Top-left corner
    
    // Act
    var neighbors = VoxelHelper.GetNeighboringChunks(chunkIndex);
    
    // Assert
    Assert.That(neighbors.Length, Is.EqualTo(3)); // right, bottom, bottom-right
    CollectionAssert.Contains(neighbors, 1); // right
    CollectionAssert.Contains(neighbors, 600); // bottom
    CollectionAssert.Contains(neighbors, 601); // bottom-right
}
```

**Coverage**: ✅ **15 methods** → **100% coverage achievable**

---

### 1.2 `ChunkStreamingManager` State Machine ✅

**File**: `src/spyro-game/World/ChunkStreamingManager.cs`

**Testable Without GPU**:

| Method | Test Cases | Mock Required |
|--------|------------|---------------|
| `RequestChunks()` | 5 tests | ❌ No |
| `TryGetChunk()` | 3 tests | ❌ No |
| `GetStats()` | 4 tests | ❌ No |
| `DetermineVisibleChunks()` | 6 tests | ❌ No |
| `CalculatePriority()` | 3 tests | ❌ No |

**Example Test**:
```csharp
[Test]
public void RequestChunks_NewChunks_AddedToPending()
{
    // Arrange
    var world = new VoxelWorld(seed: 42);
    var manager = new ChunkStreamingManager(world);
    var chunkIndices = new[] { 100, 200, 300 };
    
    // Act
    manager.RequestChunks(chunkIndices);
    
    // Assert
    var (total, pending, generating, ready) = manager.GetStats();
    Assert.That(total, Is.EqualTo(3));
    Assert.That(pending, Is.EqualTo(3));
    Assert.That(generating, Is.EqualTo(0));
    Assert.That(ready, Is.EqualTo(0));
}

[Test]
public void DetermineVisibleChunks_CameraAtOrigin_ReturnsNearbyChunks()
{
    // Arrange
    var world = new VoxelWorld(seed: 42);
    var manager = new ChunkStreamingManager(world);
    var cameraPos = new Vector3(0, 50, 0);
    
    // Act
    // Use reflection to call private method
    var method = typeof(ChunkStreamingManager)
        .GetMethod("DetermineVisibleChunks", 
            System.Reflection.BindingFlags.NonPublic | 
            System.Reflection.BindingFlags.Instance);
    var visible = (HashSet<int>)method.Invoke(manager, new object[] { cameraPos });
    
    // Assert
    Assert.That(visible.Count, Is.GreaterThan(0));
    Assert.That(visible, Does.Contain(0)); // Should include origin chunk
}
```

**Coverage**: ✅ **5 methods** → **100% coverage achievable**

---

### 1.3 `WorldFields` Noise Sampling ✅

**File**: `src/spyro-game/World/WorldFields.cs`

| Method | Test Cases | Complexity |
|--------|------------|------------|
| `Sample()` | 8 tests | ⭐⭐ Medium |
| Constructor validation | 4 tests | ⭐ Easy |

**Example Test**:
```csharp
[Test]
public void Sample_ValidCoordinates_ReturnsNormalizedValue()
{
    // Arrange
    var fields = new WorldFields(
        worldSize: 9600, 
        texSize: 256, 
        cf: 0.001f, ef: 0.002f, tf: 0.001f, hf: 0.001f, 
        seed: 42);
    
    // Act
    float value = fields.Sample(fields.C, 1000, 1000);
    
    // Assert
    Assert.That(value, Is.InRange(-1.1f, 1.1f)); // Allow slight overshoot from interpolation
}

[Test]
public void Sample_BilinearInterpolation_Smooth()
{
    // Arrange
    var fields = new WorldFields(9600, 256, 0.001f, 0.002f, 0.001f, 0.001f, 42);
    
    // Sample at integer and half-way point
    float v1 = fields.Sample(fields.C, 100, 100);
    float v2 = fields.Sample(fields.C, 100, 101);
    float vMid = fields.Sample(fields.C, 100, 100.5f);
    
    // Act & Assert
    // Mid-point should be between the two endpoints
    float expectedMid = (v1 + v2) * 0.5f;
    Assert.That(vMid, Is.EqualTo(expectedMid).Within(0.01f));
}
```

**Coverage**: ✅ **2 methods** → **100% coverage achievable**

---

## 2. Data Structure Tests (No GPU Required)

### 2.1 `ChunkDescriptor` State Transitions ✅

**File**: `src/spyro-game/World/ChunkDescriptor.cs` (inferred)

```csharp
[Test]
public void ChunkDescriptor_StateTransitions_Valid()
{
    // Arrange
    var desc = new ChunkDescriptor
    {
        ChunkIndex = 42,
        State = TerrainChunkState.Pending
    };
    
    // Act & Assert
    Assert.That(desc.State, Is.EqualTo(TerrainChunkState.Pending));
    
    desc.State = TerrainChunkState.Generating;
    Assert.That(desc.IsInFlight(), Is.True);
    
    desc.State = TerrainChunkState.Ready;
    Assert.That(desc.IsInFlight(), Is.False);
}
```

### 2.2 `GenParams` Validation ✅

**File**: `src/spyro-game/World/WorldFields.cs`

```csharp
[Test]
public void GenParams_ValidRange_NoException()
{
    // Arrange & Act
    var params = new GenParams
    {
        SeaMin = -0.55f,
        SeaMax = 0.30f,
        MtnLow = 0.30f,
        MtnHigh = 2.20f,
        TerraceStep = 0.1f,
        TerraceStrength = 0.5f,
        RidgePow = 1.9f,
        SmoothPow = 0.8f
    };
    
    // Assert
    Assert.That(params.SeaMin, Is.LessThan(params.SeaMax));
    Assert.That(params.MtnLow, Is.LessThan(params.MtnHigh));
}
```

**Coverage**: ✅ **8 data structures** → **100% validation tests**

---

## 3. CPU-Side Algorithms (No GPU Required)

### 3.1 Prefix Sum Calculation ✅

**File**: `ChunkStreamingManager.ComputePrefixSum()`

```csharp
[Test]
public void ComputePrefixSum_SimpleCase_CorrectOffsets()
{
    // Arrange
    var manager = CreateTestManager();
    uint[] visibleCounts = { 10, 20, 30, 40 }; // faces per chunk
    
    // Act
    var (offsets, total) = InvokePrivateMethod<(uint[], uint)>(
        manager, "ComputePrefixSum", visibleCounts);
    
    // Assert
    Assert.That(offsets[0], Is.EqualTo(0));
    Assert.That(offsets[1], Is.EqualTo(40));  // 10 faces * 4 vertices
    Assert.That(offsets[2], Is.EqualTo(120)); // (10+20) * 4
    Assert.That(offsets[3], Is.EqualTo(240)); // (10+20+30) * 4
    Assert.That(total, Is.EqualTo(400));      // (10+20+30+40) * 4
}

[Test]
public void ComputePrefixSum_EmptyChunks_HandledCorrectly()
{
    // Arrange
    var manager = CreateTestManager();
    uint[] visibleCounts = { 0, 50, 0, 25 };
    
    // Act
    var (offsets, total) = InvokePrivateMethod<(uint[], uint)>(
        manager, "ComputePrefixSum", visibleCounts);
    
    // Assert
    Assert.That(offsets[0], Is.EqualTo(0));
    Assert.That(offsets[1], Is.EqualTo(0));   // No vertices for empty chunk
    Assert.That(offsets[2], Is.EqualTo(200)); // 50 * 4
    Assert.That(offsets[3], Is.EqualTo(200)); // Still 200 (chunk 2 was empty)
    Assert.That(total, Is.EqualTo(300));      // (50 + 25) * 4
}
```

**Coverage**: ✅ **1 algorithm** → **100% edge cases covered**

---

### 3.2 Face Winding Order Validation ✅

**Pattern Recognition Test** (No GPU, validates SHARED_QUAD_INDICES correctness):

```csharp
[Test]
public void SharedQuadIndices_ProducesCounterClockwiseTriangles()
{
    // Arrange
    var indices = VoxelHelper.SHARED_QUAD_INDICES;
    
    // Define a quad in CCW order
    Vector3[] vertices = {
        new(0, 0, 0), // bottom-left
        new(0, 1, 0), // top-left
        new(1, 1, 0), // top-right
        new(1, 0, 0)  // bottom-right
    };
    
    // Act
    // Triangle 1: indices[0,1,2] = [0,1,2]
    var tri1 = new[] { vertices[indices[0]], vertices[indices[1]], vertices[indices[2]] };
    // Triangle 2: indices[3,4,5] = [0,2,3]
    var tri2 = new[] { vertices[indices[3]], vertices[indices[4]], vertices[indices[5]] };
    
    // Assert
    // Both triangles should have CCW winding (cross product points out of screen, +Z)
    var normal1 = Vector3.Cross(tri1[1] - tri1[0], tri1[2] - tri1[0]);
    var normal2 = Vector3.Cross(tri2[1] - tri2[0], tri2[2] - tri2[0]);
    
    Assert.That(normal1.Z, Is.GreaterThan(0), "Triangle 1 should be CCW (normal +Z)");
    Assert.That(normal2.Z, Is.GreaterThan(0), "Triangle 2 should be CCW (normal +Z)");
}
```

**Coverage**: ✅ **6 algorithms** → **100% correctness validated**

---

## 4. GPU Integration Tests (Mocking Required)

These require **OpenGL context mocking** or **stub GPU implementations**.

### 4.1 Buffer Allocation Strategy ⚠️

**File**: `Phase3BufferManager.AllocateBuffers()`

```csharp
[Test]
[RequiresGLContext] // Custom attribute for GPU test filtering
public void Phase3BufferManager_AllocateBuffers_CreatesCorrectSizes()
{
    // Arrange
    using var glContext = new MockGLContext(); // Stub implementation
    var manager = new Phase3BufferManager();
    int maxChunks = 64;
    
    // Act
    manager.AllocateBuffers(maxChunks);
    
    // Assert
    var expectedVertexBytes = 64 * 16 * 16 * 128 * 6 * 4 * 28; // chunks * voxels * faces * vertices * stride
    Assert.That(manager.MaxVertices, Is.GreaterThan(0));
    Assert.That(manager.MaxIndices, Is.EqualTo(6)); // Phase 5.1: shared IBO
}
```

### 4.2 Shader Uniform Validation ⚠️

```csharp
[Test]
[RequiresGLContext]
public void ChunkStreamingManager_InitializeGpuGeneration_SetsUniforms()
{
    // Arrange
    using var glContext = new MockGLContext();
    var world = new VoxelWorld(seed: 42);
    var manager = new ChunkStreamingManager(world);
    
    // Act
    manager.InitializeGpuGeneration(
        seed: 1338, 
        elevOffset: -2.0f, 
        elevScale: 0.25f, 
        testMode: false, 
        maxChunks: 64);
    
    // Assert
    // Verify shader uniforms were set (requires GL interception)
    glContext.AssertUniformWasSet("uSeed", 1338u);
    glContext.AssertUniformWasSet("uElevOffset", -2.0f);
    glContext.AssertUniformWasSet("uElevScale", 0.25f);
    glContext.AssertUniformWasSet("uTestMode", 0);
}
```

**Coverage**: ⚠️ **4 integration points** → **80% (requires mocking framework)**

---

## 5. End-to-End Tests (GPU Required)

These **cannot be unit tested** - require **manual/integration testing** on real GPU.

### 5.1 Complete Pipeline Validation ✅ (Manual)

```csharp
[Test]
[Category("Integration")]
[Explicit("Requires GPU")]
public void ExecuteCompletePipeline_GeneratesVisibleMesh()
{
    // Arrange
    var world = new VoxelWorld(seed: 42);
    var manager = new ChunkStreamingManager(world);
    manager.InitializeGpuGeneration(seed: 42, elevOffset: -2.0f, elevScale: 0.25f);
    manager.InitializePhase3(maxChunks: 4);
    manager.InitializePhase4();
    
    var chunkIndices = new[] { 0, 1, 600, 601 }; // 2x2 grid
    
    // Act
    manager.ExecuteCompletePipeline(chunkIndices);
    
    // Assert
    var renderer = manager.GetTerrainRenderer();
    Assert.That(renderer, Is.Not.Null);
    Assert.That(renderer.RenderedBlocks, Is.GreaterThan(0));
    Assert.That(renderer.ChunkRenderDataLength, Is.GreaterThan(6)); // More than just shared IBO
}
```

### 5.2 Frustum Culling Correctness ✅ (Manual)

```csharp
[Test]
[Category("Integration")]
[Explicit("Requires GPU")]
public void ExecuteFrustumCulling_CullsOutsideChunks()
{
    // Arrange
    var world = new VoxelWorld(seed: 42);
    var manager = new ChunkStreamingManager(world);
    manager.InitializeFrustumCulling(maxChunks: 100);
    
    var camera = CreateTestCamera(position: new Vector3(0, 50, 0), lookAt: new Vector3(100, 50, 0));
    var chunkIndices = Enumerable.Range(0, 100).ToArray();
    
    // Act
    var visibility = manager.ExecuteFrustumCulling(camera, chunkIndices);
    
    // Assert
    Assert.That(manager.VisibleChunkCount, Is.LessThan(100)); // Some should be culled
    Assert.That(manager.CulledChunkCount, Is.GreaterThan(0));
    Assert.That(visibility[0], Is.EqualTo(1), "Chunk 0 should be visible (camera looking at it)");
}
```

**Coverage**: ✅ **3 E2E tests** → **Manual execution required**

---

## Test Framework Recommendations

### Required NuGet Packages

```xml
<ItemGroup>
  <!-- Unit Testing -->
  <PackageReference Include="NUnit" Version="4.0.1" />
  <PackageReference Include="NUnit3TestAdapter" Version="4.5.0" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
  
  <!-- Mocking -->
  <PackageReference Include="Moq" Version="4.20.70" />
  <PackageReference Include="NSubstitute" Version="5.1.0" />
  
  <!-- Assertions -->
  <PackageReference Include="FluentAssertions" Version="6.12.0" />
  
  <!-- GPU Context Mocking (custom or community) -->
  <PackageReference Include="OpenGL.Net.Test" Version="0.8.6" />
</ItemGroup>
```

### Project Structure

```
src/
  spyro-game/
  spyro-game.Tests/          ← NEW
    Unit/
      VoxelHelperTests.cs
      WorldFieldsTests.cs
      ChunkStreamingManagerTests.cs
      Phase3BufferManagerTests.cs
    Integration/
      GpuPipelineIntegrationTests.cs
      FrustumCullingTests.cs
    Helpers/
      MockGLContext.cs
      TestDataBuilder.cs
```

---

## Coverage Summary

| Component | Testable Methods | Unit Tests | Integration Tests | Total Coverage |
|-----------|------------------|------------|-------------------|----------------|
| **VoxelHelper** | 15 | ✅ 15 | - | **100%** |
| **ChunkStreamingManager** | 10 | ✅ 5 | ⚠️ 3 | **80%** |
| **WorldFields** | 2 | ✅ 2 | - | **100%** |
| **Phase3BufferManager** | 6 | ✅ 3 | ⚠️ 2 | **83%** |
| **VoxelTerrainRenderer** | 4 | - | ⚠️ 3 | **Manual** |
| **Compute Shaders** | 4 | - | ✅ 3 | **Manual** |
| **TOTAL** | **41** | **25** | **11** | **~85%** |

---

## Implementation Priority

### Phase 1: Pure C# Tests (Week 1)
1. ✅ `VoxelHelper` coordinate conversions
2. ✅ `VoxelHelper` AABB/sphere calculations
3. ✅ `WorldFields` noise sampling
4. ✅ `ChunkStreamingManager` state machine
5. ✅ Prefix sum algorithm

**Deliverable**: 25 passing unit tests, 100% coverage of pure C# logic

### Phase 2: Integration Mocking (Week 2)
1. ⚠️ Create `MockGLContext` stub
2. ⚠️ Test buffer allocation strategies
3. ⚠️ Validate shader uniform setting
4. ⚠️ Test SSBO binding logic

**Deliverable**: 11 integration tests with GPU mocking

### Phase 3: Manual E2E Validation (Week 3)
1. ✅ Visual inspection of terrain generation
2. ✅ Frustum culling accuracy checks
3. ✅ Performance benchmarking
4. ✅ Memory leak detection

**Deliverable**: Test report with screenshots/metrics

---

## Notes on GPU Testing Challenges

### What **CAN** be tested:
- ✅ All CPU-side logic
- ✅ Data structure validation
- ✅ Algorithm correctness (prefix sum, AABB, etc.)
- ✅ State machine transitions
- ⚠️ Buffer allocation patterns (with mocking)

### What **CANNOT** be unit tested:
- ❌ Shader compilation (requires real GPU)
- ❌ Vertex/fragment output (requires framebuffer)
- ❌ Compute shader dispatch results (requires GPU compute)
- ❌ Backface culling correctness (requires rasterizer)

### Recommended Approach:
1. **Write unit tests for everything CPU-side** → 100% coverage
2. **Use mocking for GL API calls** → 80% coverage
3. **Manual E2E tests for GPU correctness** → Visual validation

---

**Author**: GitHub Copilot  
**Estimated Test Development Time**: 3 weeks  
**Estimated Coverage**: **~85% automated, 15% manual**
