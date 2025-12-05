using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.LightingTestHelpers;

namespace SpyroGame.Tests;

/// <summary>
/// Integration tests for block light placement that simulate the actual game flow.
/// These tests verify lighting works at various positions: center, boundary, corner, etc.
/// </summary>
public class BlockLightPlacementIntegrationTests
{
    /// <summary>
    /// Simulates placing a torch using the same flow as ApplyBlockEdit.
    /// </summary>
    private static HashSet<int> SimulateTorchPlacement(
        Dictionary<int, ChunkData> chunks,
        int chunkIdx, int localX, int localY, int localZ)
    {
        var provider = CreateChunkProvider(chunks);
        
        if (!chunks.TryGetValue(chunkIdx, out var chunkData))
            return new HashSet<int>();
            
        // Set the torch block first
        chunkData.SetBlock(localX, localY, localZ, BlockId.Torch);
        
        // Then propagate light (torch emits 14)
        var affected = LightingCalculator.AddBlockLight(
            chunkIdx, localX, localY, localZ, 14, provider);
            
        return affected;
    }
    
    /// <summary>
    /// Simulates placing an opaque block using the same flow as ApplyBlockEdit.
    /// </summary>
    private static HashSet<int> SimulateOpaqueBlockPlacement(
        Dictionary<int, ChunkData> chunks,
        int chunkIdx, int localX, int localY, int localZ)
    {
        var provider = CreateChunkProvider(chunks);
        
        if (!chunks.TryGetValue(chunkIdx, out var chunkData))
            return new HashSet<int>();
        
        // Get current light before placing
        var lightIndex = localY * VoxelHelper.ChunkSideSizeSquare + localZ * VoxelHelper.ChunkSideSize + localX;
        var oldSkyLight = chunkData.LightData[lightIndex] & 0xF;
        var oldBlockLight = (chunkData.LightData[lightIndex] >> 4) & 0xF;
        
        // Place stone block
        chunkData.SetBlock(localX, localY, localZ, BlockId.Stone);
        
        // If there was light, we need to recalculate
        if (oldSkyLight > 0 || oldBlockLight > 0)
        {
            return LightingCalculator.RecalculateLightingAroundBlock(
                chunkIdx, localX, localY, localZ, provider);
        }
        
        return new HashSet<int> { chunkIdx };
    }
    
    // ========================================================================
    // TORCH PLACEMENT AT CENTER OF CHUNK
    // ========================================================================
    
    [Fact]
    public void Torch_PlacedAtChunkCenter_LightsArea()
    {
        // Arrange
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateAirChunk(chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        
        // Act - Place torch at center (8, 200, 8)
        SimulateTorchPlacement(chunks, chunkIdx, 8, 200, 8);
        
        // Assert - Light should propagate in all directions
        Assert.Equal(14, GetBlockLight(chunk, 8, 200, 8));   // Source
        Assert.Equal(13, GetBlockLight(chunk, 9, 200, 8));   // +X
        Assert.Equal(13, GetBlockLight(chunk, 7, 200, 8));   // -X
        Assert.Equal(13, GetBlockLight(chunk, 8, 201, 8));   // +Y
        Assert.Equal(13, GetBlockLight(chunk, 8, 199, 8));   // -Y
        Assert.Equal(13, GetBlockLight(chunk, 8, 200, 9));   // +Z
        Assert.Equal(13, GetBlockLight(chunk, 8, 200, 7));   // -Z
    }
    
    // ========================================================================
    // TORCH PLACEMENT AT CHUNK BOUNDARIES
    // ========================================================================
    
    [Theory]
    [InlineData(15, 8)]   // Right boundary (X=15)
    [InlineData(0, 8)]    // Left boundary (X=0)
    [InlineData(8, 15)]   // Front boundary (Z=15)
    [InlineData(8, 0)]    // Back boundary (Z=0)
    public void Torch_PlacedAtBoundary_PropagatesAcrossChunks(int localX, int localZ)
    {
        // Arrange - Create center chunk and all 4 neighbors
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        var leftIdx = GetChunkIndex(9, 10);
        var frontIdx = GetChunkIndex(10, 11);
        var backIdx = GetChunkIndex(10, 9);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, CreateAirChunk(centerIdx) },
            { rightIdx, CreateAirChunk(rightIdx) },
            { leftIdx, CreateAirChunk(leftIdx) },
            { frontIdx, CreateAirChunk(frontIdx) },
            { backIdx, CreateAirChunk(backIdx) }
        };
        
