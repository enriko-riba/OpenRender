using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.LightingTestHelpers;

namespace SpyroGame.Tests;

/// <summary>
/// Tests for light updates when blocks are removed at chunk boundaries.
/// This is critical for preventing dark faces on newly exposed neighbor blocks.
/// </summary>
public class BoundaryBlockRemovalLightTests
{
    private const int CenterChunkX = 300;
    private const int CenterChunkZ = 300;

    /// <summary>
    /// When a block is removed at a chunk boundary, the newly exposed air space
    /// should receive light from the chunk where it exists.
    /// This reproduces the bug where breaking a block on a chunk edge
    /// reveals a completely dark face on the neighbor chunk's block.
    /// </summary>
    [Fact]
    public void BoundaryBlockRemoval_NeighborFaceReceivesLight()
    {
        // Arrange - Create two chunks with a cave and torch
        var leftChunkIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var rightChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);

        // Create underground caves in both chunks
        var leftChunk = CreateCaveChunkWithSolidBoundary(leftChunkIdx, boundaryX: 15);
        var rightChunk = CreateCaveChunkWithSolidBoundary(rightChunkIdx, boundaryX: 0);

        // Place torch in left chunk's cave to provide light
        PlaceTorch(leftChunk, 10, 50, 8);

        // Initial lighting calculation
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Verify initial state: both chunks have solid blocks at boundary
        Assert.Equal(BlockId.Stone, leftChunk.GetBlock(15, 50, 8));
        Assert.Equal(BlockId.Stone, rightChunk.GetBlock(0, 50, 8));

        // Verify torch provides light
        Assert.Equal(14, GetBlockLight(leftChunk, 10, 50, 8));

        // Act - Remove the block at the left chunk's boundary (simulating player breaking block)
        leftChunk.SetBlock(15, 50, 8, BlockId.Air);

        // Recalculate lighting for the modified chunk
        LightingCalculator.CalculateLighting(leftChunk);

        // Now propagate light between chunks
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Assert - The removed block position should now have light from the torch
        var leftRemovedBlockLight = GetBlockLight(leftChunk, 15, 50, 8);
        Assert.True(leftRemovedBlockLight > 0, 
            $"Removed block position should have block light from torch. Got: {leftRemovedBlockLight}");
        
