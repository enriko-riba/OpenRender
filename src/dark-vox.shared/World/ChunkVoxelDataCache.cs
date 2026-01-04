using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using OpenTK.Mathematics;

namespace DarkVox.World;

/// <summary>
/// Maintains CPU-side copies of voxel data for chunks that finished generation.
/// Worker threads consume immutable views; the cache also stores biome data alongside voxels.
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

    public int ActiveEntryCount => chunkBuffers.Count;

    public void StoreBiomeData(int chunkIndex, ChunkBiomeData biomeData)
    {
        ArgumentNullException.ThrowIfNull(biomeData);
        chunkBiomes[chunkIndex] = biomeData;
    }

    public readonly struct ChunkVoxelDataView
    {
        public int ChunkIndex { get; }
        public long Version { get; }
        public byte[] VoxelData { get; }
        public byte[] LightData { get; }
        public BlockId[] Palette { get; }
        public int[] SurfaceHeights { get; }

        public bool IsValid => VoxelData != null && LightData != null && Palette != null && SurfaceHeights != null;

        public ChunkVoxelDataView(ChunkData data)
        {
            ChunkIndex = data.ChunkIndex;
            Version = data.Version;
            VoxelData = data.VoxelData;
            LightData = data.LightData;
            Palette = data.Palette;
            SurfaceHeights = data.SurfaceHeights;
        }

        public bool IsWithinBounds(int x, int y, int z)
            => (uint)x < VoxelHelper.ChunkSideSize
               && (uint)z < VoxelHelper.ChunkSideSize
               && (uint)y < VoxelHelper.ChunkYSize;

        public int GetSurfaceHeight(int x, int z)
        {
            if ((uint)x >= VoxelHelper.ChunkSideSize || (uint)z >= VoxelHelper.ChunkSideSize) return 0;
            return SurfaceHeights[x + z * VoxelHelper.ChunkSideSize];
        }

        public BlockId ReadVoxel(int x, int y, int z)
        {
            var idx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
            var paletteIndex = VoxelData[idx];
            return Palette[paletteIndex];
        }

        public bool TryReadVoxel(int x, int y, int z, out BlockId voxel)
        {
            if (!IsWithinBounds(x, y, z))
            {
                voxel = default;
                return false;
            }

            voxel = ReadVoxel(x, y, z);
            return true;
        }

        public byte ReadLight(int x, int y, int z)
        {
            var idx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
            return LightData[idx];
        }
    }

    public bool TryGetVoxelDataView(int chunkIndex, out ChunkVoxelDataView view)
    {
        if (chunkBuffers.TryGetValue(chunkIndex, out var data) && data != null)
        {
            view = new ChunkVoxelDataView(data);
            return true;
        }

        view = default;
        return false;
    }

    public bool TryGetBiomeData(int chunkIndex, out ChunkBiomeData? biomeData)
        => chunkBiomes.TryGetValue(chunkIndex, out biomeData);

    public bool TryGetChunkData(int chunkIndex, out ChunkData? chunkData)
        => chunkBuffers.TryGetValue(chunkIndex, out chunkData);

    public bool TryGetVersion(int chunkIndex, out long version)
    {
        if (chunkBuffers.TryGetValue(chunkIndex, out var data) && data != null)
        {
            version = data.Version;
            return true;
        }

        version = 0;
        return false;
    }

    private object GetChunkLock(int chunkIndex) => chunkLocks.GetOrAdd(chunkIndex, static _ => new object());

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
            ReturnBuffers_NoLock(data);
        }
    }

    private void ReturnOrDeferBuffers_NoLock(ChunkData data)
    {
        if (activeLeasesByChunk.TryGetValue(data.ChunkIndex, out var count) && count > 0)
        {
            var queue = deferredReturnsByChunk.GetOrAdd(data.ChunkIndex, static _ => new ConcurrentQueue<ChunkData>());
            queue.Enqueue(data);
            return;
        }

        ReturnBuffers_NoLock(data);
    }

    private void ReturnBuffers_NoLock(ChunkData data)
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

    public BiomeId GetBiomeAtWorldPos(int worldX, int worldZ)
    {
        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(new Vector3i(worldX, 0, worldZ));
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var localX = worldX - chunkPos.X;
        var localZ = worldZ - chunkPos.Z;

        return chunkBiomes.TryGetValue(chunkIndex, out var biomeData) ? biomeData.GetBiomeAt(localX, localZ) : BiomeId.Unknown;
    }

    public ChunkData RentWritable(int chunkIndex)
    {
        var data = new ChunkData();
        data.ChunkIndex = chunkIndex;
        data.VoxelData = voxelPool.Rent(VoxelHelper.ChunkVoxelCount);
        data.VoxelDataIsPooled = true;
        Array.Clear(data.VoxelData, 0, VoxelHelper.ChunkVoxelCount);

        data.LightData = voxelPool.Rent(VoxelHelper.ChunkVoxelCount);
        data.LightDataIsPooled = true;
        Array.Clear(data.LightData, 0, VoxelHelper.ChunkVoxelCount);

        return data;
    }

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

            if (!activeLeasesByChunk.TryGetValue(data.ChunkIndex, out var leases) || leases <= 0)
            {
                FlushDeferredReturns_NoLock(data.ChunkIndex);
            }
        }
    }

    public bool TryRelease(int chunkIndex)
    {
        chunkBiomes.TryRemove(chunkIndex, out _);

        var gate = GetChunkLock(chunkIndex);
        lock (gate)
        {
            if (chunkBuffers.TryRemove(chunkIndex, out var data) && data != null)
            {
                ReturnOrDeferBuffers_NoLock(data);

                if (!activeLeasesByChunk.TryGetValue(chunkIndex, out var leases) || leases <= 0)
                {
                    FlushDeferredReturns_NoLock(chunkIndex);
                }

                return true;
            }
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

    public void Clear()
    {
        foreach (var key in chunkBuffers.Keys)
        {
            TryRelease(key);
        }

        chunkBiomes.Clear();
    }

    public void Dispose() => Clear();

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
