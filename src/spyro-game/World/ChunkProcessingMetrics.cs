namespace SpyroGame.World;

/// <summary>
/// Tracks performance metrics for chunk processing operations.
/// Provides rolling averages, per-second max values, and throughput counters.
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
    
    // Terrain generation breakdown buffers
    private readonly double[] climateSamples = new double[SampleSize];
    private readonly double[] noise3DSamples = new double[SampleSize];
    private readonly double[] biomeSamples = new double[SampleSize];
    private readonly double[] blockGenSamples = new double[SampleSize];

    private int terrainGenIndex;
    private int lightCalcIndex;
    private int lightPropIndex;
    private int meshBuildIndex;
    
    private int climateIndex;
    private int noise3DIndex;
    private int biomeIndex;
    private int blockGenIndex;

    private int terrainGenCount;
    private int lightCalcCount;
    private int lightPropCount;
    private int meshBuildCount;
    
    private int climateCount;
    private int noise3DCount;
    private int biomeCount;
    private int blockGenCount;

    // Per-second counters and max tracking
    private int chunksGeneratedThisInterval;
    private int chunksMeshedThisInterval;
    private int chunksReprocessedThisInterval;
    private double lastResetTime;
    
    // Max values this interval (reset every second)
    private double maxTerrainGenThisInterval;
    private double maxLightCalcThisInterval;
    private double maxLightPropThisInterval;
    private double maxMeshBuildThisInterval;

    /// <summary>Average terrain generation time in milliseconds.</summary>
    public double AvgTerrainGenerationMs { get; private set; }
    
    /// <summary>Max terrain generation time in the last second.</summary>
    public double MaxTerrainGenerationMs { get; private set; }

    /// <summary>Average light calculation time in milliseconds.</summary>
    public double AvgLightCalculationMs { get; private set; }
    
    /// <summary>Max light calculation time in the last second.</summary>
    public double MaxLightCalculationMs { get; private set; }

    /// <summary>Average light propagation time in milliseconds.</summary>
    public double AvgLightPropagationMs { get; private set; }
    
    /// <summary>Max light propagation time in the last second.</summary>
    public double MaxLightPropagationMs { get; private set; }

    /// <summary>Average mesh build time in milliseconds.</summary>
    public double AvgMeshBuildMs { get; private set; }
    
    /// <summary>Max mesh build time in the last second.</summary>
    public double MaxMeshBuildMs { get; private set; }
    
    /// <summary>Average climate sampling time in milliseconds (part of terrain gen).</summary>
    public double AvgClimateMs { get; private set; }
    
    /// <summary>Average 3D noise sampling time in milliseconds (part of terrain gen).</summary>
    public double AvgNoise3DMs { get; private set; }
    
    /// <summary>Average biome selection time in milliseconds (part of terrain gen).</summary>
    public double AvgBiomeMs { get; private set; }
    
    /// <summary>Average block generation time in milliseconds (part of terrain gen).</summary>
    public double AvgBlockGenMs { get; private set; }

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
        
        if (ms > maxTerrainGenThisInterval) maxTerrainGenThisInterval = ms;
        
        UpdateAverage(terrainGenSamples, terrainGenCount, out var avg);
        AvgTerrainGenerationMs = avg;
    }

    /// <summary>Record a light calculation timing.</summary>
    public void RecordLightCalculation(double ms)
    {
        lightCalcSamples[lightCalcIndex] = ms;
        lightCalcIndex = (lightCalcIndex + 1) % SampleSize;
        lightCalcCount = Math.Min(lightCalcCount + 1, SampleSize);
        
        if (ms > maxLightCalcThisInterval) maxLightCalcThisInterval = ms;
        
        UpdateAverage(lightCalcSamples, lightCalcCount, out var avg);
        AvgLightCalculationMs = avg;
    }

    /// <summary>Record a light propagation timing.</summary>
    public void RecordLightPropagation(double ms)
    {
        lightPropSamples[lightPropIndex] = ms;
        lightPropIndex = (lightPropIndex + 1) % SampleSize;
        lightPropCount = Math.Min(lightPropCount + 1, SampleSize);
        
        if (ms > maxLightPropThisInterval) maxLightPropThisInterval = ms;
        
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
        
        if (ms > maxMeshBuildThisInterval) maxMeshBuildThisInterval = ms;
        
        UpdateAverage(meshBuildSamples, meshBuildCount, out var avg);
        AvgMeshBuildMs = avg;
    }
    
    /// <summary>Record terrain generation breakdown stats.</summary>
    public void RecordTerrainBreakdown(double climateMs, double noise3DMs, double biomeMs, double blockGenMs)
    {
        climateSamples[climateIndex] = climateMs;
        climateIndex = (climateIndex + 1) % SampleSize;
        climateCount = Math.Min(climateCount + 1, SampleSize);
        UpdateAverage(climateSamples, climateCount, out var climateAvg);
        AvgClimateMs = climateAvg;
        
        noise3DSamples[noise3DIndex] = noise3DMs;
        noise3DIndex = (noise3DIndex + 1) % SampleSize;
        noise3DCount = Math.Min(noise3DCount + 1, SampleSize);
        UpdateAverage(noise3DSamples, noise3DCount, out var noise3DAvg);
        AvgNoise3DMs = noise3DAvg;
        
        biomeSamples[biomeIndex] = biomeMs;
        biomeIndex = (biomeIndex + 1) % SampleSize;
        biomeCount = Math.Min(biomeCount + 1, SampleSize);
        UpdateAverage(biomeSamples, biomeCount, out var biomeAvg);
        AvgBiomeMs = biomeAvg;
        
        blockGenSamples[blockGenIndex] = blockGenMs;
        blockGenIndex = (blockGenIndex + 1) % SampleSize;
        blockGenCount = Math.Min(blockGenCount + 1, SampleSize);
        UpdateAverage(blockGenSamples, blockGenCount, out var blockGenAvg);
        AvgBlockGenMs = blockGenAvg;
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
            // Commit per-second stats
            ChunksGeneratedPerSecond = chunksGeneratedThisInterval;
            ChunksMeshedPerSecond = chunksMeshedThisInterval;
            ChunksReprocessedPerSecond = chunksReprocessedThisInterval;
            
            // Commit max values from this interval
            MaxTerrainGenerationMs = maxTerrainGenThisInterval;
            MaxLightCalculationMs = maxLightCalcThisInterval;
            MaxLightPropagationMs = maxLightPropThisInterval;
            MaxMeshBuildMs = maxMeshBuildThisInterval;

            // Reset for next interval
            chunksGeneratedThisInterval = 0;
            chunksMeshedThisInterval = 0;
            chunksReprocessedThisInterval = 0;
            maxTerrainGenThisInterval = 0;
            maxLightCalcThisInterval = 0;
            maxLightPropThisInterval = 0;
            maxMeshBuildThisInterval = 0;
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
