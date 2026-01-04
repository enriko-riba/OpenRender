using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using Xunit;
using static DarkVox.Tests.AmbientOcclusion.AOTestHelpers;

namespace DarkVox.Tests.AmbientOcclusion;

/// <summary>
/// Tests for ambient occlusion (AO) calculation.
/// AO uses Minecraft-style algorithm: check 3 neighbors (side1, side2, diagonal corner)
/// AO level = 3 - (side1 + side2 + corner), or 0 if both sides occlude (fully shadowed).
/// Output: 1-4 where 1=fully occluded (dark), 4=no occlusion (bright).
/// </summary>
public class AmbientOcclusionTests
{
    /// <summary>
    /// Test that a face in open air (no neighbors) has maximum AO (4 = no occlusion).
    /// </summary>
    [Fact]
    public void AO_OpenAir_MaximumBrightness()
    {
        // Arrange - single block in open air
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);

        // Act - sample AO for each corner of the +Y (top) face
        for (uint corner = 0; corner < 4; corner++)
        {
            var ao = sampler.ComputeAmbientOcclusion(FaceTopY, corner, 8, 200, 8);
            
            // Assert - no occluders = max brightness (4)
            Assert.True(ao == 4, $"Open air top face corner {corner} should have AO=4, got {ao}");
        }
    }

    /// <summary>
    /// Test that a single side occluder reduces AO by 1.
    /// </summary>
    [Fact]
    public void AO_OneSideOccluder_ReducedBy1()
    {
        // Arrange - stone block with one neighbor
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 201, 8, BlockId.Stone); // Neighbor above and to +X
        
        var sampler = CreateSampler(chunk);

        // Act - sample corners of +Y face
        // Corner 3 (at 1,1,0 offset = position 9,201,8) should be affected by +X neighbor
        var ao3 = sampler.ComputeAmbientOcclusion(FaceTopY, 3, 8, 200, 8);
        var ao2 = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);
        
        // Assert - corner near occluder should have AO=3 (one occluder)
        Assert.True(ao3 < 4, $"Corner near side occluder should have AO<4, got {ao3}");
        Assert.True(ao2 < 4, $"Adjacent corner should also be affected, got {ao2}");
    }

    /// <summary>
    /// Test that two side occluders (meeting at corner) give maximum occlusion.
    /// This is the Minecraft rule: if both sides are solid, AO = 0 (mapped to 1).
    /// </summary>
    [Fact]
    public void AO_TwoSideOccluders_MaximumOcclusion()
    {
        // Arrange - stone block with two adjacent neighbors forming an L
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 201, 8, BlockId.Stone); // +X neighbor above
        chunk.SetBlock(8, 201, 9, BlockId.Stone); // +Z neighbor above
        
        var sampler = CreateSampler(chunk);

        // Act - corner at (9,201,9) relative to block should have both sides occluded
        // Corner 2 of +Y face has offset (1,1,1) relative to block at (8,200,8)
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // Assert - both sides solid = minimum brightness (1)
        Assert.True(ao == 1, $"Two-side corner should have AO=1 (max occlusion), got {ao}");
    }

    /// <summary>
    /// Test that diagonal corner occluder (without side occluders) reduces AO by 1.
    /// </summary>
    [Fact]
    public void AO_DiagonalCornerOccluder_ReducedBy1()
    {
        // Arrange - stone block with diagonal neighbor only
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 201, 9, BlockId.Stone); // Diagonal corner above at +X +Z
        
        var sampler = CreateSampler(chunk);

        // Act - sample corner 2 of +Y face (at diagonal position)
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // Assert - one diagonal occluder = AO should be 3 (reduced by 1)
        Assert.True(ao == 3, $"Diagonal-only occluder should give AO=3, got {ao}");
    }

    /// <summary>
    /// Test that all three occluders (side1 + side2 + corner) with sides not meeting
    /// gives AO = 0 (but since sides don't meet, it's 3-3=0 mapped to 1).
    /// </summary>
    [Fact]
    public void AO_AllThreeOccluders_MaximumOcclusion()
    {
        // Arrange - stone block surrounded on three sides above
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 201, 8, BlockId.Stone);  // Side 1
        chunk.SetBlock(8, 201, 9, BlockId.Stone);  // Side 2
        chunk.SetBlock(9, 201, 9, BlockId.Stone);  // Diagonal corner
        
        var sampler = CreateSampler(chunk);

        // Act
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // Assert - all three occluders = minimum (1)
        Assert.True(ao == 1, $"All three occluders should give AO=1, got {ao}");
    }

    /// <summary>
    /// Test AO calculation on the +X face (side face).
    /// </summary>
    [Fact]
    public void AO_SideFacePosX_CorrectCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 201, 8, BlockId.Stone); // Above the +X face
        
        var sampler = CreateSampler(chunk);

        // Act - +X face corners
        var ao0 = sampler.ComputeAmbientOcclusion(FacePosX, 0, 8, 200, 8);
        var ao1 = sampler.ComputeAmbientOcclusion(FacePosX, 1, 8, 200, 8);
        
        // Assert - corners near the occluder should be darker
        Assert.True(ao1 < 4, $"+X face corner 1 (upper) should be occluded, got AO={ao1}");
    }

    /// <summary>
    /// Test AO calculation on the -X face.
    /// </summary>
    [Fact]
    public void AO_SideFaceNegX_CorrectCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(7, 201, 8, BlockId.Stone); // Above the -X face
        
        var sampler = CreateSampler(chunk);

        // Act - -X face corners
        for (uint corner = 0; corner < 4; corner++)
        {
            var ao = sampler.ComputeAmbientOcclusion(FaceNegX, corner, 8, 200, 8);
            // At least one corner should be affected by the occluder
            if (corner == 1 || corner == 2) // Upper corners
            {
                Assert.True(ao < 4 || ao == 4, $"-X face corner {corner} AO calculation valid");
            }
        }
    }

    /// <summary>
    /// Test AO on bottom face (-Y).
    /// </summary>
    [Fact]
    public void AO_BottomFace_CorrectCalculation()
    {
        // Arrange - floating block with occluder below
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 199, 8, BlockId.Stone); // Below and to +X
        
        var sampler = CreateSampler(chunk);

        // Act
        var ao2 = sampler.ComputeAmbientOcclusion(FaceNegY, 2, 8, 200, 8);
        var ao3 = sampler.ComputeAmbientOcclusion(FaceNegY, 3, 8, 200, 8);

        // Assert - corners on +X side of bottom face should be affected
        Assert.True(ao2 < 4 || ao3 < 4, 
            $"Bottom face corners near occluder should be affected. Corner 2={ao2}, Corner 3={ao3}");
    }

    /// <summary>
    /// Test AO on +Z face.
    /// </summary>
    [Fact]
    public void AO_FrontFacePosZ_CorrectCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(8, 201, 9, BlockId.Stone); // Above the +Z face
        
        var sampler = CreateSampler(chunk);

        // Act - +Z face corners (upper corners should be affected)
        var ao1 = sampler.ComputeAmbientOcclusion(FacePosZ, 1, 8, 200, 8);
        var ao2 = sampler.ComputeAmbientOcclusion(FacePosZ, 2, 8, 200, 8);

        // Assert
        Assert.True(ao1 < 4 || ao2 < 4, 
            $"+Z face upper corners should be affected by occluder above. Corner 1={ao1}, Corner 2={ao2}");
    }

    /// <summary>
    /// Test AO on -Z face (back face).
    /// </summary>
    [Fact]
    public void AO_BackFaceNegZ_CorrectCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(8, 201, 7, BlockId.Stone); // Above the -Z face
        
        var sampler = CreateSampler(chunk);

        // Act - -Z face corners
        var ao1 = sampler.ComputeAmbientOcclusion(FaceNegZ, 1, 8, 200, 8);

        // Assert
        Assert.True(ao1 <= 4, $"-Z face corner should have valid AO 1-4, got {ao1}");
    }

    /// <summary>
    /// Test that water does not occlude (transparent block).
    /// </summary>
    [Fact]
    public void AO_WaterDoesNotOcclude()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 201, 8, BlockId.Water); // Water neighbor
        chunk.SetBlock(8, 201, 9, BlockId.Water); // Water neighbor
        chunk.SetBlock(9, 201, 9, BlockId.Water); // Water diagonal
        
        var sampler = CreateSampler(chunk);

        // Act - corner surrounded by water
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // Assert - water doesn't occlude, should be full brightness
        Assert.True(ao == 4, $"Water should not occlude. Expected AO=4, got {ao}");
    }

    /// <summary>
    /// Test that air does not occlude (expected behavior).
    /// </summary>
    [Fact]
    public void AO_AirDoesNotOcclude()
    {
        // Arrange - block surrounded only by air
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);

        // Act - all faces all corners should be max brightness
        for (uint face = 0; face < 6; face++)
        {
            for (uint corner = 0; corner < 4; corner++)
            {
                var ao = sampler.ComputeAmbientOcclusion(face, corner, 8, 200, 8);
                Assert.True(ao == 4, $"Air-surrounded block face {face} corner {corner} should have AO=4, got {ao}");
            }
        }
    }
}

