using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using OpenRender;

namespace SpyroGame.World;

/// <summary>
/// Maintains CPU-side copies of voxel data for chunks that finished GPU generation.
/// GL thread code populates the cache while worker threads consume the immutable views.
/// Also stores biome data alongside voxels for terrain variety and debugging.
/// </summary>
public sealed class ChunkVoxelDataCache(ArrayPool<byte>? pool = null) : IDisposable
{
    private readonly ConcurrentDictionary<int, ChunkData> chunkBuffers = new();
    private readonly ConcurrentDictionary<int, ChunkBiomeData> chunkBiomes = new();
    private readonly ArrayPool<byte> voxelPool = pool ?? ArrayPool<byte>.Shared;
    private long globalStoreVersion;
    private static readonly bool EnableVerboseLogging = false;

    public int ActiveEntryCount => chunkBuffers.Count;

    /// <summary>
    /// Store biome data for a chunk. Should be called during generation before Store().
    /// </summary>
    public void StoreBiomeData(int chunkIndex, ChunkBiomeData biomeData)
    {
        ArgumentNullException.ThrowIfNull(biomeData);
        chunkBiomes[chunkIndex] = biomeData;
        if (EnableVerboseLogging)
        {
            Log.Debug($"VoxelCache: StoreBiomeData chunk={chunkIndex}");
        }
    }

    /// <summary>
    /// Retrieve biome data for a chunk.
    /// </summary>
    public bool TryGetBiomeData(int chunkIndex, out ChunkBiomeData? biomeData)
    {
        return chunkBiomes.TryGetValue(chunkIndex, out biomeData);
    }

    /// <summary>
    /// Get biome at a specific world position by looking up the chunk and local coordinates.
    /// </summary>
    public BiomeId GetBiomeAtWorldPos(int worldX, int worldZ)
    {
        // Get chunk indices from world coordinates
        var chunkX = worldX / VoxelHelper.ChunkSideSize;
        var chunkZ = worldZ / VoxelHelper.ChunkSideSize;
        var chunkIndex = chunkX + chunkZ * VoxelHelper.WorldChunksXZ;
        
        if (chunkBiomes.TryGetValue(chunkIndex, out var biomeData))
        {
            // Calculate local coordinates within the chunk
            var localX = worldX - chunkX * VoxelHelper.ChunkSideSize;
            var localZ = worldZ - chunkZ * VoxelHelper.ChunkSideSize;
            return biomeData.GetBiomeAt(localX, localZ);
        }
        return BiomeId.Plains; // Default fallback
    }

    /// <summary>
    /// Get climate data (C/T/H/E/PV) at a specific world position (interpolated).
    /// </summary>
    public (float C, float T, float H, float E, float PV)? GetClimateAtWorldPos(int worldX, int worldZ)
    {
        var chunkX = worldX / VoxelHelper.ChunkSideSize;
        var chunkZ = worldZ / VoxelHelper.ChunkSideSize;
        var chunkIndex = chunkX + chunkZ * VoxelHelper.WorldChunksXZ;
        
        if (chunkBiomes.TryGetValue(chunkIndex, out var biomeData) && biomeData != null)
        {
            var localX = worldX - chunkX * VoxelHelper.ChunkSideSize;
            var localZ = worldZ - chunkZ * VoxelHelper.ChunkSideSize;
            var climate = biomeData.GetInterpolatedClimate(localX, localZ);
            return (climate.continentalness, climate.temperature, climate.humidity, climate.erosion, climate.peaksValleys);
        }
        return null;
    }

