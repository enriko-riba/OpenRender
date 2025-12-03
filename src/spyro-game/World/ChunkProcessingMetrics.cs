namespace SpyroGame.World;

/// <summary>
/// Tracks performance metrics for chunk processing operations.
/// Uses rolling averages for timing data.
/// </summary>
public sealed class ChunkProcessingMetrics
{
    private const int SampleSize = 100;
    private const double ResetIntervalSeconds = 1.0;

    // Rolling average buffers
    private readonly double[] terrainGenSamples = new double[SampleSize];
    private readonly double[] lightCalcSamples = new double[SampleSize];
    private readonly double[] lightPropSamples = new double[SampleSize];
    private readonly double[] meshBuildSamples = new double[SampleSize];

    private int terrainGenIndex;
    private int lightCalcIndex;
    private int lightPropIndex;
    private int meshBuildIndex;

    private int terrainGenCount;
    private int lightCalcCount;
    private int lightPropCount;
    private int meshBuildCount;

    // Per-second counters
    private int chunksGeneratedThisInterval;
    private int chunksMeshedThisInterval;
    private int chunksReprocessedThisInterval;
    private double lastResetTime;

    /// <summary>Average terrain generation time in milliseconds.</summary>
    public double AvgTerrainGenerationMs { get; private set; }

    /// <summary>Average light calculation time in milliseconds.</summary>
    public double AvgLightCalculationMs { get; private set; }

    /// <summary>Average light propagation time in milliseconds.</summary>
    public double AvgLightPropagationMs { get; private set; }

    /// <summary>Average mesh build time in milliseconds.</summary>
    public double AvgMeshBuildMs { get; private set; }

    /// <summary>Chunks generated in the last second.</summary>
    public int ChunksGeneratedPerSecond { get; private set; }

    /// <summary>Chunks meshed in the last second.</summary>
    public int ChunksMeshedPerSecond { get; private set; }

    /// <summary>Chunks reprocessed (neighbors) in the last second.</summary>
    public int ChunksReprocessedPerSecond { get; private set; }

    /// <summary>Record a terrain generation timing.</summary>
    public void RecordTerrainGeneration(double ms)
    {
        terrainGenSamples[terrainGenIndex] = ms;
        terrainGenIndex = (terrainGenIndex + 1) % SampleSize;
        terrainGenCount = Math.Min(terrainGenCount + 1, SampleSize);
        chunksGeneratedThisInterval++;
        UpdateAverage(terrainGenSamples, terrainGenCount, out var avg);
        AvgTerrainGenerationMs = avg;
    }

    /// <summary>Record a light calculation timing.</summary>
    public void RecordLightCalculation(double ms)
    {
        lightCalcSamples[lightCalcIndex] = ms;
        lightCalcIndex = (lightCalcIndex + 1) % SampleSize;
        lightCalcCount = Math.Min(lightCalcCount + 1, SampleSize);
        UpdateAverage(lightCalcSamples, lightCalcCount, out var avg);
        AvgLightCalculationMs = avg;
    }

    /// <summary>Record a light propagation timing.</summary>
    public void RecordLightPropagation(double ms)
    {
        lightPropSamples[lightPropIndex] = ms;
        lightPropIndex = (lightPropIndex + 1) % SampleSize;
        lightPropCount = Math.Min(lightPropCount + 1, SampleSize);
        UpdateAverage(lightPropSamples, lightPropCount, out var avg);
        AvgLightPropagationMs = avg;
    }

    /// <summary>Record a mesh build timing.</summary>
    public void RecordMeshBuild(double ms)
    {
        meshBuildSamples[meshBuildIndex] = ms;
        meshBuildIndex = (meshBuildIndex + 1) % SampleSize;
        meshBuildCount = Math.Min(meshBuildCount + 1, SampleSize);
        chunksMeshedThisInterval++;
        UpdateAverage(meshBuildSamples, meshBuildCount, out var avg);
        AvgMeshBuildMs = avg;
    }

    /// <summary>Record a chunk reprocessing (neighbor update).</summary>
    public void RecordReprocess()
    {
        chunksReprocessedThisInterval++;
    }

    /// <summary>Update per-second counters. Call once per frame with current time.</summary>
    public void Update(double currentTimeSeconds)
    {
        if (currentTimeSeconds - lastResetTime >= ResetIntervalSeconds)
        {
            ChunksGeneratedPerSecond = chunksGeneratedThisInterval;
            ChunksMeshedPerSecond = chunksMeshedThisInterval;
            ChunksReprocessedPerSecond = chunksReprocessedThisInterval;

            chunksGeneratedThisInterval = 0;
            chunksMeshedThisInterval = 0;
            chunksReprocessedThisInterval = 0;
            lastResetTime = currentTimeSeconds;
        }
    }

    /// <summary>Get a formatted summary string for debug display.</summary>
    public string GetSummary()
    {
        return $"Terrain:{AvgTerrainGenerationMs:F1}ms Light:{AvgLightCalculationMs:F1}ms Prop:{AvgLightPropagationMs:F1}ms Mesh:{AvgMeshBuildMs:F1}ms | Gen:{ChunksGeneratedPerSecond}/s Mesh:{ChunksMeshedPerSecond}/s Reproc:{ChunksReprocessedPerSecond}/s";
    }

    private static void UpdateAverage(double[] samples, int count, out double avg)
    {
        if (count == 0)
        {
            avg = 0;
            return;
        }

        var sum = 0.0;
        for (var i = 0; i < count; i++)
        {
            sum += samples[i];
        }
        avg = sum / count;
    }
}