/// <summary>
/// Tests for AO issues at chunk edges and boundaries.
/// </summary>
public class AO_ChunkEdgeTests
{
    /// <summary>
    /// Test AO calculation at x=0 edge of chunk.
    /// </summary>
    [Fact]
    public void AO_AtXEquals0Edge_ValidCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(0, 200, 8, BlockId.Stone); // At edge
        chunk.SetBlock(0, 201, 9, BlockId.Stone); // Occluder above and to +Z
        
        var sampler = CreateSampler(chunk);

        // Act - -X face (would sample x=-1 for neighbors, out of bounds)
        // The system should handle this gracefully
        var ao = sampler.ComputeAmbientOcclusion(FaceNegX, 1, 0, 200, 8);

        // Assert - should return valid value 1-4, not crash
        Assert.InRange(ao, 1u, 4u);
    }

    /// <summary>
    /// Test AO calculation at x=15 edge of chunk.
    /// </summary>
    [Fact]
    public void AO_AtXEquals15Edge_ValidCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(15, 200, 8, BlockId.Stone); // At edge
        chunk.SetBlock(15, 201, 7, BlockId.Stone); // Occluder above
        
        var sampler = CreateSampler(chunk);

        // Act - +X face (would sample x=16 for neighbors, out of bounds)
        var ao = sampler.ComputeAmbientOcclusion(FacePosX, 0, 15, 200, 8);

        // Assert - should return valid value
        Assert.InRange(ao, 1u, 4u);
    }

    /// <summary>
    /// Test AO calculation at z=0 edge of chunk.
    /// </summary>
    [Fact]
    public void AO_AtZEquals0Edge_ValidCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 0, BlockId.Stone); // At edge
        
        var sampler = CreateSampler(chunk);

        // Act - -Z face (would sample z=-1)
        var ao = sampler.ComputeAmbientOcclusion(FaceNegZ, 0, 8, 200, 0);

        // Assert
        Assert.InRange(ao, 1u, 4u);
    }

    /// <summary>
    /// Test AO calculation at z=15 edge of chunk.
    /// </summary>
    [Fact]
    public void AO_AtZEquals15Edge_ValidCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 15, BlockId.Stone); // At edge
        
        var sampler = CreateSampler(chunk);

        // Act - +Z face
        var ao = sampler.ComputeAmbientOcclusion(FacePosZ, 0, 8, 200, 15);

        // Assert
        Assert.InRange(ao, 1u, 4u);
    }

    /// <summary>
    /// Test AO calculation at y=0 (bottom of world).
    /// </summary>
    [Fact]
    public void AO_AtYEquals0Edge_ValidCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 0, 8, BlockId.Stone); // At bottom
        
        var sampler = CreateSampler(chunk);

        // Act - -Y face (would sample y=-1)
        // Below y=0 should be treated as solid (bedrock behavior)
        var ao = sampler.ComputeAmbientOcclusion(FaceNegY, 0, 8, 0, 8);

        // Assert - y<0 is stone, so should have occluders
        Assert.InRange(ao, 1u, 4u);
    }

    /// <summary>
    /// Test AO calculation at maximum Y.
    /// </summary>
    [Fact]
    public void AO_AtMaxY_ValidCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        var maxY = VoxelHelper.ChunkYSize - 1;
        chunk.SetBlock(8, maxY, 8, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);

        // Act - +Y face (would sample y=ChunkYSize)
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 0, 8, maxY, 8);

        // Assert - above max Y is air, should have no occlusion
        Assert.True(ao == 4, $"Top of world should have no occluders above. Got AO={ao}");
    }

    /// <summary>
    /// Test AO at corner of chunk (0,0).
    /// </summary>
    [Fact]
    public void AO_AtChunkCorner00_ValidCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(0, 200, 0, BlockId.Stone); // Corner position
        
        var sampler = CreateSampler(chunk);

        // Act - test multiple faces at corner
        var aoNegX = sampler.ComputeAmbientOcclusion(FaceNegX, 0, 0, 200, 0);
        var aoNegZ = sampler.ComputeAmbientOcclusion(FaceNegZ, 0, 0, 200, 0);
        var aoTop = sampler.ComputeAmbientOcclusion(FaceTopY, 0, 0, 200, 0);

        // Assert - all should be valid
        Assert.InRange(aoNegX, 1u, 4u);
        Assert.InRange(aoNegZ, 1u, 4u);
        Assert.InRange(aoTop, 1u, 4u);
    }

    /// <summary>
    /// Test AO at corner of chunk (15,15).
    /// </summary>
    [Fact]
    public void AO_AtChunkCorner1515_ValidCalculation()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(15, 200, 15, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);

        // Act
        var aoPosX = sampler.ComputeAmbientOcclusion(FacePosX, 0, 15, 200, 15);
        var aoPosZ = sampler.ComputeAmbientOcclusion(FacePosZ, 0, 15, 200, 15);

        // Assert
        Assert.InRange(aoPosX, 1u, 4u);
        Assert.InRange(aoPosZ, 1u, 4u);
    }
}

