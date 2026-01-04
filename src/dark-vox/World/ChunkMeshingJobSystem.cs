using DarkVox.Shared.World;
using OpenRender;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DarkVox.World;

/// <summary>
/// Job system for CPU mesh building using dedicated worker threads.
/// Optimized for low-latency chunk streaming with deduplication.
/// </summary>
public sealed class ChunkMeshingJobSystem : IDisposable
{
    private readonly ChunkVoxelDataCache voxelCache;
    private readonly ChunkProcessingMetrics? metrics;
    
    // Use ConcurrentQueue + semaphore for lower overhead than BlockingCollection
    private readonly ConcurrentQueue<ChunkMeshWorkItem> workQueue = new();
    private readonly SemaphoreSlim workAvailable = new(0, int.MaxValue);
    
    // Deduplication: only keep latest enqueue for each chunk
    private readonly ConcurrentDictionary<int, long> latestEnqueueByChunk = new();
    
    private readonly ConcurrentQueue<ChunkMesh> completedMeshes = new();
    private readonly CancellationTokenSource cancellationSource = new();
    private readonly Task[] workerTasks;
    private readonly int workerCount;
    private bool disposed;
    private long enqueueCounter;
    private long buildCounter;

    internal event Action<ChunkMeshWorkItem, bool>? WorkItemCompleted;

    public ChunkMeshingJobSystem(ChunkVoxelDataCache voxelCache, ChunkProcessingMetrics? metrics = null, int maxParallelism = 0)
    {
        this.voxelCache = voxelCache ?? throw new ArgumentNullException(nameof(voxelCache));
        this.metrics = metrics;
        
        // Use dedicated worker threads for consistent throughput
        // Leave 2 cores for main thread + generation
        workerCount = maxParallelism > 0 ? maxParallelism : Math.Max(2, Environment.ProcessorCount - 2);
        
        workerTasks = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workerTasks[i] = Task.Factory.StartNew(
                WorkerLoop,
                cancellationSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Enqueue(int chunkIndex, byte placeholderMask, long cacheVersion, bool propagateLight = false)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(ChunkMeshingJobSystem));

        var enqueueId = Interlocked.Increment(ref enqueueCounter);
        var effectiveVersion = cacheVersion <= 0 ? 0 : cacheVersion;
        
        // Update latest enqueue for this chunk (deduplication)
        latestEnqueueByChunk[chunkIndex] = enqueueId;

        var item = new ChunkMeshWorkItem(chunkIndex, placeholderMask, effectiveVersion, enqueueId, 0, propagateLight);
        workQueue.Enqueue(item);
        
        // Signal that work is available
        try
        {
            workAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // Queue is very full - this is fine, workers will catch up
        }
    }

    public bool TryDequeueResult([NotNullWhen(true)] out ChunkMesh? mesh) => completedMeshes.TryDequeue(out mesh);

    public void DrainPendingWorkItems()
    {
        while (workQueue.TryDequeue(out _)) { }
        latestEnqueueByChunk.Clear();
    }

    private void WorkerLoop()
    {
        try
        {
            while (!cancellationSource.Token.IsCancellationRequested)
            {
                // Wait for work with timeout to allow cancellation checks
                if (!workAvailable.Wait(50, cancellationSource.Token))
                    continue;
                
                // Dequeue work item
                if (!workQueue.TryDequeue(out var item))
                    continue;
                
                // Deduplication: skip if a newer enqueue exists for this chunk
                if (latestEnqueueByChunk.TryGetValue(item.ChunkIndex, out var latestId) && 
                    latestId > item.EnqueueId)
                {
                    // Skip stale work item - a newer request supersedes this one
                    continue;
                }
                
                // Process the item
                ProcessMeshItem(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private void ProcessMeshItem(ChunkMeshWorkItem item)
    {
        var buildId = Interlocked.Increment(ref buildCounter);
        var executingItem = item with { BuildId = buildId };
        var succeeded = false;

        try
        {
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
                succeeded = true;
            }
            else
            {
                Log.Debug($"CpuMeshing: builder skipped chunk {item.ChunkIndex} (seq={item.EnqueueId}, build={buildId})");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"CPU meshing failed for chunk {item.ChunkIndex}: {ex}");
        }
        finally
        {
            try
            {
                WorkItemCompleted?.Invoke(executingItem, succeeded);
            }
            catch (Exception callbackEx)
            {
                Log.Warn($"ChunkMeshingJobSystem completion callback threw: {callbackEx.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        cancellationSource.Cancel();

        try
        {
            Task.WaitAll(workerTasks, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            Log.Warn($"ChunkMeshingJobSystem dispose encountered errors: {ex.Flatten().Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"ChunkMeshingJobSystem dispose timed out: {ex.Message}");
        }

        workAvailable.Dispose();
        cancellationSource.Dispose();
    }

    /// <summary>
    /// Propagate light across chunk boundaries on background thread.
    /// </summary>
    private void PropagateBoundaryLight(int chunkIdx)
    {
        if (!voxelCache.TryAcquireChunkData(chunkIdx, out var centerLease))
            return;

        using (centerLease)
        {
            var centerData = centerLease.Data;

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

                if (!voxelCache.TryAcquireChunkData(neighborIdx, out var neighborLease))
                    continue;

                using (neighborLease)
                {
                    var neighborData = neighborLease.Data;
                    // Bidirectional propagation
                    LightingCalculator.PropagateNeighborLight(centerData, neighborData, dx, dz);
                }
            }
        }
    }

    public readonly record struct ChunkMeshWorkItem(int ChunkIndex, byte PlaceholderMask, long CacheVersion, long EnqueueId, long BuildId, bool PropagateLight = false);
}
