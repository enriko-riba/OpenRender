using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.Common.LightingTestHelpers;

namespace SpyroGame.Tests.Lighting.BlockLight;

/// <summary>
/// Tests for light removal when light sources are removed.
/// Critical for preventing "ghost light" that remains after torch/glowstone is broken.
/// </summary>
public class LightRemovalTests
{
    private const int TestChunkX = 300;
    private const int TestChunkZ = 300;

    [Fact]
    public void LightRemoval_TorchRemoved_LightCleared()
    {
        // Arrange
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateAirChunk(chunkIdx);
        PlaceTorch(chunk, 8, 200, 8);
        LightingCalculator.CalculateLighting(chunk);
        
        // Verify initial state
        Assert.Equal(14, GetBlockLight(chunk, 8, 200, 8));
        
        var chunks = new Dictionary<int, ChunkData> { [chunkIdx] = chunk };
        var provider = CreateChunkProvider(chunks);
        
        // Act - Remove the torch (simulate by removing its light)
        var modifiedChunks = LightingCalculator.RemoveBlockLight(
            chunkIdx, 8, 200, 8, 14, provider);

        // Assert - Light should be cleared
        Assert.Equal(0, GetBlockLight(chunk, 8, 200, 8));
        Assert.Contains(chunkIdx, modifiedChunks);
    }

    [Fact]
    public void LightRemoval_PropagatedLightAlsoCleared()
    {
        // Arrange
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateAirChunk(chunkIdx);
        PlaceTorch(chunk, 8, 200, 8);
        LightingCalculator.CalculateLighting(chunk);
        
        // Verify propagated light exists
        Assert.Equal(13, GetBlockLight(chunk, 9, 200, 8));
        Assert.Equal(12, GetBlockLight(chunk, 10, 200, 8));
        
        var chunks = new Dictionary<int, ChunkData> { [chunkIdx] = chunk };
        var provider = CreateChunkProvider(chunks);
        
        // Act
        LightingCalculator.RemoveBlockLight(chunkIdx, 8, 200, 8, 14, provider);

        // Assert - All propagated light should be cleared
        Assert.Equal(0, GetBlockLight(chunk, 9, 200, 8));
        Assert.Equal(0, GetBlockLight(chunk, 10, 200, 8));
        Assert.Equal(0, GetBlockLight(chunk, 8, 201, 8));
        Assert.Equal(0, GetBlockLight(chunk, 8, 199, 8));
    }

    [Fact]
    public void LightRemoval_OtherLightSourcesPreserved()
    {
        // Arrange - Two torches
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateAirChunk(chunkIdx);
        PlaceTorch(chunk, 4, 200, 8);  // Torch 1
        PlaceTorch(chunk, 12, 200, 8); // Torch 2
        LightingCalculator.CalculateLighting(chunk);
        
        var chunks = new Dictionary<int, ChunkData> { [chunkIdx] = chunk };
        var provider = CreateChunkProvider(chunks);
        
        // Act - Remove torch 1
        chunk.SetBlock(4, 200, 8, BlockId.Air); // Remove the torch block
        LightingCalculator.RemoveBlockLight(chunkIdx, 4, 200, 8, 14, provider);

        // Assert - Torch 2's light should be preserved
        Assert.Equal(14, GetBlockLight(chunk, 12, 200, 8));
        Assert.True(GetBlockLight(chunk, 11, 200, 8) > 0);
        
        // And the area should be re-lit by torch 2's propagation
        // The middle area (around x=8) should have some light from torch 2
        Assert.True(GetBlockLight(chunk, 8, 200, 8) > 0, 
            "Middle area should be re-lit by remaining torch");
    }

