using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Exposes static methods for voxel world operations, mainly focused on calculating positions and indices.
/// </summary>
public static class VoxelHelper
{
    public const float FarPlane = 500f;
    public const int MaxDistanceInChunks = (int)(FarPlane / ChunkSideSize);

    public const int MaxPickingDistance = 5;

    public const int WorldChunksXZ = 250;
    public const int ChunkSideSize = 20;
    public const int ChunkYSize = 100;

    public const float WaterLevel = ChunkYSize * 0.75f;
    public const float HeightAmplitude = ChunkYSize - WaterLevel - 1;
    public const float NoiseFrequency = 0.012f;

    public const int ChunkSideSizeSquare = ChunkSideSize * ChunkSideSize;
    public const int ChunkSizeXZMinusOne = ChunkSideSize - 1;
    public const int MaxBlockPositionXZ = WorldChunksXZ * ChunkSideSize - 1;
    public const int MaxBlockPositionY = ChunkYSize - 1;
    public const int TotalChunks = WorldChunksXZ * WorldChunksXZ;

    public static (Vertex[], uint[]) CreateVoxelCube()
    {
        Vertex[] vertices =
        [
            //  FRONT SIDE VERTICES
            new (0, 0,  1,  -1, -1, 1,    0, 1),   // lower left - 0
            new (0,  1,  1,  -1,  1, 1,    0, 0),   // upper left - 1
            new ( 1, 0,  1,   1, -1, 1,    1, 1),   // lower right - 2
            new ( 1,  1,  1,   1,  1, 1,    1, 0),   // upper right - 3
            
            //  BACK SIDE VERTICES
            new (0, 0, 0,  -1, -1, -1,   1, 1),   // lower left
            new (0,  1, 0,  -1,  1, -1,   1, 0),   // upper left
            new ( 1, 0, 0,   1, -1, -1,   0, 1),   // lower right
            new ( 1,  1, 0,   1,  1, -1,   0, 0),   // upper right
                                                             
            new (0,  1, 0,  -1,  1, -1,   0, 1),   // upper left 2nd
            new ( 1,  1, 0,   1,  1, -1,   1, 1),   // upper right 2nd
            new (0, 0, 0,  -1, -1, -1,   0, 0),   // lower left 2nd
            new ( 1, 0, 0,   1, -1, -1,   1, 0),   // lower right 2nd            
        ];
        uint[] indices =
        [
            // front quad
            2, 1, 0,
            2, 3, 1,

            // left quad
            0, 5, 4,
            0, 1, 5,

            // back quad
            4, 7, 6,
            4, 5, 7,

            // right quad
            6, 3, 2,
            6, 7, 3,

            // up quad            
            3, 8, 1,
            3, 9, 8,

            // down quad                                
            11, 0, 10,
            11, 2, 0
        ];
        return (vertices, indices);
    }