        // The torch is 5 blocks away from x=15, so light should be 14 - 5 = 9
        Assert.True(leftRemovedBlockLight >= 8 && leftRemovedBlockLight <= 10,
            $"Expected ~9 light at boundary, got: {leftRemovedBlockLight}");
    }

    /// <summary>
    /// When block light source exists near boundary and an opaque block is removed,
    /// the light should now propagate through to the neighbor chunk's air space,
    /// so that faces of opaque blocks can sample that light.
    /// </summary>
    [Fact]
    public void BoundaryBlockRemoval_BlockLightPropagates()
    {
        // Arrange - Create two chunks: left has torch in a room, right has a corridor
        var leftChunkIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var rightChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);

        var leftChunk = CreateAirChunk(leftChunkIdx);
        var rightChunk = CreateAirChunk(rightChunkIdx);

        // Create a completely enclosed underground room with opening at boundary
        CreateSolidChunkWithRoom(leftChunk, roomMinY: 48, roomMaxY: 53, roomZ: 8);
        CreateSolidChunkWithRoom(rightChunk, roomMinY: 48, roomMaxY: 53, roomZ: 8);

        // Clear the boundary between chunks so light can flow through
        // Left chunk: open x=15, Right chunk: open x=0 (tunnel through boundary)
        for (var y = 49; y <= 52; y++)
        {
            leftChunk.SetBlock(15, y, 8, BlockId.Air);
            rightChunk.SetBlock(0, y, 8, BlockId.Air);
        }

        // Place blocking wall at left chunk boundary (will be removed)
        leftChunk.SetBlock(15, 50, 8, BlockId.Stone);

        // Place torch in left chunk room
        PlaceTorch(leftChunk, 13, 50, 8);

        // Initial lighting
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Verify: torch light exists in left chunk
        var leftTorchLight = GetBlockLight(leftChunk, 13, 50, 8);
        Assert.Equal(14, leftTorchLight);

        // Before removal: right chunk boundary air has no/little light (blocked by stone)
        var rightBoundaryLightBefore = GetBlockLight(rightChunk, 0, 50, 8);
        
        // Act - Remove the stone wall at boundary
        leftChunk.SetBlock(15, 50, 8, BlockId.Air);
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Assert - Torch light should now propagate to neighbor's air space
        var leftBoundaryLight = GetBlockLight(leftChunk, 15, 50, 8);
        Assert.True(leftBoundaryLight > 0,
            $"Left boundary air should have light after wall removal. Got: {leftBoundaryLight}");

        var rightBoundaryLightAfter = GetBlockLight(rightChunk, 0, 50, 8);
        Assert.True(rightBoundaryLightAfter > rightBoundaryLightBefore || rightBoundaryLightAfter > 0,
            $"Right boundary air should receive light. Before: {rightBoundaryLightBefore}, After: {rightBoundaryLightAfter}");
    }

    /// <summary>
    /// Tests the cross-chunk update method that should be called when blocks are removed.
    /// This test is for the fix - will be enabled after implementing UpdateLightAfterBlockRemoval.
    /// </summary>
    // [Fact] - Uncomment when UpdateLightAfterBlockRemoval is implemented
    // public void UpdateNeighborLightAfterBlockRemoval_PropagatesCorrectly()
    // {
    //     ... test implementation uses method that doesn't exist yet ...
    // }

    /// <summary>
    /// Verify that removing a block at Z boundary also propagates light correctly.
    /// </summary>
    [Fact]
    public void BoundaryBlockRemoval_ZAxis_PropagatesCorrectly()
    {
        // Arrange
        var backChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ - 1);
        var frontChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);

        var backChunk = CreateAirChunk(backChunkIdx);
        var frontChunk = CreateAirChunk(frontChunkIdx);

        // Create solid chunks with a corridor at x=8 along Z
        CreateSolidChunkWithZRoom(backChunk, roomMinY: 48, roomMaxY: 53, roomX: 8);
        CreateSolidChunkWithZRoom(frontChunk, roomMinY: 48, roomMaxY: 53, roomX: 8);

        // Clear boundary positions so light can propagate
        for (var y = 49; y <= 52; y++)
        {
            backChunk.SetBlock(8, y, 15, BlockId.Air);
            frontChunk.SetBlock(8, y, 0, BlockId.Air);
        }

        // Place blocking wall and torch
        backChunk.SetBlock(8, 50, 15, BlockId.Stone);
        PlaceTorch(backChunk, 8, 50, 13);

        LightingCalculator.CalculateLighting(backChunk);
        LightingCalculator.CalculateLighting(frontChunk);
        LightingCalculator.PropagateNeighborLight(backChunk, frontChunk, 0, 1);

        // Verify blocked - front chunk boundary should have minimal light
        var frontBoundaryLightBefore = GetBlockLight(frontChunk, 8, 50, 0);

        // Act - Remove wall
        backChunk.SetBlock(8, 50, 15, BlockId.Air);
        LightingCalculator.CalculateLighting(backChunk);
        LightingCalculator.PropagateNeighborLight(backChunk, frontChunk, 0, 1);

        // Assert - back chunk boundary should have light
        var backBoundaryLight = GetBlockLight(backChunk, 8, 50, 15);
        Assert.True(backBoundaryLight > 0,
            $"Back chunk boundary air should have light. Got: {backBoundaryLight}");

        // Front chunk boundary air should receive light
        var frontBoundaryLightAfter = GetBlockLight(frontChunk, 8, 50, 0);
        Assert.True(frontBoundaryLightAfter > frontBoundaryLightBefore || frontBoundaryLightAfter > 0,
            $"Front chunk boundary should receive light. Before: {frontBoundaryLightBefore}, After: {frontBoundaryLightAfter}");
    }

    // Helper methods

    private static ChunkData CreateCaveChunkWithSolidBoundary(int chunkIdx, int boundaryX)
    {
        var chunk = new ChunkData { ChunkIndex = chunkIdx };

        // Fill everything with stone up to y=60
        for (var y = 0; y < 60; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }

        // Carve out a cave at y=48-55, z=6-10
        // Cave extends from x=1 to x=14 (so light can reach the boundary)
        for (var y = 48; y < 56; y++)
        {
            for (var z = 6; z <= 10; z++)
            {
                for (var x = 1; x < 15; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Air);
                }
            }
        }

        // Ensure boundary stays solid (will be removed in test to let light through)
        for (var y = 48; y < 56; y++)
        {
            for (var z = 6; z <= 10; z++)
            {
                chunk.SetBlock(boundaryX, y, z, BlockId.Stone);
            }
        }

        return chunk;
    }

    /// <summary>
    /// Creates a solid chunk with a small underground room - no sky light can reach inside.
    /// Room extends along X axis at the specified Z coordinate.
    /// </summary>
    private static void CreateSolidChunkWithRoom(ChunkData chunk, int roomMinY, int roomMaxY, int roomZ)
    {
        // Fill entire chunk with stone up to y=100 (ensures room is fully enclosed)
        for (var y = 0; y < 100; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }

        // Carve out a small room (leave 1-block walls on all sides except where we want opening)
        for (var y = roomMinY; y <= roomMaxY; y++)
        {
            for (var x = 1; x < VoxelHelper.ChunkSideSize - 1; x++)
            {
                chunk.SetBlock(x, y, roomZ, BlockId.Air);
            }
        }
    }

    /// <summary>
    /// Creates a solid chunk with a small underground room extending along Z axis.
    /// </summary>
    private static void CreateSolidChunkWithZRoom(ChunkData chunk, int roomMinY, int roomMaxY, int roomX)
    {
        // Fill entire chunk with stone up to y=100
        for (var y = 0; y < 100; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }

        // Carve out corridor along Z at specified X
        for (var y = roomMinY; y <= roomMaxY; y++)
        {
            for (var z = 1; z < VoxelHelper.ChunkSideSize - 1; z++)
            {
                chunk.SetBlock(roomX, y, z, BlockId.Air);
            }
        }
    }

    private static void CreateFloorAndCeiling(ChunkData chunk, int floorY, int ceilingY)
    {
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                chunk.SetBlock(x, floorY, z, BlockId.Stone);
                chunk.SetBlock(x, ceilingY, z, BlockId.Stone);
            }
        }
    }
}
