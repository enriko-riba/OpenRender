using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.LightingTestHelpers;

namespace SpyroGame.Tests;

/// <summary>
/// Tests for AddBlockLight cross-chunk light propagation when placing light sources.
/// This tests the fix for torches at chunk boundaries not illuminating neighbor chunks.
/// </summary>
public class AddBlockLightTests
{
    [Fact]
    public void AddBlockLight_TorchInCenter_PropagatesWithinChunk()
    {
        // Arrange
        var chunk = CreateAirChunk(chunkIndex: 0);
        var chunks = new Dictionary<int, ChunkData> { { 0, chunk } };
        var provider = CreateChunkProvider(chunks);

        // Act - Place torch at center of chunk
        chunk.SetBlock(8, 200, 8, BlockId.Torch);
        var affected = LightingCalculator.AddBlockLight(0, 8, 200, 8, 14, provider);

        // Assert
        Assert.Equal(14, GetBlockLight(chunk, 8, 200, 8));
        Assert.Equal(13, GetBlockLight(chunk, 9, 200, 8));
        Assert.Equal(13, GetBlockLight(chunk, 7, 200, 8));
        Assert.Contains(0, affected);
    }

    [Fact]
    public void AddBlockLight_TorchAtBoundary_PropagatesAcrossChunks()
    {
        // Arrange - Two adjacent chunks (center and right neighbor)
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        
        var centerChunk = CreateAirChunk(centerIdx);
        var rightChunk = CreateAirChunk(rightIdx);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, centerChunk },
            { rightIdx, rightChunk }
        };
        var provider = CreateChunkProvider(chunks);

        // Act - Place torch at right boundary (x=15) of center chunk
        centerChunk.SetBlock(15, 200, 8, BlockId.Torch);
        var affected = LightingCalculator.AddBlockLight(centerIdx, 15, 200, 8, 14, provider);

        // Assert - Light should propagate to both chunks
        Assert.Equal(14, GetBlockLight(centerChunk, 15, 200, 8)); // Source
        Assert.Equal(13, GetBlockLight(centerChunk, 14, 200, 8)); // -X in center
        Assert.Equal(13, GetBlockLight(rightChunk, 0, 200, 8));   // +X crosses to right chunk
        
        Assert.Contains(centerIdx, affected);
        Assert.Contains(rightIdx, affected);
    }

    [Fact]
    public void AddBlockLight_TorchAtCorner_PropagatesTo4Chunks()
    {
        // Arrange - Four chunks meeting at a corner
        var blIdx = GetChunkIndex(10, 10);  // Bottom-left
        var brIdx = GetChunkIndex(11, 10);  // Bottom-right
        var tlIdx = GetChunkIndex(10, 11);  // Top-left
        var trIdx = GetChunkIndex(11, 11);  // Top-right
        
        var blChunk = CreateAirChunk(blIdx);
        var brChunk = CreateAirChunk(brIdx);
        var tlChunk = CreateAirChunk(tlIdx);
        var trChunk = CreateAirChunk(trIdx);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { blIdx, blChunk },
            { brIdx, brChunk },
            { tlIdx, tlChunk },
            { trIdx, trChunk }
        };
        var provider = CreateChunkProvider(chunks);

        // Act - Place torch at corner (x=15, z=15) of bottom-left chunk
        blChunk.SetBlock(15, 200, 15, BlockId.Torch);
        var affected = LightingCalculator.AddBlockLight(blIdx, 15, 200, 15, 14, provider);

        // Assert - Light should reach all 4 chunks
        Assert.Equal(14, GetBlockLight(blChunk, 15, 200, 15)); // Source
        Assert.Equal(13, GetBlockLight(brChunk, 0, 200, 15));  // +X
        Assert.Equal(13, GetBlockLight(tlChunk, 15, 200, 0));  // +Z
        
        // Diagonal neighbor receives light through intermediate blocks
        // Light path: BL(15,200,15) -> BR(0,200,15) -> BR(0,200,14)... etc
        // OR: BL(15,200,15) -> TL(15,200,0) -> TR(0,200,0)
        // After enough propagation steps, TR should have some light
        Assert.True(GetBlockLight(trChunk, 0, 200, 0) > 0, "Diagonal chunk should receive light");
        
        Assert.Contains(blIdx, affected);
        Assert.Contains(brIdx, affected);
        Assert.Contains(tlIdx, affected);
    }

    [Fact]
    public void AddBlockLight_FullRadius_PropagatesCorrectDistance()
    {
        // Arrange - Multiple chunks in a row to test full 14-block propagation
        var chunk0 = CreateAirChunk(GetChunkIndex(10, 10));
        var chunk1 = CreateAirChunk(GetChunkIndex(11, 10));
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { GetChunkIndex(10, 10), chunk0 },
            { GetChunkIndex(11, 10), chunk1 }
        };
        var provider = CreateChunkProvider(chunks);

        // Act - Place torch at x=15 (boundary)
        chunk0.SetBlock(15, 200, 8, BlockId.Torch);
        LightingCalculator.AddBlockLight(GetChunkIndex(10, 10), 15, 200, 8, 14, provider);

        // Assert - Light decays correctly across boundary
        // At x=15 in chunk0: 14 (source)
        // At x=0 in chunk1: 13 (1 block away)
        // At x=12 in chunk1: 1 (13 blocks away from source: 15->0 is 1 block, 0->12 is 12 more = 13 total)
        Assert.Equal(14, GetBlockLight(chunk0, 15, 200, 8));
        Assert.Equal(13, GetBlockLight(chunk1, 0, 200, 8));
        Assert.Equal(1, GetBlockLight(chunk1, 12, 200, 8));   // 13 blocks away = light level 1
        Assert.Equal(0, GetBlockLight(chunk1, 13, 200, 8));   // 14 blocks away = out of range
    }

    [Fact]
    public void AddBlockLight_BlockedByOpaqueBlock_DoesNotPropagate()
    {
        // Arrange
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        
        var centerChunk = CreateAirChunk(centerIdx);
        var rightChunk = CreateAirChunk(rightIdx);
        
        // Place stone wall at boundary in right chunk
        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                rightChunk.SetBlock(0, y, z, BlockId.Stone);
            }
        }
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, centerChunk },
            { rightIdx, rightChunk }
        };
        var provider = CreateChunkProvider(chunks);

        // Act - Place torch at right boundary
        centerChunk.SetBlock(15, 200, 8, BlockId.Torch);
        LightingCalculator.AddBlockLight(centerIdx, 15, 200, 8, 14, provider);

        // Assert - Light should not propagate through stone wall
        Assert.Equal(14, GetBlockLight(centerChunk, 15, 200, 8));
        Assert.Equal(0, GetBlockLight(rightChunk, 0, 200, 8)); // Blocked by stone
        Assert.Equal(0, GetBlockLight(rightChunk, 1, 200, 8)); // No light beyond wall
    }

    [Fact]
    public void AddBlockLight_Glowstone_PropagatesFullRange()
    {
        // Arrange - Glowstone emits light level 15
        var chunk = CreateAirChunk(0);
        var chunks = new Dictionary<int, ChunkData> { { 0, chunk } };
        var provider = CreateChunkProvider(chunks);

        // Act
        chunk.SetBlock(8, 200, 8, BlockId.Glowstone);
        LightingCalculator.AddBlockLight(0, 8, 200, 8, 15, provider);

        // Assert - Glowstone propagates further than torch
        Assert.Equal(15, GetBlockLight(chunk, 8, 200, 8));   // Source
        Assert.Equal(14, GetBlockLight(chunk, 9, 200, 8));   // 1 block
        Assert.Equal(1, GetBlockLight(chunk, 8, 186, 8));    // 14 blocks away (-Y)
    }

    [Fact]
    public void AddBlockLight_ReturnsCorrectAffectedChunks()
    {
        // Arrange - torch at x=1 won't reach right neighbor (needs 15 blocks to cross)
        // but WILL reach left neighbor (1 block to boundary + 13 more = 14 total range)
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        var leftIdx = GetChunkIndex(9, 10);
        
        var centerChunk = CreateAirChunk(centerIdx);
        var rightChunk = CreateAirChunk(rightIdx);
        var leftChunk = CreateAirChunk(leftIdx);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, centerChunk },
            { rightIdx, rightChunk },
            { leftIdx, leftChunk }
        };
        var provider = CreateChunkProvider(chunks);

        // Act - Place torch at x=8, z=8 - this is 8 blocks from left boundary (reaches left)
        // and 7 blocks from right boundary (14-7=7 light at boundary, so it reaches right too)
        // To NOT reach neighbors, we need torch far from both boundaries
        // Actually, with 14 light range, a torch at x=8 reaches x=8-14=-6 (wraps to left chunk)
        // and x=8+14=22 (into right chunk). So it DOES affect neighbors.
        
        // Let's test that the affected set is correctly populated
        centerChunk.SetBlock(8, 200, 8, BlockId.Torch);
        var affected = LightingCalculator.AddBlockLight(centerIdx, 8, 200, 8, 14, provider);

        // Assert - Center chunk definitely affected
        Assert.Contains(centerIdx, affected);
        
        // A torch with range 14 at x=8 reaches x=-6 (left chunk) and x=22 (right chunk)
        // So both neighbors should be affected
        Assert.Contains(leftIdx, affected);
        Assert.Contains(rightIdx, affected);
    }
}
