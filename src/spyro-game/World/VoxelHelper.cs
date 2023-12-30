using OpenTK.Mathematics;
using SpyroGame.Noise;

namespace SpyroGame.World;
/// <summary>
/// Exposes static methods for voxel world operations, mainly focused on calculating positions and indices.
/// </summary>
public static class VoxelHelper
{
    public const int WorldChunksXZ = 10;
    public const int WorldChunksY = 5;
    public const int ChunkSideSize = 64;

    public const float HeightAmplitude = 30f;
    public const float WaterLevel = 13f;
    private const float NoiseFrequency = 0.075f;

    public const int WorldSizeXZSquared = WorldChunksXZ * WorldChunksXZ;
    public const int ChunkSideSizeSquare = ChunkSideSize * ChunkSideSize;
    public const int ChunkSizeMinusOne = ChunkSideSize - 1;
    public const int MaxBlockPositionXZ = WorldChunksXZ * ChunkSideSize - 1;
    public const int MaxBlockPositionY = WorldChunksY * ChunkSideSize - 1;
    public const int TotalChunks = WorldChunksXZ * WorldChunksXZ * WorldChunksY;


    public static bool IsGlobalPositionInWorld(in Vector3 position) =>
        position.X >= 0 && position.X <= WorldChunksXZ * ChunkSideSize &&
        position.Y >= 0 && position.Y <= WorldChunksY * ChunkSideSize &&
        position.Z >= 0 && position.Z <= WorldChunksXZ * ChunkSideSize;

    public static bool IsGlobalPositionOnWorldBoundary(int x, int y, int z) =>
       x == 0 ||
       y == 0 ||
       z == 0 ||
       x == MaxBlockPositionXZ ||
       y == MaxBlockPositionY ||
       z == MaxBlockPositionXZ;

    public static int GetChunkIndexFromGlobalPosition(in Vector3i position)
    {
        var chunkPosition = position / ChunkSideSize;
        return chunkPosition.Z * WorldSizeXZSquared + chunkPosition.Y * WorldChunksXZ + chunkPosition.X;
    }

    public static float[] CalcChunkHeightGrid(int chunkIndex, int seed)
    {
        var worldX = chunkIndex % WorldChunksXZ;
        var worldZ = chunkIndex / WorldSizeXZSquared;
        //var worldY = (chunkIndex / WorldChunksXZ) % WorldChunksXZ;
        return NoiseData.CreateFromEncoding("BwA=", worldX * ChunkSideSize, worldZ * ChunkSideSize, ChunkSideSize, NoiseFrequency, seed, out _);
    }
}
