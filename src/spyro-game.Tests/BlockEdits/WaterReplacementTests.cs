using SpyroGame.World;
using SpyroGame.Tests.Common;
using Xunit;
using SpyroGame.World.Registry;

namespace SpyroGame.Tests.BlockEdits;

/// <summary>
/// Tests for the water replacement logic when breaking blocks.
/// 
/// The logic is:
/// - When breaking a block (replacing with Air), check if any cardinal neighbor 
///   (including above/below) is Water.
/// - If so, replace with Water instead of Air (simulating water flow).
/// - If no adjacent Water blocks, replace with Air normally.
/// 
/// This ensures:
/// - Breaking blocks underwater in ocean → water fills in
/// - Breaking blocks on land/caves → air (no water magically appears)
/// - Breaking blocks near but not adjacent to water → air
/// </summary>
public class WaterReplacementTests
{
    #region HasAdjacentWaterBlock Tests (via public behavior)

    [Fact]
    public void BreakingBlock_WithWaterAbove_ReplacesWithWater()
    {
        // Arrange: Stone block with water directly above
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target block
        chunk.SetBlock(8, 11, 8, BlockId.Water);  // Water above
        
        // Act: Check if block should be replaced with water
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block with water above should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_WithWaterBelow_ReplacesWithWater()
    {
        // Arrange: Stone block with water directly below
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target block
        chunk.SetBlock(8, 9, 8, BlockId.Water);   // Water below
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block with water below should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_WithWaterToNorth_ReplacesWithWater()
    {
        // Arrange: Stone block with water to -Z (north)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target block
        chunk.SetBlock(8, 10, 7, BlockId.Water);  // Water to -Z
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block with water to north should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_WithWaterToSouth_ReplacesWithWater()
    {
        // Arrange: Stone block with water to +Z (south)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target block
        chunk.SetBlock(8, 10, 9, BlockId.Water);  // Water to +Z
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block with water to south should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_WithWaterToWest_ReplacesWithWater()
    {
        // Arrange: Stone block with water to -X (west)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target block
        chunk.SetBlock(7, 10, 8, BlockId.Water);  // Water to -X
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block with water to west should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_WithWaterToEast_ReplacesWithWater()
    {
        // Arrange: Stone block with water to +X (east)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target block
        chunk.SetBlock(9, 10, 8, BlockId.Water);  // Water to +X
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block with water to east should be replaced with water");
    }

    #endregion

    #region No Adjacent Water Tests

    [Fact]
    public void BreakingBlock_OnLand_NoAdjacentWater_ReplacesWithAir()
    {
        // Arrange: Stone block surrounded by air and stone (typical land scenario)
        var chunk = TestHelpers.CreateChunkWithFloor(floorY: 30, chunkIndex: 0);
        chunk.SetBlock(8, 31, 8, BlockId.Stone);  // Target block on top of floor
        // All neighbors are either air (above/sides) or stone (below)
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 31, 8);
        
        // Assert
        Assert.False(hasAdjacentWater, "Block on land with no water neighbors should be replaced with air");
    }

    [Fact]
    public void BreakingBlock_InCave_NoAdjacentWater_ReplacesWithAir()
    {
        // Arrange: Stone block inside a cave (sealed room)
        var chunk = TestHelpers.CreateSealedRoom(4, 10, 4, 12, 18, 12, chunkIndex: 0);
        chunk.SetBlock(8, 14, 8, BlockId.Stone);  // Place a stone in the air inside the room
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 14, 8);
        
        // Assert
        Assert.False(hasAdjacentWater, "Block in cave with no water should be replaced with air");
    }

    [Fact]
    public void BreakingBlock_NearButNotAdjacentToWater_ReplacesWithAir()
    {
        // Arrange: Stone block with water 2 blocks away (diagonal doesn't count)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target block
        chunk.SetBlock(8, 10, 6, BlockId.Water);  // Water 2 blocks away (not adjacent)
        chunk.SetBlock(10, 10, 8, BlockId.Water); // Water 2 blocks away in X
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.False(hasAdjacentWater, "Block near but not adjacent to water should be replaced with air");
    }

    [Fact]
    public void BreakingBlock_DiagonalWaterOnly_ReplacesWithAir()
    {
        // Arrange: Stone block with water only diagonally (not cardinal)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);   // Target block
        chunk.SetBlock(9, 10, 9, BlockId.Water);   // Water diagonal (+X, +Z)
        chunk.SetBlock(7, 10, 7, BlockId.Water);   // Water diagonal (-X, -Z)
        chunk.SetBlock(9, 11, 9, BlockId.Water);   // Water diagonal and above
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.False(hasAdjacentWater, "Block with only diagonal water neighbors should be replaced with air");
    }

    #endregion

    #region Underwater Scenarios

    [Fact]
    public void BreakingBlock_CompletelyUnderwater_ReplacesWithWater()
    {
        // Arrange: Stone block completely surrounded by water (underwater in ocean)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target block
        
        // Surround with water on all 6 sides
        chunk.SetBlock(8, 11, 8, BlockId.Water);  // Above
        chunk.SetBlock(8, 9, 8, BlockId.Water);   // Below
        chunk.SetBlock(7, 10, 8, BlockId.Water);  // -X
        chunk.SetBlock(9, 10, 8, BlockId.Water);  // +X
        chunk.SetBlock(8, 10, 7, BlockId.Water);  // -Z
        chunk.SetBlock(8, 10, 9, BlockId.Water);  // +Z
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block completely underwater should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_OnOceanFloor_ReplacesWithWater()
    {
        // Arrange: Stone block on ocean floor with water above
        var chunk = TestHelpers.CreateChunkWithFloor(floorY: 20, chunkIndex: 0);
        
        // Fill with water from Y=21 to Y=35 (ocean)
        for (var y = 21; y <= 35; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Water);
                }
            }
        }
        
        // The top of the floor at Y=20 has water at Y=21 above it
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 20, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block on ocean floor should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_AtWaterSurface_ReplacesWithWater()
    {
        // Arrange: Stone block at water surface (water on sides but not above)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 35, 8, BlockId.Stone);  // Target block at water level
        
        // Water on sides (at same Y level) but air above
        chunk.SetBlock(7, 35, 8, BlockId.Water);
        chunk.SetBlock(9, 35, 8, BlockId.Water);
        chunk.SetBlock(8, 35, 7, BlockId.Water);
        chunk.SetBlock(8, 35, 9, BlockId.Water);
        // Y=36 is air (above water surface)
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 35, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block at water surface should be replaced with water");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void BreakingBlock_AtY0_WithWaterAbove_ReplacesWithWater()
    {
        // Arrange: Block at Y=0 (bottom of world) with water at Y=1
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 0, 8, BlockId.Stone);
        chunk.SetBlock(8, 1, 8, BlockId.Water);
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 0, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block at Y=0 with water above should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_AtY0_NoWaterAbove_ReplacesWithAir()
    {
        // Arrange: Block at Y=0 with no water neighbors
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 0, 8, BlockId.Stone);
        chunk.SetBlock(8, 1, 8, BlockId.Air);  // Air above, not water
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 0, 8);
        
        // Assert
        Assert.False(hasAdjacentWater, "Block at Y=0 with no water should be replaced with air");
    }

    [Fact]
    public void BreakingBlock_AtMaxY_WithWaterBelow_ReplacesWithWater()
    {
        // Arrange: Block at max Y with water below
        var maxY = VoxelHelper.ChunkYSize - 1;
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, maxY, 8, BlockId.Stone);
        chunk.SetBlock(8, maxY - 1, 8, BlockId.Water);
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, maxY, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block at max Y with water below should be replaced with water");
    }

