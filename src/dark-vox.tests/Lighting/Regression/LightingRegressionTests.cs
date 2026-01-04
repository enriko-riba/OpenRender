using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using DarkVox.World;
using Xunit;
using static DarkVox.Tests.Common.LightingTestHelpers;

namespace DarkVox.Tests.Lighting.Regression;

/// <summary>
/// Regression tests for specific bugs that have been encountered.
/// Each test documents a specific issue that was found and fixed.
/// </summary>
public class LightingRegressionTests
{
    private const int TestChunkX = 300;
    private const int TestChunkZ = 300;

    /// <summary>
    /// Regression: Light bleeding through diagonal corners.
    /// Bug: Light would leak through corners where two walls meet diagonally,
    /// causing rooms to be lit when they should be sealed.
    /// </summary>
    [Fact]
    public void Regression_LightDoesNotBleedThroughDiagonalCorner()
    {
        var chunk = CreateAirChunk();
        
        // Create an L-shaped wall configuration
        // Wall 1: X=8, Z=0-15 (vertical wall)
        // Wall 2: X=0-15, Z=8 (horizontal wall)
        for (var y = 195; y <= 205; y++)
        {
            for (var i = 0; i < VoxelHelper.ChunkSideSize; i++)
            {
                chunk.SetBlock(8, y, i, BlockId.Stone); // Vertical wall
                chunk.SetBlock(i, y, 8, BlockId.Stone); // Horizontal wall
            }
        }
        
        // Place torch in quadrant (0-7, 0-7)
        PlaceTorch(chunk, 4, 200, 4);
        
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should NOT reach quadrant (9-15, 9-15)
        // which is diagonally opposite through the corner
        Assert.True(GetBlockLight(chunk, 12, 200, 12) == 0,
            "Light should not bleed through diagonal corner");
        Assert.True(GetBlockLight(chunk, 10, 200, 10) == 0,
            "Light should not bleed through diagonal corner (closer)");
    }

    /// <summary>
    /// Regression: Light stops propagating at chunk boundary.
    /// Bug: Light would not cross chunk boundaries, causing dark seams
    /// between chunks even when both sides are air.
    /// </summary>
    [Fact]
    public void Regression_LightCrossesChunkBoundary()
    {
        var leftIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var rightIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        
        var leftChunk = CreateAirChunk(leftIdx);
        var rightChunk = CreateAirChunk(rightIdx);
        
        // Torch at boundary of left chunk
        PlaceTorch(leftChunk, 15, 200, 8);
        
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Assert - Light must cross into right chunk
        Assert.True(GetBlockLight(rightChunk, 0, 200, 8) > 0,
            "Regression: Light must cross chunk boundary");
        var actualLight = GetBlockLight(rightChunk, 0, 200, 8);
        Assert.True(actualLight == 13,
            $"Regression: Light decay must be correct at boundary, expected 13 but got {actualLight}");
    }

    /// <summary>
    /// Regression: Ghost light remains after torch removal.
    /// Bug: When a torch is removed, its propagated light would remain,
    /// causing areas to stay lit with no light source.
    /// </summary>
    [Fact]
    public void Regression_NoGhostLightAfterTorchRemoval()
    {
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateAirChunk(chunkIdx);
        
        PlaceTorch(chunk, 8, 200, 8);
        LightingCalculator.CalculateLighting(chunk);
        
        // Record light values at several positions
        var positions = new[] { (9, 200, 8), (10, 200, 8), (8, 201, 8), (8, 200, 9) };
        foreach (var (x, y, z) in positions)
        {
            Assert.True(GetBlockLight(chunk, x, y, z) > 0, 
                $"Pre-condition: Position ({x},{y},{z}) should be lit");
        }
        
        var chunks = new Dictionary<int, ChunkData> { [chunkIdx] = chunk };
        var provider = CreateChunkProvider(chunks);
        
        // Remove torch
        chunk.SetBlock(8, 200, 8, BlockId.Air);
        LightingCalculator.RemoveBlockLight(chunkIdx, 8, 200, 8, 14, provider);

        // Assert - All positions should be dark
        foreach (var (x, y, z) in positions)
        {
            Assert.True(GetBlockLight(chunk, x, y, z) == 0,
                $"Regression: Ghost light at ({x},{y},{z}) after torch removal");
        }
    }