    [Fact]
    public void LightRemoval_CrossChunkBoundary()
    {
        // Arrange - Torch near boundary
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var centerChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        
        var leftChunk = CreateAirChunk(leftChunkIdx);
        var centerChunk = CreateAirChunk(centerChunkIdx);
        
        // Torch at x=15 (boundary) of left chunk
        PlaceTorch(leftChunk, 15, 200, 8);
        
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(centerChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, centerChunk, 1, 0);
        
        // Verify light propagated to center chunk
        Assert.True(GetBlockLight(centerChunk, 0, 200, 8) > 0);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            [leftChunkIdx] = leftChunk,
            [centerChunkIdx] = centerChunk
        };
        var provider = CreateChunkProvider(chunks);
        
        // Act - Remove the torch
        leftChunk.SetBlock(15, 200, 8, BlockId.Air);
        var modifiedChunks = LightingCalculator.RemoveBlockLight(
            leftChunkIdx, 15, 200, 8, 14, provider);

        // Assert - Light should be cleared from both chunks
        Assert.Equal(0, GetBlockLight(leftChunk, 15, 200, 8));
        Assert.Equal(0, GetBlockLight(centerChunk, 0, 200, 8));
        Assert.Contains(leftChunkIdx, modifiedChunks);
        Assert.Contains(centerChunkIdx, modifiedChunks);
    }

    [Fact]
    public void LightRemoval_DoesNotAffectSkyLight()
    {
        // Arrange
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateAirChunk(chunkIdx);
        PlaceTorch(chunk, 8, 200, 8);
        LightingCalculator.CalculateLighting(chunk);
        
        // Verify sky light exists
        var initialSkyLight = GetSkyLight(chunk, 9, 200, 8);
        Assert.True(initialSkyLight > 0);
        
        var chunks = new Dictionary<int, ChunkData> { [chunkIdx] = chunk };
        var provider = CreateChunkProvider(chunks);
        
        // Act
        LightingCalculator.RemoveBlockLight(chunkIdx, 8, 200, 8, 14, provider);

        // Assert - Sky light should be unaffected
        Assert.Equal(initialSkyLight, GetSkyLight(chunk, 9, 200, 8));
    }

    [Fact]
    public void LightRemoval_StopsAtOpaqueBlocks()
    {
        // Arrange - Torch with a wall nearby
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateAirChunk(chunkIdx);
        PlaceTorch(chunk, 8, 200, 8);
        
        // Place a stone wall
        chunk.SetBlock(10, 200, 8, BlockId.Stone);
        
        // Place another torch behind the wall (should not be affected)
        PlaceTorch(chunk, 12, 200, 8);
        
        LightingCalculator.CalculateLighting(chunk);
        
        var chunks = new Dictionary<int, ChunkData> { [chunkIdx] = chunk };
        var provider = CreateChunkProvider(chunks);
        
        // Act - Remove torch 1
        chunk.SetBlock(8, 200, 8, BlockId.Air);
        LightingCalculator.RemoveBlockLight(chunkIdx, 8, 200, 8, 14, provider);

        // Assert - Torch 2's light behind wall should be preserved
        Assert.Equal(14, GetBlockLight(chunk, 12, 200, 8));
    }

    [Fact]
    public void LightRemoval_GlowstoneMaxLight()
    {
        // Arrange
        var chunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);
        var chunk = CreateAirChunk(chunkIdx);
        PlaceGlowstone(chunk, 8, 200, 8);
        LightingCalculator.CalculateLighting(chunk);
        
        Assert.Equal(15, GetBlockLight(chunk, 8, 200, 8));
        
        var chunks = new Dictionary<int, ChunkData> { [chunkIdx] = chunk };
        var provider = CreateChunkProvider(chunks);
        
        // Act - Remove glowstone (light level 15)
        chunk.SetBlock(8, 200, 8, BlockId.Air);
        LightingCalculator.RemoveBlockLight(chunkIdx, 8, 200, 8, 15, provider);

        // Assert
        Assert.Equal(0, GetBlockLight(chunk, 8, 200, 8));
        Assert.Equal(0, GetBlockLight(chunk, 9, 200, 8));
    }
}
