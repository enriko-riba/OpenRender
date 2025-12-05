using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.LightingTestHelpers;

namespace SpyroGame.Tests;

/// <summary>
/// Tests for sky light initialization and propagation within a single chunk.
/// </summary>
public class SkyLightTests
{
    [Fact]
    public void SkyLight_AirChunk_HasFullLightAtTop()
    {
        // Arrange
        var chunk = CreateAirChunk();

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - top of chunk should have full sky light (15)
        var topY = VoxelHelper.ChunkYSize - 1;
        Assert.Equal(15, GetSkyLight(chunk, 8, topY, 8));
        Assert.Equal(15, GetSkyLight(chunk, 0, topY, 0));
        Assert.Equal(15, GetSkyLight(chunk, 15, topY, 15));
    }

    [Fact]
    public void SkyLight_AirChunk_PropagatesDownward()
    {
        // Arrange
        var chunk = CreateAirChunk();

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - light should propagate all the way down in air (no decay for sky light going straight down)
        Assert.Equal(15, GetSkyLight(chunk, 8, 100, 8));
        Assert.Equal(15, GetSkyLight(chunk, 8, 50, 8));
        Assert.Equal(15, GetSkyLight(chunk, 8, 1, 8));
    }

    [Fact]
    public void SkyLight_SolidFloor_BlocksLight()
    {
        // Arrange
        var chunk = CreateChunkWithFloor(floorY: 100);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - above floor should have light, inside floor should not
        Assert.Equal(15, GetSkyLight(chunk, 8, 101, 8)); // Just above floor
        Assert.Equal(0, GetSkyLight(chunk, 8, 100, 8));  // The stone block itself
        Assert.Equal(0, GetSkyLight(chunk, 8, 99, 8));   // Below floor (underground)
    }

    [Fact]
    public void SkyLight_DecaysThroughWater()
    {
        // Arrange
        var chunk = CreateAirChunk();
        // Place water layer at y=200
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                chunk.SetBlock(x, 200, z, BlockId.Water);
            }
        }

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - light should decay through water (filter=2)
        Assert.Equal(15, GetSkyLight(chunk, 8, 201, 8)); // Above water
        // Below water, light decays by 2 per block through water
        Assert.True(GetSkyLight(chunk, 8, 199, 8) <= 13);
    }

    [Fact]
    public void SkyLight_DoesNotBleedThroughDiagonalCorner()
    {
        // Arrange - Create an L-shaped solid structure that would allow diagonal bleeding
        var chunk = CreateAirChunk();
        
        // Create a corner structure at y=200
        // Stone at (8,200,8) and (9,200,9), air at (8,200,9) and (9,200,8)
        // Fill a floor below to create a cave scenario
        for (var y = 195; y <= 200; y++)
        {
            for (var z = 7; z <= 10; z++)
            {
                for (var x = 7; x <= 10; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }
        // Create an opening from above at one corner
        chunk.SetBlock(7, 200, 7, BlockId.Air);
        chunk.SetBlock(7, 199, 7, BlockId.Air);
        chunk.SetBlock(7, 198, 7, BlockId.Air);
        
        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should not bleed diagonally into sealed areas
        // The block at (10, 198, 10) should be dark since it's surrounded by stone
        Assert.Equal(0, GetSkyLight(chunk, 10, 198, 10));
    }

    [Fact]
    public void SkyLight_PropagatesToCaveEntrance()
    {
        // Arrange - Create a horizontal cave entrance under stone
        var chunk = CreateChunkWithFloor(floorY: 100);
        
        // Add stone roof at y=101 to y=105 over the cave area (except at entrance z=0)
        for (var y = 101; y <= 105; y++)
        {
            for (var z = 1; z < 10; z++)  // Leave z=0 open for entrance
            {
                for (var x = 5; x <= 11; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }
        
        // Carve a horizontal tunnel at y=99 (inside the stone floor)
        // Only the entrance at z=0 has open sky above
        for (var z = 0; z < 8; z++)
        {
            chunk.SetBlock(8, 99, z, BlockId.Air);  // Tunnel at y=99
        }
        // Clear the entrance at z=0 to connect to sky
        chunk.SetBlock(8, 100, 0, BlockId.Air);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - At entrance (z=0), should have sky light from above
        Assert.Equal(15, GetSkyLight(chunk, 8, 100, 0)); // Open to sky
        Assert.Equal(15, GetSkyLight(chunk, 8, 99, 0));  // Direct vertical from above
        
        // Deeper in cave, light should decay (horizontal propagation decays)
        // z=5 is 5 blocks away from z=0 horizontally
        Assert.True(GetSkyLight(chunk, 8, 99, 5) > 0, "Should have some light 5 blocks into cave");
        Assert.True(GetSkyLight(chunk, 8, 99, 5) < 15, "Light should decay horizontally into cave");
    }

    [Fact]
    public void SkyLight_SealedRoomInterior_RemainsDark()
    {
        // Arrange - fully sealed stone room
        var chunk = CreateSealedRoom(4, 195, 4, 12, 205, 12);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Center stays dark because roof blocks sky light
        Assert.Equal(0, GetSkyLight(chunk, 8, 200, 8));
        Assert.Equal(0, GetSkyLight(chunk, 9, 200, 9));
    }

    [Fact]
    public void SkyLight_Doorway_AllowsLimitedInteriorLight()
    {
        // Arrange - sealed room with a doorway carved into one wall
        var chunk = CreateSealedRoom(4, 195, 4, 12, 205, 12);
        chunk.SetBlock(4, 200, 8, BlockId.Air);
        chunk.SetBlock(4, 201, 8, BlockId.Air);

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light enters near doorway but decays quickly deeper inside
        Assert.True(GetSkyLight(chunk, 5, 200, 8) > 0);
        Assert.Equal(0, GetSkyLight(chunk, 8, 200, 8));
        Assert.Equal(0, GetSkyLight(chunk, 9, 200, 9));
    }
}