    /// <summary>
    /// Regression: Light not recalculated when neighbor loads.
    /// Bug: When a neighboring chunk loaded, the existing chunk's boundary
    /// would not update, causing permanent dark seams.
    /// </summary>
    [Fact]
    public void Regression_BoundaryUpdatesWhenNeighborLoads()
    {
        var centerIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var rightIdx = GetChunkIndex(TestChunkX + 1, TestChunkZ);
        
        // Right chunk has torch at boundary
        var rightChunk = CreateAirChunk(rightIdx);
        PlaceTorch(rightChunk, 0, 200, 8);
        LightingCalculator.CalculateLighting(rightChunk);
        
        // Center chunk loads later
        var centerChunk = CreateAirChunk(centerIdx);
        LightingCalculator.CalculateLighting(centerChunk);
        
        // Initial state - center chunk boundary should be dark
        var beforePropagation = GetBlockLight(centerChunk, 15, 200, 8);
        
        // Propagate from right to center (simulating neighbor becoming available)
        LightingCalculator.PropagateNeighborLight(centerChunk, rightChunk, 1, 0);

        // Assert - Center chunk boundary should now be lit
        Assert.True(GetBlockLight(centerChunk, 15, 200, 8) > beforePropagation,
            "Regression: Boundary must update when neighbor becomes available");
    }

    /// <summary>
    /// Regression: Light bleeds from vertex corner into sealed room.
    /// Bug: When smooth lighting samples corner neighbors, it would pull
    /// light from outside a sealed room through the diagonal.
    /// </summary>
    [Fact]
    public void Regression_SealedRoomStaysDark()
    {
        var chunk = CreateAirChunk();
        
        // Create a completely sealed 5x5x5 room
        var roomCenter = (x: 8, y: 200, z: 8);
        var roomSize = 2; // 2 blocks from center = 5x5x5 interior
        
        // Build walls (3 blocks thick to prevent any corner bleeding)
        for (var dy = -roomSize - 1; dy <= roomSize + 1; dy++)
        {
            for (var dz = -roomSize - 1; dz <= roomSize + 1; dz++)
            {
                for (var dx = -roomSize - 1; dx <= roomSize + 1; dx++)
                {
                    var x = roomCenter.x + dx;
                    var y = roomCenter.y + dy;
                    var z = roomCenter.z + dz;
                    
                    // Outer shell is stone
                    if (Math.Abs(dx) >= roomSize || Math.Abs(dy) >= roomSize || Math.Abs(dz) >= roomSize)
                    {
                        chunk.SetBlock(x, y, z, BlockId.Stone);
                    }
                }
            }
        }
        
        // Place bright light sources all around the outside
        PlaceTorch(chunk, roomCenter.x - roomSize - 2, roomCenter.y, roomCenter.z);
        PlaceTorch(chunk, roomCenter.x + roomSize + 2, roomCenter.y, roomCenter.z);
        PlaceTorch(chunk, roomCenter.x, roomCenter.y, roomCenter.z - roomSize - 2);
        PlaceTorch(chunk, roomCenter.x, roomCenter.y, roomCenter.z + roomSize + 2);
        PlaceTorch(chunk, roomCenter.x, roomCenter.y - roomSize - 2, roomCenter.z);
        PlaceTorch(chunk, roomCenter.x, roomCenter.y + roomSize + 2, roomCenter.z);
        
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Inside of sealed room must be completely dark
        Assert.True(GetBlockLight(chunk, roomCenter.x, roomCenter.y, roomCenter.z) == 0,
            "Regression: Center of sealed room must be dark");
        Assert.True(GetBlockLight(chunk, roomCenter.x + 1, roomCenter.y, roomCenter.z) == 0,
            "Regression: Near wall of sealed room must be dark");
        Assert.True(GetSkyLight(chunk, roomCenter.x, roomCenter.y, roomCenter.z) == 0,
            "Regression: Sealed room must have no sky light");
    }

    /// <summary>
    /// Regression: Sky light doesn't propagate into caves properly.
    /// Bug: Sky light would stop at the first solid block and not propagate
    /// horizontally into cave entrances.
    /// </summary>
    [Fact]
    public void Regression_SkyLightEntersCaves()
    {
        var chunk = CreateChunkWithFloor(floorY: 100);
        
        // Create a horizontal cave entrance
        // Opening at surface (y=101) leading to underground chamber
        for (var x = 5; x <= 10; x++)
        {
            chunk.SetBlock(x, 101, 8, BlockId.Air); // Surface opening
            chunk.SetBlock(x, 100, 8, BlockId.Air); // Just below surface
            chunk.SetBlock(x, 99, 8, BlockId.Air);  // Deeper
            chunk.SetBlock(x, 98, 8, BlockId.Air);  // Cave floor
        }
        
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Sky light should reach into the cave
        var surfaceLight = GetSkyLight(chunk, 5, 101, 8);
        Assert.True(surfaceLight == 15, $"Surface should have full sky light, got {surfaceLight}");
        Assert.True(GetSkyLight(chunk, 5, 100, 8) > 0, "Regression: Sky light must enter caves");
        Assert.True(GetSkyLight(chunk, 8, 99, 8) > 0, "Regression: Sky light must propagate into caves");
    }

