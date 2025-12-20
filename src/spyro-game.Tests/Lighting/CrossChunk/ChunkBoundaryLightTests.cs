using SpyroGame.World;
using SpyroGame.World.Registry;
using Xunit;
using static SpyroGame.Tests.Common.LightingTestHelpers;

namespace SpyroGame.Tests.Lighting.CrossChunk;

/// <summary>
/// Tests for light propagation across chunk boundaries.
/// These tests cover the critical case of light bleeding/stopping at chunk borders.
/// </summary>
public class ChunkBoundaryLightTests
{
    private const int CenterChunkX = 300;
    private const int CenterChunkZ = 300;

    private Dictionary<int, ChunkData> CreateChunkGrid()
    {
        // Create a 3x3 grid of chunks centered at (CenterChunkX, CenterChunkZ)
        var chunks = new Dictionary<int, ChunkData>();
        
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var chunkIndex = GetChunkIndex(CenterChunkX + dx, CenterChunkZ + dz);
                chunks[chunkIndex] = CreateAirChunk(chunkIndex);
            }
        }
        
        return chunks;
    }

    [Fact]
    public void ChunkBoundary_LightPropagatesFromLeftToRight()
    {
        // Arrange
        var chunks = CreateChunkGrid();
        var leftChunkIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        
        // Place torch at right edge of left chunk (x=15)
        PlaceTorch(chunks[leftChunkIdx], 15, 200, 8);
        
        // Calculate lighting for left chunk
        LightingCalculator.CalculateLighting(chunks[leftChunkIdx]);
        LightingCalculator.CalculateLighting(chunks[centerChunkIdx]);
        
        // Act - Propagate light across boundary
        LightingCalculator.PropagateNeighborLight(chunks[leftChunkIdx], chunks[centerChunkIdx], 1, 0);

        // Assert - Light should propagate into center chunk at x=0
        Assert.True(GetBlockLight(chunks[centerChunkIdx], 0, 200, 8) > 0, 
            "Light should propagate from left chunk to center chunk at boundary");
        Assert.Equal(13, GetBlockLight(chunks[centerChunkIdx], 0, 200, 8));
    }

    [Fact]
    public void ChunkBoundary_LightPropagatesFromRightToLeft()
    {
        // Arrange
        var chunks = CreateChunkGrid();
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        var rightChunkIdx = GetChunkIndex(CenterChunkX + 1, CenterChunkZ);
        
        // Place torch at left edge of right chunk (x=0)
        PlaceTorch(chunks[rightChunkIdx], 0, 200, 8);
        
        // Calculate lighting
        LightingCalculator.CalculateLighting(chunks[rightChunkIdx]);
        LightingCalculator.CalculateLighting(chunks[centerChunkIdx]);
        
        // Act - Propagate light across boundary
        LightingCalculator.PropagateNeighborLight(chunks[centerChunkIdx], chunks[rightChunkIdx], 1, 0);

        // Assert
        Assert.True(GetBlockLight(chunks[centerChunkIdx], 15, 200, 8) > 0,
            "Light should propagate from right chunk to center chunk at boundary");
    }

    [Fact]
    public void ChunkBoundary_LightPropagatesFromBackToFront()
    {
        // Arrange
        var chunks = CreateChunkGrid();
        var backChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ - 1);
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        
        // Place torch at front edge of back chunk (z=15)
        PlaceTorch(chunks[backChunkIdx], 8, 200, 15);
        
        // Calculate lighting
        LightingCalculator.CalculateLighting(chunks[backChunkIdx]);
        LightingCalculator.CalculateLighting(chunks[centerChunkIdx]);
        
        // Act
        LightingCalculator.PropagateNeighborLight(chunks[backChunkIdx], chunks[centerChunkIdx], 0, 1);

        // Assert
        Assert.True(GetBlockLight(chunks[centerChunkIdx], 8, 200, 0) > 0,
            "Light should propagate from back chunk to center chunk at boundary");
    }

    [Fact]
    public void ChunkBoundary_LightPropagatesFromFrontToBack()
    {
        // Arrange
        var chunks = CreateChunkGrid();
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        var frontChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ + 1);
        
        // Place torch at back edge of front chunk (z=0)
        PlaceTorch(chunks[frontChunkIdx], 8, 200, 0);
        
        // Calculate lighting
        LightingCalculator.CalculateLighting(chunks[frontChunkIdx]);
        LightingCalculator.CalculateLighting(chunks[centerChunkIdx]);
        
        // Act
        LightingCalculator.PropagateNeighborLight(chunks[centerChunkIdx], chunks[frontChunkIdx], 0, 1);

        // Assert
        Assert.True(GetBlockLight(chunks[centerChunkIdx], 8, 200, 15) > 0,
            "Light should propagate from front chunk to center chunk at boundary");
    }

    [Fact]
    public void ChunkBoundary_LightStopsAtOpaqueBlockOnBoundary()
    {
        // Arrange
        var chunks = CreateChunkGrid();
        var leftChunkIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        
        // Place torch near right edge of left chunk
        PlaceTorch(chunks[leftChunkIdx], 14, 200, 8);
        
        // Place stone wall at boundary - create a 3x3 wall in Y-Z plane at x=15
        // This prevents light from going around via Y or Z directions
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                chunks[leftChunkIdx].SetBlock(15, 200 + dy, 8 + dz, BlockId.Stone);
            }
        }
        
        // Also block at x=0 in center chunk to prevent any leakage
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                chunks[centerChunkIdx].SetBlock(0, 200 + dy, 8 + dz, BlockId.Stone);
            }
        }
        
        // Calculate lighting
        LightingCalculator.CalculateLighting(chunks[leftChunkIdx]);
        LightingCalculator.CalculateLighting(chunks[centerChunkIdx]);
        
        // Act
        LightingCalculator.PropagateNeighborLight(chunks[leftChunkIdx], chunks[centerChunkIdx], 1, 0);

        // Assert - Light should NOT propagate through the stone wall
        Assert.True(GetBlockLight(chunks[centerChunkIdx], 0, 200, 8) == 0,
            "Light should not propagate through opaque block at boundary");
    }

    [Fact]
    public void ChunkBoundary_SkyLightPropagatesAcrossBoundary()
    {
        // Arrange - Create chunks with different terrain
        var chunks = CreateChunkGrid();
        var leftChunkIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        
        // Left chunk is all air (has sky light)
        // Center chunk has a ceiling except at boundary
        for (var y = 201; y <= 210; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 1; x < VoxelHelper.ChunkSideSize; x++) // Leave x=0 open
                {
                    chunks[centerChunkIdx].SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }
        
        // Calculate lighting
        LightingCalculator.CalculateLighting(chunks[leftChunkIdx]);
        LightingCalculator.CalculateLighting(chunks[centerChunkIdx]);
        
        // Act
        LightingCalculator.PropagateNeighborLight(chunks[leftChunkIdx], chunks[centerChunkIdx], 1, 0);

        // Assert - Sky light should propagate into the opening at x=0
        Assert.True(GetSkyLight(chunks[centerChunkIdx], 0, 200, 8) > 0,
            "Sky light should propagate through opening at chunk boundary");
    }

    [Fact]
    public void ChunkBoundary_BidirectionalPropagation()
    {
        // Arrange - Torches on both sides near boundary
        var chunks = CreateChunkGrid();
        var leftChunkIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        
        // Torch at x=14 in left chunk (2 blocks from boundary)
        PlaceTorch(chunks[leftChunkIdx], 14, 200, 8);
        
        // Torch at x=2 in center chunk (2 blocks from boundary)
        PlaceTorch(chunks[centerChunkIdx], 2, 200, 8);
        
        // Calculate lighting
        LightingCalculator.CalculateLighting(chunks[leftChunkIdx]);
        LightingCalculator.CalculateLighting(chunks[centerChunkIdx]);
        
        // Act - Propagate in both directions
        LightingCalculator.PropagateNeighborLight(chunks[leftChunkIdx], chunks[centerChunkIdx], 1, 0);

        // Assert - Each chunk should receive light from the other
        Assert.True(GetBlockLight(chunks[leftChunkIdx], 15, 200, 8) > 0,
            "Left chunk boundary should receive light from center chunk");
        Assert.True(GetBlockLight(chunks[centerChunkIdx], 0, 200, 8) > 0,
            "Center chunk boundary should receive light from left chunk");
    }

    [Fact]
    public void ChunkBoundary_LightDecaysCorrectlyAcrossBoundary()
    {
        // Arrange
        var chunks = CreateChunkGrid();
        var leftChunkIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        
        // Place torch exactly at boundary
        PlaceTorch(chunks[leftChunkIdx], 15, 200, 8);
        
        // Calculate and propagate
        LightingCalculator.CalculateLighting(chunks[leftChunkIdx]);
        LightingCalculator.CalculateLighting(chunks[centerChunkIdx]);
        LightingCalculator.PropagateNeighborLight(chunks[leftChunkIdx], chunks[centerChunkIdx], 1, 0);

        // Assert - Light should decay correctly across boundary
        Assert.Equal(14, GetBlockLight(chunks[leftChunkIdx], 15, 200, 8)); // Torch
        Assert.Equal(13, GetBlockLight(chunks[centerChunkIdx], 0, 200, 8)); // One block away
        
        // Continue checking decay
        Assert.True(GetBlockLight(chunks[centerChunkIdx], 1, 200, 8) <= 12);
    }
}
