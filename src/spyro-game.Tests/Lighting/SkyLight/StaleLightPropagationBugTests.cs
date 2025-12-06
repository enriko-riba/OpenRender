using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.Common.LightingTestHelpers;

namespace SpyroGame.Tests.Lighting.SkyLight;

/// <summary>
/// Tests for the stale light propagation bug where light persists in sealed areas
/// because neighbor chunks have cached light that gets re-propagated after recalculation.
/// 
/// The bug: When RecalculateLightingAroundBlock is called:
/// 1. It recalculates a 3x3 area of chunks around the placed block
/// 2. Then calls PropagateNeighborLight to propagate between chunks
/// 3. BUT: If a chunk OUTSIDE the 3x3 area has stale light, it can propagate back
///    into the sealed area via the chunks at the edge of the 3x3 area
/// 
/// This test verifies that stale light from non-recalculated neighbor chunks
/// doesn't leak back into sealed areas.
/// </summary>
public class StaleLightPropagationBugTests
{
    private const int CenterChunkX = 300;
    private const int CenterChunkZ = 300;

    /// <summary>
    /// This test exposes the bug where stale light from a chunk OUTSIDE the recalculation
    /// radius can propagate back into a sealed area.
    /// 
    /// Setup:
    /// - Left chunk (299,300): has sky shaft at x=14-15 near boundary
    /// - Center chunk (300,300): has room that receives light from left chunk
    /// 
    /// When the tunnel is sealed at the boundary:
    /// - The 3x3 area gets recalculated
    /// - But PropagateNeighborLight might re-introduce stale light
    /// 
    /// The key insight: The bug manifests when PropagateNeighborLight runs AFTER
    /// CalculateLighting and re-introduces light from a chunk that still has light
    /// at its boundary (because the light source is still present, just the path is blocked).
    /// </summary>
    [Fact]
    public void StaleLightFromOutsideRecalculationArea_DoesNotLeakBack()
    {
        // Arrange - Create 2 chunks: left has sky access, center gets light via tunnel
        var leftIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var centerIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);

        var leftChunk = new ChunkData { ChunkIndex = leftIdx };
        var centerChunk = new ChunkData { ChunkIndex = centerIdx };

        // Fill chunks with stone
        FillWithStone(leftChunk, 60);
        FillWithStone(centerChunk, 60);

        // Create tunnel in both chunks at y=50, z=8
        CreateHorizontalTunnel(leftChunk, y: 50, z: 8);
        CreateHorizontalTunnel(centerChunk, y: 50, z: 8);

        // Create sky access in LEFT chunk at x=14-15 (near boundary)
        // This ensures sky light is strong at the boundary
        for (var y = 52; y < 384; y++)
        {
            leftChunk.SetBlock(14, y, 8, BlockId.Air);
            leftChunk.SetBlock(15, y, 8, BlockId.Air);
        }

        // Calculate initial lighting
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(centerChunk);

        // Propagate light from left to center
        LightingCalculator.PropagateNeighborLight(leftChunk, centerChunk, 1, 0);

        // Verify light reaches center chunk before blocking
        var centerLightBefore = GetSkyLight(centerChunk, 0, 50, 8);
        Assert.True(centerLightBefore > 0,
            $"Center chunk boundary should have sky light before blocking. Got: {centerLightBefore}");

        // Act - Place blocks at CENTER chunk boundary (x=0) to seal the tunnel
        centerChunk.SetBlock(0, 50, 8, BlockId.Stone);
        centerChunk.SetBlock(0, 51, 8, BlockId.Stone);

        ChunkDataProvider getChunk = idx => {
            if (idx == leftIdx) return leftChunk;
            if (idx == centerIdx) return centerChunk;
            return null;
        };

        // Use RecalculateLightingAroundBlock
        LightingCalculator.RecalculateLightingAroundBlock(
            centerIdx,
            0, 50, 8,
            getChunk
        );

        // Assert - Center chunk interior should be dark
        // The blocks at x=0 should stop light from entering
        var centerLightAfter = GetSkyLight(centerChunk, 8, 50, 8);
        