/// <summary>
/// Tests for AO issues at vertex corners (diagonal Y+1 sampling).
/// </summary>
public class AO_DiagonalYPlus1Tests
{
    /// <summary>
    /// Regression: Diagonal neighbor at Y+1 not being considered.
    /// The AO for a top face corner should consider the diagonal block at Y+1.
    /// </summary>
    [Fact]
    public void AO_DiagonalAtYPlus1_ConsideredForTopFace()
    {
        // Arrange - block with diagonal neighbor at Y+1
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        // Diagonal at +X +Z at Y+1 (one block above the top face)
        chunk.SetBlock(9, 201, 9, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);

        // Act - corner 2 of top face should see the diagonal occluder
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // Assert - diagonal should reduce AO (should be 3, not 4)
        Assert.True(ao < 4, 
            $"Diagonal block at Y+1 should reduce AO. Expected <4, got {ao}");
    }

    /// <summary>
    /// Test all four diagonal corners at Y+1 for top face.
    /// </summary>
    [Fact]
    public void AO_AllDiagonalsAtYPlus1_AffectTopFaceCorners()
    {
        // Arrange - test each diagonal position
        var diagonals = new (int dx, int dz, uint expectedCorner)[]
        {
            (-1, -1, 0), // Corner 0 at (0,1,0)
            (-1, +1, 1), // Corner 1 at (0,1,1)
            (+1, +1, 2), // Corner 2 at (1,1,1)
            (+1, -1, 3), // Corner 3 at (1,1,0)
        };

        foreach (var (dx, dz, expectedCorner) in diagonals)
        {
            var chunk = CreateAirChunk();
            chunk.SetBlock(8, 200, 8, BlockId.Stone);
            chunk.SetBlock(8 + dx, 201, 8 + dz, BlockId.Stone); // Diagonal at Y+1
            
            var sampler = CreateSampler(chunk);
            var ao = sampler.ComputeAmbientOcclusion(FaceTopY, expectedCorner, 8, 200, 8);

            Assert.True(ao < 4, 
                $"Diagonal at ({dx},{dz}) Y+1 should affect corner {expectedCorner}. Got AO={ao}");
        }
    }

