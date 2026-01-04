using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using DarkVox.World;
using Xunit;
using static DarkVox.Tests.Common.LightingTestHelpers;

namespace DarkVox.Tests.Lighting.SkyLight;

/// <summary>
/// Tests for the sealed underground room lighting bug.
/// 
/// SCENARIO: Player digs an underground room with shaft openings to the sky.
/// When all openings are sealed by placing opaque blocks, the room interior
/// should become dark (sky light = 0). However, a bug causes sky light to
/// persist in the sealed room because RecalculateLightingAroundBlock doesn't
/// properly remove stale horizontal sky light propagation.
/// 
/// REPRO STEPS:
/// 1. Create a room at y=45 with 4 shaft openings to the sky
/// 2. Sky light propagates down through shafts and laterally into the room
/// 3. Seal all 4 openings by placing stone blocks
/// 4. Room interior should now be dark, but remains lit
/// 
/// ROOT CAUSE: RecalculateLightingAroundBlock calls CalculateLighting on affected
/// chunks, which re-initializes sky light from direct sky columns. However, blocks
/// that previously received horizontal sky light propagation retain their stale
/// values because CalculateLighting only clears and re-propagates from sky columns,
/// not from previously propagated interior blocks.
/// </summary>
public class SealedRoomLightingTests
{
    private const int MaxLight = 15;

    /// <summary>
    /// Creates a room with a single shaft opening to test sealing behavior.
    /// Room is at y=40-50 with shaft at center going up to y=100.
    /// </summary>
    private static ChunkData CreateRoomWithShaft(int chunkIndex = 0)
    {
        var chunk = new ChunkData { ChunkIndex = chunkIndex };

        // Fill everything with stone first
        for (var y = 0; y <= 100; y++)
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            chunk.SetBlock(x, y, z, BlockId.Stone);
        }

        // Carve out room interior at y=41-49 (floor at 40, ceiling at 50)
        // Room from x=4-11, z=4-11
        for (var y = 41; y <= 49; y++)
        for (var z = 5; z <= 10; z++)
        for (var x = 5; x <= 10; x++)
        {
            chunk.SetBlock(x, y, z, BlockId.Air);
        }

        // Carve shaft from room ceiling (y=50) up to y=100
        // Shaft at center x=7-8, z=7-8
        for (var y = 50; y <= 100; y++)
        for (var z = 7; z <= 8; z++)
        for (var x = 7; x <= 8; x++)
        {
            chunk.SetBlock(x, y, z, BlockId.Air);
        }