    public static (Vertex[], uint[]) CreateVoxelBox()
    {
        Vertex[] vertices =
        [
            // Position                 Normal      Texture
            //  FRONT SIDE (z = +)
            new (0, 0,  1,   0,  0,  1,   0.666f, 0.5f),     // lower left - 0
            new (0, 1,  1,   0,  0,  1,   0.666f, 1f),       // upper left - 1
            new (1, 0,  1,   0,  0,  1,   1f, 0.5f),         // lower right - 2
            new (1, 1,  1,   0,  0,  1,   1f, 1f),           // upper right - 3
                                           
            //  BACK SIDE (z = -)
            new (0, 0, 0,   0,  0, -1,   1, 0),             // lower left
            new (0, 1, 0,   0,  0, -1,   1, 0.5f),          // upper left
            new (1, 0, 0,   0,  0, -1,   0.666f, 0),        // lower right
            new (1, 1, 0,   0,  0, -1,   0.666f, 0.5f),     // upper right
                                                               
            //  LEFT SIDE (X = -)
            new (0, 0, 0,  -1,  0,  0,   0, 0),             // lower left  - 8
            new (0, 1, 0,  -1,  0,  0,   0, 0.5f),          // upper left - 9
            new (0, 0, 1,  -1,  0,  0,   0.333f, 0),        // lower right - 10
            new (0, 1, 1,  -1,  0,  0,   0.333f, 0.5f),     // upper right - 11

            //  RIGHT SIDE (X = +)
            new (1, 0, 1,   1,  0,  0,   0.333f, 0),         // lower left  - 12
            new (1, 1, 1,   1,  0,  0,   0.333f, 0.5f),      // upper left - 13
            new (1, 0, 0,   1,  0,  0,   0.666f, 0),         // lower right - 14
            new (1, 1, 0,   1,  0,  0,   0.666f, 0.5f),      // upper right - 15            
            
            //  TOP SIDE (Y = +)
            new (0,  1, 1,   0,  1,  0,   0, 0.5f),          // lower left - 16
            new (0,  1, 0,   0,  1,  0,   0, 1),             // upper left - 17
            new (1,  1, 1,   0,  1,  0,   0.333f, 0.5f),     // lower right - 18
            new (1,  1, 0,   0,  1,  0,   0.333f, 1),        // upper right - 19

            //  BOTTOM SIDE (Y = -)
            new (0, 0, 0,   0, -1,  0,   0.333f, 0.5f),     // lower left - 20
            new (0, 0, 1,   0, -1,  0,   0.333f, 1),        // upper left - 21
            new (1, 0, 0,   0, -1,  0,   0.666f, 0.5f),     // lower right - 22
            new (1, 0, 1,   0, -1,  0,   0.666f, 1),        // upper right - 23             
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
   
    public static Vector3i GetBlockPositionGlobal(int chunkIndex, int blockIndex)
    {
        var chunkPosition = GetChunkPositionGlobal(chunkIndex);
        var x = blockIndex % ChunkSideSize;
        var y = blockIndex / ChunkSideSize / ChunkSideSize;
        var z = blockIndex / ChunkSideSize % ChunkSideSize;
        return new Vector3i(chunkPosition.X + x, chunkPosition.Y + y, chunkPosition.Z + z);
    }

    public static bool IsGlobalPositionInWorld(in Vector3 position)
    {
        var x = position.X;
        var z = position.Z;
        var y = position.Y;
        return x >= 0 && x < WorldChunksXZ * ChunkSideSize &&
        z >= 0 && z < WorldChunksXZ * ChunkSideSize &&
        y >= 0 && y < ChunkYSize;
    }

    public static bool IsGlobalPositionOnWorldBoundary(int x, int y, int z) =>
       x == 0 ||
       y == 0 ||
       z == 0 ||
       x == MaxBlockPositionXZ ||
       y == MaxBlockPositionY ||
       z == MaxBlockPositionXZ;

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


    public static int[] GetNeighboringChunks(int chunkIndex)
    {
        var left = chunkIndex - 1;
        var right = chunkIndex + 1;
        var top = chunkIndex - WorldChunksXZ;
        var bottom = chunkIndex + WorldChunksXZ;
        var topLeft = top - 1;
        var topRight = top + 1;
        var bottomLeft = bottom - 1;
        var bottomRight = bottom + 1;

        int[] result =
        [
            topLeft,
            top,
            topRight,
            left,
            right,
            bottomLeft,
            bottom,
            bottomRight,
        ];
        return result.Where(x => x is >= 0 and < TotalChunks).ToArray();
    }

    /// <summary>
    /// Check if this ray represented by origin and direction intersects the specified <see cref="AABB"/>.
    /// </summary>
    /// <param name="box">The <see cref="AABB"/> to test for intersection.</param>
    /// <returns>
    /// The distance along the ray of the intersection or <code>null</code> if this
    /// <see cref="Ray"/> does not intersect the <see cref="AABB"/>.
    /// </returns>
    public static float? RayIntersect(Vector3 origin, Vector3 direction, AABB box)
    {
        const float Epsilon = 1e-6f;

        float? tMin = null, tMax = null;

        if (Math.Abs(direction.X) < Epsilon)
        {
            if (origin.X < box.Min.X || origin.X > box.Max.X)
                return null;
        }
        else
        {
            tMin = (box.Min.X - origin.X) / direction.X;
            tMax = (box.Max.X - origin.X) / direction.X;

            if (tMin > tMax)
            {
                (tMax, tMin) = (tMin, tMax);
            }
        }

        if (Math.Abs(direction.Y) < Epsilon)
        {
            if (origin.Y < box.Min.Y || origin.Y > box.Max.Y)
                return null;
        }
        else
        {
            var tMinY = (box.Min.Y - origin.Y) / direction.Y;
            var tMaxY = (box.Max.Y - origin.Y) / direction.Y;

            if (tMinY > tMaxY)
            {
                (tMaxY, tMinY) = (tMinY, tMaxY);
            }

            if ((tMin.HasValue && tMin > tMaxY) || (tMax.HasValue && tMinY > tMax))
                return null;

            if (!tMin.HasValue || tMinY > tMin) tMin = tMinY;
            if (!tMax.HasValue || tMaxY < tMax) tMax = tMaxY;
        }

        if (Math.Abs(direction.Z) < Epsilon)
        {
            if (origin.Z < box.Min.Z || origin.Z > box.Max.Z)
                return null;
        }
        else
        {
            var tMinZ = (box.Min.Z - origin.Z) / direction.Z;
            var tMaxZ = (box.Max.Z - origin.Z) / direction.Z;

            if (tMinZ > tMaxZ)
            {
                (tMaxZ, tMinZ) = (tMinZ, tMaxZ);
            }

            if ((tMin.HasValue && tMin > tMaxZ) || (tMax.HasValue && tMinZ > tMax))
                return null;

            if (!tMin.HasValue || tMinZ > tMin) tMin = tMinZ;
            if (!tMax.HasValue || tMaxZ < tMax) tMax = tMaxZ;
        }

        // having a positive tMax and a negative tMin means the ray is inside the box
        // we expect the intersection distance to be 0 in that case
        if ((tMin.HasValue && tMin < 0) && tMax > 0) return 0;

        // a negative tMin means that the intersection point is behind the ray's origin
        // we discard these as not hitting the AABB
        return tMin < 0 ? null : tMin;
    }
}