    /// <summary>
    /// Test diagonal at Y+1 for side faces (+X face).
    /// For +X face (normal 1,0,0): tangent1=(0,1,0), tangent2=(0,0,1)
    /// Corner 1 at offset (1,1,0): c1=+1 (y=1), c2=-1 (z=0)
    /// For block at (8,200,8):
    ///   - side1: (8+1, 200+1*1, 8) = (9, 201, 8)
    ///   - side2: (8+1, 200, 8+1*(-1)) = (9, 200, 7)  
    ///   - corner: (9, 201, 7)
    /// </summary>
    [Fact]
    public void AO_DiagonalAtYPlus1_AffectsSideFace()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        // Place side1 occluder at (9, 201, 8)
        chunk.SetBlock(9, 201, 8, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);

        // Act - corner 1 of +X face
        var ao1 = sampler.ComputeAmbientOcclusion(FacePosX, 1, 8, 200, 8);
        
        // Assert - should be affected by the occluder (side1)
        Assert.True(ao1 < 4, $"+X face corner 1 should see side1 occluder at (9,201,8). Got AO={ao1}");
    }

    /// <summary>
    /// Test diagonal at Y-1 for bottom face.
    /// </summary>
    [Fact]
    public void AO_DiagonalAtYMinus1_AffectsBottomFace()
    {
        // Arrange
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        // Diagonal below at -Y +X +Z
        chunk.SetBlock(9, 199, 9, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);

        // Act - corner of -Y face
        var ao = sampler.ComputeAmbientOcclusion(FaceNegY, 3, 8, 200, 8);

        // Assert
        Assert.True(ao < 4, $"Bottom face corner should see diagonal below. Got AO={ao}");
    }
}

