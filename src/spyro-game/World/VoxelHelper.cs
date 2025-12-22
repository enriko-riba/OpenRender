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
    public const float FarPlane = 500f; // Was 430f
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

    public const int INITIAL_LOAD_BATCH_SIZE = 32; // Reduced batch size for initial load to avoid TDR
    public const int VERTEX_STRIDE_BYTES = 8; // Phase 5.2: Compressed vertex format (8 bytes)

   
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

    /// <summary>
    /// Calculates the number of chunks in a square region (Chebyshev distance).
    /// Used when visibility is determined by max(|dx|, |dz|) rather than Euclidean distance.
    /// </summary>
    public static int CalculateSquareChunkCount(int radius)
    {
        var clamped = Math.Clamp(radius, 0, WorldChunksXZ);
        var side = 2 * clamped + 1;
        return Math.Max(side * side, 1);
    }
}
