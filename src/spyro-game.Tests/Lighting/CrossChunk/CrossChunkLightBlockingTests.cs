using SpyroGame.World;
using Xunit;
using static SpyroGame.Tests.Common.LightingTestHelpers;

namespace SpyroGame.Tests.Lighting.CrossChunk;

/// <summary>
/// Tests for light removal when blocks are placed in a NEIGHBOR chunk that blocks light
/// from reaching the CURRENT chunk. This covers scenarios where the player is standing
/// in one chunk but places blocks in an adjacent chunk to seal off light.
/// </summary>
public class CrossChunkLightBlockingTests
{
    private const int TestChunkX = 300;
    private const int TestChunkZ = 300;

    /// <summary>
    /// When placing an opaque block in the LEFT chunk that blocks sky light from reaching
    /// the RIGHT chunk (where the player might be standing), the right chunk should become dark.
    /// 
    /// Layout:
    /// - Left chunk has vertical shaft to sky at x=14-15, z=7-8 (at boundary)
    /// - Horizontal tunnel connects both chunks at boundary (left x=15, right x=0)
    /// - Player in right chunk, places blocks at left chunk x=15 to seal the tunnel (both levels)
    /// - Right chunk should become dark
    /// </summary>
    [Fact]
    public void BlockPlacedInLeftChunk_RemovesLightFromRightChunk()
    {
        // Arrange
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var rightChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);

        var leftChunk = new ChunkData { ChunkIndex = leftChunkIdx };
        var rightChunk = new ChunkData { ChunkIndex = rightChunkIdx };

        // Fill both chunks with stone up to y=60
        FillWithStone(leftChunk, 60);
        FillWithStone(rightChunk, 60);

        // Create horizontal tunnel at y=50, z=7-8 in both chunks (2 blocks high, 2 wide in z)
        for (var x = 0; x < 16; x++)
        {
            leftChunk.SetBlock(x, 50, 7, BlockId.Air);
            leftChunk.SetBlock(x, 50, 8, BlockId.Air);
            leftChunk.SetBlock(x, 51, 7, BlockId.Air);
            leftChunk.SetBlock(x, 51, 8, BlockId.Air);
            
            rightChunk.SetBlock(x, 50, 7, BlockId.Air);
            rightChunk.SetBlock(x, 50, 8, BlockId.Air);
            rightChunk.SetBlock(x, 51, 7, BlockId.Air);
            rightChunk.SetBlock(x, 51, 8, BlockId.Air);
        }

        // Create vertical shaft in LEFT chunk at x=14-15, z=7-8 going to sky (at boundary)
        // This ensures sky light reaches the boundary at x=15
        for (var y = 52; y < 384; y++)
        {
            leftChunk.SetBlock(14, y, 7, BlockId.Air);
            leftChunk.SetBlock(14, y, 8, BlockId.Air);
            leftChunk.SetBlock(15, y, 7, BlockId.Air);
            leftChunk.SetBlock(15, y, 8, BlockId.Air);
        }

        // Calculate initial lighting
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Verify light reaches right chunk
        var rightTunnelLight = GetSkyLight(rightChunk, 8, 50, 8);
        Assert.True(rightTunnelLight > 0, 
            $"Right chunk should have sky light before blocking. Got: {rightTunnelLight}");

        // Record light value at positions where we'll place the blocks
        // Block all 4 positions at the boundary (y=50,51 and z=7,8)
        var blockPositions = new[] {
            (15, 50, 7), (15, 50, 8),
            (15, 51, 7), (15, 51, 8)
        };
        var oldLights = new int[blockPositions.Length];
        for (var i = 0; i < blockPositions.Length; i++)
        {
            var (bx, by, bz) = blockPositions[i];
            oldLights[i] = GetSkyLight(leftChunk, bx, by, bz);
        }

