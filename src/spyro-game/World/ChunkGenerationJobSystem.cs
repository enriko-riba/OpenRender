using System.Collections.Concurrent;
using System.Diagnostics;
using OpenRender;

namespace SpyroGame.World;

/// <summary>
/// Job system for CPU terrain generation using the ThreadPool.
/// Uses a single dispatcher thread to batch work items and process them via Parallel.ForEach.
/// </summary>
internal sealed class ChunkGenerationJobSystem : IDisposable
{
    private readonly ChunkVoxelDataCache voxelCache;
    private readonly ChunkProcessingMetrics? metrics;
    private readonly BlockingCollection<GenerationWorkItem> workQueue = [];
    private readonly ConcurrentQueue<ChunkGenerationJobResult> completedResults = new();
    private readonly CancellationTokenSource cancellationSource = new();
    private readonly Task dispatcherTask;
    private readonly int maxParallelism;
    private TerrainConfig config;
    private long configVersion;
    private long enqueueCounter;
    private long buildCounter;
    private bool disposed;

    // Thread-local generators to avoid contention on shared state
    private readonly ThreadLocal<CpuTerrainGenerator> threadLocalGenerator;
    private readonly ThreadLocal<long> threadLocalConfigVersion;

    public ChunkGenerationJobSystem(ChunkVoxelDataCache voxelCache, TerrainConfig initialConfig, ChunkProcessingMetrics? metrics = null, int maxParallelism = 0)
    {
        this.voxelCache = voxelCache ?? throw new ArgumentNullException(nameof(voxelCache));
        this.metrics = metrics;
        config = initialConfig ?? throw new ArgumentNullException(nameof(initialConfig));
        
        // Use ProcessorCount but cap at a reasonable limit to avoid excessive parallelism
        this.maxParallelism = maxParallelism > 0 ? maxParallelism : Math.Max(1, Environment.ProcessorCount);
        
        threadLocalGenerator = new ThreadLocal<CpuTerrainGenerator>(() => new CpuTerrainGenerator(config), trackAllValues: false);
        threadLocalConfigVersion = new ThreadLocal<long>(() => configVersion, trackAllValues: false);

        // Single dispatcher thread that batches and processes work
        dispatcherTask = Task.Factory.StartNew(
            DispatcherLoop,
            cancellationSource.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public int WorkerCount => maxParallelism;

    public void UpdateConfig(TerrainConfig newConfig)
    {
        config = newConfig ?? throw new ArgumentNullException(nameof(newConfig));
        Interlocked.Increment(ref configVersion);
    }

    public void Enqueue(int chunkIndex, IReadOnlyDictionary<int, BlockId>? blockIdEdits, GenerationJobType type = GenerationJobType.BaseTerrain)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(ChunkGenerationJobSystem));

        var enqueueId = Interlocked.Increment(ref enqueueCounter);
        var work = new GenerationWorkItem(chunkIndex, blockIdEdits, enqueueId, type);
        
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

    private void DispatcherLoop()
    {
        const int batchSize = 32;
        var batch = new List<GenerationWorkItem>(batchSize);

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

    private void ProcessBatch(List<GenerationWorkItem> batch)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = cancellationSource.Token
        };

        try
        {
            Parallel.ForEach(batch, options, ProcessWorkItem);
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress.
        }
    }

