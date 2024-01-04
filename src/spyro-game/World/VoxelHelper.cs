using OpenTK.Mathematics;
using SpyroGame.Noise;

namespace SpyroGame.World;

/// <summary>
/// Exposes static methods for voxel world operations, mainly focused on calculating positions and indices.
/// </summary>
public static class VoxelHelper
{
    public const float FarPlane = 450f;
    public const int MaxDistanceInChunks = (int)(FarPlane / ChunkSideSize) + 1;

    public const int WorldChunksXZ = 250;
    public const int ChunkSideSize = 28;
    public const int ChunkYSize = 300;

    public const float HeightAmplitude = 30f;
    public const float WaterLevel = ChunkYSize - HeightAmplitude;
    private const float NoiseFrequency = 0.014f;

    public const int WorldSizeXZSquared = WorldChunksXZ * WorldChunksXZ;
    public const int ChunkSideSizeSquare = ChunkSideSize * ChunkSideSize;
    public const int ChunkSizeXZMinusOne = ChunkSideSize - 1;
    public const int MaxBlockPositionXZ = WorldChunksXZ * ChunkSideSize - 1;
    public const int MaxBlockPositionY = ChunkYSize - 1;
    public const int TotalChunks = WorldChunksXZ * WorldChunksXZ;

    public static Vector3i GetBlockCoordinatesGlobal(in Vector3 position) => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y), (int)MathF.Round(position.Z));

    public static bool IsGlobalPositionInWorld(in Vector3 position)
    {
        var x = (int)MathF.Round(position.X);
        var z = (int)MathF.Round(position.Z);
        var y = (int)MathF.Round(position.Y);
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

    public static float[] CalcTerrainData(int chunkIndex, int seed)
    {
        var worldX = chunkIndex % WorldChunksXZ;
        var worldZ = chunkIndex / WorldChunksXZ;
        var offsetX = worldX * ChunkSideSize;
        var offsetZ = worldZ * ChunkSideSize;
        var data = NoiseData.CreateFromEncoding("BwA=", offsetX, offsetZ, ChunkSideSize, NoiseFrequency, seed, out var minmax);
        //Log.Info($"Calculating terrain data for chunk {chunkIndex} at {worldX},{worldZ}, noise offset {offsetX},{offsetZ}, minmax {minmax}");
        return data;
    }
}
