using SpyroGame.World;

namespace SpyroGame.Tests.AmbientOcclusion;

/// <summary>
/// Test helpers for creating and manipulating chunk data in AO tests.
/// </summary>
public static class AOTestHelpers
{
    // Face indices matching ChunkMeshBuilder.FaceDirections
    public const uint FacePosX = 0;  // +X
    public const uint FaceNegX = 1;  // -X
    public const uint FaceTopY = 2;  // +Y (top)
    public const uint FaceNegY = 3;  // -Y (bottom)
    public const uint FacePosZ = 4;  // +Z
    public const uint FaceNegZ = 5;  // -Z

    /// <summary>
    /// Creates a chunk filled entirely with air.
    /// </summary>
    public static ChunkData CreateAirChunk(int chunkIndex = 0)
    {
        var chunk = new ChunkData { ChunkIndex = chunkIndex };
        return chunk;
    }

    /// <summary>
    /// Creates a ChunkVoxelSampler for testing AO calculations.
    /// The sampler wraps the chunk data and provides AO computation methods.
    /// </summary>
    public static TestableChunkVoxelSampler CreateSampler(ChunkData chunk)
    {
        return new TestableChunkVoxelSampler(chunk);
    }

    /// <summary>
    /// Converts world coordinates to chunk index.
    /// </summary>
    public static int GetChunkIndex(int chunkX, int chunkZ)
    {
        return chunkZ * VoxelHelper.WorldChunksXZ + chunkX;
    }
}

/// <summary>
/// A testable version of the ChunkVoxelSampler that can be used in unit tests
/// without requiring the full meshing job system infrastructure.
/// </summary>
public class TestableChunkVoxelSampler
{
    private readonly ChunkData chunk;

    // Face directions matching ChunkMeshBuilder
    private static readonly (int dx, int dy, int dz)[] FaceDirections =
    [
        (1, 0, 0),  // +X
        (-1, 0, 0), // -X
        (0, 1, 0),  // +Y
        (0, -1, 0), // -Y
        (0, 0, 1),  // +Z
        (0, 0, -1)  // -Z
    ];

    public TestableChunkVoxelSampler(ChunkData chunk)
    {
        this.chunk = chunk;
    }

    /// <summary>
    /// Sample block at given coordinates, handling out-of-bounds.
    /// </summary>
    public BlockId SampleBlock(int x, int y, int z)
    {
        if (y < 0) return BlockId.Stone; // Below world = solid
        if (y >= VoxelHelper.ChunkYSize) return BlockId.Air; // Above world = air
        
        if (x < 0 || x >= VoxelHelper.ChunkSideSize ||
            z < 0 || z >= VoxelHelper.ChunkSideSize)
        {
            // Out of chunk bounds - treat as air for test purposes
            // In real usage, would check neighbor chunks
            return BlockId.Air;
        }

        return chunk.GetBlock(x, y, z);
    }

    /// <summary>
    /// Minecraft-style ambient occlusion calculation.
    /// For each vertex corner, check the 3 adjacent neighbor blocks (side1, side2, corner).
    /// AO level = 3 - (side1 + side2 + corner) if no sides occlude, or 0 if both sides occlude.
    /// Returns 1-4 (1=fully occluded/dark, 4=no occlusion/bright).
    /// </summary>
    public uint ComputeAmbientOcclusion(uint face, uint corner, int x, int y, int z)
    {
        // Get the face normal direction
        var (nx, ny, nz) = FaceDirections[(int)face];

        // Get the two tangent directions for this face
        var (t1x, t1y, t1z, t2x, t2y, t2z) = GetFaceTangents(face);

        // Get corner-specific offsets (-1 or +1 in tangent directions)
        var (c1, c2) = GetCornerSigns(face, corner);

        // Sample the 3 neighbors that affect this corner's AO
        // side1: offset in first tangent direction
        // side2: offset in second tangent direction  
        // cornerBlock: offset in both tangent directions (diagonal)
        var side1 = IsOccluder(SampleBlock(x + nx + t1x * c1, y + ny + t1y * c1, z + nz + t1z * c1));
        var side2 = IsOccluder(SampleBlock(x + nx + t2x * c2, y + ny + t2y * c2, z + nz + t2z * c2));
        var cornerOccluder = IsOccluder(SampleBlock(x + nx + t1x * c1 + t2x * c2, y + ny + t1y * c1 + t2y * c2, z + nz + t1z * c1 + t2z * c2));

        // Minecraft formula: if both sides are solid, corner doesn't matter (full occlusion)
        // Otherwise, AO = 3 - (side1 + side2 + corner)
        int ao;
        if (side1 && side2)
        {
            ao = 0; // Maximum occlusion
        }
        else
        {
            var occluders = (side1 ? 1 : 0) + (side2 ? 1 : 0) + (cornerOccluder ? 1 : 0);
            ao = 3 - occluders;
        }

        // Shader expects: 0=darkest, 4=brightest
        // Our ao is 0-3, shift to 1-4 for shader compatibility
        return (uint)(ao + 1);
    }