    private void ProcessWorkItem(GenerationWorkItem work)
    {
        try
        {
            var generator = threadLocalGenerator.Value!;
            
            // Check if config needs update
            var currentVersion = Volatile.Read(ref configVersion);
            if (currentVersion != threadLocalConfigVersion.Value)
            {
                generator.UpdateConfig(config);
                threadLocalConfigVersion.Value = currentVersion;
            }

            ChunkData? writable = null;
            CpuTerrainGenerator.ChunkGenerationResult result = default;

            try
            {
                if (work.Type == GenerationJobType.BaseTerrain)
                {
                    writable = voxelCache.RentWritable(work.ChunkIndex);

                    // Time terrain generation
                    var terrainSw = Stopwatch.StartNew();
                    result = generator.GenerateBaseTerrain(work.ChunkIndex, writable, work.BlockIdEdits);
                    terrainSw.Stop();
                    metrics?.RecordTerrainGeneration(terrainSw.Elapsed.TotalMilliseconds);
                    
                    // Record detailed terrain breakdown from profiler
                    var profiler = generator.Profiler;
                    metrics?.RecordTerrainBreakdown(
                        profiler.GetLastMs(TerrainGenerationProfiler.Step.ClimateSampling),
                        profiler.GetLastMs(TerrainGenerationProfiler.Step.Noise3DSampling),
                        profiler.GetLastMs(TerrainGenerationProfiler.Step.BiomeSelection),
                        profiler.GetLastMs(TerrainGenerationProfiler.Step.BlockGeneration));

                    // Store biome data alongside voxels
                    var biomeData = generator.GetLastChunkBiomeData();
                    if (biomeData != null)
                    {
                        voxelCache.StoreBiomeData(work.ChunkIndex, biomeData);
                    }

                    // Time lighting calculation
                    var lightSw = Stopwatch.StartNew();
                    LightingCalculator.CalculateLighting(writable);
                    lightSw.Stop();
                    metrics?.RecordLightCalculation(lightSw.Elapsed.TotalMilliseconds);

                    voxelCache.Store(writable);
                    writable = null;
                }
                else if (work.Type is GenerationJobType.Decoration or GenerationJobType.DecorationRepair)
                {
                    // For decoration, we need the base terrain
                    if (voxelCache.TryGetChunkData(work.ChunkIndex, out var baseData) && baseData != null)
                    {
                        // Rent a NEW buffer
                        writable = voxelCache.RentWritable(work.ChunkIndex);
                        
                        // Copy base data to writable
                        baseData.CloneTo(writable);
                        
                        // Get Biome Data
                        voxelCache.TryGetBiomeData(work.ChunkIndex, out var biomeData);
                        
                        // Decorate
                        var terrainSw = Stopwatch.StartNew();
                        result = generator.DecorateChunk(writable, biomeData, work.ChunkIndex, voxelCache);
                        terrainSw.Stop();
                        // We can record this as terrain generation time or separate metric
                        metrics?.RecordTerrainGeneration(terrainSw.Elapsed.TotalMilliseconds);

                        // Recalculate lighting? Trees cast shadows.
                        var lightSw = Stopwatch.StartNew();
                        LightingCalculator.CalculateLighting(writable);
                        lightSw.Stop();
                        metrics?.RecordLightCalculation(lightSw.Elapsed.TotalMilliseconds);
                        
                        // Store
                        voxelCache.Store(writable);
                        writable = null;
                    }
                    else
                    {
                        Log.Error($"Decoration failed: Base terrain missing for chunk {work.ChunkIndex}");
                        return; 
                    }
                }

                var buildId = Interlocked.Increment(ref buildCounter);
                completedResults.Enqueue(new ChunkGenerationJobResult(work.ChunkIndex, result, work.EnqueueId, buildId, work.Type));
            }
            finally
            {
                // If writable is still set, we failed before storing - data is lost but buffer leaked
                // TODO: Add ReturnWritable to ChunkVoxelDataCache for proper cleanup
            }
        }
        catch (Exception ex)
        {
            Log.Error($"CpuGeneration: failed chunk {work.ChunkIndex}: {ex.Message}");
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
            Log.Warn($"ChunkGenerationJobSystem dispose encountered errors: {ex.Flatten().Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"ChunkGenerationJobSystem dispose timed out: {ex.Message}");
        }

        threadLocalGenerator.Dispose();
        threadLocalConfigVersion.Dispose();
        workQueue.Dispose();
        cancellationSource.Dispose();
    }

    internal readonly record struct GenerationWorkItem(int ChunkIndex, IReadOnlyDictionary<int, BlockId>? BlockIdEdits, long EnqueueId, GenerationJobType Type);

    internal readonly record struct ChunkGenerationJobResult(int ChunkIndex, CpuTerrainGenerator.ChunkGenerationResult Generation, long EnqueueId, long BuildId, GenerationJobType Type);

}

internal enum GenerationJobType
{
    BaseTerrain,
    Decoration,
    DecorationRepair
}
