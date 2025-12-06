using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.Common.LightingTestHelpers;

namespace SpyroGame.Tests.Collision;

/// <summary>
/// Tests for collision span rebuild after block edits.
/// Verifies that both solid and non-solid blocks are properly tracked.
/// </summary>
public class CollisionRebuildTests
{
    [Fact]
    public void RebuildColumnFromVoxelData_IncludesTorches()
    {
        // Arrange
        var chunk = CreateAirChunk(chunkIndex: 0);
        var collision = new CollisionManager();
        
        // Place a torch at y=50
        chunk.SetBlock(8, 50, 8, BlockId.Torch);
        
        // Act - rebuild collision from voxel data
        collision.RebuildColumnFromVoxelData(0, 8, 8, chunk);
        
        // Assert - torch should be in collision data for picking
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 55f, 8.5f),  // Start above
            new OpenTK.Mathematics.Vector3(0, -1, 0),         // Look down
            10f,
            out var hitPoint,
            out var blockPos,
            out var normal,
            out var block,
            forPicking: true);
        
        Assert.True(found, "Should find torch via raycast");
        Assert.Equal(BlockId.Torch, block);
        Assert.Equal(50, blockPos.Y);
    }

    [Fact]
    public void RebuildColumnFromVoxelData_TorchNotSolidForCollision()
    {
        // Arrange
        var chunk = CreateAirChunk(chunkIndex: 0);
        var collision = new CollisionManager();
        
        // Place a torch at y=50
        chunk.SetBlock(8, 50, 8, BlockId.Torch);
        
        // Act
        collision.RebuildColumnFromVoxelData(0, 8, 8, chunk);
        
        // Assert - torch should NOT block collision (not solid)
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 55f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            10f,
            out _, out _, out _, out _,
            forPicking: false);  // Collision mode, not picking
        
        Assert.False(found, "Torch should not be solid for collision");
    }

    [Fact]
    public void RebuildColumnFromVoxelData_MixedSolidAndNonSolid()
    {
        // Arrange
        var chunk = CreateAirChunk(chunkIndex: 0);
        var collision = new CollisionManager();
        
        // Create a column with stone, torch on top
        for (var y = 40; y < 50; y++)
            chunk.SetBlock(8, y, 8, BlockId.Stone);
        chunk.SetBlock(8, 50, 8, BlockId.Torch);
        
        // Act
        collision.RebuildColumnFromVoxelData(0, 8, 8, chunk);
        
        // Assert - picking from above should find torch first
        var foundPicking = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 55f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            20f,
            out _, out var blockPosPick, out _, out var blockPick,
            forPicking: true);
        
        Assert.True(foundPicking);
        Assert.Equal(BlockId.Torch, blockPick);
        Assert.Equal(50, blockPosPick.Y);
        
        // Assert - collision should find stone (torch is not solid)
        var foundCollision = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 55f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            20f,
            out _, out var blockPosCol, out _, out var blockCol,
            forPicking: false);
        
        Assert.True(foundCollision);
        Assert.Equal(BlockId.Stone, blockCol);
        Assert.Equal(49, blockPosCol.Y);  // Top of stone column
    }

    [Fact]
    public void RebuildColumnFromVoxelData_AfterPlacingTorch_StillPickable()
    {
        // Arrange - simulate the bug scenario
        var chunk = CreateAirChunk(chunkIndex: 0);
        var collision = new CollisionManager();
        
        // Initial terrain generation would have set up collision data
        // Simulate by setting up some stone
        for (var y = 40; y < 50; y++)
            chunk.SetBlock(8, y, 8, BlockId.Stone);
        collision.RebuildColumnFromVoxelData(0, 8, 8, chunk);
        
        // Act - place torch (simulating block edit)
        chunk.SetBlock(8, 50, 8, BlockId.Torch);
        collision.RebuildColumnFromVoxelData(0, 8, 8, chunk);
        
        // Assert - torch should still be pickable
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 55f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            10f,
            out _, out _, out _, out var block,
            forPicking: true);
        
        Assert.True(found, "Torch should be pickable after placement");
        Assert.Equal(BlockId.Torch, block);
    }

    [Fact]
    public void RebuildColumnFromVoxelData_AfterBreakingBlock_TorchStillPickable()
    {
        // Arrange
        var chunk = CreateAirChunk(chunkIndex: 0);
        var collision = new CollisionManager();
        
        // Set up: stone with torch on top
        for (var y = 40; y < 50; y++)
            chunk.SetBlock(8, y, 8, BlockId.Stone);
        chunk.SetBlock(8, 50, 8, BlockId.Torch);
        collision.RebuildColumnFromVoxelData(0, 8, 8, chunk);
        
        // Act - break a stone block nearby (different column)
        chunk.SetBlock(7, 45, 8, BlockId.Air);
        collision.RebuildColumnFromVoxelData(0, 7, 8, chunk);
        
        // Assert - torch should STILL be pickable (different column shouldn't affect it)
        var found = collision.Raycast(
            new OpenTK.Mathematics.Vector3(8.5f, 55f, 8.5f),
            new OpenTK.Mathematics.Vector3(0, -1, 0),
            10f,
            out _, out _, out _, out var block,
            forPicking: true);
        
        Assert.True(found, "Torch should still be pickable");
        Assert.Equal(BlockId.Torch, block);
    }
}