        Assert.True(centerLightAfter == 0,
            $"Center chunk should be DARK after sealing. Got: {centerLightAfter}. " +
            "BUG: Light is leaking through the sealed boundary.");
    }

    /// <summary>
    /// Similar test but with torch light instead of sky light.
    /// Torch is in left chunk near boundary, tunnel is sealed at center chunk boundary.
    /// Center chunk should become dark.
    /// </summary>
    [Fact]
    public void StaleTorchLightFromOutsideRecalculationArea_DoesNotLeakBack()
    {
        // Arrange
        var leftIdx = GetChunkIndex(CenterChunkX - 1, CenterChunkZ);
        var centerIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);

        var leftChunk = new ChunkData { ChunkIndex = leftIdx };
        var centerChunk = new ChunkData { ChunkIndex = centerIdx };

        FillWithStone(leftChunk, 60);
        FillWithStone(centerChunk, 60);

        CreateHorizontalTunnel(leftChunk, y: 50, z: 8);
        CreateHorizontalTunnel(centerChunk, y: 50, z: 8);

        // Place torch in left chunk near boundary (x=14)
        PlaceTorch(leftChunk, 14, 50, 8);

        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(centerChunk);

        LightingCalculator.PropagateNeighborLight(leftChunk, centerChunk, 1, 0);

        var centerLightBefore = GetBlockLight(centerChunk, 0, 50, 8);
        Assert.True(centerLightBefore > 0,
            $"Center chunk boundary should have torch light before blocking. Got: {centerLightBefore}");

        // Act - Seal tunnel at center chunk boundary
        centerChunk.SetBlock(0, 50, 8, BlockId.Stone);
        centerChunk.SetBlock(0, 51, 8, BlockId.Stone);

        ChunkDataProvider getChunk = idx => {
            if (idx == leftIdx) return leftChunk;
            if (idx == centerIdx) return centerChunk;
            return null;
        };

        LightingCalculator.RecalculateLightingAroundBlock(centerIdx, 0, 50, 8, getChunk);

        // Assert
        var centerLightAfter = GetBlockLight(centerChunk, 8, 50, 8);
        Assert.True(centerLightAfter == 0,
            $"Center chunk interior should be DARK after sealing. Got: {centerLightAfter}. " +
            "BUG: Torch light is leaking through sealed boundary.");
    }

    /// <summary>
    /// Test the exact user bug scenario: Room with shafts to sky, all sealed, room should be dark.
    /// This is a simpler version that tests the core issue.
    /// </summary>
    [Fact]
    public void RoomWith4Shafts_AllSealed_RoomBecomesDark_WithExternalLightSources()
    {
        // Arrange - Single chunk with a room and 4 shafts to sky
        var centerIdx = GetChunkIndex(CenterChunkX, CenterChunkZ);
        var centerChunk = new ChunkData { ChunkIndex = centerIdx };

        // Fill with stone up to y=80
        FillWithStone(centerChunk, 80);

        // Create room at y=45 (from 4,45,4 to 12,49,12)
        CreateRoom(centerChunk, 4, 45, 4, 12, 49, 12);

        // Create 4 shafts from room corners to sky (y=50 to y=383)
        // Shaft 1: corner (5,50,5)
        for (var y = 50; y < 384; y++)
        {
            centerChunk.SetBlock(5, y, 5, BlockId.Air);
        }
        // Connect to room
        centerChunk.SetBlock(5, 49, 5, BlockId.Air);
        
        // Shaft 2: corner (5,50,11)
        for (var y = 50; y < 384; y++)
        {
            centerChunk.SetBlock(5, y, 11, BlockId.Air);
        }
        centerChunk.SetBlock(5, 49, 11, BlockId.Air);
        
        // Shaft 3: corner (11,50,5)
        for (var y = 50; y < 384; y++)
        {
            centerChunk.SetBlock(11, y, 5, BlockId.Air);
        }
        centerChunk.SetBlock(11, 49, 5, BlockId.Air);
        
        // Shaft 4: corner (11,50,11)
        for (var y = 50; y < 384; y++)
        {
            centerChunk.SetBlock(11, y, 11, BlockId.Air);
        }
        centerChunk.SetBlock(11, 49, 11, BlockId.Air);

        // Calculate lighting
        LightingCalculator.CalculateLighting(centerChunk);

        // Verify room has sky light before sealing
        var roomLightBefore = GetSkyLight(centerChunk, 8, 47, 8);
        Assert.True(roomLightBefore > 0,
            $"Room should have sky light before sealing. Got: {roomLightBefore}");

        // Act - Seal all 4 shafts at y=49 (the openings into the room)
        centerChunk.SetBlock(5, 49, 5, BlockId.Stone);
        centerChunk.SetBlock(5, 49, 11, BlockId.Stone);
        centerChunk.SetBlock(11, 49, 5, BlockId.Stone);
        centerChunk.SetBlock(11, 49, 11, BlockId.Stone);

        ChunkDataProvider getChunk = idx => {
            if (idx == centerIdx) return centerChunk;
            return null;
        };

        // Recalculate for each sealed position
        LightingCalculator.RecalculateLightingAroundBlock(centerIdx, 5, 49, 5, getChunk);
        LightingCalculator.RecalculateLightingAroundBlock(centerIdx, 5, 49, 11, getChunk);
        LightingCalculator.RecalculateLightingAroundBlock(centerIdx, 11, 49, 5, getChunk);
        LightingCalculator.RecalculateLightingAroundBlock(centerIdx, 11, 49, 11, getChunk);

        // Assert - Room should be dark
        var roomLightAfter = GetSkyLight(centerChunk, 8, 47, 8);
        Assert.True(roomLightAfter == 0,
            $"Room should be DARK after sealing all shafts. Got: {roomLightAfter}.");
        
        // Check corners too
        Assert.True(GetSkyLight(centerChunk, 5, 47, 5) == 0, "Room corner (5,47,5) should be dark");
        Assert.True(GetSkyLight(centerChunk, 5, 47, 11) == 0, "Room corner (5,47,11) should be dark");
        Assert.True(GetSkyLight(centerChunk, 11, 47, 5) == 0, "Room corner (11,47,5) should be dark");
        Assert.True(GetSkyLight(centerChunk, 11, 47, 11) == 0, "Room corner (11,47,11) should be dark");
    }

    // Helper methods
    private static void FillWithStone(ChunkData chunk, int maxY)
    {
        for (var y = 0; y < maxY; y++)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }
    }

    private static void CreateHorizontalTunnel(ChunkData chunk, int y, int z)
    {
        for (var x = 0; x < 16; x++)
        {
            chunk.SetBlock(x, y, z, BlockId.Air);
            chunk.SetBlock(x, y + 1, z, BlockId.Air);
        }
    }

    private static void CreateRoom(ChunkData chunk, int x1, int y1, int z1, int x2, int y2, int z2)
    {
        for (var y = y1; y <= y2; y++)
        {
            for (var z = z1; z <= z2; z++)
            {
                for (var x = x1; x <= x2; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Air);
                }
            }
        }
    }

    private static void CreateVerticalShaft(ChunkData chunk, int x, int y, int z)
    {
        // Create a 2-high shaft
        chunk.SetBlock(x, y, z, BlockId.Air);
        chunk.SetBlock(x, y + 1, z, BlockId.Air);
    }
}
