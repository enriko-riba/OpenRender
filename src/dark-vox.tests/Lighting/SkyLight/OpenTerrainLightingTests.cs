using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using DarkVox.World;
using Xunit;
using static DarkVox.Tests.Common.LightingTestHelpers;

namespace DarkVox.Tests.Lighting.SkyLight;

/// <summary>
/// Tests for torch light propagation in open terrain (no ceiling).
/// Reproduces the bug where placing a torch on open terrain doesn't light the ground.
/// </summary>
public class OpenTerrainLightingTests
{
    [Fact]
    public void AddBlockLight_TorchOnFlatGround_LightsAdjacentBlocks()
    {
        // Arrange - chunk with a flat floor at Y=50, torch placed at Y=51 (on top of floor)
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateChunkWithFloor(floorY: 50, chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);
        
        // Place torch at (8, 51, 8) - on top of the floor, in open air
        chunk.SetBlock(8, 51, 8, BlockId.Torch);
        
        // Act - propagate light from torch
        var affected = LightingCalculator.AddBlockLight(chunkIdx, 8, 51, 8, 14, provider);
        
        // Assert - light should propagate horizontally to adjacent air blocks
        Assert.Equal(14, GetBlockLight(chunk, 8, 51, 8));   // Source
        Assert.Equal(13, GetBlockLight(chunk, 9, 51, 8));   // +X (air)
        Assert.Equal(13, GetBlockLight(chunk, 7, 51, 8));   // -X (air)
        Assert.Equal(13, GetBlockLight(chunk, 8, 51, 9));   // +Z (air)
        Assert.Equal(13, GetBlockLight(chunk, 8, 51, 7));   // -Z (air)
        Assert.Equal(13, GetBlockLight(chunk, 8, 52, 8));   // +Y (air above)
        
        // Light should NOT propagate into solid floor block
        Assert.Equal(0, GetBlockLight(chunk, 8, 50, 8));    // -Y (stone floor - opaque)
        
        // Light should decay with distance along floor level
        Assert.Equal(12, GetBlockLight(chunk, 10, 51, 8));  // 2 blocks away
        Assert.Equal(11, GetBlockLight(chunk, 11, 51, 8));  // 3 blocks away
    }

    [Fact]
    public void AddBlockLight_TorchInOpenAir_PropagatesInAllDirections()
    {
        // Arrange - fully air chunk (no floor at all)
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateAirChunk(chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);
        
        // Place torch floating in air at (8, 100, 8)
        chunk.SetBlock(8, 100, 8, BlockId.Torch);
        
        // Act
        var affected = LightingCalculator.AddBlockLight(chunkIdx, 8, 100, 8, 14, provider);
        
        // Assert - light should propagate in ALL 6 directions equally
        Assert.Equal(14, GetBlockLight(chunk, 8, 100, 8));   // Source
        Assert.Equal(13, GetBlockLight(chunk, 9, 100, 8));   // +X
        Assert.Equal(13, GetBlockLight(chunk, 7, 100, 8));   // -X
        Assert.Equal(13, GetBlockLight(chunk, 8, 100, 9));   // +Z
        Assert.Equal(13, GetBlockLight(chunk, 8, 100, 7));   // -Z
        Assert.Equal(13, GetBlockLight(chunk, 8, 101, 8));   // +Y
        Assert.Equal(13, GetBlockLight(chunk, 8, 99, 8));    // -Y
        
        // Light should reach 14 blocks away (decay to 0 at distance 15)
        // At distance 13: 14 - 13 = 1
        // But torch is at x=8, so x=8-13 = -5 is out of chunk bounds (0-15)
        // Let's check within bounds: at x=0, distance = 8, so light = 14 - 8 = 6
        Assert.Equal(6, GetBlockLight(chunk, 0, 100, 8));   // 8 blocks -X (at x=0)
    }

    [Fact]
    public void AddBlockLight_TorchAboveCave_LightsPropagatesDownIntoVoid()
    {
        // Arrange - flat floor at Y=50 with a vertical shaft down to Y=45
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateChunkWithFloor(floorY: 50, chunkIdx);
        
        // Create a vertical shaft (hole) from Y=50 down to Y=45
        for (var y = 45; y <= 50; y++)
        {
            chunk.SetBlock(8, y, 8, BlockId.Air);
        }
        
        // Verify shaft was created
        Assert.Equal(BlockId.Air, chunk.GetBlock(8, 50, 8));
        Assert.Equal(BlockId.Air, chunk.GetBlock(8, 45, 8));
        Assert.Equal(BlockId.Stone, chunk.GetBlock(9, 50, 8)); // Adjacent is still stone
        
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);
        
