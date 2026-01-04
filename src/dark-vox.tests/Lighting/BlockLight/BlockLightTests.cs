using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using DarkVox.World;
using Xunit;
using static DarkVox.Tests.Common.LightingTestHelpers;

namespace DarkVox.Tests.Lighting.BlockLight;

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
        // Arrange - Place torch at x=1 so we can test decay up to x=14 (13 blocks)
        var chunk = CreateAirChunk();
        PlaceTorch(chunk, 1, 200, 8);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light decays by 1 per block in air
        Assert.Equal(14, GetBlockLight(chunk, 1, 200, 8));   // Distance 0 (torch)
        Assert.Equal(13, GetBlockLight(chunk, 2, 200, 8));   // Distance 1
        Assert.Equal(12, GetBlockLight(chunk, 3, 200, 8));   // Distance 2
        Assert.Equal(11, GetBlockLight(chunk, 4, 200, 8));   // Distance 3
        Assert.Equal(1, GetBlockLight(chunk, 14, 200, 8));   // Distance 13 (14-13=1)
        Assert.Equal(0, GetBlockLight(chunk, 15, 200, 8));   // Distance 14 (out of range)
    }

    [Fact]
    public void BlockLight_StoppedByOpaqueBlocks()
    {
        // Arrange - Create a floor to prevent light from going around via Y direction
        var chunk = CreateAirChunk();
        
        // Fill floor and ceiling around the test area to force light through XZ plane only
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                chunk.SetBlock(x, 199, z, BlockId.Stone); // Floor
                chunk.SetBlock(x, 201, z, BlockId.Stone); // Ceiling
            }
        }
        
        PlaceTorch(chunk, 8, 200, 8);
        // Place stone wall blocking all paths in the XZ plane
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            chunk.SetBlock(9, 200, z, BlockId.Stone); // Full wall at x=9
        }

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should not pass through stone wall
        Assert.Equal(14, GetBlockLight(chunk, 8, 200, 8));  // Torch
        Assert.Equal(0, GetBlockLight(chunk, 9, 200, 8));   // Stone wall (opaque blocks have 0 light)
        Assert.Equal(0, GetBlockLight(chunk, 10, 200, 8));  // Behind stone wall
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
        // Arrange - Create corner geometry with floor and ceiling to prevent going over
        var chunk = CreateAirChunk();
        
        // Create floor and ceiling to constrain light to y=200 plane
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                chunk.SetBlock(x, 199, z, BlockId.Stone); // Floor
                chunk.SetBlock(x, 201, z, BlockId.Stone); // Ceiling
            }
        }
        
        // Build a complete wall that separates torch area from test area
        // Wall along X at z=8 (from x=0 to x=15) - spans entire chunk
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            chunk.SetBlock(x, 200, 8, BlockId.Stone);
        }
        // Wall along Z at x=8 (from z=8 to z=15) - extends to edge
        for (var z = 8; z < VoxelHelper.ChunkSideSize; z++)
        {
            chunk.SetBlock(8, 200, z, BlockId.Stone);
        }
        
        // Place torch on one side of the L (at z=6, which is before the z=8 wall)
        PlaceTorch(chunk, 6, 200, 6);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should NOT bleed diagonally through the corner
        // Block at (10, 200, 10) is completely enclosed by walls on all accessible paths
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