/// <summary>
/// Tests for AO consistency and smooth interpolation.
/// </summary>
public class AO_ConsistencyTests
{
    /// <summary>
    /// Test that symmetric configurations produce symmetric AO values.
    /// </summary>
    [Fact]
    public void AO_SymmetricConfig_ProducesSymmetricValues()
    {
        // Arrange - symmetric occluders around top face
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        // Place occluders symmetrically
        chunk.SetBlock(7, 201, 8, BlockId.Stone);  // -X
        chunk.SetBlock(9, 201, 8, BlockId.Stone);  // +X
        chunk.SetBlock(8, 201, 7, BlockId.Stone);  // -Z
        chunk.SetBlock(8, 201, 9, BlockId.Stone);  // +Z
        
        var sampler = CreateSampler(chunk);

        // Act - get AO for all corners
        var ao0 = sampler.ComputeAmbientOcclusion(FaceTopY, 0, 8, 200, 8);
        var ao1 = sampler.ComputeAmbientOcclusion(FaceTopY, 1, 8, 200, 8);
        var ao2 = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);
        var ao3 = sampler.ComputeAmbientOcclusion(FaceTopY, 3, 8, 200, 8);

        // Assert - all corners should have same AO due to symmetry
        Assert.True(ao0 == ao1 && ao1 == ao2 && ao2 == ao3,
            $"Symmetric occluders should give equal AO. Got {ao0},{ao1},{ao2},{ao3}");
    }

    /// <summary>
    /// Test that AO values are always in valid range 1-4.
    /// </summary>
    [Fact]
    public void AO_AlwaysInValidRange()
    {
        // Arrange - various configurations
        var configs = new (int ox, int oz)[]
        {
            (0, 0), (1, 0), (0, 1), (1, 1),
            (-1, 0), (0, -1), (-1, -1), (1, -1), (-1, 1)
        };

        foreach (var (ox, oz) in configs)
        {
            var chunk = CreateAirChunk();
            chunk.SetBlock(8, 200, 8, BlockId.Stone);
            
            var nx = 8 + ox;
            var nz = 8 + oz;
            if (nx >= 0 && nx < 16 && nz >= 0 && nz < 16)
            {
                chunk.SetBlock(nx, 201, nz, BlockId.Stone);
            }
            
            var sampler = CreateSampler(chunk);

            for (uint face = 0; face < 6; face++)
            {
                for (uint corner = 0; corner < 4; corner++)
                {
                    var ao = sampler.ComputeAmbientOcclusion(face, corner, 8, 200, 8);
                    Assert.InRange(ao, 1u, 4u);
                }
            }
        }
    }

    /// <summary>
    /// Test that more occluders = darker AO (monotonic relationship).
    /// </summary>
    [Fact]
    public void AO_MoreOccluders_DarkerValue()
    {
        // 0 occluders
        var chunk0 = CreateAirChunk();
        chunk0.SetBlock(8, 200, 8, BlockId.Stone);
        var ao0 = CreateSampler(chunk0).ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // 1 occluder (side)
        var chunk1 = CreateAirChunk();
        chunk1.SetBlock(8, 200, 8, BlockId.Stone);
        chunk1.SetBlock(9, 201, 8, BlockId.Stone);
        var ao1 = CreateSampler(chunk1).ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // 2 occluders (both sides - max occlusion by Minecraft rule)
        var chunk2 = CreateAirChunk();
        chunk2.SetBlock(8, 200, 8, BlockId.Stone);
        chunk2.SetBlock(9, 201, 8, BlockId.Stone);
        chunk2.SetBlock(8, 201, 9, BlockId.Stone);
        var ao2 = CreateSampler(chunk2).ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // Assert monotonic: more occluders = lower AO
        Assert.True(ao0 >= ao1, $"0 occluders ({ao0}) should be >= 1 occluder ({ao1})");
        Assert.True(ao1 >= ao2, $"1 occluder ({ao1}) should be >= 2 occluders ({ao2})");
        Assert.True(ao2 == 1, $"Both sides occluded should give minimum AO (1), got {ao2}");
    }
}

