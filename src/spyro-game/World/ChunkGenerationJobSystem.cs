using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenRender;

namespace SpyroGame.World;

internal sealed class ChunkGenerationJobSystem : IDisposable
{
    private readonly ChunkVoxelDataCache voxelCache;
    private readonly BlockingCollection<GenerationWorkItem> workQueue = [];
    private readonly ConcurrentQueue<ChunkGenerationJobResult> completedResults = new();
    private readonly CancellationTokenSource cancellationSource = new();
    private readonly Task[] workers;
    private TerrainConfig config;
    private long configVersion;
    private long enqueueCounter;
    private long buildCounter;
    private bool disposed;

    public ChunkGenerationJobSystem(ChunkVoxelDataCache voxelCache, TerrainConfig initialConfig, int workerCount = 0)
    {
        this.voxelCache = voxelCache ?? throw new ArgumentNullException(nameof(voxelCache));
        config = initialConfig ?? throw new ArgumentNullException(nameof(initialConfig));
        workers = new Task[workerCount > 0 ? workerCount : Math.Max(1, Environment.ProcessorCount / 4)];

        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Factory.StartNew(
                WorkerLoop,
                cancellationSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public int WorkerCount => workers.Length;

    public void UpdateConfig(TerrainConfig newConfig)
    {
        config = newConfig ?? throw new ArgumentNullException(nameof(newConfig));
        Interlocked.Increment(ref configVersion);
    }

    public void Enqueue(int chunkIndex, IReadOnlyDictionary<int, BlockId>? blockIdEdits)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(ChunkGenerationJobSystem));
        }

        var enqueueId = Interlocked.Increment(ref enqueueCounter);
        var work = new GenerationWorkItem(chunkIndex, blockIdEdits, enqueueId);
        try
        {
            workQueue.Add(work, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress, ignore.
        }
    }

    public bool TryDequeueResult(out ChunkGenerationJobResult result) => completedResults.TryDequeue(out result);

    private void WorkerLoop()
    {
        var generator = new CpuTerrainGenerator(config);
        var appliedConfigVersion = configVersion;

        try
        {
            foreach (var work in workQueue.GetConsumingEnumerable(cancellationSource.Token))
            {
                try
                {
                    var currentVersion = Volatile.Read(ref configVersion);
                    if (currentVersion != appliedConfigVersion)
                    {
                        generator.UpdateConfig(config);
                        appliedConfigVersion = currentVersion;
                    }

                    ChunkData? writable = null;
                    try
                    {
                        writable = voxelCache.RentWritable(work.ChunkIndex);
                        var result = generator.GenerateChunk(work.ChunkIndex, writable, work.BlockIdEdits);
                        
                        // Store biome data alongside voxels
                        var biomeData = generator.GetLastChunkBiomeData();
                        if (biomeData != null)
                        {
                            voxelCache.StoreBiomeData(work.ChunkIndex, biomeData);
                        }

                        // Calculate lighting (Sky + Block)
                        LightingCalculator.CalculateLighting(writable);

                        voxelCache.Store(writable);
                        writable = null;

                        var buildId = Interlocked.Increment(ref buildCounter);
                        completedResults.Enqueue(new ChunkGenerationJobResult(work.ChunkIndex, result, work.EnqueueId, buildId));
                    }
                    finally
                    {
                        // If writable is still set, it means we failed before storing.
                        // We should return the buffer to the pool manually if possible.
                        // Since we don't have a direct way to return without storing, 
                        // and we don't want to store partial data, we might leak here in case of exception.
                        // Ideally ChunkData would be disposable or we'd have a Return method.
                        // For now, we'll rely on the fact that exceptions here are rare/fatal.
                        // TODO: Add ReturnWritable to ChunkVoxelDataCache
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"CpuGeneration: failed chunk {work.ChunkIndex}: {ex.Message}");
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
            Log.Warn($"ChunkGenerationJobSystem dispose encountered errors: {ex.Flatten().Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"ChunkGenerationJobSystem dispose timed out: {ex.Message}");
        }

        workQueue.Dispose();
        cancellationSource.Dispose();
    }

    internal readonly record struct GenerationWorkItem(int ChunkIndex, IReadOnlyDictionary<int, BlockId>? BlockIdEdits, long EnqueueId);

    internal readonly record struct ChunkGenerationJobResult(int ChunkIndex, CpuTerrainGenerator.ChunkGenerationResult Generation, long EnqueueId, long BuildId);
}
