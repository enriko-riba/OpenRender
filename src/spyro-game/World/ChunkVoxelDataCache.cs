using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using OpenRender;
using SpyroGame.World.Registry;

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
    private readonly ConcurrentDictionary<int, object> chunkLocks = new();
    private readonly ConcurrentDictionary<int, int> activeLeasesByChunk = new();
    private readonly ConcurrentDictionary<int, ConcurrentQueue<ChunkData>> deferredReturnsByChunk = new();
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
    public bool TryGetBiomeData(int chunkIndex, out ChunkBiomeData? biomeData) => chunkBiomes.TryGetValue(chunkIndex, out biomeData);

    /// <summary>
    /// Retrieve voxel data for a chunk.
    /// </summary>
    public bool TryGetChunkData(int chunkIndex, out ChunkData? chunkData) => chunkBuffers.TryGetValue(chunkIndex, out chunkData);

    private object GetChunkLock(int chunkIndex) => chunkLocks.GetOrAdd(chunkIndex, static _ => new object());

    /// <summary>
    /// Acquire a lease to safely use chunk data across threads.
    /// While leased, pooled buffers for this chunk will not be returned to the pool.
    /// </summary>
    public bool TryAcquireChunkData(int chunkIndex, out ChunkDataLease lease)
    {
        var gate = GetChunkLock(chunkIndex);
        lock (gate)
        {
            if (!chunkBuffers.TryGetValue(chunkIndex, out var data) || data == null)
            {
                lease = default;
                return false;
            }

            _ = activeLeasesByChunk.AddOrUpdate(chunkIndex, 1, static (_, current) => current + 1);
            lease = new ChunkDataLease(this, chunkIndex, data);
            return true;
        }
    }

    private void ReleaseChunkDataLease(int chunkIndex)
    {
        var gate = GetChunkLock(chunkIndex);
        lock (gate)
        {
            if (!activeLeasesByChunk.TryGetValue(chunkIndex, out var current) || current <= 0)
            {
                return;
            }

            if (current == 1)
            {
                _ = activeLeasesByChunk.TryRemove(chunkIndex, out _);
                FlushDeferredReturns_NoLock(chunkIndex);
                return;
            }

            _ = activeLeasesByChunk.TryUpdate(chunkIndex, current - 1, current);
        }
    }

    private void FlushDeferredReturns_NoLock(int chunkIndex)
    {
        if (!deferredReturnsByChunk.TryRemove(chunkIndex, out var queue) || queue == null)
        {
            return;
        }

        while (queue.TryDequeue(out var data))
        {
            if (data.VoxelData != null && data.VoxelDataIsPooled)
            {
                voxelPool.Return(data.VoxelData);
            }

            if (data.LightData != null && data.LightDataIsPooled)
            {
                voxelPool.Return(data.LightData);
            }
        }
    }

    private void ReturnOrDeferBuffers_NoLock(ChunkData data)
    {
        // If anyone is currently using this chunk's buffers, defer returning pooled arrays.
        if (activeLeasesByChunk.TryGetValue(data.ChunkIndex, out var count) && count > 0)
        {
            var queue = deferredReturnsByChunk.GetOrAdd(data.ChunkIndex, static _ => new ConcurrentQueue<ChunkData>());
            queue.Enqueue(data);
            return;
        }

        if (data.VoxelData != null && data.VoxelDataIsPooled)
        {
            voxelPool.Return(data.VoxelData);
        }

        if (data.LightData != null && data.LightDataIsPooled)
        {
            voxelPool.Return(data.LightData);
        }
    }

    /// <summary>
    /// Get biome at a specific world position by looking up the chunk and local coordinates.
    /// </summary>
    public BiomeId GetBiomeAtWorldPos(int worldX, int worldZ)
    {
        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(new OpenTK.Mathematics.Vector3i(worldX, 0, worldZ));
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var localX = worldX - chunkPos.X;
        var localZ = worldZ - chunkPos.Z;

        // Prefer high-resolution per-column biome data when present.
        return chunkBiomes.TryGetValue(chunkIndex, out var biomeData) ? biomeData.GetBiomeAt(localX, localZ) : BiomeId.Unknown;
    }

    /// <summary>
    /// Get climate data (C/T/H/E/PV) at a specific world position (interpolated).
    /// </summary>
    public (float C, float T, float H, float E, float PV)? GetClimateAtWorldPos(int worldX, int worldZ)
    {
        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(new OpenTK.Mathematics.Vector3i(worldX, 0, worldZ));
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var localX = worldX - chunkPos.X;
        var localZ = worldZ - chunkPos.Z;

        if (chunkBiomes.TryGetValue(chunkIndex, out var biomeData) && biomeData != null)
        {
            var climate = biomeData.GetInterpolatedClimate(localX, localZ);
            return (climate.continentalness, climate.temperature, climate.humidity, climate.erosion, climate.peaksValleys);
        }
        return null;
    }

    /// <summary>
    /// Get raw cell climate data (NOT interpolated) - this is what biome selection uses.
    /// </summary>
    public (float C, float T, float H, float E, float PV, float W)? GetCellClimateAtWorldPos(int worldX, int worldZ)
    {
        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(new OpenTK.Mathematics.Vector3i(worldX, 0, worldZ));
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var localX = worldX - chunkPos.X;
        var localZ = worldZ - chunkPos.Z;

        if (chunkBiomes.TryGetValue(chunkIndex, out var biomeData) && biomeData != null)
        {
            localX = ((localX % VoxelHelper.ChunkSideSize) + VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
            localZ = ((localZ % VoxelHelper.ChunkSideSize) + VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
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
        data.VoxelDataIsPooled = true;

        // ArrayPool returns dirty buffers. Ensure any unwritten voxels default to Air (palette index 0).
        Array.Clear(data.VoxelData, 0, VoxelHelper.ChunkVoxelCount);

        data.LightData = voxelPool.Rent(VoxelHelper.ChunkVoxelCount);
        data.LightDataIsPooled = true;

        // Ensure light starts cleared (some call sites clear LightData, but not all).
        Array.Clear(data.LightData, 0, VoxelHelper.ChunkVoxelCount);
        
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

        EnsurePooledBuffers(data);

        var version = Interlocked.Increment(ref globalStoreVersion);
        data.Version = version;

        var gate = GetChunkLock(data.ChunkIndex);
        lock (gate)
        {
            if (chunkBuffers.TryGetValue(data.ChunkIndex, out var existing) && existing != null)
            {
                chunkBuffers[data.ChunkIndex] = data;
                ReturnOrDeferBuffers_NoLock(existing);
            }
            else
            {
                chunkBuffers[data.ChunkIndex] = data;
            }

            // If no one is currently leasing this chunk, we can flush any deferred returns.
            if (!activeLeasesByChunk.TryGetValue(data.ChunkIndex, out var leases) || leases <= 0)
            {
                FlushDeferredReturns_NoLock(data.ChunkIndex);
            }
        }

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

        var gate = GetChunkLock(chunkIndex);
        lock (gate)
        {
            if (chunkBuffers.TryRemove(chunkIndex, out var data) && data != null)
            {
                if (EnableVerboseLogging)
                {
                    Log.Debug($"VoxelCache: Release chunk={chunkIndex} version={data.Version}");
                }

                ReturnOrDeferBuffers_NoLock(data);

                if (!activeLeasesByChunk.TryGetValue(chunkIndex, out var leases) || leases <= 0)
                {
                    FlushDeferredReturns_NoLock(chunkIndex);
                }

                return true;
            }
        }

        if (EnableVerboseLogging)
        {
            Log.Debug($"VoxelCache: Release miss chunk={chunkIndex}");
        }
        return false;
    }

    private void EnsurePooledBuffers(ChunkData data)
    {
        var expected = VoxelHelper.ChunkVoxelCount;

        if (data.VoxelData is null || data.VoxelData.Length < expected)
        {
            throw new ArgumentException($"ChunkData.VoxelData must be at least {expected} bytes", nameof(data));
        }

        if (!data.VoxelDataIsPooled)
        {
            var pooled = voxelPool.Rent(expected);
            Buffer.BlockCopy(data.VoxelData, 0, pooled, 0, expected);
            data.VoxelData = pooled;
            data.VoxelDataIsPooled = true;
        }

        if (data.LightData is null || data.LightData.Length < expected)
        {
            throw new ArgumentException($"ChunkData.LightData must be at least {expected} bytes", nameof(data));
        }

        if (!data.LightDataIsPooled)
        {
            var pooled = voxelPool.Rent(expected);
            Buffer.BlockCopy(data.LightData, 0, pooled, 0, expected);
            data.LightData = pooled;
            data.LightDataIsPooled = true;
        }
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

        public BiomeId ReadCoarseBiome(int localX, int localZ) => BiomeId.Unknown;

        /// <summary>
        /// Gets the surface height (highest opaque block Y) for a column in the chunk.
        /// Returns -1 if the column is empty/air.
        /// </summary>
        public int GetSurfaceHeight(int x, int z)
        {
            if (!IsWithinBounds(x, 0, z)) return -1;
            var idx = z * VoxelHelper.ChunkSideSize + x;
            return chunkData.SurfaceHeights[idx];
        }

        public bool TryReadVoxel(int x, int y, int z, out BlockId voxel)
        {
            if (!IsWithinBounds(x, y, z))
            {
                voxel = BlockId.Air;
                return false;
            }

            var idx = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
            var voxels = chunkData.VoxelData;
            if (voxels is null || (uint)idx >= (uint)voxels.Length)
            {
                voxel = BlockId.Air;
                return false;
            }

            var paletteIndex = voxels[idx];
            var palette = chunkData.Palette;
            if (palette is null || (uint)paletteIndex >= (uint)palette.Length)
            {
                voxel = BlockId.Air;
                return false;
            }

            voxel = palette[paletteIndex];
            return true;
        }

        public BlockId ReadVoxel(int x, int y, int z)
        {
            if (!IsWithinBounds(x, y, z))
            {
                // Return Air for out of bounds
                return BlockId.Air;
            }

            return TryReadVoxel(x, y, z, out var voxel) ? voxel : BlockId.Air;
        }

        public uint ReadLight(int x, int y, int z)
        {
            if (!IsWithinBounds(x, y, z))
            {
                return 0;
            }
            var idx = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;

            // Be defensive: corrupt/legacy chunks can have incorrectly sized LightData.
            // Returning 0 is safe (dark) and avoids crashing the mesher.
            var light = chunkData.LightData;
            return light is null || (uint)idx >= (uint)light.Length ? 0 : (uint)light[idx];
        }

        public bool IsWithinBounds(int x, int y, int z) => x is >= 0 and < VoxelHelper.ChunkSideSize &&
                   z is >= 0 and < VoxelHelper.ChunkSideSize &&
                   y is >= 0 and < VoxelHelper.ChunkYSize &&
                   IsValid;
    }

    /// <summary>
    /// Lease object for safely using cached chunk data across threads.
    /// </summary>
    public readonly struct ChunkDataLease : IDisposable
    {
        private readonly ChunkVoxelDataCache? owner;
        private readonly int chunkIndex;

        public ChunkData Data { get; }

        internal ChunkDataLease(ChunkVoxelDataCache owner, int chunkIndex, ChunkData data)
        {
            this.owner = owner;
            this.chunkIndex = chunkIndex;
            Data = data;
        }

        public void Dispose() => owner?.ReleaseChunkDataLease(chunkIndex);
    }
}
