using DarkVox.Shared.World;
using DarkVox.World;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DarkVox.Shared.World.Registry;

namespace DarkVox.Server.World.Generation;

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

    // Thread-local state to avoid contention and ensure config consistency
    private readonly ThreadLocal<GeneratorState> threadLocalState;

    public ChunkGenerationJobSystem(ChunkVoxelDataCache voxelCache, TerrainConfig initialConfig, ChunkProcessingMetrics? metrics = null, int maxParallelism = 0)
    {
        this.voxelCache = voxelCache ?? throw new ArgumentNullException(nameof(voxelCache));
        this.metrics = metrics;

        // Clone the initial config to ensure we own a private snapshot.
        // This prevents external mutations (like changing the seed on the main thread) from affecting us.
        config = CloneConfig(initialConfig ?? throw new ArgumentNullException(nameof(initialConfig)));

        // Use ProcessorCount but cap at a reasonable limit to avoid excessive parallelism
        this.maxParallelism = maxParallelism > 0 ? maxParallelism : Math.Max(1, Environment.ProcessorCount - 1);

        // Initialize thread-local state. 
        // We capture version BEFORE config to ensure that if we get a newer config with an older version,
        // the version check in ProcessWorkItem will trigger an update (safe redundancy).
        threadLocalState = new ThreadLocal<GeneratorState>(() =>
        {
            var v = Volatile.Read(ref configVersion);
            var c = config;
            return new GeneratorState(new CpuTerrainGenerator(c), v);
        }, trackAllValues: false);

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
        if (newConfig == null) throw new ArgumentNullException(nameof(newConfig));

        // CRITICAL FIX: Clone the config to create an immutable snapshot for the workers.
        // The caller (ChunkStreamingManager) mutates the config object in-place (e.g. setting Seed).
        // Without cloning, running threads would see the seed change mid-generation, causing
        // inconsistencies (e.g. Biomes using NewSeed vs Heights using OldSeed).
        config = CloneConfig(newConfig);
        Interlocked.Increment(ref configVersion);
    }

    private static TerrainConfig CloneConfig(TerrainConfig source)
    {
        // Deep clone via JSON to ensure complete isolation.
        // Since TerrainConfig is data-only and loaded from JSON, this is safe and robust.
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<TerrainConfig>(json)!;
    }

    public void Enqueue(int chunkIndex, IReadOnlyDictionary<int, BlockId>? blockIdEdits, GenerationJobType type = GenerationJobType.BaseTerrain)
    {
        ObjectDisposedException.ThrowIf(disposed, nameof(ChunkGenerationJobSystem));

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
            var state = threadLocalState.Value!;
            var generator = state.Generator;

            // Check if config needs update
            var currentVersion = Volatile.Read(ref configVersion);
            if (currentVersion != state.Version)
            {
                generator.UpdateConfig(config);
                state.Version = currentVersion;
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
                    if (result.BiomeData != null)
                    {
                        voxelCache.StoreBiomeData(work.ChunkIndex, result.BiomeData);
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
                    if (voxelCache.TryAcquireChunkData(work.ChunkIndex, out var baseLease))
                    {
                        using (baseLease)
                        {
                            var baseData = baseLease.Data;

                            // Rent a NEW buffer
                            writable = voxelCache.RentWritable(work.ChunkIndex);

                            // Copy base data to writable
                            baseData.CloneTo(writable);

                            // Get Biome Data
                            voxelCache.TryGetBiomeData(work.ChunkIndex, out var biomeData);

                            // Decorate
                            var terrainSw = Stopwatch.StartNew();
                            result = generator.DecorateChunk(writable, biomeData, work.ChunkIndex);
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
                    }
                    else
                    {
                        Trace.TraceError($"Decoration failed: Base terrain missing for chunk {work.ChunkIndex}");
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
            Trace.TraceError($"CpuGeneration: failed chunk {work.ChunkIndex}: {ex}");
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
            Trace.TraceWarning($"ChunkGenerationJobSystem dispose encountered errors: {ex.Flatten().Message}");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"ChunkGenerationJobSystem dispose timed out: {ex.Message}");
        }

        threadLocalState.Dispose();
        workQueue.Dispose();
        cancellationSource.Dispose();
    }

    internal readonly record struct GenerationWorkItem(int ChunkIndex, IReadOnlyDictionary<int, BlockId>? BlockIdEdits, long EnqueueId, GenerationJobType Type);

    internal readonly record struct ChunkGenerationJobResult(int ChunkIndex, CpuTerrainGenerator.ChunkGenerationResult Generation, long EnqueueId, long BuildId, GenerationJobType Type);

    private sealed class GeneratorState(CpuTerrainGenerator generator, long version)
    {
        public CpuTerrainGenerator Generator { get; } = generator;
        public long Version { get; set; } = version;
    }
}

internal enum GenerationJobType
{
    BaseTerrain,
    Decoration,
    DecorationRepair
}
