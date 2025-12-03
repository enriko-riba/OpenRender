using OpenRender;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SpyroGame.World;

/// <summary>
/// Job system for CPU mesh building using the ThreadPool.
/// Uses a single dispatcher thread to batch work items and process them via Parallel.ForEach.
/// </summary>
public sealed class ChunkMeshingJobSystem : IDisposable
{
    private readonly ChunkVoxelDataCache voxelCache;
    private readonly ChunkProcessingMetrics? metrics;
    private readonly BlockingCollection<ChunkMeshWorkItem> workQueue = [];
    private readonly ConcurrentQueue<CpuChunkMesh> completedMeshes = [];
    private readonly CancellationTokenSource cancellationSource = new();
    private readonly Task dispatcherTask;
    private readonly int maxParallelism;
    private bool disposed;
    private long enqueueCounter;
    private long buildCounter;

    public ChunkMeshingJobSystem(ChunkVoxelDataCache voxelCache, ChunkProcessingMetrics? metrics = null, int maxParallelism = 0)
    {
        this.voxelCache = voxelCache ?? throw new ArgumentNullException(nameof(voxelCache));
        this.metrics = metrics;
        
        // Use ProcessorCount for parallelism
        this.maxParallelism = maxParallelism > 0 ? maxParallelism : Math.Max(1, Environment.ProcessorCount);

        // Single dispatcher thread that batches and processes work
        dispatcherTask = Task.Factory.StartNew(
            DispatcherLoop,
            cancellationSource.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Enqueue(int chunkIndex, byte placeholderMask, long cacheVersion, bool propagateLight = false)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(ChunkMeshingJobSystem));

        var enqueueId = Interlocked.Increment(ref enqueueCounter);
        var effectiveVersion = cacheVersion <= 0 ? 0 : cacheVersion;
        
        if (effectiveVersion == 0)
        {
            Log.Debug($"CpuMeshing: enqueue chunk={chunkIndex} without cache version (mask=0x{placeholderMask:X2}, seq={enqueueId})");
        }
        else
        {
            Log.Debug($"CpuMeshing: enqueue chunk={chunkIndex} mask=0x{placeholderMask:X2} cacheVer={effectiveVersion} seq={enqueueId} light={propagateLight}");
        }

        var item = new ChunkMeshWorkItem(chunkIndex, placeholderMask, effectiveVersion, enqueueId, 0, propagateLight);

        try
        {
            workQueue.Add(item, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            // System is shutting down; ignore new work.
        }
    }

    public bool TryDequeueResult([NotNullWhen(true)] out CpuChunkMesh? mesh) => completedMeshes.TryDequeue(out mesh);

    public void DrainPendingWorkItems()
    {
        while (workQueue.TryTake(out _, 0)) { }
    }

    private void DispatcherLoop()
    {
        const int batchSize = 32;
        var batch = new List<ChunkMeshWorkItem>(batchSize);

        try
        {
            while (!cancellationSource.Token.IsCancellationRequested)
            {
                batch.Clear();

                // Wait for first item (blocking)
                if (workQueue.TryTake(out var firstItem, 50, cancellationSource.Token))
                {
                    batch.Add(firstItem);

                    // Collect more items if available (non-blocking)
                    while (batch.Count < batchSize && workQueue.TryTake(out var item))
                    {
                        batch.Add(item);
                    }

                    // Process batch in parallel using ThreadPool
                    ProcessBatch(batch);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private void ProcessBatch(List<ChunkMeshWorkItem> batch)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = cancellationSource.Token
        };

        try
        {
            Parallel.ForEach(batch, options, ProcessMeshItem);
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress.
        }
    }

    private void ProcessMeshItem(ChunkMeshWorkItem item)
    {
        try
        {
            var buildId = Interlocked.Increment(ref buildCounter);
            var executingItem = item with { BuildId = buildId };

            // Propagate boundary light BEFORE building mesh (on background thread)
            if (item.PropagateLight)
            {
                var lightSw = Stopwatch.StartNew();
                PropagateBoundaryLight(item.ChunkIndex);
                lightSw.Stop();
                metrics?.RecordLightPropagation(lightSw.Elapsed.TotalMilliseconds);
            }

            var meshSw = Stopwatch.StartNew();
            if (ChunkMeshBuilder.TryBuild(executingItem, voxelCache, out var mesh))
            {
                meshSw.Stop();
                metrics?.RecordMeshBuild(meshSw.Elapsed.TotalMilliseconds);
                completedMeshes.Enqueue(mesh);
            }
            else
            {
                Log.Warn($"CpuMeshing: builder skipped chunk {item.ChunkIndex} (seq={item.EnqueueId}, build={buildId})");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"CPU meshing failed for chunk {item.ChunkIndex}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        workQueue.CompleteAdding();
        cancellationSource.Cancel();

        try
        {
            dispatcherTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            Log.Warn($"ChunkMeshingJobSystem dispose encountered errors: {ex.Flatten().Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"ChunkMeshingJobSystem dispose timed out: {ex.Message}");
        }

        workQueue.Dispose();
        cancellationSource.Dispose();
    }

    /// <summary>
    /// Propagate light across chunk boundaries on background thread.
    /// </summary>
    private void PropagateBoundaryLight(int chunkIdx)
    {
        if (!voxelCache.TryGetChunkData(chunkIdx, out var centerData) || centerData == null)
            return;

        var chunkX = chunkIdx % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIdx / VoxelHelper.WorldChunksXZ;

        // Cardinal neighbors only
        (int dx, int dz)[] neighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];

        foreach (var (dx, dz) in neighbors)
        {
            var nx = chunkX + dx;
            var nz = chunkZ + dz;

            if (nx < 0 || nx >= VoxelHelper.WorldChunksXZ || nz < 0 || nz >= VoxelHelper.WorldChunksXZ)
                continue;

            var neighborIdx = nz * VoxelHelper.WorldChunksXZ + nx;

            if (!voxelCache.TryGetChunkData(neighborIdx, out var neighborData) || neighborData == null)
                continue;

            // Bidirectional propagation
            LightingCalculator.PropagateNeighborLight(centerData, neighborData, dx, dz);
        }
    }

    internal readonly record struct ChunkMeshWorkItem(int ChunkIndex, byte PlaceholderMask, long CacheVersion, long EnqueueId, long BuildId, bool PropagateLight = false);
}
