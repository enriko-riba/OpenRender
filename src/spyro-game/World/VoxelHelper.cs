using System;
using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Exposes static methods for voxel world operations, mainly focused on calculating positions and indices.
/// </summary>
public static class VoxelHelper
{
    // Rendering far plane: should be slightly larger than max chunk distance
    // to avoid popping when chunks at the edge are culled
    // MaxDistanceInChunks * ChunkSideSize * 2 = 16 * 16 * 2 = 512 blocks
    // Far plane should be ~600 to account for chunk height and diagonal distance
    public const float FarPlane = 600f; // Was 430f
    // Chunk loading distance: determines how far chunks are loaded/generated
    // LOD 0 (Full detail): 0-16 chunks = 256 blocks = 256m
    // LOD 1 (Medium): 16-32 chunks = 512m (future: half-res mesh)
    // LOD 2 (Low): 32-64 chunks = 1024m = ~1km (future: impostor)
    // 
    // Current: No LOD system, so keep this small to avoid memory issues
    // For kilometers view: implement LOD tiers (Phase 6)
    public const int MaxDistanceInChunks = 16; // 16 chunks = 512m diameter (256m radius)

    // Future LOD tiers (Phase 6):
    // public const int LOD0_Distance = 16;  // Full detail
    // public const int LOD1_Distance = 32;  // Half resolution
    // public const int LOD2_Distance = 64;  // Quarter resolution / impostor

    public const int MaxPickingDistance = 3;

    public const int WorldChunksXZ = 9600 / ChunkSideSize;
    public const int ChunkSideSize = 16;
    public const int ChunkYSize = 384;

    public const int WaterLevel = 35;
    //public const float NoiseFrequency = 0.0012f;

    public const int ChunkSideSizeSquare = ChunkSideSize * ChunkSideSize;
    public const int ChunkSizeXZMinusOne = ChunkSideSize - 1;
    public const int MaxBlockPositionXZ = WorldChunksXZ * ChunkSideSize - 1;
    public const int MaxBlockPositionY = ChunkYSize - 1;
    public const int TotalChunks = WorldChunksXZ * WorldChunksXZ;
    public const int ChunkVoxelCount = ChunkSideSizeSquare * ChunkYSize;
    public const int PackedChunkVoxelCount = ChunkVoxelCount; // 1 voxel per uint (UNPACKED)

    // GPU Pipeline Constants (Phase 2-5)
    public const int DEFAULT_MAX_CHUNKS_PER_BATCH = 64;
    public const int INITIAL_LOAD_BATCH_SIZE = 32; // Reduced batch size for initial load to avoid TDR
    public const int VERTEX_STRIDE_BYTES = 8; // Phase 5.2: Compressed vertex format (8 bytes)

    // Shared quad indices for instanced rendering
    //public static readonly uint[] SHARED_QUAD_INDICES = [0, 1, 2, 2, 3, 0];

    //public static (Vertex[], uint[]) CreateVoxelCube()
    //{
    //    Vertex[] vertices =
    //    [
    //        //  FRONT SIDE VERTICES
    //        new (0, 0,  1,  -1, -1, 1,    0, 1),   // lower left - 0
    //        new (0,  1,  1,  -1,  1, 1,    0, 0),   // upper left - 1
    //        new ( 1, 0,  1,   1, -1, 1,    1, 1),   // lower right - 2
    //        new ( 1,  1,  1,   1,  1, 1,    1, 0),   // upper right - 3

    //        //  BACK SIDE VERTICES
    //        new (0, 0, 0,  -1, -1, -1,   1, 1),   // lower left
    //        new (0,  1, 0,  -1,  1, -1,   1, 0),   // upper left
    //        new ( 1, 0, 0,   1, -1, -1,   0, 1),   // lower right
    //        new ( 1,  1, 0,   1,  1, -1,   0, 0),   // upper right

    //        new (0,  1, 0,  -1,  1, -1,   0, 1),   // upper left 2nd
    //        new ( 1,  1, 0,   1,  1, -1,   1, 1),   // upper right 2nd
    //        new (0, 0, 0,  -1, -1, -1,   0, 0),   // lower left 2nd
    //        new ( 1, 0, 0,   1, -1, -1,   1, 0),   // lower right 2nd            
    //    ];
    //    uint[] indices =
    //    [
    //        // front quad
    //        2, 1, 0,
    //        2, 3, 1,

    //        // left quad
    //        0, 5, 4,
    //        0, 1, 5,

    //        // back quad
    //        4, 7, 6,
    //        4, 5, 7,

    //        // right quad
    //        6, 3, 2,
    //        6, 7, 3,

    //        // up quad            
    //        3, 8, 1,
    //        3, 9, 8,

    //        // down quad                                
    //        11, 0, 10,
    //        11, 2, 0
    //    ];
    //    return (vertices, indices);
    //}