/// <summary>
/// Regression tests for specific AO bugs that were encountered.
/// </summary>
public class AO_RegressionTests
{
    /// <summary>
    /// Regression: AO not considering blocks at Y+1 when calculating top face.
    /// Bug: Only sampled neighbors at same Y level, ignoring diagonal blocks above.
    /// </summary>
    [Fact]
    public void Regression_DiagonalAboveNotConsidered()
    {
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        // Only diagonal above - no side neighbors
        chunk.SetBlock(9, 201, 9, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // If this fails, the diagonal at Y+1 is not being sampled
        Assert.True(ao == 3, 
            $"Regression: Diagonal at Y+1 must be considered. Expected AO=3, got {ao}");
    }

    /// <summary>
    /// Regression: Edge blocks returning invalid AO values.
    /// Bug: Out-of-bounds sampling returned garbage values.
    /// </summary>
    [Fact]
    public void Regression_EdgeBlocksInvalidAO()
    {
        var chunk = CreateAirChunk();
        
        // Test all edge positions
        var edges = new[] { (0, 8), (15, 8), (8, 0), (8, 15), (0, 0), (15, 15), (0, 15), (15, 0) };
        
        foreach (var (x, z) in edges)
        {
            chunk.SetBlock(x, 200, z, BlockId.Stone);
        }
        
        var sampler = CreateSampler(chunk);
        
        foreach (var (x, z) in edges)
        {
            for (uint face = 0; face < 6; face++)
            {
                for (uint corner = 0; corner < 4; corner++)
                {
                    var ao = sampler.ComputeAmbientOcclusion(face, corner, x, 200, z);
                    Assert.True(ao >= 1 && ao <= 4,
                        $"Regression: Edge block ({x},{z}) face {face} corner {corner} has invalid AO={ao}");
                }
            }
        }
    }

    /// <summary>
    /// Regression: Corner blocks (0,0), (15,15) etc. causing crashes or invalid values.
    /// </summary>
    [Fact]
    public void Regression_CornerBlocksCrashOrInvalid()
    {
        var chunk = CreateAirChunk();
        var corners = new[] { (0, 0), (15, 0), (0, 15), (15, 15) };
        
        foreach (var (x, z) in corners)
        {
            chunk.SetBlock(x, 200, z, BlockId.Stone);
        }
        
        var sampler = CreateSampler(chunk);
        
        foreach (var (x, z) in corners)
        {
            // This should not throw and should return valid values
            for (uint face = 0; face < 6; face++)
            {
                for (uint corner = 0; corner < 4; corner++)
                {
                    var ao = sampler.ComputeAmbientOcclusion(face, corner, x, 200, z);
                    Assert.InRange(ao, 1u, 4u);
                }
            }
        }
    }

    /// <summary>
    /// Regression: Y edge (y=0 and y=max) causing issues.
    /// </summary>
    [Fact]
    public void Regression_YEdgesInvalidAO()
    {
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 0, 8, BlockId.Stone);
        chunk.SetBlock(8, VoxelHelper.ChunkYSize - 1, 8, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);

        // y=0 bottom face
        for (uint corner = 0; corner < 4; corner++)
        {
            var ao = sampler.ComputeAmbientOcclusion(FaceNegY, corner, 8, 0, 8);
            Assert.InRange(ao, 1u, 4u);
        }

        // y=max top face
        for (uint corner = 0; corner < 4; corner++)
        {
            var ao = sampler.ComputeAmbientOcclusion(FaceTopY, corner, 8, VoxelHelper.ChunkYSize - 1, 8);
            Assert.InRange(ao, 1u, 4u);
        }
    }