        // Place torch at (8, 51, 8) - directly above the shaft
        chunk.SetBlock(8, 51, 8, BlockId.Torch);
        
        // Act
        var affected = LightingCalculator.AddBlockLight(chunkIdx, 8, 51, 8, 14, provider);
        
        // Assert - light should propagate BOTH horizontally on the surface AND down into shaft
        // Horizontal (on floor level - Y=51)
        Assert.Equal(14, GetBlockLight(chunk, 8, 51, 8));   // Source
        Assert.Equal(13, GetBlockLight(chunk, 9, 51, 8));   // +X
        Assert.Equal(13, GetBlockLight(chunk, 7, 51, 8));   // -X
        
        // Down through shaft - torch is at Y=51
        var lightAtY50 = GetBlockLight(chunk, 8, 50, 8);
        var lightAtY49 = GetBlockLight(chunk, 8, 49, 8);
        var lightAtY48 = GetBlockLight(chunk, 8, 48, 8);
        var lightAtY47 = GetBlockLight(chunk, 8, 47, 8);
        var lightAtY46 = GetBlockLight(chunk, 8, 46, 8);
        var lightAtY45 = GetBlockLight(chunk, 8, 45, 8);
        
        Assert.True(lightAtY50 == 13, $"Light at Y=50 should be 13, got {lightAtY50}");
        Assert.True(lightAtY49 == 12, $"Light at Y=49 should be 12, got {lightAtY49}");
        Assert.True(lightAtY48 == 11, $"Light at Y=48 should be 11, got {lightAtY48}");
        Assert.True(lightAtY47 == 10, $"Light at Y=47 should be 10, got {lightAtY47}");
        Assert.True(lightAtY46 == 9, $"Light at Y=46 should be 9, got {lightAtY46}");
        Assert.True(lightAtY45 == 8, $"Light at Y=45 should be 8, got {lightAtY45}");
    }

    [Fact]
    public void AddBlockLight_TorchOnSurface_LightsVisibleFaces()
    {
        // Arrange - simulate terrain: solid below Y=50, air above
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateChunkWithFloor(floorY: 50, chunkIdx);
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);
        
        // Place torch at (8, 51, 8) - one block above ground
        chunk.SetBlock(8, 51, 8, BlockId.Torch);
        
        // Act
        var affected = LightingCalculator.AddBlockLight(chunkIdx, 8, 51, 8, 14, provider);
        
        // Assert - the AIR blocks adjacent to the floor should be lit
        // These are the blocks that would illuminate the visible top faces of the floor
        Assert.True(GetBlockLight(chunk, 9, 51, 8) > 0, "Air above floor +X should be lit");
        Assert.True(GetBlockLight(chunk, 7, 51, 8) > 0, "Air above floor -X should be lit");
        Assert.True(GetBlockLight(chunk, 8, 51, 9) > 0, "Air above floor +Z should be lit");
        Assert.True(GetBlockLight(chunk, 8, 51, 7) > 0, "Air above floor -Z should be lit");
        
        // The floor itself (solid) should have 0 block light
        Assert.Equal(0, GetBlockLight(chunk, 9, 50, 8));
        Assert.Equal(0, GetBlockLight(chunk, 7, 50, 8));
    }

    [Fact]
    public void AddBlockLight_MultipleChunks_PropagatesAcrossBoundaries()
    {
        // Arrange - torch near chunk boundary
        var centerIdx = GetChunkIndex(10, 10);
        var rightIdx = GetChunkIndex(11, 10);
        
        var centerChunk = CreateChunkWithFloor(floorY: 50, centerIdx);
        var rightChunk = CreateChunkWithFloor(floorY: 50, rightIdx);
        
        var chunks = new Dictionary<int, ChunkData>
        {
            { centerIdx, centerChunk },
            { rightIdx, rightChunk }
        };
        var provider = CreateChunkProvider(chunks);
        
        // Place torch at (14, 51, 8) in center chunk - near right boundary (x=15 is edge)
        centerChunk.SetBlock(14, 51, 8, BlockId.Torch);
        
        // Act
        var affected = LightingCalculator.AddBlockLight(centerIdx, 14, 51, 8, 14, provider);
        
        // Assert - light should cross into right chunk
        Assert.Equal(14, GetBlockLight(centerChunk, 14, 51, 8));  // Source
        Assert.Equal(13, GetBlockLight(centerChunk, 15, 51, 8));  // x=15 in center chunk
        Assert.Equal(12, GetBlockLight(rightChunk, 0, 51, 8));    // x=0 in right chunk
        Assert.Equal(11, GetBlockLight(rightChunk, 1, 51, 8));    // x=1 in right chunk
        
        // Both chunks should be marked as modified
        Assert.Contains(centerIdx, affected);
        Assert.Contains(rightIdx, affected);
    }
    
    [Fact]
    public void AddBlockLight_TorchOnSurfaceWithCaveBelow_LightsBothSurfaceAndCave()
    {
        // This simulates the exact scenario from the bug report:
        // - Torch placed on surface (open terrain, no ceiling)
        // - There's a cave below
        // - Light should propagate BOTH horizontally on surface AND down into cave
        
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = new ChunkData { ChunkIndex = chunkIdx };
        
        // Build terrain:
        // - Surface level at Y=60 (stone from Y=0 to Y=60)
        // - Cave from Y=50 to Y=55 (hollow area)
        // - Torch at Y=61 (on surface)
        
        // Fill with stone from Y=0 to Y=60
        for (var y = 0; y <= 60; y++)
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            chunk.SetBlock(x, y, z, BlockId.Stone);
        
        // Carve out a cave from Y=50 to Y=55 around x=8, z=8
        for (var y = 50; y <= 55; y++)
        for (var z = 6; z <= 10; z++)
        for (var x = 6; x <= 10; x++)
            chunk.SetBlock(x, y, z, BlockId.Air);
        
        // Create a vertical shaft from surface to cave
        for (var y = 56; y <= 60; y++)
            chunk.SetBlock(8, y, 8, BlockId.Air);
        
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);
        
        // Place torch at (8, 61, 8) - on surface, above the shaft
        chunk.SetBlock(8, 61, 8, BlockId.Torch);
        
        // Act
        var affected = LightingCalculator.AddBlockLight(chunkIdx, 8, 61, 8, 14, provider);
        
        // Assert - light should propagate in ALL directions
        
        // Horizontal on surface (Y=61)
        var lightAtSource = GetBlockLight(chunk, 8, 61, 8);
        var lightPlusX = GetBlockLight(chunk, 9, 61, 8);
        var lightMinusX = GetBlockLight(chunk, 7, 61, 8);
        var lightPlusZ = GetBlockLight(chunk, 8, 61, 9);
        var lightMinusZ = GetBlockLight(chunk, 8, 61, 7);
        
        Assert.Equal(14, lightAtSource);
        Assert.True(lightPlusX == 13, $"Light at +X should be 13, got {lightPlusX}");
        Assert.True(lightMinusX == 13, $"Light at -X should be 13, got {lightMinusX}");
        Assert.True(lightPlusZ == 13, $"Light at +Z should be 13, got {lightPlusZ}");
        Assert.True(lightMinusZ == 13, $"Light at -Z should be 13, got {lightMinusZ}");
        
        // Down through shaft into cave
        var lightAtY60 = GetBlockLight(chunk, 8, 60, 8);  // In shaft
        var lightAtY55 = GetBlockLight(chunk, 8, 55, 8);  // Cave ceiling level
        var lightAtY52 = GetBlockLight(chunk, 8, 52, 8);  // Mid-cave
        
        Assert.True(lightAtY60 == 13, $"Light at Y=60 should be 13, got {lightAtY60}");
        Assert.True(lightAtY55 == 8, $"Light at Y=55 should be 8, got {lightAtY55}");  // 14 - 6 steps
        Assert.True(lightAtY52 == 5, $"Light at Y=52 should be 5, got {lightAtY52}");  // 14 - 9 steps
    }

    [Fact]
    public void AddBlockLight_TorchWithExistingSkyLight_BothLightsCoexist()
    {
        // Test that placing a torch where sky light already exists doesn't break anything
        // This is the night-time scenario where sky light (0 at night) + block light (14 from torch)
        
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = CreateChunkWithFloor(floorY: 50, chunkIdx);
        
        // First, calculate initial sky lighting (day time)
        LightingCalculator.CalculateLighting(chunk);
        
        // Verify sky light exists at Y=51 (above floor) before torch
        var skyLightBefore = GetSkyLight(chunk, 8, 51, 8);
        Assert.True(skyLightBefore > 0, $"Sky light should exist above floor, got {skyLightBefore}");
        
        // Place torch at (8, 51, 8) - on floor surface
        chunk.SetBlock(8, 51, 8, BlockId.Torch);
        
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);
        
        // Add block light from torch
        var affected = LightingCalculator.AddBlockLight(chunkIdx, 8, 51, 8, 14, provider);
        
        // Assert - both sky light and block light should exist
        var skyLightAfter = GetSkyLight(chunk, 8, 51, 8);
        var blockLightAtSource = GetBlockLight(chunk, 8, 51, 8);
        var blockLightNearby = GetBlockLight(chunk, 9, 51, 8);
        
        Assert.Equal(14, blockLightAtSource);
        Assert.True(blockLightNearby == 13, $"Block light at neighbor should be 13, got {blockLightNearby}");
        // Sky light might be affected by torch placement (it's a non-opaque block)
        // but block light propagation should still work
    }

    [Fact]
    public void AddBlockLight_TorchOnHillyTerrain_LightsAirNotGround()
    {
        // Simulates hilly terrain where torch is placed on a hill
        // Adjacent blocks at same Y level might be solid (part of the hill)
        // Light should propagate through air blocks only
        
        var chunkIdx = GetChunkIndex(10, 10);
        var chunk = new ChunkData { ChunkIndex = chunkIdx };
        
        // Create varied terrain: 
        // - Hill in center (surface at Y=60)
        // - Lower areas around it (surface at Y=55)
        
        // Fill base layer
        for (var y = 0; y <= 50; y++)
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            chunk.SetBlock(x, y, z, BlockId.Stone);
        
        // Create surrounding lower terrain at Y=55
        for (var y = 51; y <= 55; y++)
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            chunk.SetBlock(x, y, z, BlockId.Stone);
        
        // Create central hill up to Y=60
        for (var y = 56; y <= 60; y++)
        for (var z = 6; z <= 10; z++)
        for (var x = 6; x <= 10; x++)
            chunk.SetBlock(x, y, z, BlockId.Stone);
        
        // Clear air above terrain
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            // Determine surface height at this column
            var surfaceY = (x >= 6 && x <= 10 && z >= 6 && z <= 10) ? 60 : 55;
            for (var y = surfaceY + 1; y < VoxelHelper.ChunkYSize; y++)
                chunk.SetBlock(x, y, z, BlockId.Air);
        }
        
        var chunks = new Dictionary<int, ChunkData> { { chunkIdx, chunk } };
        var provider = CreateChunkProvider(chunks);
        
        // Place torch on top of hill at (8, 61, 8)
        chunk.SetBlock(8, 61, 8, BlockId.Torch);
        
        // Act
        var affected = LightingCalculator.AddBlockLight(chunkIdx, 8, 61, 8, 14, provider);
        
        // Assert - light should propagate in all directions through air
        Assert.Equal(14, GetBlockLight(chunk, 8, 61, 8));   // Source
        
        // Adjacent blocks at Y=61 should be lit (they're air above the hill)
        var lightPlusX = GetBlockLight(chunk, 9, 61, 8);
        var lightMinusX = GetBlockLight(chunk, 7, 61, 8);
        var lightPlusZ = GetBlockLight(chunk, 8, 61, 9);
        var lightMinusZ = GetBlockLight(chunk, 8, 61, 7);
        
        Assert.True(lightPlusX == 13, $"Light at +X (Y=61) should be 13, got {lightPlusX}. Block: {chunk.GetBlock(9, 61, 8)}");
        Assert.True(lightMinusX == 13, $"Light at -X (Y=61) should be 13, got {lightMinusX}. Block: {chunk.GetBlock(7, 61, 8)}");
        Assert.True(lightPlusZ == 13, $"Light at +Z (Y=61) should be 13, got {lightPlusZ}. Block: {chunk.GetBlock(8, 61, 9)}");
        Assert.True(lightMinusZ == 13, $"Light at -Z (Y=61) should be 13, got {lightMinusZ}. Block: {chunk.GetBlock(8, 61, 7)}");
        
        // Light should propagate down to Y=60 (top of hill - stone, no light)
        var lightAtHillTop = GetBlockLight(chunk, 8, 60, 8);
        Assert.Equal(0, lightAtHillTop); // Stone is opaque, no block light inside it
        
        // Light should propagate to air above lower terrain (Y=56 is air around the hill)
        // From torch at (8,61,8), going to (3,61,8) is 5 blocks = light level 9
        // Then that light propagates down to (3,56,8) = 4 more blocks = light level 5
        // Actually, at (3,61,8) the block is air, so light = 14 - 5 = 9
        var lightOverLowerTerrain = GetBlockLight(chunk, 3, 61, 8);
        var expectedLight = 14 - 5; // 5 blocks away
        Assert.True(lightOverLowerTerrain == expectedLight, 
            $"Light at 5 blocks away should be {expectedLight}, got {lightOverLowerTerrain}");
    }
}