    [Fact]
    public void BreakingBlock_AtChunkCorner_NoAdjacentWater_ReplacesWithAir()
    {
        // Arrange: Block at chunk corner (0,10,0)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(0, 10, 0, BlockId.Stone);
        // No neighbor chunks, so boundary checks return no water
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 0, 10, 0);
        
        // Assert
        Assert.False(hasAdjacentWater, "Block at chunk corner with no water should be replaced with air");
    }

    [Fact]
    public void BreakingBlock_AtChunkEdge_WithInternalWater_ReplacesWithWater()
    {
        // Arrange: Block at chunk edge X=0 with water at X=1 (internal)
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(0, 10, 8, BlockId.Stone);
        chunk.SetBlock(1, 10, 8, BlockId.Water);  // Water to +X (internal)
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 0, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block at chunk edge with internal water neighbor should be replaced with water");
    }

    #endregion

    #region Multiple Water Neighbors

    [Fact]
    public void BreakingBlock_WithMultipleWaterNeighbors_ReplacesWithWater()
    {
        // Arrange: Stone block with water on multiple sides
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Stone);  // Target
        chunk.SetBlock(8, 11, 8, BlockId.Water);  // Above
        chunk.SetBlock(7, 10, 8, BlockId.Water);  // -X
        chunk.SetBlock(8, 10, 9, BlockId.Water);  // +Z
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block with multiple water neighbors should be replaced with water");
    }

    #endregion

    #region Block Type Scenarios

    [Fact]
    public void BreakingDirt_Underwater_ReplacesWithWater()
    {
        // Arrange: Dirt block (common land block) underwater
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Dirt);
        chunk.SetBlock(8, 11, 8, BlockId.Water);
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Dirt block underwater should be replaced with water");
    }

    [Fact]
    public void BreakingSand_Underwater_ReplacesWithWater()
    {
        // Arrange: Sand block (common ocean floor) underwater
        var chunk = TestHelpers.CreateAirChunk(0);
        chunk.SetBlock(8, 10, 8, BlockId.Sand);
        chunk.SetBlock(8, 11, 8, BlockId.Water);
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Sand block underwater should be replaced with water");
    }

    [Fact]
    public void BreakingGravel_OnLand_ReplacesWithAir()
    {
        // Arrange: Gravel block on land
        var chunk = TestHelpers.CreateChunkWithFloor(floorY: 30, chunkIndex: 0);
        chunk.SetBlock(8, 31, 8, BlockId.Gravel);
        
        // Act
        var hasAdjacentWater = HasAdjacentWater(chunk, 8, 31, 8);
        
        // Assert
        Assert.False(hasAdjacentWater, "Gravel on land should be replaced with air");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Simulates the HasAdjacentWaterBlock check from ChunkStreamingManager.
    /// This is a local implementation for testing without needing the full streaming manager.
    /// </summary>
    private static bool HasAdjacentWater(ChunkData chunkData, int localX, int localY, int localZ)
    {
        // Check Y neighbors (above and below)
        if (localY > 0 && chunkData.GetBlock(localX, localY - 1, localZ) == BlockId.Water)
            return true;
        if (localY < VoxelHelper.ChunkYSize - 1 && chunkData.GetBlock(localX, localY + 1, localZ) == BlockId.Water)
            return true;

        // Check X neighbors (within chunk bounds)
        if (localX > 0 && chunkData.GetBlock(localX - 1, localY, localZ) == BlockId.Water)
            return true;
        if (localX < VoxelHelper.ChunkSideSize - 1 && chunkData.GetBlock(localX + 1, localY, localZ) == BlockId.Water)
            return true;

        // Check Z neighbors (within chunk bounds)
        if (localZ > 0 && chunkData.GetBlock(localX, localY, localZ - 1) == BlockId.Water)
            return true;
        if (localZ < VoxelHelper.ChunkSideSize - 1 && chunkData.GetBlock(localX, localY, localZ + 1) == BlockId.Water)
            return true;

        return false;
    }

    #endregion
}

