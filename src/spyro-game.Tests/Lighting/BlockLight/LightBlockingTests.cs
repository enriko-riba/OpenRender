using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.Common.LightingTestHelpers;

namespace SpyroGame.Tests.Lighting.BlockLight;

/// <summary>
/// Tests for light removal when placing opaque blocks that block light paths.
/// This covers the scenario where closing an opening (placing a block) should
/// make the enclosed area dark.
/// </summary>
public class LightBlockingTests
{
    private const int TestChunkX = 300;
    private const int TestChunkZ = 300;

    /// <summary>
    /// When placing an opaque block that closes off an opening, the light
    /// on the blocked side should be removed/recalculated to become dark.
    /// This reproduces the bug where closing a tunnel entrance doesn't
    /// update the lighting inside the tunnel.
    /// </summary>
    [Fact]
    public void PlacingOpaqueBlock_BlocksLightPropagation()
    {
        // Arrange - Create an underground room with a single opening to the sky
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateUndergroundRoomWithOpening(chunkIdx);

        // Calculate initial lighting - light should enter through the shaft at x=8, z=8
        LightingCalculator.CalculateLighting(chunk);

        // Verify the opening position has sky light (shaft at x=8, z=8)
        var shaftLight = GetSkyLight(chunk, 8, 55, 8);
        Assert.True(shaftLight > 0, 
            $"Shaft should have sky light. Got: {shaftLight}");

        // Verify the room has light (propagated from shaft)
        var roomCornerLight = GetSkyLight(chunk, 6, 50, 6);
        Assert.True(roomCornerLight > 0, 
            $"Room corner should have sky light. Got: {roomCornerLight}");

        // Act - Close the opening by placing a stone block at the shaft opening
        chunk.SetBlock(8, 55, 8, BlockId.Stone);
        
        // Recalculate lighting after placing the block
        LightingCalculator.CalculateLighting(chunk);

        // Assert - The room should now be dark (no light source inside)
        var roomCornerLightAfter = GetSkyLight(chunk, 6, 50, 6);
        Assert.Equal(0, roomCornerLightAfter);
        
        // The area below the newly placed block should be dark
        var belowBlockLight = GetSkyLight(chunk, 8, 54, 8);
        Assert.Equal(0, belowBlockLight);
    }

    /// <summary>
    /// When placing a block that blocks torch light, the blocked area should become dark.
    /// </summary>
    [Fact]
    public void PlacingOpaqueBlock_BlocksTorchLight()
    {
        // Arrange - Create a room with torch on one side, open corridor on other side
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = new ChunkData { ChunkIndex = chunkIdx };

        // Fill with stone
        for (var y = 0; y < 60; y++)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }

        // Carve a corridor along Z axis at y=50, x=8
        for (var z = 0; z < 16; z++)
        {
            chunk.SetBlock(8, 50, z, BlockId.Air);
            chunk.SetBlock(8, 51, z, BlockId.Air);
        }

        // Place torch at one end (z=0)
        PlaceTorch(chunk, 8, 50, 0);

        // Calculate lighting
        LightingCalculator.CalculateLighting(chunk);

        // Verify light propagates through corridor
        var farEndLight = GetBlockLight(chunk, 8, 50, 10);
        Assert.True(farEndLight > 0, $"Far end should have light from torch. Got: {farEndLight}");

        // Act - Place a wall in the middle of the corridor (blocking light)
        chunk.SetBlock(8, 50, 5, BlockId.Stone);
        chunk.SetBlock(8, 51, 5, BlockId.Stone);

        // Recalculate lighting
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Far end should now be dark (blocked by wall)
        var farEndLightAfter = GetBlockLight(chunk, 8, 50, 10);
        Assert.Equal(0, farEndLightAfter);

