using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.LightingTestHelpers;

namespace SpyroGame.Tests;

/// <summary>
/// Tests for block light (torches, glowstone, etc.) within a single chunk.
/// </summary>
public class BlockLightTests
{
    [Fact]
    public void BlockLight_Torch_EmitsLight14()
    {
        // Arrange
        var chunk = CreateChunkWithFloor(floorY: 100);
        PlaceTorch(chunk, 8, 101, 8);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Torch emits light level 14
        Assert.Equal(14, GetBlockLight(chunk, 8, 101, 8));
    }

    [Fact]
    public void BlockLight_Glowstone_EmitsLight15()
    {
        // Arrange
        var chunk = CreateChunkWithFloor(floorY: 100);
        PlaceGlowstone(chunk, 8, 101, 8);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Glowstone emits light level 15
        Assert.Equal(15, GetBlockLight(chunk, 8, 101, 8));
    }

    [Fact]
    public void BlockLight_PropagatesInAllDirections()
    {
        // Arrange - Torch in open air
        var chunk = CreateAirChunk();
        PlaceTorch(chunk, 8, 200, 8);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light propagates in all 6 directions
        Assert.Equal(14, GetBlockLight(chunk, 8, 200, 8));     // Source
        Assert.Equal(13, GetBlockLight(chunk, 9, 200, 8));     // +X
        Assert.Equal(13, GetBlockLight(chunk, 7, 200, 8));     // -X
        Assert.Equal(13, GetBlockLight(chunk, 8, 201, 8));     // +Y
        Assert.Equal(13, GetBlockLight(chunk, 8, 199, 8));     // -Y
        Assert.Equal(13, GetBlockLight(chunk, 8, 200, 9));     // +Z
        Assert.Equal(13, GetBlockLight(chunk, 8, 200, 7));     // -Z
    }

    [Fact]
    public void BlockLight_DecaysWithDistance()
    {
        // Arrange
        var chunk = CreateAirChunk();
        PlaceTorch(chunk, 8, 200, 8);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light decays by 1 per block in air
        Assert.Equal(14, GetBlockLight(chunk, 8, 200, 8));   // Distance 0
        Assert.Equal(13, GetBlockLight(chunk, 9, 200, 8));   // Distance 1
        Assert.Equal(12, GetBlockLight(chunk, 10, 200, 8));  // Distance 2
        Assert.Equal(11, GetBlockLight(chunk, 11, 200, 8));  // Distance 3
        Assert.Equal(1, GetBlockLight(chunk, 8, 200, 8 + 13)); // Distance 13 (14-13=1)
        Assert.Equal(0, GetBlockLight(chunk, 8, 200, 8 + 14)); // Distance 14 (out of range)
    }

    [Fact]
    public void BlockLight_StoppedByOpaqueBlocks()
    {
        // Arrange
        var chunk = CreateAirChunk();
        PlaceTorch(chunk, 8, 200, 8);
        // Place stone wall between torch and target
        chunk.SetBlock(9, 200, 8, BlockId.Stone);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should not pass through stone
        Assert.Equal(14, GetBlockLight(chunk, 8, 200, 8));  // Torch
        Assert.Equal(0, GetBlockLight(chunk, 9, 200, 8));   // Stone block itself
        Assert.Equal(0, GetBlockLight(chunk, 10, 200, 8));  // Behind stone
    }

    [Fact]
    public void BlockLight_DoesNotBleedIntoSealedRoom()
    {
        // Arrange - Create a sealed room with torch outside
        var chunk = CreateSealedRoom(4, 195, 4, 12, 205, 12);
        // Place torch outside the room
        PlaceTorch(chunk, 2, 200, 8);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should not reach inside the sealed room
        Assert.Equal(0, GetBlockLight(chunk, 8, 200, 8)); // Center of sealed room
    }

    [Fact]
    public void BlockLight_ReachesThroughDoorway()
    {
        // Arrange - Sealed room with a doorway
        var chunk = CreateSealedRoom(4, 195, 4, 12, 205, 12);
        // Create doorway
        chunk.SetBlock(4, 200, 8, BlockId.Air);
        chunk.SetBlock(4, 201, 8, BlockId.Air);
        // Place torch outside near doorway
        PlaceTorch(chunk, 2, 200, 8);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should reach through doorway into the room
        Assert.True(GetBlockLight(chunk, 5, 200, 8) > 0); // Just inside doorway
    }

    [Fact]
    public void BlockLight_MultipleSources_Combine()
    {
        // Arrange - Two torches at distance
        var chunk = CreateAirChunk();
        PlaceTorch(chunk, 4, 200, 8);
        PlaceTorch(chunk, 12, 200, 8);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Middle point should have light from both sources
        // Distance from each torch to middle (8,200,8) is 4 blocks
        // Light from each: 14 - 4 = 10
        // Should take the max, which is 10
        Assert.Equal(10, GetBlockLight(chunk, 8, 200, 8));
    }

    [Fact]
    public void BlockLight_DoesNotBleedThroughCorner()
    {
        // This is a critical test for diagonal light bleeding
        // Arrange - Create corner geometry
        var chunk = CreateAirChunk();
        
        // Build an L-shaped wall
        for (var y = 195; y <= 205; y++)
        {
            // Wall along X at z=8
            for (var x = 6; x <= 10; x++)
            {
                chunk.SetBlock(x, y, 8, BlockId.Stone);
            }
            // Wall along Z at x=8  
            for (var z = 8; z <= 12; z++)
            {
                chunk.SetBlock(8, y, z, BlockId.Stone);
            }
        }
        
        // Place torch on one side of the L
        PlaceTorch(chunk, 6, 200, 6);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should NOT bleed diagonally through the corner
        // Block at (10, 200, 10) is on the opposite side of both walls
        Assert.Equal(0, GetBlockLight(chunk, 10, 200, 10));
    }

    [Fact]
    public void BlockLight_Lava_EmitsMaxLight()
    {
        // Arrange
        var chunk = CreateChunkWithFloor(floorY: 100);
        chunk.SetBlock(8, 101, 8, BlockId.Lava);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Lava emits light level 15
        Assert.Equal(15, GetBlockLight(chunk, 8, 101, 8));
    }
}
