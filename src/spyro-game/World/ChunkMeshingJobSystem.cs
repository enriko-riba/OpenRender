using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using OpenRender;

namespace SpyroGame.World;

/// <summary>
/// Background worker pool that translates cached voxel data into CPU-built meshes.
/// GL thread schedules work items, worker threads pull data from <see cref="ChunkVoxelDataCache"/>
/// and the finished meshes are queued for upload.
/// </summary>
public sealed class ChunkMeshingJobSystem : IDisposable
{
    private readonly ChunkVoxelDataCache voxelCache;
    private readonly BlockingCollection<ChunkMeshWorkItem> workQueue = [];
    private readonly ConcurrentQueue<CpuChunkMesh> completedMeshes = [];
    private readonly CancellationTokenSource cancellationSource = new();
    private readonly Task[] workers;
    private bool disposed;
    private long enqueueCounter;
    private long buildCounter;

    public ChunkMeshingJobSystem(ChunkVoxelDataCache voxelCache, int workerCount = 0)
    {
        this.voxelCache = voxelCache ?? throw new ArgumentNullException(nameof(voxelCache));
        var effectiveWorkers = workerCount > 0 ? workerCount : Math.Max(1, Environment.ProcessorCount - 2);
        workers = new Task[effectiveWorkers];

        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Factory.StartNew(
                WorkerLoop,
                cancellationSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Enqueue(int chunkIndex, byte placeholderMask, long cacheVersion)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(ChunkMeshingJobSystem));
        }

        var enqueueId = Interlocked.Increment(ref enqueueCounter);
        var effectiveVersion = cacheVersion <= 0 ? 0 : cacheVersion;
        if (effectiveVersion == 0)
        {
            Log.Debug($"CpuMeshing: enqueue chunk={chunkIndex} without cache version (mask=0x{placeholderMask:X2}, seq={enqueueId})");
        }
        else
        {
            Log.Debug($"CpuMeshing: enqueue chunk={chunkIndex} mask=0x{placeholderMask:X2} cacheVer={effectiveVersion} seq={enqueueId}");
        }

        var item = new ChunkMeshWorkItem(chunkIndex, placeholderMask, effectiveVersion, enqueueId, 0);

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
        while (workQueue.TryTake(out _, 0))
        {
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var item in workQueue.GetConsumingEnumerable(cancellationSource.Token))
            {
                try
                {
                    var buildId = Interlocked.Increment(ref buildCounter);
                    var executingItem = item with { BuildId = buildId };

                    if (ChunkMeshBuilder.TryBuild(executingItem, voxelCache, out var mesh))
                    {
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
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        workQueue.CompleteAdding();
        cancellationSource.Cancel();

        try
        {
            Task.WaitAll(workers, TimeSpan.FromSeconds(1));
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

    internal readonly record struct ChunkMeshWorkItem(int ChunkIndex, byte PlaceholderMask, long CacheVersion, long EnqueueId, long BuildId);
}