        // Near side (before wall) should still have light
        var nearSideLight = GetBlockLight(chunk, 8, 50, 3);
        Assert.True(nearSideLight > 0, $"Near side should still have light. Got: {nearSideLight}");
    }

    /// <summary>
    /// When placing a block at chunk boundary that blocks light from neighbor,
    /// the light should be properly removed from the enclosed area.
    /// This simulates the user's scenario: horizontal tunnel dug across chunks,
    /// then closing the opening at chunk boundary.
    /// </summary>
    [Fact]
    public void PlacingOpaqueBlock_AtBoundary_BlocksNeighborLight()
    {
        // Arrange - Create two chunks: left has sky access, right has enclosed tunnel
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var rightChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);

        var leftChunk = new ChunkData { ChunkIndex = leftChunkIdx };
        var rightChunk = new ChunkData { ChunkIndex = rightChunkIdx };

        // Create underground structure in both chunks
        // Fill with stone up to y=60
        for (var y = 0; y < 60; y++)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    leftChunk.SetBlock(x, y, z, BlockId.Stone);
                    rightChunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }

        // Create horizontal tunnel at y=50, z=8 that goes across both chunks
        // Tunnel is 2 blocks high (y=50 and y=51)
        for (var x = 0; x < 16; x++)
        {
            leftChunk.SetBlock(x, 50, 8, BlockId.Air);
            leftChunk.SetBlock(x, 51, 8, BlockId.Air);
            rightChunk.SetBlock(x, 50, 8, BlockId.Air);
            rightChunk.SetBlock(x, 51, 8, BlockId.Air);
        }

        // Create vertical shaft in left chunk at x=8 going to sky - this is the entrance
        for (var y = 52; y < 384; y++)
        {
            leftChunk.SetBlock(8, y, 8, BlockId.Air);
        }

        // Calculate lighting for both chunks
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        
        // Propagate light from left chunk (has sky access) to right chunk
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Verify light reaches right chunk through the boundary opening
        var rightTunnelLight = GetSkyLight(rightChunk, 5, 50, 8);
        Assert.True(rightTunnelLight > 0, 
            $"Right chunk tunnel should have sky light from left chunk. Got: {rightTunnelLight}");

        // Get light at boundary positions BEFORE placing blocks
        var oldLightLower = GetSkyLight(rightChunk, 0, 50, 8);
        var oldLightUpper = GetSkyLight(rightChunk, 0, 51, 8);
        
        // Act - Close the opening at the boundary by placing blocks at x=0 (right chunk boundary)
        rightChunk.SetBlock(0, 50, 8, BlockId.Stone);
        rightChunk.SetBlock(0, 51, 8, BlockId.Stone);

        // Create chunk data provider for cross-chunk operations
        ChunkDataProvider getChunk = idx => {
            if (idx == leftChunkIdx) return leftChunk;
            if (idx == rightChunkIdx) return rightChunk;
            return null;
        };

        // Use RemoveSkyLight to properly remove light that was flowing through both positions
        if (oldLightLower > 0)
        {
            LightingCalculator.RemoveSkyLight(rightChunkIdx, 0, 50, 8, oldLightLower, getChunk);
        }
        if (oldLightUpper > 0)
        {
            LightingCalculator.RemoveSkyLight(rightChunkIdx, 0, 51, 8, oldLightUpper, getChunk);
        }

        // Assert - Right chunk tunnel should now be dark (no sky access)
        var rightRoomLightAfter = GetSkyLight(rightChunk, 5, 50, 8);
        Assert.Equal(0, rightRoomLightAfter);
    }

    /// <summary>
    /// Placing multiple blocks to seal a room should make it completely dark.
    /// </summary>
    [Fact]
    public void SealingRoom_MakesItDark()
    {
        // Arrange - Create a room with a vertical shaft opening to sky
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = new ChunkData { ChunkIndex = chunkIdx };

        // Fill with stone
        for (var y = 0; y < 60; y++)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }

        // Carve out a room (5x5x5) at y=48-53
        for (var y = 48; y < 53; y++)
        {
            for (var z = 5; z < 10; z++)
            {
                for (var x = 5; x < 10; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Air);
                }
            }
        }

        // Create 2x2 vertical shaft from room ceiling to sky
        for (var y = 53; y < 384; y++)
        {
            chunk.SetBlock(7, y, 7, BlockId.Air);
            chunk.SetBlock(8, y, 7, BlockId.Air);
            chunk.SetBlock(7, y, 8, BlockId.Air);
            chunk.SetBlock(8, y, 8, BlockId.Air);
        }

        // Calculate lighting
        LightingCalculator.CalculateLighting(chunk);

        // Verify room has sky light
        var roomLight = GetSkyLight(chunk, 7, 50, 7);
        Assert.True(roomLight > 0, $"Room should have sky light. Got: {roomLight}");

        // Act - Seal the opening by placing 4 blocks at the shaft entrance (ceiling)
        chunk.SetBlock(7, 53, 7, BlockId.Stone);
        chunk.SetBlock(8, 53, 7, BlockId.Stone);
        chunk.SetBlock(7, 53, 8, BlockId.Stone);
        chunk.SetBlock(8, 53, 8, BlockId.Stone);

        // Recalculate lighting
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Room should be completely dark
        var roomLightAfter = GetSkyLight(chunk, 7, 50, 7);
        Assert.Equal(0, roomLightAfter);
        
        // All positions in room should be dark
        Assert.Equal(0, GetSkyLight(chunk, 6, 50, 8));
        Assert.Equal(0, GetSkyLight(chunk, 9, 50, 8));
        Assert.Equal(0, GetSkyLight(chunk, 7, 52, 8));
    }

    // Helper methods

    /// <summary>
    /// Creates an underground room with a single opening to the sky at y=55.
    /// The room is directly below the opening so sky light propagates straight down.
    /// </summary>
    private static ChunkData CreateUndergroundRoomWithOpening(int chunkIdx)
    {
        var chunk = new ChunkData { ChunkIndex = chunkIdx };

        // Fill with stone up to y=60
        for (var y = 0; y < 60; y++)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }

        // Carve out an underground room at y=48-55, x=5-11, z=5-11
        for (var y = 48; y < 56; y++)
        {
            for (var z = 5; z < 11; z++)
            {
                for (var x = 5; x < 11; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Air);
                }
            }
        }

        // Create a vertical shaft from room to sky at x=8, z=8 (center of room)
        // This goes from the room ceiling (y=55) up to the sky
        for (var y = 55; y < 384; y++)
        {
            chunk.SetBlock(8, y, 8, BlockId.Air);
        }

        return chunk;
    }

    /// <summary>
    /// Creates a solid chunk with a horizontal corridor at specified Y and Z.
    /// </summary>
    private static void CreateSolidChunkWithCorridor(ChunkData chunk, int corridorY, int corridorZ)
    {
        // Fill with stone up to y=60
        for (var y = 0; y < 60; y++)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }

        // Carve corridor along X
        for (var x = 1; x < 15; x++)
        {
            chunk.SetBlock(x, corridorY, corridorZ, BlockId.Air);
            chunk.SetBlock(x, corridorY + 1, corridorZ, BlockId.Air);
        }
    }
}