    /// <summary>
    /// Compute smooth lighting for a vertex corner.
    /// Returns packed light value (sky in low nibble, block in high nibble).
    /// </summary>
    public uint ComputeSmoothLight(uint face, uint corner, int x, int y, int z)
    {
        var (nx, ny, nz) = FaceDirections[(int)face];
        var (t1x, t1y, t1z, t2x, t2y, t2z) = GetFaceTangents(face);
        var (c1, c2) = GetCornerSigns(face, corner);

        // Coordinates of the 4 neighbors
        var bx = x + nx;
        var by = y + ny;
        var bz = z + nz;

        var s1x = bx + t1x * c1;
        var s1y = by + t1y * c1;
        var s1z = bz + t1z * c1;

        var s2x = bx + t2x * c2;
        var s2y = by + t2y * c2;
        var s2z = bz + t2z * c2;

        var cx = bx + t1x * c1 + t2x * c2;
        var cy = by + t1y * c1 + t2y * c2;
        var cz = bz + t1z * c1 + t2z * c2;

        var lBase = SamplePackedLight(bx, by, bz);
        var lSide1 = SamplePackedLight(s1x, s1y, s1z);
        var lSide2 = SamplePackedLight(s2x, s2y, s2z);
        var lCorner = SamplePackedLight(cx, cy, cz);

        // Check if side blocks are opaque
        var side1Opaque = SampleBlock(s1x, s1y, s1z).IsOpaque();
        var side2Opaque = SampleBlock(s2x, s2y, s2z).IsOpaque();
        var cornerBlocked = side1Opaque && side2Opaque;

        int skySum = 0;
        int blockSum = 0;
        int count = 0;

        void AddSample(uint l)
        {
            if (l != uint.MaxValue)
            {
                skySum += (int)(l & 0xF);
                blockSum += (int)((l >> 4) & 0xF);
                count++;
            }
        }

        AddSample(lBase);
        AddSample(lSide1);
        AddSample(lSide2);
        if (!cornerBlocked)
        {
            AddSample(lCorner);
        }

        if (count == 0)
        {
            return y >= VoxelHelper.ChunkYSize - 1 ? 15u : 0u;
        }

        int skyAvg = skySum / count;
        int blockAvg = blockSum / count;

        return (uint)(skyAvg | (blockAvg << 4));
    }

    private uint SamplePackedLight(int x, int y, int z)
    {
        if (y < 0 || y >= VoxelHelper.ChunkYSize) return uint.MaxValue;
        if (x < 0 || x >= VoxelHelper.ChunkSideSize ||
            z < 0 || z >= VoxelHelper.ChunkSideSize)
        {
            return uint.MaxValue;
        }

        var index = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        return chunk.LightData[index];
    }

    private static bool IsOccluder(BlockId block)
    {
        return block.IsOpaque();
    }

    /// <summary>
    /// Get the two tangent vectors for a face (perpendicular to normal).
    /// </summary>
    private static (int, int, int, int, int, int) GetFaceTangents(uint face)
    {
        return face switch
        {
            0 => (0, 1, 0, 0, 0, 1),   // +X: spans Y and Z
            1 => (0, 1, 0, 0, 0, 1),   // -X: spans Y and Z
            2 => (1, 0, 0, 0, 0, 1),   // +Y: spans X and Z
            3 => (1, 0, 0, 0, 0, 1),   // -Y: spans X and Z
            4 => (1, 0, 0, 0, 1, 0),   // +Z: spans X and Y
            5 => (1, 0, 0, 0, 1, 0),   // -Z: spans X and Y
            _ => (1, 0, 0, 0, 1, 0)
        };
    }

    // Face corner offsets matching ChunkMeshBuilder
    private static readonly (int x, int y, int z)[][] FaceCornerOffsets =
    [
        [(1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)], // +X
        [(0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0)], // -X
        [(0, 1, 0), (0, 1, 1), (1, 1, 1), (1, 1, 0)], // +Y
        [(0, 0, 1), (0, 0, 0), (1, 0, 0), (1, 0, 1)], // -Y
        [(1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1)], // +Z
        [(0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)]  // -Z
    ];

    /// <summary>
    /// Get the corner-specific sign multipliers for tangent directions.
    /// </summary>
    private static (int, int) GetCornerSigns(uint face, uint corner)
    {
        var (ox, oy, oz) = FaceCornerOffsets[(int)face][(int)corner];

        return face switch
        {
            // +X and -X faces: vary in Y (t1) and Z (t2)
            0 or 1 => (oy == 0 ? -1 : 1, oz == 0 ? -1 : 1),

            // +Y and -Y faces: vary in X (t1) and Z (t2)
            2 or 3 => (ox == 0 ? -1 : 1, oz == 0 ? -1 : 1),

            // +Z and -Z faces: vary in X (t1) and Y (t2)
            4 or 5 => (ox == 0 ? -1 : 1, oy == 0 ? -1 : 1),

            _ => (0, 0)
        };
    }
}
