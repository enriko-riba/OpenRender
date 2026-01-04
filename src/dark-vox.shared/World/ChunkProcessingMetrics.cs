namespace DarkVox.World;

/// <summary>
/// Tracks performance metrics for chunk processing operations.
/// Kept renderer-free so it can be used by both server and client.
/// </summary>
public sealed class ChunkProcessingMetrics
{
    private const int SampleSize = 100;
    private const double ResetIntervalSeconds = 1.0;

    private readonly double[] terrainGenSamples = new double[SampleSize];
    private readonly double[] lightCalcSamples = new double[SampleSize];

     private readonly double[] meshBuildSamples = new double[SampleSize];
     private readonly double[] lightPropSamples = new double[SampleSize];

    private readonly double[] climateSamples = new double[SampleSize];
    private readonly double[] noise3DSamples = new double[SampleSize];
    private readonly double[] biomeSamples = new double[SampleSize];
    private readonly double[] blockGenSamples = new double[SampleSize];

    private int terrainGenIndex;
    private int lightCalcIndex;
    private int meshBuildIndex;
    private int lightPropIndex;
    private int climateIndex;
    private int noise3DIndex;
    private int biomeIndex;
    private int blockGenIndex;

    private int terrainGenCount;
    private int lightCalcCount;
    private int meshBuildCount;
    private int lightPropCount;

    private double maxTerrainGenMs;
    private double maxLightCalcMs;
    private double maxMeshBuildMs;
    private double maxLightPropMs;
    private int climateCount;
    private int noise3DCount;
    private int biomeCount;
    private int blockGenCount;

    public double AverageTerrainGenerationMs => GetAverage(terrainGenSamples, terrainGenCount);
    public double AverageLightCalculationMs => GetAverage(lightCalcSamples, lightCalcCount);

    public double AverageClimateMs => GetAverage(climateSamples, climateCount);
    public double AverageNoise3DMs => GetAverage(noise3DSamples, noise3DCount);
    public double AverageBiomeMs => GetAverage(biomeSamples, biomeCount);
    public double AverageBlockGenMs => GetAverage(blockGenSamples, blockGenCount);

    public double AverageMeshBuildMs => GetAverage(meshBuildSamples, meshBuildCount);
    public double AverageLightPropagationMs => GetAverage(lightPropSamples, lightPropCount);

    // mobs/spyro-game legacy names (client HUD expects these)
    public double AvgTerrainGenerationMs => AverageTerrainGenerationMs;
    public double MaxTerrainGenerationMs => maxTerrainGenMs;
    public int ChunksGeneratedPerSecond => TerrainGenerationsPerSecond;

    public double AvgLightCalculationMs => AverageLightCalculationMs;
    public double MaxLightCalculationMs => maxLightCalcMs;

    public double AvgClimateMs => AverageClimateMs;
    public double AvgNoise3DMs => AverageNoise3DMs;
    public double AvgBiomeMs => AverageBiomeMs;
    public double AvgBlockGenMs => AverageBlockGenMs;

    public double AvgMeshBuildMs => AverageMeshBuildMs;
    public double MaxMeshBuildMs => maxMeshBuildMs;
    public int ChunksMeshedPerSecond { get; private set; }

    public double AvgLightPropagationMs => AverageLightPropagationMs;
    public double MaxLightPropagationMs => maxLightPropMs;

    public int TerrainGenerationsPerSecond { get; private set; }

    private int meshesThisInterval;

    private double lastResetTime;
    private int terrainGenThisInterval;

    public void RecordTerrainGeneration(double elapsedMs)
    {
        AddSample(terrainGenSamples, ref terrainGenIndex, ref terrainGenCount, elapsedMs);
        maxTerrainGenMs = Math.Max(maxTerrainGenMs, elapsedMs);
        terrainGenThisInterval++;
    }

    public void Update(double timeSeconds) => Tick(timeSeconds);

    public void RecordLightCalculation(double elapsedMs)
    {
        AddSample(lightCalcSamples, ref lightCalcIndex, ref lightCalcCount, elapsedMs);
        maxLightCalcMs = Math.Max(maxLightCalcMs, elapsedMs);
    }

    public void RecordMeshBuild(double elapsedMs)
    {
        AddSample(meshBuildSamples, ref meshBuildIndex, ref meshBuildCount, elapsedMs);
        maxMeshBuildMs = Math.Max(maxMeshBuildMs, elapsedMs);
        meshesThisInterval++;
    }

    public void RecordLightPropagation(double elapsedMs)
    {
        AddSample(lightPropSamples, ref lightPropIndex, ref lightPropCount, elapsedMs);
        maxLightPropMs = Math.Max(maxLightPropMs, elapsedMs);
    }

    public void RecordTerrainBreakdown(double climateMs, double noise3DMs, double biomeMs, double blockGenMs)
    {
        AddSample(climateSamples, ref climateIndex, ref climateCount, climateMs);
        AddSample(noise3DSamples, ref noise3DIndex, ref noise3DCount, noise3DMs);
        AddSample(biomeSamples, ref biomeIndex, ref biomeCount, biomeMs);
        AddSample(blockGenSamples, ref blockGenIndex, ref blockGenCount, blockGenMs);
    }

    public void Tick(double timeSeconds)
    {
        if (lastResetTime <= 0)
        {
            lastResetTime = timeSeconds;
            return;
        }

        if (timeSeconds - lastResetTime >= ResetIntervalSeconds)
        {
            TerrainGenerationsPerSecond = terrainGenThisInterval;
            terrainGenThisInterval = 0;

            ChunksMeshedPerSecond = meshesThisInterval;
            meshesThisInterval = 0;

            maxTerrainGenMs = 0;
            maxLightCalcMs = 0;
            maxMeshBuildMs = 0;
            maxLightPropMs = 0;
            lastResetTime = timeSeconds;
        }
    }

    private static void AddSample(double[] buffer, ref int index, ref int count, double value)
    {
        buffer[index] = value;
        index = (index + 1) % buffer.Length;
        count = Math.Min(count + 1, buffer.Length);
    }

    private static double GetAverage(double[] buffer, int count)
    {
        if (count <= 0) return 0;

        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += buffer[i];
        }

        return sum / count;
    }
}