    /// <summary>
    /// Regression: Light removal doesn't cross chunk boundaries.
    /// Bug: When removing a torch near a chunk boundary, the light in
    /// the neighboring chunk would not be cleared.
    /// </summary>
    [Fact]
    public void Regression_LightRemovalCrossesChunkBoundary()
    {
        var leftIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var centerIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        
        var leftChunk = CreateAirChunk(leftIdx);
        var centerChunk = CreateAirChunk(centerIdx);
        
        // Torch at boundary
        PlaceTorch(leftChunk, 15, 200, 8);
        
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(centerChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, centerChunk, 1, 0);
        
        // Verify light propagated
        Assert.True(GetBlockLight(centerChunk, 0, 200, 8) > 0,
            "Pre-condition: Light should have propagated to neighbor");
        
        var chunks = new Dictionary<int, ChunkData>
        {
            [leftIdx] = leftChunk,
            [centerIdx] = centerChunk
        };
        var provider = CreateChunkProvider(chunks);
        
        // Remove torch
        leftChunk.SetBlock(15, 200, 8, BlockId.Air);
        var modified = LightingCalculator.RemoveBlockLight(leftIdx, 15, 200, 8, 14, provider);

        // Assert - Light must be removed from neighbor chunk too
        Assert.True(GetBlockLight(centerChunk, 0, 200, 8) == 0,
            "Regression: Light removal must cross chunk boundaries");
        Assert.Contains(centerIdx, modified);
    }

    /// <summary>
    /// Regression: Water doesn't filter light correctly.
    /// Bug: Light passing through water would not decay properly,
    /// causing underwater areas to be too bright.
    /// </summary>
    [Fact]
    public void Regression_WaterFiltersLight()
    {
        var chunk = CreateAirChunk();
        
        // Create a column of water
        for (var y = 195; y <= 205; y++)
        {
            chunk.SetBlock(8, y, 8, BlockId.Water);
        }
        
        // Light source above water
        PlaceTorch(chunk, 8, 210, 8);
        
        LightingCalculator.CalculateLighting(chunk);

        // Assert - Light should decay through water (filter=2)
        var lightAboveWater = GetBlockLight(chunk, 8, 206, 8);
        var lightInWater = GetBlockLight(chunk, 8, 200, 8);
        
        Assert.True(lightInWater < lightAboveWater,
            "Regression: Light must decay through water");
        
        // With filter=2, after 5 blocks of water (205 to 200), light should decay by 10
        // Torch at 210 -> 206 above water = 14 - 4 = 10
        // Then through water 205->200 = 5 blocks * 2 filter = -10, should be 0
        Assert.True(lightInWater <= lightAboveWater - 5,
            "Regression: Water must filter light with correct decay rate");
    }

    /// <summary>
    /// Regression: Block light and sky light interfere.
    /// Bug: Operations on block light would accidentally modify sky light values.
    /// </summary>
    [Fact]
    public void Regression_BlockLightDoesNotAffectSkyLight()
    {
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateAirChunk(chunkIdx);
        
        LightingCalculator.CalculateLighting(chunk);
        
        // Record sky light
        var skyLightBefore = GetSkyLight(chunk, 8, 200, 8);
        Assert.True(skyLightBefore == 15, "Pre-condition: Should have full sky light");
        
        // Add and remove a torch
        PlaceTorch(chunk, 8, 200, 8);
        LightingCalculator.CalculateLighting(chunk);
        
        var chunks = new Dictionary<int, ChunkData> { [chunkIdx] = chunk };
        chunk.SetBlock(8, 200, 8, BlockId.Air);
        LightingCalculator.RemoveBlockLight(chunkIdx, 8, 200, 8, 14, CreateChunkProvider(chunks));

        // Assert - Sky light should be unchanged
        var skyLightAfter = GetSkyLight(chunk, 8, 200, 8);
        Assert.True(skyLightAfter == skyLightBefore,
            $"Regression: Block light operations must not affect sky light. Before: {skyLightBefore}, After: {skyLightAfter}");
    }
}
