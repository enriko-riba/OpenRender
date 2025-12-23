using SpyroGame.World;
using SpyroGame.World.Registry;
using Xunit;

namespace SpyroGame.Tests.Generation;

/// <summary>
/// Tests for SurfaceHeights population during terrain generation.
/// These tests verify that ChunkData.SurfaceHeights is correctly populated
/// after generation, which is critical for meshing optimization.
/// </summary>
public class SurfaceHeightsTests
{
    [Fact]
    public void ChunkData_SetBlock_UpdatesSurfaceHeight()
    {
        // Arrange
        var chunk = new ChunkData { ChunkIndex = 0 };
        
        // Act - place a block
        chunk.SetBlock(5, 100, 5, BlockId.Stone);
        
        // Assert - surface height should be updated
        var columnIndex = 5 * VoxelHelper.ChunkSideSize + 5;
        Assert.Equal(100, chunk.SurfaceHeights[columnIndex]);
    }

    [Fact]
    public void ChunkData_SetBlock_TracksHighestBlock()
    {
        // Arrange
        var chunk = new ChunkData { ChunkIndex = 0 };
        
        // Act - place blocks at different heights in same column
        chunk.SetBlock(5, 50, 5, BlockId.Stone);
        chunk.SetBlock(5, 100, 5, BlockId.Dirt);
        chunk.SetBlock(5, 75, 5, BlockId.Sand);  // This should NOT lower the height
        
        // Assert - should track the highest (100)
        var columnIndex = 5 * VoxelHelper.ChunkSideSize + 5;
        Assert.Equal(100, chunk.SurfaceHeights[columnIndex]);
    }

    [Fact]
    public void ChunkData_RecalculateSurfaceHeights_FindsCorrectHeight()
    {
        // Arrange
        var chunk = new ChunkData { ChunkIndex = 0 };
        
        // Manually set voxel data without using SetBlock (simulates loading)
        var stoneIdx = chunk.GetOrAddPaletteEntry(BlockId.Stone);
        for (var y = 0; y <= 80; y++)
        {
            var voxelIdx = y * VoxelHelper.ChunkSideSizeSquare + 5 * VoxelHelper.ChunkSideSize + 5;
            chunk.VoxelData[voxelIdx] = stoneIdx;
        }
        
        // Act
        chunk.RecalculateSurfaceHeights();
        
        // Assert
        var columnIndex = 5 * VoxelHelper.ChunkSideSize + 5;
        Assert.Equal(80, chunk.SurfaceHeights[columnIndex]);
    }

    [Fact]
    public void ChunkData_RecalculateSurfaceHeights_EmptyColumn_ReturnsMinusOne()
    {
        // Arrange
        var chunk = new ChunkData { ChunkIndex = 0 };
        // Column at (3,3) is all air
        
        // Act
        chunk.RecalculateSurfaceHeights();
        
        // Assert - empty columns should have -1
        var columnIndex = 3 * VoxelHelper.ChunkSideSize + 3;
        Assert.Equal(-1, chunk.SurfaceHeights[columnIndex]);
    }

    [Fact]
    public void ChunkData_RecalculateSurfaceHeights_TallColumn_CorrectHeight()
    {
        // Arrange
        var chunk = new ChunkData { ChunkIndex = 0 };
        var stoneIdx = chunk.GetOrAddPaletteEntry(BlockId.Stone);
        
        // Fill column to y=300 (simulates tall mountain)
        for (var y = 0; y <= 300; y++)
        {
            var voxelIdx = y * VoxelHelper.ChunkSideSizeSquare + 8 * VoxelHelper.ChunkSideSize + 8;
            chunk.VoxelData[voxelIdx] = stoneIdx;
        }
        
        // Act
        chunk.RecalculateSurfaceHeights();
        
        // Assert
        var columnIndex = 8 * VoxelHelper.ChunkSideSize + 8;
        Assert.Equal(300, chunk.SurfaceHeights[columnIndex]);
    }

    [Fact]
    public void ChunkData_CloneTo_CopiesSurfaceHeights()
    {
        // Arrange
        var source = new ChunkData { ChunkIndex = 0 };
        source.SetBlock(5, 100, 5, BlockId.Stone);
        source.SetBlock(10, 200, 10, BlockId.Dirt);
        
        var target = new ChunkData { ChunkIndex = 0 };
        
        // Act
        source.CloneTo(target);
        
        // Assert
        var col1 = 5 * VoxelHelper.ChunkSideSize + 5;
        var col2 = 10 * VoxelHelper.ChunkSideSize + 10;
        Assert.Equal(100, target.SurfaceHeights[col1]);
        Assert.Equal(200, target.SurfaceHeights[col2]);
    }

    [Fact]
    public void ChunkData_MultipleColumns_IndependentHeights()
    {
        // Arrange
        var chunk = new ChunkData { ChunkIndex = 0 };
        
        // Act - set different heights in different columns
        chunk.SetBlock(0, 50, 0, BlockId.Stone);
        chunk.SetBlock(1, 100, 1, BlockId.Dirt);
        chunk.SetBlock(2, 150, 2, BlockId.Sand);
        chunk.SetBlock(15, 250, 15, BlockId.Grass);
        
        // Assert - each column should have independent height
        Assert.Equal(50, chunk.SurfaceHeights[0 * VoxelHelper.ChunkSideSize + 0]);
        Assert.Equal(100, chunk.SurfaceHeights[1 * VoxelHelper.ChunkSideSize + 1]);
        Assert.Equal(150, chunk.SurfaceHeights[2 * VoxelHelper.ChunkSideSize + 2]);
        Assert.Equal(250, chunk.SurfaceHeights[15 * VoxelHelper.ChunkSideSize + 15]);
    }

    [Fact]
    public void ChunkData_SurfaceHeights_UsedByMesher_WhenCorrect()
    {
        // This test verifies the contract: SurfaceHeights determines mesh iteration bounds
        // If SurfaceHeights is wrong, mesher would iterate wrong Y range
        
        // Arrange
        var chunk = new ChunkData { ChunkIndex = 0 };
        chunk.SetBlock(8, 64, 8, BlockId.Stone);
        
        // The mesher uses: maxY = Math.Max(surfaceHeight + 2, WaterLevel + 1)
        var columnIndex = 8 * VoxelHelper.ChunkSideSize + 8;
        var surfaceHeight = chunk.SurfaceHeights[columnIndex];
        
        // Assert - surface height should allow mesher to find the block
        Assert.True(surfaceHeight >= 64, $"SurfaceHeight {surfaceHeight} should be >= 64 to include the block");
    }
}
