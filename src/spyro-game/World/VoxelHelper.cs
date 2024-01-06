using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Exposes static methods for voxel world operations, mainly focused on calculating positions and indices.
/// </summary>
public static class VoxelHelper
{
    public const float FarPlane = 450f;
    public const int MaxDistanceInChunks = (int)(FarPlane / ChunkSideSize);

    public const int WorldChunksXZ = 250;
    public const int ChunkSideSize = 25;
    public const int ChunkYSize = 300;

    public const float HeightAmplitude = 30f;
    public const float WaterLevel = ChunkYSize - HeightAmplitude;
    public const float NoiseFrequency = 0.014f;

    public const int ChunkSideSizeSquare = ChunkSideSize * ChunkSideSize;
    public const int ChunkSizeXZMinusOne = ChunkSideSize - 1;
    public const int MaxBlockPositionXZ = WorldChunksXZ * ChunkSideSize - 1;
    public const int MaxBlockPositionY = ChunkYSize - 1;
    public const int TotalChunks = WorldChunksXZ * WorldChunksXZ;


    public static (Vertex[], uint[]) CreateVoxelCube()
    {
        const float HALF = 0.5f;

        Vertex[] vertices =
        [
            // Position                 Normal      Texture
            //  FRONT SIDE (z = +)
            new (-HALF, -HALF,  HALF,   0,  0,  1,   0.666f, 0.5f),     // lower left - 0
            new (-HALF,  HALF,  HALF,   0,  0,  1,   0.666f, 1f),       // upper left - 1
            new ( HALF, -HALF,  HALF,   0,  0,  1,   1f, 0.5f),         // lower right - 2
            new ( HALF,  HALF,  HALF,   0,  0,  1,   1f, 1f),           // upper right - 3
                                           
            //  BACK SIDE (z = -)
            new (-HALF, -HALF, -HALF,   0,  0, -1,   1, 0),             // lower left
            new (-HALF,  HALF, -HALF,   0,  0, -1,   1, 0.5f),          // upper left
            new ( HALF, -HALF, -HALF,   0,  0, -1,   0.666f, 0),        // lower right
            new ( HALF,  HALF, -HALF,   0,  0, -1,   0.666f, 0.5f),     // upper right
                                                               
            //  LEFT SIDE (X = -)
            new (-HALF, -HALF, -HALF,  -1,  0,  0,   0, 0),             // lower left  - 8
            new (-HALF,  HALF, -HALF,  -1,  0,  0,   0, 0.5f),          // upper left - 9
            new (-HALF, -HALF,  HALF,  -1,  0,  0,   0.333f, 0),        // lower right - 10
            new (-HALF,  HALF,  HALF,  -1,  0,  0,   0.333f, 0.5f),     // upper right - 11

            //  RIGHT SIDE (X = +)
            new (HALF, -HALF,  HALF,   1,  0,  0,   0.333f, 0),         // lower left  - 12
            new (HALF,  HALF,  HALF,   1,  0,  0,   0.333f, 0.5f),      // upper left - 13
            new (HALF, -HALF, -HALF,   1,  0,  0,   0.666f, 0),         // lower right - 14
            new (HALF,  HALF, -HALF,   1,  0,  0,   0.666f, 0.5f),      // upper right - 15            
            
            //  TOP SIDE (Y = +)
            new (-HALF,  HALF,  HALF,   0,  1,  0,   0, 0.5f),          // lower left - 16
            new (-HALF,  HALF, -HALF,   0,  1,  0,   0, 1),             // upper left - 17
            new ( HALF,  HALF,  HALF,   0,  1,  0,   0.333f, 0.5f),     // lower right - 18
            new ( HALF,  HALF, -HALF,   0,  1,  0,   0.333f, 1),        // upper right - 19

            //  BOTTOM SIDE (Y = -)
            new (-HALF, -HALF, -HALF,   0, -1,  0,   0.333f, 0.5f),     // lower left - 20
            new (-HALF, -HALF,  HALF,   0, -1,  0,   0.333f, 1),        // upper left - 21
            new ( HALF, -HALF, -HALF,   0, -1,  0,   0.666f, 0.5f),     // lower right - 22
            new ( HALF, -HALF,  HALF,   0, -1,  0,   0.666f, 1),        // upper right - 23             
        ];
        uint[] indices =
        [
            // front quad
            2, 1, 0,
            2, 3, 1,

            // back quad
            4, 7, 6,
            4, 5, 7,

            // left quad
            10, 9, 8,
            10, 11, 9,

            // right quad
            14, 13, 12,
            14, 15, 13,
            
            // up quad            
            18, 17, 16,
            18, 19, 17,

            // down quad                                
            22, 21, 20,
            22, 23, 21
        ];

        return (vertices, indices);
    }

    public static Vector3i GetBlockCoordinatesGlobal(in Vector3 position) => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y), (int)MathF.Round(position.Z));

    public static bool IsGlobalPositionInWorld(in Vector3 position)
    {
        var x = position.X;
        var z = position.Z;
        var y = position.Y;
        return x >= 0 && x < WorldChunksXZ * ChunkSideSize &&
        z >= 0 && z < WorldChunksXZ * ChunkSideSize &&
        y >= 0 && y <= MaxBlockPositionY;
    }

    public static bool IsGlobalPositionOnWorldBoundary(int x, int y, int z) =>
       x == 0 ||
       y == 0 ||
       z == 0 ||
       x == MaxBlockPositionXZ ||
       y == MaxBlockPositionY ||
       z == MaxBlockPositionXZ;

    public static int GetChunkIndexFromGlobalPosition(in Vector3i position)
    {
        var x = position.X / ChunkSideSize;
        var z = position.Z / ChunkSideSize;
        return x + z * WorldChunksXZ;
    }

    public static Vector3i GetChunkPositionGlobal(int chunkIndex)
    {
        var x = (chunkIndex % WorldChunksXZ) * ChunkSideSize;
        var z = (chunkIndex / (WorldChunksXZ) * ChunkSideSize);
        return new Vector3i(x, 0, z);
    }

    public static int GetChunkIndexFromGlobalPosition(in Vector3 position)
    {
        var x = (int)MathF.Round(position.X) / ChunkSideSize;
        var z = (int)MathF.Round(position.Z) / ChunkSideSize;
        return x + z * WorldChunksXZ;
    }
}
