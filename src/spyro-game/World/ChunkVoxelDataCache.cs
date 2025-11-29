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
/// </summary>
public sealed class ChunkVoxelDataCache(ArrayPool<uint>? pool = null) : IDisposable
{
    private readonly ConcurrentDictionary<int, ChunkVoxelBuffer> chunkBuffers = new();
    private readonly ArrayPool<uint> voxelPool = pool ?? ArrayPool<uint>.Shared;
    private long globalStoreVersion;
    private static bool EnableVerboseLogging = false;

    public int ActiveEntryCount => chunkBuffers.Count;

    /// <summary>
    /// Allocate a writable buffer for the specified chunk index.
    /// Call <see cref="Store"/> when the buffer has been filled with voxel data.
    /// </summary>
    public ChunkVoxelBuffer RentWritable(int chunkIndex)
    {
        var array = voxelPool.Rent(VoxelHelper.ChunkVoxelCount);
        if (EnableVerboseLogging)
        {
            Log.Debug($"VoxelCache: RentWritable chunk={chunkIndex} bufferId={RuntimeHelpers.GetHashCode(array)}");
        }

        return new ChunkVoxelBuffer(chunkIndex, array, voxelPool);
    }

    /// <summary>
    /// Persist the populated buffer so worker threads can read from it.
    /// If an older buffer exists for the same chunk it is returned to the pool.
    /// </summary>
    public void Store(ChunkVoxelBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var version = Interlocked.Increment(ref globalStoreVersion);
        buffer.SetVersion(version);

        chunkBuffers.AddOrUpdate(
            buffer.ChunkIndex,
            buffer,
            (_, existing) =>
            {
                existing.Dispose();
                return buffer;
            });

        if (EnableVerboseLogging)
        {
            Log.Debug($"VoxelCache: Store chunk={buffer.ChunkIndex} version={version} bufferId={RuntimeHelpers.GetHashCode(buffer.RawData)}");
        }
    }

    /// <summary>
    /// Retrieve a read-only view of the cached voxel data for a chunk.
    /// </summary>
    public bool TryGetReadOnly(int chunkIndex, out ChunkVoxelDataView view)
    {
        if (chunkBuffers.TryGetValue(chunkIndex, out var buffer))
        {
            view = new ChunkVoxelDataView(chunkIndex, buffer.RawData, buffer.Version);
            if (EnableVerboseLogging)
            {
                Log.Debug($"VoxelCache: TryGetReadOnly chunk={chunkIndex} version={buffer.Version} bufferId={RuntimeHelpers.GetHashCode(buffer.RawData)}");
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
        if (chunkBuffers.TryGetValue(chunkIndex, out var buffer))
        {
            version = buffer.Version;
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
        if (chunkBuffers.TryRemove(chunkIndex, out var buffer))
        {
            if (EnableVerboseLogging)
            {
                Log.Debug($"VoxelCache: Release chunk={chunkIndex} version={buffer.Version} bufferId={RuntimeHelpers.GetHashCode(buffer.RawData)}");
            }
            buffer.Dispose();
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
    }

    public void Dispose() => Clear();

    /// <summary>
    /// Wrapper over a pooled uint[] tracked by chunk index.
    /// </summary>
    public sealed class ChunkVoxelBuffer : IDisposable
    {
        private readonly ArrayPool<uint> pool;
        private uint[] data;
        private bool disposed;
        public long Version { get; private set; }

        internal ChunkVoxelBuffer(int chunkIndex, uint[] data, ArrayPool<uint> pool)
        {
            ChunkIndex = chunkIndex;
            this.data = data ?? throw new ArgumentNullException(nameof(data));
            this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        public int ChunkIndex { get; }

        internal uint[] RawData => data;

        internal void SetVersion(long version) => Version = version;

        /// <summary>
        /// Span-based accessor used during the GL-thread copy operation.
        /// </summary>
        public Span<uint> Span
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, nameof(ChunkVoxelBuffer));
                return data.AsSpan(0, VoxelHelper.ChunkVoxelCount);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            var array = data;
            data = Array.Empty<uint>();
            if (array.Length > 0)
            {
                pool.Return(array, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Lightweight view that exposes read helpers without copying the underlying data.
    /// </summary>
    public readonly struct ChunkVoxelDataView
    {
        private readonly uint[] data;

        internal ChunkVoxelDataView(int chunkIndex, uint[] data, long version)
        {
            ChunkIndex = chunkIndex;
            Version = version;
            this.data = data ?? Array.Empty<uint>();
        }

        public int ChunkIndex { get; }
        public long Version { get; }

        public bool IsValid => data.Length >= VoxelHelper.ChunkVoxelCount;

        public ReadOnlySpan<uint> Voxels => data.AsSpan(0, Math.Min(data.Length, VoxelHelper.ChunkVoxelCount));

        public bool TryReadVoxel(int x, int y, int z, out uint voxel)
        {
            if (!IsWithinBounds(x, y, z))
            {
                voxel = 0u;
                return false;
            }

            voxel = ReadUnchecked(x, y, z);
            return true;
        }

        public uint ReadVoxel(int x, int y, int z)
        {
            if (!IsWithinBounds(x, y, z))
            {
                throw new ArgumentOutOfRangeException(nameof(x), "Voxel coordinates are outside the chunk bounds.");
            }

            return ReadUnchecked(x, y, z);
        }

        public bool IsWithinBounds(int x, int y, int z)
        {
            return x is >= 0 and < VoxelHelper.ChunkSideSize &&
                   z is >= 0 and < VoxelHelper.ChunkSideSize &&
                   y is >= 0 and < VoxelHelper.ChunkYSize &&
                   IsValid;
        }

        private uint ReadUnchecked(int x, int y, int z)
        {
            var index = y * VoxelHelper.ChunkSideSizeSquare + z * VoxelHelper.ChunkSideSize + x;
            return data[index];
        }
    }
}
