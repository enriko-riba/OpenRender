using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.LightingTestHelpers;

namespace SpyroGame.Tests;

/// <summary>
/// Tests for light recalculation when neighbor data becomes available.
/// This is critical for streaming scenarios where chunks load asynchronously.
/// </summary>
public class NeighborDataAvailableTests
{
    private const int CenterChunkX = 300;
    private const int CenterChunkZ = 300;

    [Fact]
    public void NeighborAvailable_LightPropagatesWhenNeighborLoads()
    {
        // Arrange - Center chunk with torch at boundary, initially no neighbor
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        var centerChunk = CreateAirChunk(centerChunkIdx);
        PlaceTorch(centerChunk, 15, 200, 8); // At right boundary
        
        LightingCalculator.CalculateLighting(centerChunk);
        
        // Assert initial state - light exists in center chunk
        Assert.Equal(14, GetBlockLight(centerChunk, 15, 200, 8));
        
        // Act - Neighbor chunk "loads"
        var rightChunkIdx = GetChunkIndex(CenterChunkX + 1, CenterChunkZ);
        var rightChunk = CreateAirChunk(rightChunkIdx);
        LightingCalculator.CalculateLighting(rightChunk);
        
        // Propagate light to newly loaded neighbor
        LightingCalculator.PropagateNeighborLight(centerChunk, rightChunk, 1, 0);

        // Assert - Light should now propagate into the neighbor
        Assert.True(GetBlockLight(rightChunk, 0, 200, 8) > 0,
            "Light should propagate when neighbor becomes available");
        Assert.Equal(13, GetBlockLight(rightChunk, 0, 200, 8));
    }

    [Fact]
    public void NeighborAvailable_LightFromNeighborPropagatesBack()
    {
        // Arrange - Neighbor loads first with a torch, then center chunk loads
        var rightChunkIdx = GetChunkIndex(CenterChunkX + 1, CenterChunkZ);
        var rightChunk = CreateAirChunk(rightChunkIdx);
        PlaceTorch(rightChunk, 0, 200, 8); // At left boundary of right chunk
        LightingCalculator.CalculateLighting(rightChunk);
        
        // Center chunk loads later
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        var centerChunk = CreateAirChunk(centerChunkIdx);
        LightingCalculator.CalculateLighting(centerChunk);
        
        // Act - Propagate between chunks
        LightingCalculator.PropagateNeighborLight(centerChunk, rightChunk, 1, 0);

        // Assert - Center chunk should receive light from right chunk
        Assert.True(GetBlockLight(centerChunk, 15, 200, 8) > 0,
            "Light from neighbor should propagate back when center loads");
    }