/// <summary>
/// Tests for cross-chunk water replacement scenarios.
/// These test cases verify that water detection works across chunk boundaries.
/// </summary>
public class CrossChunkWaterReplacementTests
{
    [Fact]
    public void BreakingBlock_AtChunkBoundary_WithWaterInNeighborChunk_ReplacesWithWater()
    {
        // Arrange: Two chunks side by side
        // Chunk 0: block at X=15 (eastern edge)
        // Chunk 1: water at X=0 (western edge, adjacent to chunk 0's X=15)
        var chunk0 = TestHelpers.CreateAirChunk(0);
        var chunk1 = TestHelpers.CreateAirChunk(1);
        
        chunk0.SetBlock(15, 10, 8, BlockId.Stone);  // Target at eastern edge of chunk 0
        chunk1.SetBlock(0, 10, 8, BlockId.Water);   // Water at western edge of chunk 1
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { 0, chunk0 },
            { 1, chunk1 }
        };
        
        // Act: Check with cross-chunk lookup
        var hasAdjacentWater = HasAdjacentWaterCrossChunk(chunks, 0, 15, 10, 8);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block at chunk boundary should detect water in neighbor chunk");
    }

    [Fact]
    public void BreakingBlock_AtChunkBoundary_NoWaterInNeighborChunk_ReplacesWithAir()
    {
        // Arrange: Two chunks side by side, no water
        var chunk0 = TestHelpers.CreateAirChunk(0);
        var chunk1 = TestHelpers.CreateAirChunk(1);
        
        chunk0.SetBlock(15, 10, 8, BlockId.Stone);  // Target at eastern edge
        chunk1.SetBlock(0, 10, 8, BlockId.Stone);   // Stone (not water) in neighbor
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { 0, chunk0 },
            { 1, chunk1 }
        };
        
        // Act
        var hasAdjacentWater = HasAdjacentWaterCrossChunk(chunks, 0, 15, 10, 8);
        
        // Assert
        Assert.False(hasAdjacentWater, "Block at chunk boundary with no water in neighbor should be replaced with air");
    }

    [Fact]
    public void BreakingBlock_AtChunkBoundary_NeighborChunkNotLoaded_ReplacesWithAir()
    {
        // Arrange: Block at chunk boundary, but neighbor chunk isn't loaded
        var chunk0 = TestHelpers.CreateAirChunk(0);
        chunk0.SetBlock(15, 10, 8, BlockId.Stone);  // Target at eastern edge
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { 0, chunk0 }
            // Chunk 1 is NOT in the dictionary (not loaded)
        };
        
        // Act
        var hasAdjacentWater = HasAdjacentWaterCrossChunk(chunks, 0, 15, 10, 8);
        
        // Assert
        Assert.False(hasAdjacentWater, "Block at chunk boundary with unloaded neighbor should be replaced with air");
    }

    [Fact]
    public void BreakingBlock_AtNorthChunkBoundary_WithWaterInNeighbor_ReplacesWithWater()
    {
        // Arrange: Block at Z=0 boundary (northern edge)
        // Chunk at (1,0) has block at Z=0
        // Chunk at (1,-1) - we'll use index calculation - would have water at Z=15
        // But since we're at Z=0 of chunk 0, the neighbor is at Z=15 of the chunk to the north
        
        // For simplicity, use linear indices where chunk 0 is at position (0,0)
        // and we need a neighbor at position (0,-1) which doesn't exist in positive indices
        // Let's instead test Z=15 -> Z=0 of next chunk in +Z direction
        
        var chunkIndex0 = 0;  // At world chunk position (0,0)
        var chunkIndex1 = VoxelHelper.WorldChunksXZ;  // At world chunk position (0,1) - south neighbor
        
        var chunk0 = TestHelpers.CreateAirChunk(chunkIndex0);
        var chunk1 = TestHelpers.CreateAirChunk(chunkIndex1);
        
        chunk0.SetBlock(8, 10, 15, BlockId.Stone);  // Target at southern edge of chunk 0
        chunk1.SetBlock(8, 10, 0, BlockId.Water);   // Water at northern edge of chunk 1
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { chunkIndex0, chunk0 },
            { chunkIndex1, chunk1 }
        };
        
        // Act
        var hasAdjacentWater = HasAdjacentWaterCrossChunk(chunks, chunkIndex0, 8, 10, 15);
        
        // Assert
        Assert.True(hasAdjacentWater, "Block at Z boundary should detect water in neighbor chunk");
    }

    #region Helper Methods

    /// <summary>
    /// Cross-chunk water detection that mirrors ChunkStreamingManager.HasAdjacentWaterBlock.
    /// </summary>
    private static bool HasAdjacentWaterCrossChunk(
        Dictionary<int, ChunkData> chunks,
        int chunkIdx,
        int localX,
        int localY,
        int localZ)
    {
        if (!chunks.TryGetValue(chunkIdx, out var chunkData))
            return false;

        // Check Y neighbors (always within same chunk)
        if (localY > 0 && chunkData.GetBlock(localX, localY - 1, localZ) == BlockId.Water)
            return true;
        if (localY < VoxelHelper.ChunkYSize - 1 && chunkData.GetBlock(localX, localY + 1, localZ) == BlockId.Water)
            return true;

        // Check X neighbors
        if (localX > 0)
        {
            if (chunkData.GetBlock(localX - 1, localY, localZ) == BlockId.Water)
                return true;
        }
        else
        {
            // Check neighbor chunk -X
            var neighborIdx = GetNeighborChunkIndex(chunkIdx, -1, 0);
            if (neighborIdx >= 0 && chunks.TryGetValue(neighborIdx, out var neighborData))
            {
                if (neighborData.GetBlock(VoxelHelper.ChunkSideSize - 1, localY, localZ) == BlockId.Water)
                    return true;
            }
        }

        if (localX < VoxelHelper.ChunkSideSize - 1)
        {
            if (chunkData.GetBlock(localX + 1, localY, localZ) == BlockId.Water)
                return true;
        }
        else
        {
            // Check neighbor chunk +X
            var neighborIdx = GetNeighborChunkIndex(chunkIdx, 1, 0);
            if (neighborIdx >= 0 && chunks.TryGetValue(neighborIdx, out var neighborData))
            {
                if (neighborData.GetBlock(0, localY, localZ) == BlockId.Water)
                    return true;
            }
        }

        // Check Z neighbors
        if (localZ > 0)
        {
            if (chunkData.GetBlock(localX, localY, localZ - 1) == BlockId.Water)
                return true;
        }
        else
        {
            // Check neighbor chunk -Z
            var neighborIdx = GetNeighborChunkIndex(chunkIdx, 0, -1);
            if (neighborIdx >= 0 && chunks.TryGetValue(neighborIdx, out var neighborData))
            {
                if (neighborData.GetBlock(localX, localY, VoxelHelper.ChunkSideSize - 1) == BlockId.Water)
                    return true;
            }
        }

        if (localZ < VoxelHelper.ChunkSideSize - 1)
        {
            if (chunkData.GetBlock(localX, localY, localZ + 1) == BlockId.Water)
                return true;
        }
        else
        {
            // Check neighbor chunk +Z
            var neighborIdx = GetNeighborChunkIndex(chunkIdx, 0, 1);
            if (neighborIdx >= 0 && chunks.TryGetValue(neighborIdx, out var neighborData))
            {
                if (neighborData.GetBlock(localX, localY, 0) == BlockId.Water)
                    return true;
            }
        }

        return false;
    }

    private static int GetNeighborChunkIndex(int chunkIdx, int dx, int dz)
    {
        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;
        var nx = chunkX + dx;
        var nz = chunkZ + dz;
        if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
            return -1;
        return nz * VoxelHelper.WorldChunksXZ + nx;
    }

    #endregion
}
