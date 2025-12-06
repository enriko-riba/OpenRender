using SpyroGame.World;

namespace SpyroGame.Tests.Common;

/// <summary>
/// Shared test helpers for creating and manipulating chunk data across all test categories.
/// </summary>
public static class TestHelpers
{
    #region Chunk Creation
    
    /// <summary>
    /// Creates a chunk filled entirely with air (transparent to light).
    /// </summary>
    public static ChunkData CreateAirChunk(int chunkIndex = 0)
    {
        var chunk = new ChunkData { ChunkIndex = chunkIndex };
        // ChunkData initializes with Air by default (palette index 0 = Air)
        return chunk;
    }

    /// <summary>
    /// Creates a chunk with a solid stone floor at the specified Y level and air above.
    /// </summary>
    public static ChunkData CreateChunkWithFloor(int floorY, int chunkIndex = 0)
    {
        var chunk = new ChunkData { ChunkIndex = chunkIndex };
        
        for (var y = 0; y <= floorY; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }
        
        return chunk;
    }

    /// <summary>
    /// Creates a sealed room (stone walls, floor, ceiling) with air inside.
    /// </summary>
    public static ChunkData CreateSealedRoom(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, int chunkIndex = 0)
    {
        var chunk = new ChunkData { ChunkIndex = chunkIndex };
        
        // Fill the entire bounding box with stone first
        for (var y = minY; y <= maxY; y++)
        {
            for (var z = minZ; z <= maxZ; z++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Stone);
                }
            }
        }
        
        // Hollow out the interior (leave 1-block thick walls)
        for (var y = minY + 1; y < maxY; y++)
        {
            for (var z = minZ + 1; z < maxZ; z++)
            {
                for (var x = minX + 1; x < maxX; x++)
                {
                    chunk.SetBlock(x, y, z, BlockId.Air);
                }
            }
        }
        
        return chunk;
    }

    #endregion

    #region Block Placement
    
    /// <summary>
    /// Places a torch at the specified position.
    /// </summary>
    public static void PlaceTorch(ChunkData chunk, int x, int y, int z)
    {
        chunk.SetBlock(x, y, z, BlockId.Torch);
    }

    /// <summary>
    /// Places glowstone at the specified position.
    /// </summary>
    public static void PlaceGlowstone(ChunkData chunk, int x, int y, int z)
    {
        chunk.SetBlock(x, y, z, BlockId.Glowstone);
    }

    #endregion

    #region Light Access
    
    /// <summary>
    /// Gets the sky light value at a position.
    /// </summary>
    public static int GetSkyLight(ChunkData chunk, int x, int y, int z)
    {
        var index = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        return chunk.LightData[index] & 0xF;
    }

    /// <summary>
    /// Gets the block light value at a position.
    /// </summary>
    public static int GetBlockLight(ChunkData chunk, int x, int y, int z)
    {
        var index = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        return (chunk.LightData[index] >> 4) & 0xF;
    }

    /// <summary>
    /// Sets the block light value at a position (for test setup).
    /// </summary>
    public static void SetBlockLight(ChunkData chunk, int x, int y, int z, int value)
    {
        var index = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        chunk.LightData[index] = (byte)((chunk.LightData[index] & 0x0F) | ((value & 0xF) << 4));
    }

    /// <summary>
    /// Sets the sky light value at a position (for test setup).
    /// </summary>
    public static void SetSkyLight(ChunkData chunk, int x, int y, int z, int value)
    {
        var index = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
        chunk.LightData[index] = (byte)((chunk.LightData[index] & 0xF0) | (value & 0xF));
    }

    #endregion

    #region Chunk Utilities
    
    /// <summary>
    /// Converts world coordinates to chunk index (for multi-chunk tests).
    /// </summary>
    public static int GetChunkIndex(int chunkX, int chunkZ)
    {
        return chunkZ * VoxelHelper.WorldChunksXZ + chunkX;
    }

    /// <summary>
    /// Creates a simple chunk provider from a dictionary of chunks.
    /// </summary>
    public static ChunkDataProvider CreateChunkProvider(Dictionary<int, ChunkData> chunks)
    {
        return chunkIdx => chunks.TryGetValue(chunkIdx, out var chunk) ? chunk : null;
    }

    #endregion
}