    [Fact]
    public void NeighborAvailable_SkyLightUpdatesWhenNeighborLoads()
    {
        // Arrange - Center chunk with solid roof and a horizontal tunnel at the boundary
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        var centerChunk = CreateChunkWithFloor(floorY: 100, centerChunkIdx);
        
        // Add a ceiling above the cave so it's actually dark
        for (var y = 101; y < 110; y++)
        {
            for (var x = 10; x <= 15; x++)
            {
                for (var z = 5; z <= 11; z++)
                {
                    centerChunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }
        
        // Create a horizontal cave at the boundary (underground, covered by stone)
        // The cave goes from x=14 to x=15 at y=95 (in the floor stone)
        centerChunk.SetBlock(15, 95, 8, BlockId.Air); // Opening at boundary
        centerChunk.SetBlock(14, 95, 8, BlockId.Air); // Cave tunnel inside
        
        LightingCalculator.CalculateLighting(centerChunk);
        
        // Cave should be dark initially (stone above, stone around, no sky access)
        var initialLight = GetSkyLight(centerChunk, 14, 95, 8);
        
        // Act - Neighbor loads which has an open sky path to the cave entrance
        var rightChunkIdx = GetChunkIndex(CenterChunkX + 1, CenterChunkZ);
        var rightChunk = CreateAirChunk(rightChunkIdx); // All air = full sky light
        LightingCalculator.CalculateLighting(rightChunk);
        
        LightingCalculator.PropagateNeighborLight(centerChunk, rightChunk, 1, 0);

        // Assert - The cave entrance at x=15 should receive sky light from neighbor
        // The neighbor has sky light 15 at (0, 95, 8) since it's all air
        var entranceLight = GetSkyLight(centerChunk, 15, 95, 8);
        Assert.True(entranceLight > initialLight,
            $"Cave at boundary should receive sky light when neighbor loads (initial={initialLight}, entrance={entranceLight})");
    }

    [Fact]
    public void NeighborAvailable_AllFourCardinalNeighbors()
    {
        // Arrange - Center chunk with torches at all four boundaries
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        var centerChunk = CreateAirChunk(centerChunkIdx);
        
        PlaceTorch(centerChunk, 15, 200, 8);  // Right boundary (+X)
        PlaceTorch(centerChunk, 0, 200, 8);   // Left boundary (-X)
        PlaceTorch(centerChunk, 8, 200, 15);  // Front boundary (+Z)
        PlaceTorch(centerChunk, 8, 200, 0);   // Back boundary (-Z)
        
        LightingCalculator.CalculateLighting(centerChunk);
        
        // Create all four neighbors
        var rightIdx = GetChunkIndex(CenterChunkX + 1, CenterChunkZ);
        var leftIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var frontIdx = GetChunkIndex(CenterChunkX, CenterChunkZ + 1);
        var backIdx = GetChunkIndex(CenterChunkX, CenterChunkZ - 1);
        
        var rightChunk = CreateAirChunk(rightIdx);
        var leftChunk = CreateAirChunk(leftIdx);
        var frontChunk = CreateAirChunk(frontIdx);
        var backChunk = CreateAirChunk(backIdx);
        
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(frontChunk);
        LightingCalculator.CalculateLighting(backChunk);
        
        // Act - Propagate to all neighbors
        LightingCalculator.PropagateNeighborLight(centerChunk, rightChunk, 1, 0);
        LightingCalculator.PropagateNeighborLight(leftChunk, centerChunk, 1, 0);
        LightingCalculator.PropagateNeighborLight(centerChunk, frontChunk, 0, 1);
        LightingCalculator.PropagateNeighborLight(backChunk, centerChunk, 0, 1);

        // Assert - All neighbors should receive light
        Assert.True(GetBlockLight(rightChunk, 0, 200, 8) > 0, "Right neighbor should receive light");
        Assert.True(GetBlockLight(leftChunk, 15, 200, 8) > 0, "Left neighbor should receive light");
        Assert.True(GetBlockLight(frontChunk, 8, 200, 0) > 0, "Front neighbor should receive light");
        Assert.True(GetBlockLight(backChunk, 8, 200, 15) > 0, "Back neighbor should receive light");
    }

    [Fact]
    public void NeighborAvailable_MultiplePropagationPasses()
    {
        // Arrange - Scenario where light needs multiple propagation passes
        var centerChunkIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        var rightChunkIdx = GetChunkIndex(CenterChunkX + 1, CenterChunkZ);
        
        var centerChunk = CreateAirChunk(centerChunkIdx);
        var rightChunk = CreateAirChunk(rightChunkIdx);
        
        // Torch in center of center chunk
        PlaceTorch(centerChunk, 8, 200, 8);
        
        // Torch in center of right chunk
        PlaceTorch(rightChunk, 8, 200, 8);
        
        LightingCalculator.CalculateLighting(centerChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        
        // First propagation
        LightingCalculator.PropagateNeighborLight(centerChunk, rightChunk, 1, 0);
        
        var afterFirstPass = GetBlockLight(centerChunk, 15, 200, 8);
        
        // Act - Second propagation (might update boundary values)
        LightingCalculator.PropagateNeighborLight(centerChunk, rightChunk, 1, 0);
        
        var afterSecondPass = GetBlockLight(centerChunk, 15, 200, 8);

        // Assert - Values should stabilize (not increase indefinitely)
        Assert.True(afterSecondPass >= afterFirstPass,
            "Light values should not decrease on subsequent propagation");
        Assert.True(afterSecondPass <= 14,
            "Light values should not exceed source light level");
    }
}