    /// <summary>
    /// Get raw cell climate data (NOT interpolated) - this is what biome selection uses.
    /// </summary>
    public (float C, float T, float H, float E, float PV)? GetCellClimateAtWorldPos(int worldX, int worldZ)
    {
        var chunkX = worldX / VoxelHelper.ChunkSideSize;
        var chunkZ = worldZ / VoxelHelper.ChunkSideSize;
        var chunkIndex = chunkX + chunkZ * VoxelHelper.WorldChunksXZ;
        
        if (chunkBiomes.TryGetValue(chunkIndex, out var biomeData) && biomeData != null)
        {
            var localX = ((worldX % VoxelHelper.ChunkSideSize) + VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
            var localZ = ((worldZ % VoxelHelper.ChunkSideSize) + VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
            var cellX = localX / ChunkBiomeData.BlocksPerCell;
            var cellZ = localZ / ChunkBiomeData.BlocksPerCell;
            return biomeData.GetCellClimate(cellX, cellZ);
        }
        return null;
    }

    /// <summary>
    /// Allocate a writable buffer for the specified chunk index.
    /// Call <see cref="Store"/> when the buffer has been filled with voxel data.
    /// </summary>
    public ChunkData RentWritable(int chunkIndex)
    {
        var data = new ChunkData();
        data.ChunkIndex = chunkIndex;
        data.VoxelData = voxelPool.Rent(VoxelHelper.ChunkVoxelCount);
        
        if (EnableVerboseLogging)
        {
            Log.Debug($"VoxelCache: RentWritable chunk={chunkIndex}");
        }

        return data;
    }

    /// <summary>
    /// Persist the populated buffer so worker threads can read from it.
    /// If an older buffer exists for the same chunk it is returned to the pool.
    /// </summary>
    public void Store(ChunkData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var version = Interlocked.Increment(ref globalStoreVersion);
        data.Version = version;

        chunkBuffers.AddOrUpdate(
            data.ChunkIndex,
            data,
            (_, existing) =>
            {
                // Return old buffer to pool
                if (existing.VoxelData != null)
                    voxelPool.Return(existing.VoxelData);
                return data;
            });

        if (EnableVerboseLogging)
        {
            Log.Debug($"VoxelCache: Store chunk={data.ChunkIndex} version={version}");
        }
    }

    /// <summary>
    /// Retrieve a read-only view of the cached voxel data for a chunk.
    /// </summary>
    public bool TryGetReadOnly(int chunkIndex, out ChunkVoxelDataView view)
    {
        if (chunkBuffers.TryGetValue(chunkIndex, out var data))
        {
            view = new ChunkVoxelDataView(data);
            if (EnableVerboseLogging)
            {
                Log.Debug($"VoxelCache: TryGetReadOnly chunk={chunkIndex} version={data.Version}");
            }
            return true;
        }

        view = default;
        if (EnableVerboseLogging)
        {
            Log.Warn($"VoxelCache: TryGetReadOnly miss chunk={chunkIndex}");
        }
        return false;
    }

    public bool TryGetVersion(int chunkIndex, out long version)
    {
        if (chunkBuffers.TryGetValue(chunkIndex, out var data))
        {
            version = data.Version;
            return true;
        }

        version = -1;
        return false;
    }

    /// <summary>
    /// Release cached data for a chunk and return its buffer to the pool.
    /// </summary>
    public bool TryRelease(int chunkIndex)
    {
        // Also remove biome data
        chunkBiomes.TryRemove(chunkIndex, out _);

        if (chunkBuffers.TryRemove(chunkIndex, out var data))
        {
            if (EnableVerboseLogging)
            {
                Log.Debug($"VoxelCache: Release chunk={chunkIndex} version={data.Version}");
            }
            
            if (data.VoxelData != null)
                voxelPool.Return(data.VoxelData);
                
            return true;
        }

        if (EnableVerboseLogging)
        {
            Log.Debug($"VoxelCache: Release miss chunk={chunkIndex}");
        }
        return false;
    }

    /// <summary>
    /// Drop all cached chunk buffers. Used when the GPU resources are reinitialized.
    /// </summary>
    public void Clear()
    {
        if (EnableVerboseLogging)
        {
            Log.Warn($"VoxelCache: Clear requested (entries={chunkBuffers.Count})");
        }
        foreach (var key in chunkBuffers.Keys)
        {
            TryRelease(key);
        }
        chunkBiomes.Clear();
    }

    public void Dispose() => Clear();

    /// <summary>
    /// Lightweight view that exposes read helpers without copying the underlying data.
    /// </summary>
    public readonly struct ChunkVoxelDataView
    {
        private readonly ChunkData chunkData;

        internal ChunkVoxelDataView(ChunkData data)
        {
            chunkData = data;
        }

        public int ChunkIndex => chunkData?.ChunkIndex ?? -1;
        public long Version => chunkData?.Version ?? -1;

        public bool IsValid => chunkData != null && chunkData.VoxelData != null;

        public bool TryReadVoxel(int x, int y, int z, out BlockId voxel)
        {
            if (!IsWithinBounds(x, y, z))
            {
                voxel = BlockId.Air;
                return false;
            }

            voxel = chunkData.GetBlock(x, y, z);
            return true;
        }

        public BlockId ReadVoxel(int x, int y, int z)
        {
            if (!IsWithinBounds(x, y, z))
            {
                // Return Air for out of bounds
                return BlockId.Air;
            }

            return chunkData.GetBlock(x, y, z);
        }

        public bool IsWithinBounds(int x, int y, int z)
        {
            return x is >= 0 and < VoxelHelper.ChunkSideSize &&
                   z is >= 0 and < VoxelHelper.ChunkSideSize &&
                   y is >= 0 and < VoxelHelper.ChunkYSize &&
                   IsValid;
        }
    }
}