        // Act - Place blocks in LEFT chunk at boundary to seal tunnel (all 4 positions)
        foreach (var (bx, by, bz) in blockPositions)
        {
            leftChunk.SetBlock(bx, by, bz, BlockId.Stone);
        }

        // Create chunk provider for cross-chunk operations
        ChunkDataProvider getChunk = idx => {
            if (idx == leftChunkIdx) return leftChunk;
            if (idx == rightChunkIdx) return rightChunk;
            return null;
        };

        // Remove sky light starting from the blocked positions
        for (var i = 0; i < blockPositions.Length; i++)
        {
            if (oldLights[i] > 0)
            {
                var (bx, by, bz) = blockPositions[i];
                LightingCalculator.RemoveSkyLight(leftChunkIdx, bx, by, bz, oldLights[i], getChunk);
            }
        }

        // Assert - Right chunk should now be dark
        var rightTunnelLightAfter = GetSkyLight(rightChunk, 8, 50, 8);
        Assert.Equal(0, rightTunnelLightAfter);
        
        // Position just past the boundary should also be dark
        var rightBoundaryLight = GetSkyLight(rightChunk, 0, 50, 8);
        Assert.Equal(0, rightBoundaryLight);
    }

    /// <summary>
    /// Same scenario but with torch light instead of sky light.
    /// Torch in left chunk, player seals tunnel from left chunk side,
    /// right chunk should become dark.
    /// </summary>
    [Fact]
    public void TorchInLeftChunk_BlockPlacedInLeftChunk_RemovesLightFromRightChunk()
    {
        // Arrange
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var rightChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);

        var leftChunk = new ChunkData { ChunkIndex = leftChunkIdx };
        var rightChunk = new ChunkData { ChunkIndex = rightChunkIdx };

        // Fill both chunks with stone
        FillWithStone(leftChunk, 60);
        FillWithStone(rightChunk, 60);

        // Create horizontal tunnel in both chunks
        CreateHorizontalTunnel(leftChunk, y: 50, z: 8);
        CreateHorizontalTunnel(rightChunk, y: 50, z: 8);

        // Place torch in LEFT chunk
        PlaceTorch(leftChunk, 8, 50, 8);

        // Calculate initial lighting
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Verify light reaches right chunk
        var rightTunnelLight = GetBlockLight(rightChunk, 5, 50, 8);
        Assert.True(rightTunnelLight > 0, 
            $"Right chunk should have torch light before blocking. Got: {rightTunnelLight}");

        // Record light at block position
        var oldBlockLightAtPos = GetBlockLight(leftChunk, 15, 50, 8);
        var oldBlockLightAtPos2 = GetBlockLight(leftChunk, 15, 51, 8);

        // Act - Place blocks in LEFT chunk at boundary
        leftChunk.SetBlock(15, 50, 8, BlockId.Stone);
        leftChunk.SetBlock(15, 51, 8, BlockId.Stone);

        ChunkDataProvider getChunk = idx => {
            if (idx == leftChunkIdx) return leftChunk;
            if (idx == rightChunkIdx) return rightChunk;
            return null;
        };

        // Remove block light
        if (oldBlockLightAtPos > 0)
        {
            LightingCalculator.RemoveBlockLight(leftChunkIdx, 15, 50, 8, oldBlockLightAtPos, getChunk);
        }
        if (oldBlockLightAtPos2 > 0)
        {
            LightingCalculator.RemoveBlockLight(leftChunkIdx, 15, 51, 8, oldBlockLightAtPos2, getChunk);
        }

        // Assert - Right chunk should now be dark
        var rightTunnelLightAfter = GetBlockLight(rightChunk, 5, 50, 8);
        Assert.Equal(0, rightTunnelLightAfter);
    }

    /// <summary>
    /// Light source (torch) is in RIGHT chunk, blocks placed in LEFT chunk
    /// to seal tunnel. The part of LEFT chunk beyond the seal should become dark.
    /// </summary>
    [Fact]
    public void TorchInRightChunk_BlockPlacedInLeftChunk_RemovesLightFromLeftChunk()
    {
        // Arrange
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var rightChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);

        var leftChunk = new ChunkData { ChunkIndex = leftChunkIdx };
        var rightChunk = new ChunkData { ChunkIndex = rightChunkIdx };

        FillWithStone(leftChunk, 60);
        FillWithStone(rightChunk, 60);

        CreateHorizontalTunnel(leftChunk, y: 50, z: 8);
        CreateHorizontalTunnel(rightChunk, y: 50, z: 8);

        // Place torch in RIGHT chunk (near boundary)
        PlaceTorch(rightChunk, 2, 50, 8);

        // Calculate lighting and propagate
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(rightChunk, leftChunk, -1, 0);

        // Verify light reaches left chunk
        var leftTunnelLight = GetBlockLight(leftChunk, 10, 50, 8);
        Assert.True(leftTunnelLight > 0, 
            $"Left chunk should have torch light before blocking. Got: {leftTunnelLight}");

        // Record light at block positions (LEFT chunk, at boundary x=15 won't block much,
        // let's block at x=14 to test the scenario)
        var oldBlockLight1 = GetBlockLight(leftChunk, 15, 50, 8);
        var oldBlockLight2 = GetBlockLight(leftChunk, 15, 51, 8);

        // Act - Place blocks in LEFT chunk at boundary
        leftChunk.SetBlock(15, 50, 8, BlockId.Stone);
        leftChunk.SetBlock(15, 51, 8, BlockId.Stone);

        ChunkDataProvider getChunk = idx => {
            if (idx == leftChunkIdx) return leftChunk;
            if (idx == rightChunkIdx) return rightChunk;
            return null;
        };

        if (oldBlockLight1 > 0)
        {
            LightingCalculator.RemoveBlockLight(leftChunkIdx, 15, 50, 8, oldBlockLight1, getChunk);
        }
        if (oldBlockLight2 > 0)
        {
            LightingCalculator.RemoveBlockLight(leftChunkIdx, 15, 51, 8, oldBlockLight2, getChunk);
        }

        // Assert - Left chunk (far from boundary) should now be dark
        var leftTunnelLightAfter = GetBlockLight(leftChunk, 10, 50, 8);
        Assert.Equal(0, leftTunnelLightAfter);
    }

    /// <summary>
    /// Sky light comes from LEFT chunk, multiple blocks placed in LEFT chunk
    /// should remove all light from RIGHT chunk through proper cross-chunk propagation.
    /// </summary>
    [Fact]
    public void MultipleBlocksPlacedInNeighbor_RemovesAllLightFromCurrentChunk()
    {
        // Arrange
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var rightChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);

        var leftChunk = new ChunkData { ChunkIndex = leftChunkIdx };
        var rightChunk = new ChunkData { ChunkIndex = rightChunkIdx };

        FillWithStone(leftChunk, 60);
        FillWithStone(rightChunk, 60);

        // Create a wider tunnel (2 blocks wide in Z)
        for (var x = 0; x < 16; x++)
        {
            leftChunk.SetBlock(x, 50, 7, BlockId.Air);
            leftChunk.SetBlock(x, 50, 8, BlockId.Air);
            leftChunk.SetBlock(x, 51, 7, BlockId.Air);
            leftChunk.SetBlock(x, 51, 8, BlockId.Air);
            
            rightChunk.SetBlock(x, 50, 7, BlockId.Air);
            rightChunk.SetBlock(x, 50, 8, BlockId.Air);
            rightChunk.SetBlock(x, 51, 7, BlockId.Air);
            rightChunk.SetBlock(x, 51, 8, BlockId.Air);
        }

        // Create vertical shaft in LEFT chunk at x=14-15, z=7-8 going to sky (at boundary)
        // This ensures sky light directly reaches the boundary at x=15
        for (var y = 52; y < 384; y++)
        {
            leftChunk.SetBlock(14, y, 7, BlockId.Air);
            leftChunk.SetBlock(14, y, 8, BlockId.Air);
            leftChunk.SetBlock(15, y, 7, BlockId.Air);
            leftChunk.SetBlock(15, y, 8, BlockId.Air);
        }

        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Verify light in right chunk
        var rightLight = GetSkyLight(rightChunk, 8, 50, 7);
        Assert.True(rightLight > 0, $"Right chunk should have light. Got: {rightLight}");

        ChunkDataProvider getChunk = idx => {
            if (idx == leftChunkIdx) return leftChunk;
            if (idx == rightChunkIdx) return rightChunk;
            return null;
        };

        // Record old light values at all positions we'll block
        var positions = new[] {
            (15, 50, 7), (15, 50, 8),
            (15, 51, 7), (15, 51, 8)
        };
        
        var oldLights = new int[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var (x, y, z) = positions[i];
            oldLights[i] = GetSkyLight(leftChunk, x, y, z);
        }

        // Act - Place all blocks to seal the tunnel
        foreach (var (x, y, z) in positions)
        {
            leftChunk.SetBlock(x, y, z, BlockId.Stone);
        }

        // Remove light from all positions
        for (var i = 0; i < positions.Length; i++)
        {
            if (oldLights[i] > 0)
            {
                var (x, y, z) = positions[i];
                LightingCalculator.RemoveSkyLight(leftChunkIdx, x, y, z, oldLights[i], getChunk);
            }
        }

        // Assert - Right chunk should be completely dark
        Assert.Equal(0, GetSkyLight(rightChunk, 8, 50, 7));
        Assert.Equal(0, GetSkyLight(rightChunk, 8, 50, 8));
        Assert.Equal(0, GetSkyLight(rightChunk, 0, 50, 7));
        Assert.Equal(0, GetSkyLight(rightChunk, 0, 50, 8));
    }

    /// <summary>
    /// Tests that the light removal crosses the chunk boundary properly when 
    /// sealing both levels of a 2-high tunnel.
    /// </summary>
    [Fact]
    public void LightRemovalBFS_CrossesChunkBoundary()
    {
        // Arrange
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);
        var rightChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);

        var leftChunk = new ChunkData { ChunkIndex = leftChunkIdx };
        var rightChunk = new ChunkData { ChunkIndex = rightChunkIdx };

        FillWithStone(leftChunk, 60);
        FillWithStone(rightChunk, 60);

        CreateHorizontalTunnel(leftChunk, y: 50, z: 8);
        CreateHorizontalTunnel(rightChunk, y: 50, z: 8);

        // Place torch near the boundary in left chunk
        PlaceTorch(leftChunk, 14, 50, 8);

        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);
        LightingCalculator.PropagateNeighborLight(rightChunk, leftChunk, -1, 0);

        // Verify boundary blocks have light
        var leftBoundaryLightLower = GetBlockLight(leftChunk, 15, 50, 8);
        var leftBoundaryLightUpper = GetBlockLight(leftChunk, 15, 51, 8);
        var rightBoundaryLight = GetBlockLight(rightChunk, 0, 50, 8);
        
        Assert.True(leftBoundaryLightLower > 0, $"Left boundary (lower) should have light. Got: {leftBoundaryLightLower}");
        Assert.True(leftBoundaryLightUpper > 0, $"Left boundary (upper) should have light. Got: {leftBoundaryLightUpper}");
        Assert.True(rightBoundaryLight > 0, $"Right boundary should have light. Got: {rightBoundaryLight}");

        // Get old light values BEFORE placing blocks
        var oldLightLower = leftBoundaryLightLower;
        var oldLightUpper = leftBoundaryLightUpper;
        
        // Place blocks at BOTH levels to fully seal the tunnel
        leftChunk.SetBlock(15, 50, 8, BlockId.Stone);
        leftChunk.SetBlock(15, 51, 8, BlockId.Stone);

        ChunkDataProvider getChunk = idx => {
            if (idx == leftChunkIdx) return leftChunk;
            if (idx == rightChunkIdx) return rightChunk;
            return null;
        };

        // Remove light from both positions
        LightingCalculator.RemoveBlockLight(leftChunkIdx, 15, 50, 8, oldLightLower, getChunk);
        LightingCalculator.RemoveBlockLight(leftChunkIdx, 15, 51, 8, oldLightUpper, getChunk);

        // Assert - Right chunk boundary should now be dark
        var rightBoundaryLightAfter = GetBlockLight(rightChunk, 0, 50, 8);
        var rightBoundaryLightUpperAfter = GetBlockLight(rightChunk, 0, 51, 8);
        
        Assert.Equal(0, rightBoundaryLightAfter);
        Assert.Equal(0, rightBoundaryLightUpperAfter);
    }

    /// <summary>
    /// Torch is in LEFT (neighbor) chunk. Player is in RIGHT (current) chunk.
    /// Player places a block at RIGHT chunk x=0 boundary to seal the opening.
    /// This tests the exact scenario from the bug report - blocking light from neighbor chunk
    /// by placing a block at the boundary of your current chunk.
    /// Light near the block may be HIGHER than at the block position itself (closer to torch).
    /// </summary>
    [Fact]
    public void TorchInNeighborChunk_BlockAtCurrentChunkBoundary_CurrentChunkBecomesDark()
    {
        // Arrange
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);  // Neighbor with torch
        var rightChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);     // Current chunk (player is here)

        var leftChunk = new ChunkData { ChunkIndex = leftChunkIdx };
        var rightChunk = new ChunkData { ChunkIndex = rightChunkIdx };

        // Fill both chunks with stone
        FillWithStone(leftChunk, 60);
        FillWithStone(rightChunk, 60);

        // Create horizontal tunnel in both chunks (2 blocks high at z=8)
        CreateHorizontalTunnel(leftChunk, y: 50, z: 8);
        CreateHorizontalTunnel(rightChunk, y: 50, z: 8);

        // Place torch in LEFT (neighbor) chunk - close to boundary so light is strong there
        PlaceTorch(leftChunk, 14, 50, 8);  // Torch at x=14, one block from boundary

        // Calculate initial lighting
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Verify light reaches right chunk (current chunk)
        var rightChunkLight = GetBlockLight(rightChunk, 5, 50, 8);
        Assert.True(rightChunkLight > 0, 
            $"Right chunk should have torch light before blocking. Got: {rightChunkLight}");

        // Record light values at right chunk boundary (x=0) - this is where we'll place blocks
        // These values may be LOWER than the light in left chunk at x=15 (closer to torch)
        var oldLightAtBoundaryLower = GetBlockLight(rightChunk, 0, 50, 8);
        var oldLightAtBoundaryUpper = GetBlockLight(rightChunk, 0, 51, 8);
        
        // Debug: Print light values to understand the gradient
        var leftBoundaryLight = GetBlockLight(leftChunk, 15, 50, 8);
        
        Assert.True(oldLightAtBoundaryLower > 0 || oldLightAtBoundaryUpper > 0,
            $"Should have some light at boundary. Lower:{oldLightAtBoundaryLower} Upper:{oldLightAtBoundaryUpper} LeftBoundary:{leftBoundaryLight}");

        // Act - Place blocks in RIGHT (current) chunk at boundary (x=0)
        rightChunk.SetBlock(0, 50, 8, BlockId.Stone);
        rightChunk.SetBlock(0, 51, 8, BlockId.Stone);

        ChunkDataProvider getChunk = idx => {
            if (idx == leftChunkIdx) return leftChunk;
            if (idx == rightChunkIdx) return rightChunk;
            return null;
        };

        // Remove block light starting from the blocked positions in RIGHT chunk
        if (oldLightAtBoundaryLower > 0)
        {
            LightingCalculator.RemoveBlockLight(rightChunkIdx, 0, 50, 8, oldLightAtBoundaryLower, getChunk);
        }
        if (oldLightAtBoundaryUpper > 0)
        {
            LightingCalculator.RemoveBlockLight(rightChunkIdx, 0, 51, 8, oldLightAtBoundaryUpper, getChunk);
        }

        // Assert - Right chunk interior should now be dark
        var rightChunkLightAfter = GetBlockLight(rightChunk, 5, 50, 8);
        var rightChunkLightAtBoundaryAfter = GetBlockLight(rightChunk, 1, 50, 8);  // Just past the block
        
        Assert.Equal(0, rightChunkLightAfter);
        Assert.Equal(0, rightChunkLightAtBoundaryAfter);
    }

    // Helper methods

    /// <summary>
    /// Test RecalculateLightingAroundBlock - the comprehensive recalculation method.
    /// Torch in neighbor chunk, place block at boundary of current chunk.
    /// Uses the full recalculation approach that finds all light sources in radius.
    /// </summary>
    [Fact]
    public void RecalculateLightingAroundBlock_TorchInNeighborChunk_CurrentChunkBecomesDark()
    {
        // Arrange
        var leftChunkIdx = GetChunkIndex(TestChunkX - 1, TestChunkZ);  // Neighbor with torch
        var rightChunkIdx = GetChunkIndex(TestChunkX, TestChunkZ);     // Current chunk (player is here)

        var leftChunk = new ChunkData { ChunkIndex = leftChunkIdx };
        var rightChunk = new ChunkData { ChunkIndex = rightChunkIdx };

        // Fill both chunks with stone
        FillWithStone(leftChunk, 60);
        FillWithStone(rightChunk, 60);

        // Create horizontal tunnel in both chunks (2 blocks high at z=8)
        CreateHorizontalTunnel(leftChunk, y: 50, z: 8);
        CreateHorizontalTunnel(rightChunk, y: 50, z: 8);

        // Place torch in LEFT (neighbor) chunk - close to boundary
        PlaceTorch(leftChunk, 14, 50, 8);

        // Calculate initial lighting
        LightingCalculator.CalculateLighting(leftChunk);
        LightingCalculator.CalculateLighting(rightChunk);
        LightingCalculator.PropagateNeighborLight(leftChunk, rightChunk, 1, 0);

        // Verify light reaches right chunk before blocking
        var rightChunkLightBefore = GetBlockLight(rightChunk, 5, 50, 8);
        Assert.True(rightChunkLightBefore > 0, 
            $"Right chunk should have torch light before blocking. Got: {rightChunkLightBefore}");

        // Act - Place blocks in RIGHT (current) chunk at boundary (x=0) - both tunnel levels
        rightChunk.SetBlock(0, 50, 8, BlockId.Stone);
        rightChunk.SetBlock(0, 51, 8, BlockId.Stone);

        ChunkDataProvider getChunk = idx => {
            if (idx == leftChunkIdx) return leftChunk;
            if (idx == rightChunkIdx) return rightChunk;
            return null;
        };

        // Use the new comprehensive recalculation method
        var affectedChunks = LightingCalculator.RecalculateLightingAroundBlock(
            rightChunkIdx,
            0, 50, 8,  // Block placed at x=0, y=50, z=8
            getChunk
        );

        // Assert - Right chunk interior should now be dark
        var rightChunkLightAfter = GetBlockLight(rightChunk, 5, 50, 8);
        var rightChunkLightNearBoundary = GetBlockLight(rightChunk, 1, 50, 8);
        
        Assert.Equal(0, rightChunkLightAfter);
        Assert.Equal(0, rightChunkLightNearBoundary);
        
        // Both chunks should be in affected set
        Assert.Contains(rightChunkIdx, affectedChunks);
        Assert.Contains(leftChunkIdx, affectedChunks);
    }

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
}