        chunk.RecalculateSurfaceHeights();
        return chunk;
    }

    /// <summary>
    /// Creates a room with 4 shaft openings in the corners, matching the user's scenario.
    /// </summary>
    private static ChunkData CreateRoomWith4Shafts(int chunkIndex = 0)
    {
        var chunk = new ChunkData { ChunkIndex = chunkIndex };

        // Fill everything with stone from y=0 to y=100
        for (var y = 0; y <= 100; y++)
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            chunk.SetBlock(x, y, z, BlockId.Stone);
        }

        // Carve out room at y=41-49 (floor at 40, ceiling at 50)
        // Room from x=3-12, z=3-12
        for (var y = 41; y <= 49; y++)
        for (var z = 4; z <= 11; z++)
        for (var x = 4; x <= 11; x++)
        {
            chunk.SetBlock(x, y, z, BlockId.Air);
        }

        // Create 4 shaft openings at corners of the room ceiling
        // Each shaft is 2x2 and goes from room ceiling up to surface
        int[,] shaftPositions = { { 4, 4 }, { 4, 10 }, { 10, 4 }, { 10, 10 } };

        for (var i = 0; i < 4; i++)
        {
            var sx = shaftPositions[i, 0];
            var sz = shaftPositions[i, 1];
            
            // Carve 2x2 shaft from ceiling to surface
            for (var y = 50; y <= 100; y++)
            {
                chunk.SetBlock(sx, y, sz, BlockId.Air);
                chunk.SetBlock(sx + 1, y, sz, BlockId.Air);
                chunk.SetBlock(sx, y, sz + 1, BlockId.Air);
                chunk.SetBlock(sx + 1, y, sz + 1, BlockId.Air);
            }
        }

        chunk.RecalculateSurfaceHeights();
        return chunk;
    }

    [Fact]
    public void RoomWithShaft_HasSkyLightInInterior()
    {
        // Arrange
        var chunk = CreateRoomWithShaft();

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - room interior should have sky light via shaft
        // Shaft is at x=7-8, z=7-8, room interior at y=45
        var shaftLight = GetSkyLight(chunk, 7, 45, 7);
        Assert.True(shaftLight > 0, $"Room below shaft should have sky light, got {shaftLight}");

        // Light should propagate horizontally into room
        var interiorLight = GetSkyLight(chunk, 5, 45, 7);
        Assert.True(interiorLight > 0, $"Room interior should have propagated sky light, got {interiorLight}");
    }

    [Fact]
    public void SealedShaft_RoomShouldBeDark()
    {
        // Arrange - room with open shaft
        var chunk = CreateRoomWithShaft();
        LightingCalculator.CalculateLighting(chunk);

        // Verify light exists before sealing
        var lightBefore = GetSkyLight(chunk, 5, 45, 7);
        Assert.True(lightBefore > 0, "Room should have light before sealing");

        // Act - seal the shaft at y=50 (room ceiling level)
        for (var z = 7; z <= 8; z++)
        for (var x = 7; x <= 8; x++)
        {
            chunk.SetBlock(x, 50, z, BlockId.Stone);
        }

        // IMPORTANT: Recalculate surface heights after placing blocks
        // This simulates what happens in the real game via SetBlockWithHeightUpdate
        chunk.RecalculateSurfaceHeights();

        // Recalculate lighting
        LightingCalculator.CalculateLighting(chunk);

        // Assert - room should be completely dark
        var lightAfter = GetSkyLight(chunk, 5, 45, 7);
        Assert.True(lightAfter == 0, 
            $"Sealed room should be dark (sky light 0), got {lightAfter}. BUG: Sky light persists after sealing shaft.");

        // Check multiple positions to ensure the whole room is dark
        Assert.True(GetSkyLight(chunk, 7, 45, 7) == 0, "Center of room should be dark");
        Assert.True(GetSkyLight(chunk, 8, 45, 8) == 0, "Another center position should be dark");
        Assert.True(GetSkyLight(chunk, 6, 45, 6) == 0, "Corner should be dark");
    }

    [Fact]
    public void RecalculateLightingAroundBlock_SealingShaft_DarkensRoom()
    {
        // Arrange - this is the actual API used when player places blocks
        var chunkIdx = 0;
        var chunk = CreateRoomWithShaft(chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);

        LightingCalculator.CalculateLighting(chunk);

        // Verify light exists
        var lightBefore = GetSkyLight(chunk, 5, 45, 7);
        Assert.True(lightBefore > 0, "Room should have light before sealing");

        // Act - seal the shaft one block at a time using RecalculateLightingAroundBlock
        // This simulates what happens when player places blocks
        for (var z = 7; z <= 8; z++)
        for (var x = 7; x <= 8; x++)
        {
            chunk.SetBlock(x, 50, z, BlockId.Stone);
            // In real game, SetBlockWithHeightUpdate handles this; simulate it here
            chunk.RecalculateSurfaceHeights();
            LightingCalculator.RecalculateLightingAroundBlock(chunkIdx, x, 50, z, provider);
        }

        // Assert - room should be dark after all blocks are placed
        var lightAfter = GetSkyLight(chunk, 5, 45, 7);
        Assert.True(lightAfter == 0,
            $"Room should be dark after sealing all shaft blocks via RecalculateLightingAroundBlock, got {lightAfter}. BUG: Incremental lighting update doesn't properly darken enclosed spaces.");
    }

    [Fact]
    public void RoomWith4Shafts_AllSealed_ShouldBeDark()
    {
        // Arrange - this is the exact scenario from the bug report
        var chunkIdx = 0;
        var chunk = CreateRoomWith4Shafts(chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);

        LightingCalculator.CalculateLighting(chunk);

        // Verify room is lit from all 4 shafts
        var centerLight = GetSkyLight(chunk, 7, 45, 7);
        Assert.True(centerLight > 0, $"Room center should have light from shafts, got {centerLight}");

        // Act - seal all 4 shafts
        int[,] shaftPositions = { { 4, 4 }, { 4, 10 }, { 10, 4 }, { 10, 10 } };
        for (var i = 0; i < 4; i++)
        {
            var sx = shaftPositions[i, 0];
            var sz = shaftPositions[i, 1];
            
            // Seal 2x2 shaft at ceiling level (y=50)
            for (var dx = 0; dx <= 1; dx++)
            for (var dz = 0; dz <= 1; dz++)
            {
                chunk.SetBlock(sx + dx, 50, sz + dz, BlockId.Stone);
                // Simulate what happens in real game via SetBlockWithHeightUpdate
                chunk.RecalculateSurfaceHeights();
                LightingCalculator.RecalculateLightingAroundBlock(chunkIdx, sx + dx, 50, sz + dz, provider);
            }
        }

        // Assert - room should be completely dark
        var centerLightAfter = GetSkyLight(chunk, 7, 45, 7);
        Assert.True(centerLightAfter == 0,
            $"Room center should be dark after sealing all 4 shafts, got {centerLightAfter}. BUG: This is the exact scenario from the bug report.");

        // Check all corners of the room
        Assert.True(GetSkyLight(chunk, 5, 45, 5) == 0, "Room corner (5,45,5) should be dark");
        Assert.True(GetSkyLight(chunk, 5, 45, 10) == 0, "Room corner (5,45,10) should be dark");
        Assert.True(GetSkyLight(chunk, 10, 45, 5) == 0, "Room corner (10,45,5) should be dark");
        Assert.True(GetSkyLight(chunk, 10, 45, 10) == 0, "Room corner (10,45,10) should be dark");
    }

    [Fact]
    public void PartiallySealed_3of4Shafts_ShouldStillHaveLight()
    {
        // Arrange
        var chunkIdx = 0;
        var chunk = CreateRoomWith4Shafts(chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);

        LightingCalculator.CalculateLighting(chunk);

        // Act - seal only 3 of 4 shafts
        int[,] shaftPositions = { { 4, 4 }, { 4, 10 }, { 10, 4 } }; // Leave (10,10) open
        for (var i = 0; i < 3; i++)
        {
            var sx = shaftPositions[i, 0];
            var sz = shaftPositions[i, 1];
            
            for (var dx = 0; dx <= 1; dx++)
            for (var dz = 0; dz <= 1; dz++)
            {
                chunk.SetBlock(sx + dx, 50, sz + dz, BlockId.Stone);
                chunk.RecalculateSurfaceHeights();
                LightingCalculator.RecalculateLightingAroundBlock(chunkIdx, sx + dx, 50, sz + dz, provider);
            }
        }

        // Assert - room should still have some light from remaining open shaft
        var lightNearOpenShaft = GetSkyLight(chunk, 10, 45, 10);
        Assert.True(lightNearOpenShaft > 0,
            $"Room near open shaft should still have light, got {lightNearOpenShaft}");

        // Light should propagate from the remaining shaft
        var centerLight = GetSkyLight(chunk, 7, 45, 7);
        Assert.True(centerLight >= 0, "Center should have some propagated light or be dark depending on distance");
    }

    [Fact]
    public void UnsealShaft_LightReturns()
    {
        // Arrange - seal the shaft first
        var chunkIdx = 0;
        var chunk = CreateRoomWithShaft(chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);

        LightingCalculator.CalculateLighting(chunk);

        // Seal the shaft
        for (var z = 7; z <= 8; z++)
        for (var x = 7; x <= 8; x++)
        {
            chunk.SetBlock(x, 50, z, BlockId.Stone);
        }
        chunk.RecalculateSurfaceHeights();
        LightingCalculator.CalculateLighting(chunk);

        var lightSealed = GetSkyLight(chunk, 5, 45, 7);

        // Act - break one block to unseal
        chunk.SetBlock(7, 50, 7, BlockId.Air);
        chunk.RecalculateSurfaceHeights();
        LightingCalculator.RecalculateLightingAroundBlock(chunkIdx, 7, 50, 7, provider);

        // Assert - light should return through the opening
        var lightUnsealed = GetSkyLight(chunk, 7, 45, 7);
        Assert.True(lightUnsealed > lightSealed,
            $"Light should return after unsealing, was {lightSealed}, now {lightUnsealed}");
    }

    [Fact]
    public void DeepUnderground_SkyLightShouldNotLeak()
    {
        // Arrange - create a room with no direct sky access at all
        var chunk = new ChunkData { ChunkIndex = 0 };

        // Fill everything with stone
        for (var y = 0; y <= 100; y++)
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            chunk.SetBlock(x, y, z, BlockId.Stone);
        }

        // Carve out a completely sealed room at y=20-30
        for (var y = 21; y <= 29; y++)
        for (var z = 5; z <= 10; z++)
        for (var x = 5; x <= 10; x++)
        {
            chunk.SetBlock(x, y, z, BlockId.Air);
        }

        chunk.RecalculateSurfaceHeights();

        // Act
        LightingCalculator.CalculateLighting(chunk);

        // Assert - room should have no sky light
        Assert.True(GetSkyLight(chunk, 7, 25, 7) == 0, "Sealed underground room should have 0 sky light");
        Assert.True(GetSkyLight(chunk, 5, 25, 5) == 0, "Corner of sealed room should have 0 sky light");
    }

    [Fact]
    public void MultipleRecalculations_ShouldBeIdempotent()
    {
        // Arrange - ensure recalculating lighting multiple times gives same result
        var chunk = CreateRoomWithShaft();

        // Act - calculate lighting multiple times
        LightingCalculator.CalculateLighting(chunk);
        var light1 = GetSkyLight(chunk, 5, 45, 7);

        LightingCalculator.CalculateLighting(chunk);
        var light2 = GetSkyLight(chunk, 5, 45, 7);

        LightingCalculator.CalculateLighting(chunk);
        var light3 = GetSkyLight(chunk, 5, 45, 7);

        // Assert
        Assert.Equal(light1, light2);
        Assert.Equal(light2, light3);
    }
}
