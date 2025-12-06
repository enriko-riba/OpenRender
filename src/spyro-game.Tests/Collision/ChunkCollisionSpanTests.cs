using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.Common.LightingTestHelpers;

namespace SpyroGame.Tests.Collision;

/// <summary>
/// Tests for collision data and span handling, specifically:
/// - Proper span creation through CollisionManager
/// - Raycast accuracy for block picking
/// - Edge cases with multiple spans per column
/// </summary>
public class ChunkCollisionSpanTests
{
    [Fact]
    public void CollisionManager_SingleBlockSpan_CorrectRaycast()
    {
        // Arrange - single block at y=100
        var voxelData = CreateAirChunk();
        voxelData.SetBlock(5, 100, 5, BlockId.Stone);
        
        var collision = new CollisionManager();
        collision.RebuildColumnFromVoxelData(0, 5, 5, voxelData);
        
        // Act - raycast down
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(5.5f, 105f, 5.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            10f,
            out _, out var blockPos, out _, out var block,
            forPicking: true);
        
        // Assert - should hit the single block
        Assert.True(found);
        Assert.Equal(100, blockPos.Y);
        Assert.Equal(BlockId.Stone, block);
    }

    [Fact]
    public void CollisionManager_MultipleSpans_RaycastHitsCorrectSpan()
    {
        // Arrange - column with gaps (multiple spans)
        var voxelData = CreateAirChunk();
        
        // First span: stone from y=10-19
        for (var y = 10; y < 20; y++)
            voxelData.SetBlock(8, y, 8, BlockId.Stone);
        
        // Second span: dirt from y=30-39
        for (var y = 30; y < 40; y++)
            voxelData.SetBlock(8, y, 8, BlockId.Dirt);
        
        // Third span: sand from y=50-54
        for (var y = 50; y < 55; y++)
            voxelData.SetBlock(8, y, 8, BlockId.Sand);
        
        var collision = new CollisionManager();
        collision.RebuildColumnFromVoxelData(0, 8, 8, voxelData);
        
        // Act - raycast from above third span
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 60f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            20f,
            out _, out var blockPos, out _, out var block,
            forPicking: true);
        
        // Assert - should hit top of third span (sand at y=54)
        Assert.True(found);
        Assert.Equal(54, blockPos.Y);
        Assert.Equal(BlockId.Sand, block);
    }

    [Fact]
    public void CollisionManager_Raycast_UsesInclusiveEndY()
    {
        // Arrange - setup that would fail with exclusive EndY
        var voxelData = CreateAirChunk();
        
        // Stone floor from y=50 to y=60 (inclusive)
        for (var y = 50; y <= 60; y++)
            voxelData.SetBlock(8, y, 8, BlockId.Stone);
        
        var collision = new CollisionManager();
        collision.RebuildColumnFromVoxelData(0, 8, 8, voxelData);
        
        // Act - raycast down to hit the TOP of the span (y=60)
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 65f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            10f,
            out _, out var blockPos, out _, out var block,
            forPicking: true);
        
        // Assert - should hit y=60 (the top of the span)
        // With the bug (exclusive EndY), it would miss or hit wrong block
        Assert.True(found);
        Assert.Equal(60, blockPos.Y);
        Assert.Equal(BlockId.Stone, block);
    }

    [Fact]
    public void CollisionManager_Raycast_HitsExactTopBlock()
    {
        // This tests the specific bug scenario: raycast hitting exactly the top block of a span
        var voxelData = CreateAirChunk();
        
        // Single layer of stone at y=100
        for (var x = 0; x < 16; x++)
        for (var z = 0; z < 16; z++)
            voxelData.SetBlock(x, 100, z, BlockId.Stone);
        
        var collision = new CollisionManager();
        // Rebuild all columns
        for (var x = 0; x < 16; x++)
        for (var z = 0; z < 16; z++)
            collision.RebuildColumnFromVoxelData(0, x, z, voxelData);
        
        // Raycast from above, should hit exactly at y=100
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 105f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            10f,
            out _, out var blockPos, out _, out var block,
            forPicking: true);
        
        Assert.True(found);
        Assert.Equal(100, blockPos.Y);
        Assert.Equal(BlockId.Stone, block);
    }

    [Fact]
    public void CollisionManager_RebuildColumn_TorchPickable()
    {
        // Arrange - torch on top of stone
        var voxelData = CreateAirChunk();
        voxelData.SetBlock(8, 50, 8, BlockId.Stone);
        voxelData.SetBlock(8, 51, 8, BlockId.Torch);
        
        var collision = new CollisionManager();
        collision.RebuildColumnFromVoxelData(0, 8, 8, voxelData);
        
        // Act - pick torch from above
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 55f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            10f,
            out _, out var blockPos, out _, out var block,
            forPicking: true);
        
        // Assert - should find the torch
        Assert.True(found);
        Assert.Equal(51, blockPos.Y);
        Assert.Equal(BlockId.Torch, block);
    }

    [Fact]
    public void CollisionManager_RebuildColumn_TorchNotSolidForCollision()
    {
        // Arrange - torch above solid stone
        var voxelData = CreateAirChunk();
        voxelData.SetBlock(8, 50, 8, BlockId.Stone);
        voxelData.SetBlock(8, 51, 8, BlockId.Torch);
        
        var collision = new CollisionManager();
        collision.RebuildColumnFromVoxelData(0, 8, 8, voxelData);
        
        // Act - collision check (not picking) from above
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 55f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            10f,
            out _, out var blockPos, out _, out var block,
            forPicking: false);  // Collision mode, not picking
        
        // Assert - should find the stone, not the torch
        Assert.True(found);
        Assert.Equal(50, blockPos.Y);
        Assert.Equal(BlockId.Stone, block);
    }

    [Fact]
    public void CollisionManager_RebuildColumn_MixedBlockTypes()
    {
        // Arrange - column with different block types adjacent
        var voxelData = CreateAirChunk();
        
        // Stone layer
        voxelData.SetBlock(5, 50, 5, BlockId.Stone);
        voxelData.SetBlock(5, 51, 5, BlockId.Stone);
        
        // Dirt layer on top
        voxelData.SetBlock(5, 52, 5, BlockId.Dirt);
        voxelData.SetBlock(5, 53, 5, BlockId.Dirt);
        
        // Grass on top
        voxelData.SetBlock(5, 54, 5, BlockId.Grass);
        
        var collision = new CollisionManager();
        collision.RebuildColumnFromVoxelData(0, 5, 5, voxelData);
        
        // Act - pick from above
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(5.5f, 60f, 5.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            15f,
            out _, out var blockPos, out _, out var block,
            forPicking: true);
        
        // Assert - should hit the top grass block
        Assert.True(found);
        Assert.Equal(54, blockPos.Y);
        Assert.Equal(BlockId.Grass, block);
    }
}