        // Act - Place torch at boundary position
        var affected = SimulateTorchPlacement(chunks, centerIdx, localX, 200, localZ);
        
        // Assert - Center chunk definitely affected
        Assert.Contains(centerIdx, affected);
        
        // Check that the appropriate neighbor is affected
        if (localX == 15) Assert.Contains(rightIdx, affected);
        if (localX == 0) Assert.Contains(leftIdx, affected);
        if (localZ == 15) Assert.Contains(frontIdx, affected);
        if (localZ == 0) Assert.Contains(backIdx, affected);
        
        // Verify light at source
        Assert.Equal(14, GetBlockLight(chunks[centerIdx], localX, 200, localZ));
    }
    
    [Fact]
    public void Torch_PlacedAtRightBoundary_NeighborReceivesLight()
    {
        // Arrange
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        
        var centerChunk = CreateAirChunk(centerIdx);
        var rightChunk = CreateAirChunk(rightIdx);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, centerChunk },
            { rightIdx, rightChunk }
        };
        
        // Act - Place torch at X=15 (right boundary)
        SimulateTorchPlacement(chunks, centerIdx, 15, 200, 8);
        
        // Assert
        Assert.Equal(14, GetBlockLight(centerChunk, 15, 200, 8));  // Source
        Assert.Equal(13, GetBlockLight(centerChunk, 14, 200, 8));  // -X in center
        Assert.Equal(13, GetBlockLight(rightChunk, 0, 200, 8));    // +X in right neighbor
        Assert.Equal(12, GetBlockLight(rightChunk, 1, 200, 8));    // Further into neighbor
    }
    
    [Fact]
    public void Torch_PlacedAtLeftBoundary_NeighborReceivesLight()
    {
        // Arrange
        var centerIdx = GetChunkIndex(10, 10);
        var leftIdx = GetChunkIndex(9, 10);
        
        var centerChunk = CreateAirChunk(centerIdx);
        var leftChunk = CreateAirChunk(leftIdx);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, centerChunk },
            { leftIdx, leftChunk }
        };
        
        // Act - Place torch at X=0 (left boundary)
        SimulateTorchPlacement(chunks, centerIdx, 0, 200, 8);
        
        // Assert
        Assert.Equal(14, GetBlockLight(centerChunk, 0, 200, 8));    // Source
        Assert.Equal(13, GetBlockLight(centerChunk, 1, 200, 8));    // +X in center
        Assert.Equal(13, GetBlockLight(leftChunk, 15, 200, 8));     // -X in left neighbor
        Assert.Equal(12, GetBlockLight(leftChunk, 14, 200, 8));     // Further into neighbor
    }
    
    // ========================================================================
    // TORCH PLACEMENT AT CORNERS
    // ========================================================================
    
    [Theory]
    [InlineData(15, 15)]  // Front-right corner
    [InlineData(0, 15)]   // Front-left corner
    [InlineData(15, 0)]   // Back-right corner
    [InlineData(0, 0)]    // Back-left corner
    public void Torch_PlacedAtCorner_PropagatesTo3Chunks(int localX, int localZ)
    {
        // Arrange - Create all 9 chunks (center + 8 neighbors)
        var chunks = new Dictionary<int, ChunkData>();
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var idx = GetChunkIndex(10 + dx, 10 + dz);
                chunks[idx] = CreateAirChunk(idx);
            }
        }
        
        var centerIdx = GetChunkIndex(10, 10);
        
        // Act - Place torch at corner
        var affected = SimulateTorchPlacement(chunks, centerIdx, localX, 200, localZ);
        
        // Assert - At least 3 chunks should be affected (center + 2 adjacent)
        Assert.True(affected.Count >= 3, $"Expected at least 3 affected chunks, got {affected.Count}");
        Assert.Contains(centerIdx, affected);
        
        // Verify light at source
        Assert.Equal(14, GetBlockLight(chunks[centerIdx], localX, 200, localZ));
    }
    
    [Fact]
    public void Torch_PlacedAtFrontRightCorner_PropagatesCorrectly()
    {
        // Arrange
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        var frontIdx = GetChunkIndex(10, 11);
        var diagonalIdx = GetChunkIndex(11, 11);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, CreateAirChunk(centerIdx) },
            { rightIdx, CreateAirChunk(rightIdx) },
            { frontIdx, CreateAirChunk(frontIdx) },
            { diagonalIdx, CreateAirChunk(diagonalIdx) }
        };
        
        // Act - Place torch at front-right corner (15, 200, 15)
        SimulateTorchPlacement(chunks, centerIdx, 15, 200, 15);
        
        // Assert
        Assert.Equal(14, GetBlockLight(chunks[centerIdx], 15, 200, 15));   // Source
        Assert.Equal(13, GetBlockLight(chunks[rightIdx], 0, 200, 15));     // Right neighbor
        Assert.Equal(13, GetBlockLight(chunks[frontIdx], 15, 200, 0));     // Front neighbor
        
        // Diagonal should get light through the adjacent chunks
        Assert.True(GetBlockLight(chunks[diagonalIdx], 0, 200, 0) > 0, 
            "Diagonal chunk should receive light");
    }
    
    // ========================================================================
    // TORCH PLACEMENT NEAR BOUNDARY (1 BLOCK FROM EDGE)
    // ========================================================================
    
    [Theory]
    [InlineData(14, 8)]   // 1 block from right boundary
    [InlineData(1, 8)]    // 1 block from left boundary
    [InlineData(8, 14)]   // 1 block from front boundary
    [InlineData(8, 1)]    // 1 block from back boundary
    public void Torch_PlacedNearBoundary_PropagatesAcrossChunks(int localX, int localZ)
    {
        // Arrange
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        var leftIdx = GetChunkIndex(9, 10);
        var frontIdx = GetChunkIndex(10, 11);
        var backIdx = GetChunkIndex(10, 9);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, CreateAirChunk(centerIdx) },
            { rightIdx, CreateAirChunk(rightIdx) },
            { leftIdx, CreateAirChunk(leftIdx) },
            { frontIdx, CreateAirChunk(frontIdx) },
            { backIdx, CreateAirChunk(backIdx) }
        };
        
        // Act
        var affected = SimulateTorchPlacement(chunks, centerIdx, localX, 200, localZ);
        
        // Assert - Source has light
        Assert.Equal(14, GetBlockLight(chunks[centerIdx], localX, 200, localZ));
        
        // The boundary block in center should have light level 13
        if (localX == 14) Assert.Equal(13, GetBlockLight(chunks[centerIdx], 15, 200, localZ));
        if (localX == 1) Assert.Equal(13, GetBlockLight(chunks[centerIdx], 0, 200, localZ));
        if (localZ == 14) Assert.Equal(13, GetBlockLight(chunks[centerIdx], localX, 200, 15));
        if (localZ == 1) Assert.Equal(13, GetBlockLight(chunks[centerIdx], localX, 200, 0));
        
        // Neighbor should have light level 12 at boundary
        if (localX == 14) Assert.Equal(12, GetBlockLight(chunks[rightIdx], 0, 200, localZ));
        if (localX == 1) Assert.Equal(12, GetBlockLight(chunks[leftIdx], 15, 200, localZ));
        if (localZ == 14) Assert.Equal(12, GetBlockLight(chunks[frontIdx], localX, 200, 0));
        if (localZ == 1) Assert.Equal(12, GetBlockLight(chunks[backIdx], localX, 200, 15));
    }
    
    // ========================================================================
    // PLACING OPAQUE BLOCK IN LIT AREA
    // ========================================================================
    
    [Fact]
    public void OpaqueBlock_PlacedInLitArea_BlocksLight()
    {
        // Arrange - Create chunk with torch
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateAirChunk(chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        
        // Place torch first
        SimulateTorchPlacement(chunks, chunkIdx, 5, 200, 8);
        
        // Verify light propagated
        Assert.Equal(14, GetBlockLight(chunk, 5, 200, 8));
        Assert.Equal(13, GetBlockLight(chunk, 6, 200, 8));
        Assert.Equal(12, GetBlockLight(chunk, 7, 200, 8));
        Assert.Equal(11, GetBlockLight(chunk, 8, 200, 8));
        
        // Act - Place stone block between torch and far area
        SimulateOpaqueBlockPlacement(chunks, chunkIdx, 6, 200, 8);
        
        // Assert - Light should be recalculated
        // The torch should still emit light
        Assert.Equal(14, GetBlockLight(chunk, 5, 200, 8));
        // But the stone block has no light
        Assert.Equal(0, GetBlockLight(chunk, 6, 200, 8));
    }
    
    [Fact]
    public void OpaqueBlock_PlacedAtBoundary_RecalculatesNeighbor()
    {
        // Arrange - Create two chunks with light propagating between them
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        
        var centerChunk = CreateAirChunk(centerIdx);
        var rightChunk = CreateAirChunk(rightIdx);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, centerChunk },
            { rightIdx, rightChunk }
        };
        
        // Place torch at right boundary
        SimulateTorchPlacement(chunks, centerIdx, 15, 200, 8);
        
        // Verify light in neighbor
        Assert.True(GetBlockLight(rightChunk, 0, 200, 8) > 0, "Right chunk should have light");
        
        // Act - Place stone block at the boundary in right chunk
        SimulateOpaqueBlockPlacement(chunks, rightIdx, 0, 200, 8);
        
        // Assert - Light should be blocked
        Assert.Equal(0, GetBlockLight(rightChunk, 0, 200, 8));
    }
    
    // ========================================================================
    // MULTIPLE TORCHES
    // ========================================================================
    
    [Fact]
    public void MultipleTorches_LightCombines()
    {
        // Arrange
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateAirChunk(chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        
        // Act - Place two torches on opposite sides of a point
        SimulateTorchPlacement(chunks, chunkIdx, 4, 200, 8);  // Light at x=8 will be 10
        SimulateTorchPlacement(chunks, chunkIdx, 12, 200, 8); // Light at x=8 will be 10 from this one too
        
        // Assert - The midpoint should have max(10, 10) = 10 light
        Assert.Equal(10, GetBlockLight(chunk, 8, 200, 8));
        
        // Each torch position should have full light
        Assert.Equal(14, GetBlockLight(chunk, 4, 200, 8));
        Assert.Equal(14, GetBlockLight(chunk, 12, 200, 8));
    }
    
    // ========================================================================
    // NIBBLE ORDER VERIFICATION
    // ========================================================================
    
    [Fact]
    public void LightData_NibbleOrder_IsCorrect()
    {
        // This test verifies the nibble order convention:
        // Low nibble (& 0xF) = sky light
        // High nibble (>> 4) = block light
        
        var chunk = CreateAirChunk(0);
        var testY = 200;
        var index = testY * VoxelHelper.ChunkSideSizeSquare + 8 * VoxelHelper.ChunkSideSize + 8;
        
        // Set sky light to 10 and block light to 5
        chunk.LightData[index] = (byte)((5 << 4) | 10);  // 0x5A
        
        // Verify using our helper functions
        Assert.Equal(10, GetSkyLight(chunk, 8, testY, 8));
        Assert.Equal(5, GetBlockLight(chunk, 8, testY, 8));
        
        // Verify the raw byte
        Assert.Equal(0x5A, chunk.LightData[index]);
    }
    
    [Fact]
    public void AddBlockLight_DoesNotCorruptSkyLight()
    {
        // Arrange - Create chunk with pre-existing sky light
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateAirChunk(chunkIdx);
        
        // Manually set some sky light values
        SetSkyLight(chunk, 8, 200, 8, 15);
        SetSkyLight(chunk, 9, 200, 8, 14);
        SetSkyLight(chunk, 10, 200, 8, 13);
        
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        
        // Act - Add block light
        SimulateTorchPlacement(chunks, chunkIdx, 8, 200, 8);
        
        // Assert - Sky light should be preserved
        Assert.Equal(15, GetSkyLight(chunk, 8, 200, 8));
        Assert.Equal(14, GetSkyLight(chunk, 9, 200, 8));
        Assert.Equal(13, GetSkyLight(chunk, 10, 200, 8));
        
        // And block light should be set
        Assert.Equal(14, GetBlockLight(chunk, 8, 200, 8));
        Assert.Equal(13, GetBlockLight(chunk, 9, 200, 8));
    }
}