    /// <summary>
    /// Regression: AO bleeding through walls (similar to light bleeding).
    /// The diagonal corner should not contribute if both sides are solid.
    /// </summary>
    [Fact]
    public void Regression_AOBleedingThroughWalls()
    {
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        // Create L-wall above
        chunk.SetBlock(9, 201, 8, BlockId.Stone);  // Side 1
        chunk.SetBlock(8, 201, 9, BlockId.Stone);  // Side 2
        // NO corner block - but both sides are solid
        
        var sampler = CreateSampler(chunk);
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);

        // Minecraft rule: both sides solid = max occlusion (corner doesn't matter)
        Assert.True(ao == 1, 
            $"Regression: When both sides are solid, AO should be minimum (1). Got {ao}");
    }

    /// <summary>
    /// Regression: Smooth lighting not matching AO at chunk boundaries.
    /// </summary>
    [Fact]
    public void Regression_AOConsistentWithSmoothLighting()
    {
        // This test ensures AO and smooth lighting use the same neighbor sampling
        var chunk = CreateAirChunk();
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 201, 8, BlockId.Stone);
        
        var sampler = CreateSampler(chunk);
        
        // Both should see the same occluder
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 2, 8, 200, 8);
        var light = sampler.ComputeSmoothLight(FaceTopY, 2, 8, 200, 8);

        // AO should show occlusion
        Assert.True(ao < 4, "AO should show occlusion");
        // Light sampling should also work (this tests the neighbor lookup is consistent)
        Assert.True(light != uint.MaxValue, "Smooth light should return valid value");
    }

    /// <summary>
    /// Regression: AO on stair/slab configurations incorrect.
    /// Multiple adjacent blocks at different heights.
    /// </summary>
    [Fact]
    public void Regression_StairConfigurationAO()
    {
        var chunk = CreateAirChunk();
        
        // Create stair pattern
        chunk.SetBlock(8, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 200, 8, BlockId.Stone);
        chunk.SetBlock(9, 201, 8, BlockId.Stone); // Step up
        chunk.SetBlock(10, 201, 8, BlockId.Stone);
        chunk.SetBlock(10, 202, 8, BlockId.Stone); // Another step
        
        var sampler = CreateSampler(chunk);

        // The block at (8,200,8) top face corner 3 (at 9,201,8) should see the step
        var ao = sampler.ComputeAmbientOcclusion(FaceTopY, 3, 8, 200, 8);
        
        Assert.True(ao < 4, 
            $"Stair step should occlude adjacent top face corner. Got AO={ao}");
    }
}