    //public static (Vertex[], uint[]) CreateVoxelBox()
    //{
    //    Vertex[] vertices =
    //    [
    //        // Position                 Normal      Texture
    //        //  FRONT SIDE (z = +)
    //        new (0, 0,  1,   0,  0,  1,   0.666f, 0.5f),     // lower left - 0
    //        new (0, 1,  1,   0,  0,  1,   0.666f, 1f),       // upper left - 1
    //        new (1, 0,  1,   0,  0,  1,   1f, 0.5f),         // lower right - 2
    //        new (1, 1,  1,   0,  0,  1,   1f, 1f),           // upper right - 3

    //        //  BACK SIDE (z = -)
    //        new (0, 0, 0,   0,  0, -1,   1, 0),             // lower left
    //        new (0, 1, 0,   0,  0, -1,   1, 0.5f),          // upper left
    //        new (1, 0, 0,   0,  0, -1,   0.666f, 0),        // lower right
    //        new (1, 1, 0,   0,  0, -1,   0.666f, 0.5f),     // upper right

    //        //  LEFT SIDE (X = -)
    //        new (0, 0, 0,  -1,  0,  0,   0, 0),             // lower left  - 8
    //        new (0, 1, 0,  -1,  0,  0,   0, 0.5f),          // upper left - 9
    //        new (0, 0, 1,  -1,  0,  0,   0.333f, 0),        // lower right - 10
    //        new (0, 1, 1,  -1,  0,  0,   0.333f, 0.5f),     // upper right - 11

    //        //  RIGHT SIDE (X = +)
    //        new (1, 0, 1,   1,  0,  0,   0.333f, 0),         // lower left  - 12
    //        new (1, 1, 1,   1,  0,  0,   0.333f, 0.5f),      // upper left - 13
    //        new (1, 0, 0,   1,  0,  0,   0.666f, 0),         // lower right - 14
    //        new (1, 1, 0,   1,  0,  0,   0.666f, 0.5f),      // upper right - 15            

    //        //  TOP SIDE (Y = +)
    //        new (0,  1, 1,   0,  1,  0,   0, 0.5f),          // lower left - 16
    //        new (0,  1, 0,   0,  1,  0,   0, 1),             // upper left - 17
    //        new (1,  1, 1,   0,  1,  0,   0.333f, 0.5f),     // lower right - 18
    //        new (1,  1, 0,   0,  1,  0,   0.333f, 1),        // upper right - 19

    //        //  BOTTOM SIDE (Y = -)
    //        new (0, 0, 0,   0, -1,  0,   0.333f, 0.5f),     // lower left - 20
    //        new (0, 0, 1,   0, -1,  0,   0.333f, 1),        // upper left - 21
    //        new (1, 0, 0,   0, -1,  0,   0.666f, 0.5f),     // lower right - 22
    //        new (1, 0, 1,   0, -1,  0,   0.666f, 1),        // upper right - 23             
    //    ];
    //    uint[] indices =
    //    [
    //        // front quad
    //        2, 1, 0,
    //        2, 3, 1,

    //        // back quad
    //        4, 7, 6,
    //        4, 5, 7,

    //        // left quad
    //        10, 9, 8,
    //        10, 11, 9,

    //        // right quad
    //        14, 13, 12,
    //        14, 15, 13,

    //        // up quad            
    //        18, 17, 16,
    //        18, 19, 17,

    //        // down quad                                
    //        22, 21, 20,
    //        22, 23, 21
    //    ];

    //    return (vertices, indices);
    //}

    public static bool IsGlobalPositionInWorld(in Vector3 position)
    {
        var x = position.X;
        var z = position.Z;
        var y = position.Y;
        return x >= 0 && x < WorldChunksXZ * ChunkSideSize &&
        z >= 0 && z < WorldChunksXZ * ChunkSideSize &&
        y >= 0 && y < ChunkYSize;
    }

    public static int GetChunkIndexFromPositionGlobal(in Vector3i position)
    {
        var x = position.X / ChunkSideSize;
        var z = position.Z / ChunkSideSize;
        return x + z * WorldChunksXZ;
    }

    public static int GetChunkIndexFromPositionGlobal(in Vector3 position)
    {
        var x = (int)position.X / ChunkSideSize;
        var z = (int)position.Z / ChunkSideSize;
        return x + z * WorldChunksXZ;
    }

    public static Vector3i GetChunkPositionGlobal(int chunkIndex)
    {
        var x = (chunkIndex % WorldChunksXZ) * ChunkSideSize;
        var z = (chunkIndex / (WorldChunksXZ) * ChunkSideSize);
        return new Vector3i(x, 0, z);
    }

    public static int CalculateCircularChunkCount(int radius)
    {
        var clamped = Math.Clamp(radius, 0, WorldChunksXZ);
        var count = 0;

        for (var dz = -clamped; dz <= clamped; dz++)
        {
            var maxDx = (int)MathF.Floor(MathF.Sqrt(clamped * clamped - dz * dz));
            count += maxDx * 2 + 1;
        }

        return Math.Max(count, 1);
    }
}
