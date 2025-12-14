using NoiseDotNet;
using System.Numerics;

namespace SpyroGame.World.Generation;

/// <summary>
/// Fully CPU-based terrain generator that mirrors the GLSL pipeline.
/// Produces voxel descriptors and collision spans for a chunk.
/// Check the TERRAIN_ARCHITECTURE.md document for details.
/// </summary>
internal sealed class CpuTerrainGenerator
{
    private const int HeightSplineResolution = 256;
    private const int ColumnCount = VoxelHelper.ChunkSideSizeSquare;
    private const int ColumnHeightWords = ColumnCount * VoxelHelper.ChunkYSize;

    // Sparse 3D sampling constants (Minecraft-style optimization)
    // Sample every 4 blocks and trilinear interpolate for ~40x speedup
    private const int SparseStep = 4;
    private const int SparseSamplesXZ = VoxelHelper.ChunkSideSize / SparseStep + 1; // 5 samples: 0,4,8,12,16
    private const int SparseSamplesY = VoxelHelper.ChunkYSize / SparseStep + 1;      // 97 samples: 0,4,8,...,384
    private const int SparseSampleCount = SparseSamplesXZ * SparseSamplesXZ;         // 25 samples per Y slice
    private const int SparseVolumeSize = SparseSampleCount * SparseSamplesY;         // 25 * 97 = 2425 total

    private TerrainConfig config = null!;
    private TerrainConfig.TerrainGenerationParams terrainParams;
    private readonly float[] heightSpline = new float[HeightSplineResolution];

    private readonly float[] columnWorldX = new float[ColumnCount];
    private readonly float[] columnWorldZ = new float[ColumnCount];
    private readonly float[] columnContinentalness = new float[ColumnCount];
    private readonly float[] columnContinentalness01 = new float[ColumnCount];
    private readonly float[] columnErosion = new float[ColumnCount];
    private readonly float[] columnPeaks = new float[ColumnCount];
    private readonly float[] columnCliff = new float[ColumnCount];
    private readonly float[] columnHeights = new float[ColumnCount];
    private readonly int[] columnHeightInts = new int[ColumnCount];
    private readonly float[] columnWarpX = new float[ColumnCount];
    private readonly float[] columnWarpZ = new float[ColumnCount];
    private readonly float[] column3DFactor = new float[ColumnCount];  // Phase 4: Weirdness-based 3D strength
    private static readonly (int dx, int dz)[] EntranceNeighborOffsets =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1)
    ];
    
    // SINGLE SOURCE OF TRUTH: Per-column water body info computed ONCE in PrepareChunkCaches
    // Used by both biome selection AND block generation for consistency
    private readonly WaterBodyInfo[] columnWaterBody = new WaterBodyInfo[ColumnCount];
    
    // Beach system: distance to nearest ocean (in blocks) and noise-modulated beach threshold
    // For varying beach widths around oceans. Rivers/lakes use simple 1-block adjacency.
    private readonly float[] columnOceanDistance = new float[ColumnCount];
    private readonly float[] columnBeachThreshold = new float[ColumnCount];
    
    private readonly float[] scratch2DA = new float[ColumnCount];
    private readonly float[] scratch2DB = new float[ColumnCount];
    private readonly float[] scratch2DC = new float[ColumnCount];
    private readonly float[] scratch2DOutput = new float[ColumnCount];
    private readonly float[] sampleScratch2D = new float[ColumnCount];

    private readonly float[] cheeseVolume = new float[ColumnHeightWords];
    private readonly float[] spaghettiVolume = new float[ColumnHeightWords];
    private readonly float[] overhangVolume = new float[ColumnHeightWords];
    private readonly byte[] caveMaskVolume = new byte[ColumnHeightWords];
    private readonly float[] scratch3DOutput = new float[VoxelHelper.ChunkYSize];

    // Sparse sampling buffers (reused each chunk)
    private readonly float[] sparseSampleX = new float[SparseSampleCount];
    private readonly float[] sparseSampleZ = new float[SparseSampleCount];
    private readonly float[] sparseCheeseGrid = new float[SparseVolumeSize];
    private readonly float[] sparseSpaghettiA = new float[SparseVolumeSize];
    private readonly float[] sparseSpaghettiB = new float[SparseVolumeSize];
    private readonly float[] sparseOverhangGrid = new float[SparseVolumeSize];
    private readonly float[] sparseSliceScratch = new float[SparseSampleCount];

    private int currentChunkX;
    private int currentChunkZ;

    // Biome selection (Stage 1)
    private BiomeSelector biomeSelector = null!;  // Set in UpdateConfig
    private ChunkBiomeData? currentChunkBiome;

    // Phase 0 Infrastructure: Climate cache and performance profiling
    private readonly ChunkClimateCache climateCache = new();
    private readonly TerrainGenerationProfiler profiler = new();
    
    // Stage 2: Biome-driven terrain density evaluation
    private TerrainDensityEvaluator densityEvaluator = null!;  // Set in UpdateConfig
    
    // Stage 3: Deterministic aquifer system for water level lookup
    private AquiferSystem aquiferSystem = null!;  // Set in UpdateConfig
    
    // Stage 5: Cave carving system with volume-based entrances
    private CaveCarver caveCarver = null!;  // Set in UpdateConfig

    // Cached biome definitions for fast lookups
    private readonly Dictionary<int, BiomeDefinition> biomeById = new(32);

    // Reused working set for weighted biome blending (avoid per-chunk allocations)
    private readonly List<(BiomeDefinition Biome, float Weight)> weightedBiomes = new(8);
    
    // Per-column biome definitions cache (avoids repeated lookups)
    private readonly BiomeDefinition?[] columnBiomes = new BiomeDefinition?[ColumnCount];

    /// <summary>Gets the performance profiler for terrain generation.</summary>
    public TerrainGenerationProfiler Profiler => profiler;

    /// <summary>Gets the climate cache for the current chunk.</summary>
    public ChunkClimateCache ClimateCache => climateCache;

    public CpuTerrainGenerator(TerrainConfig config) => UpdateConfig(config);

    public void UpdateConfig(TerrainConfig newConfig)
    {
        config = newConfig ?? throw new ArgumentNullException(nameof(newConfig));
        terrainParams = config.GetGenerationParams();
        var baked = config.BakeHeightSplineLut(HeightSplineResolution);
        Array.Copy(baked, heightSpline, HeightSplineResolution);

        biomeSelector = new BiomeSelector(config.Biomes);
        densityEvaluator = new TerrainDensityEvaluator(config);
        aquiferSystem = new AquiferSystem(config);
        caveCarver = new CaveCarver(config);

        biomeById.Clear();
        biomeById.EnsureCapacity(config.Biomes.Count);
        foreach (var biome in config.Biomes)
        {
            biomeById[biome.Id] = biome;
        }
        
        climateCache.Invalidate();
    }

    /// <summary>
    /// Gets the biome data for the last generated chunk. 
    /// Call after GenerateChunk to retrieve the biome grid.
    /// </summary>
    public ChunkBiomeData? GetLastChunkBiomeData() => currentChunkBiome;

    /// <summary>
    /// Generate base terrain voxels (Phase 1).
    /// Does NOT generate vegetation, collision, or lighting.
    /// </summary>
    public ChunkGenerationResult GenerateBaseTerrain(int chunkIndex, ChunkData chunkData, IReadOnlyDictionary<int, BlockId>? edits = null)
    {
        profiler.BeginStep(TerrainGenerationProfiler.Step.Total);
        
        if (chunkData.VoxelData.Length < VoxelHelper.ChunkVoxelCount)
        {
            throw new ArgumentException($"Destination buffer must contain at least {VoxelHelper.ChunkVoxelCount} voxels", nameof(chunkData));
        }

        // Prepare a biome data container for this chunk.
        // Stage 1 will populate both per-column biomes and the coarse climate grid.
        currentChunkBiome = new ChunkBiomeData();

        // 1. Generate base terrain voxels
        FillChunk(chunkIndex, chunkData, edits);

        var collision = new ChunkCollisionData();
        var spanPairs = new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        var spanTypes = new BlockId[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
        var spanCounts = new byte[VoxelHelper.ChunkSideSizeSquare];

        // Generate collision spans from base terrain
        GenerateCollisionData(chunkData, collision, spanPairs, spanTypes, spanCounts);

        profiler.EndStep(TerrainGenerationProfiler.Step.Total);
        profiler.FinalizeChunk();
        
        return new ChunkGenerationResult(collision, spanPairs, spanCounts, spanTypes);
    }

    /// <summary>
    /// Apply vegetation and generate collision data (Phase 2).
    /// Requires neighbors to be present in the cache for cross-chunk vegetation.
    /// </summary>
    public ChunkGenerationResult DecorateChunk(ChunkData chunkData, ChunkBiomeData? biomeData, int chunkIndex)
    {
        profiler.BeginStep(TerrainGenerationProfiler.Step.Total);

        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        
        // Use provided biome data, otherwise regenerate it from the climate cache
        currentChunkBiome = biomeData ?? new ChunkBiomeData();
        if (biomeData is null)
        {
            PrepareChunkCaches(chunkX, chunkZ);
            SelectBiomesFromClimate();
        }

        // 2. Place vegetation (trees, flowers, etc.)
        profiler.BeginStep(TerrainGenerationProfiler.Step.Vegetation);
        var vegetationGen = new VegetationGenerator(config);
        vegetationGen.DecorateChunk(chunkData, currentChunkBiome, chunkX, chunkZ);
        profiler.EndStep(TerrainGenerationProfiler.Step.Vegetation);

        var collision = new ChunkCollisionData();
        var spanPairs = new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        var spanTypes = new BlockId[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
        var spanCounts = new byte[VoxelHelper.ChunkSideSizeSquare];

        // 3. Generate collision spans from final voxel data
        GenerateCollisionData(chunkData, collision, spanPairs, spanTypes, spanCounts);

        profiler.EndStep(TerrainGenerationProfiler.Step.Total);
        profiler.FinalizeChunk();
        
        return new ChunkGenerationResult(collision, spanPairs, spanCounts, spanTypes);
    }

    private void FillChunk(
        int chunkIndex,
        ChunkData chunkData,
        IReadOnlyDictionary<int, BlockId>? edits)
    {
        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;

        currentChunkX = chunkX;
        currentChunkZ = chunkZ;
        PrepareChunkCaches(chunkX, chunkZ);

        // Pre-cache common palette entries to avoid lookup overhead
        // Air is always index 0
        chunkData.GetOrAddPaletteEntry(BlockId.Air);

        profiler.BeginStep(TerrainGenerationProfiler.Step.BlockGeneration);
        
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            var worldZ = chunkZ * VoxelHelper.ChunkSideSize + lz;
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var worldX = chunkX * VoxelHelper.ChunkSideSize + lx;
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                var height = columnHeightInts[columnIndex];
                var baseHeight = columnHeights[columnIndex];
                var continentalness01 = columnContinentalness01[columnIndex];
                var overhangSlice = GetColumnVolumeSpan(overhangVolume, columnIndex);

                // Optimization: Pre-calculate biome and slope for the column
                // This avoids 384 lookups per column
                var biomeId = currentChunkBiome?.GetBiomeAt(lx, lz) ?? BiomeId.Plains;
                biomeById.TryGetValue((int)biomeId, out var biomeDef);

                for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
                {
                    var block = GenerateBlock(
                        height,
                        y,
                        worldX,
                        worldZ,
                        baseHeight,
                        continentalness01,
                        columnIndex,
                        overhangSlice,
                        biomeDef);
                    var localIndex = y * VoxelHelper.ChunkSideSizeSquare + columnIndex;

                    if (edits != null && edits.TryGetValue(localIndex, out var editedBlock))
                    {
                        block = editedBlock;
                    }

                    // Palette lookup
                    var paletteIndex = chunkData.GetOrAddPaletteEntry(block);
                    chunkData.VoxelData[localIndex] = paletteIndex;
                }
                
                // Performance optimization: Compute surface height for this column (highest opaque block)
                // This is used by lighting to quickly skip underground air columns
                chunkData.SurfaceHeights[columnIndex] = ComputeSurfaceHeight(chunkData, lx, lz);
            }
        }

        profiler.EndStep(TerrainGenerationProfiler.Step.BlockGeneration);

#if DEBUG
        RecordTerrainStatsIfEnabled(chunkIndex, chunkData);
#endif

        // ChunkCollisionData.Spans populated inside TryCommitSpan
    }

#if DEBUG
    private static readonly object TerrainStatsLock = new();
    private static readonly bool TerrainStatsEnabled = IsTerrainStatsEnabled();
    private static int terrainStatsChunks;
    private static int terrainStatsLastLoggedAt;
    private static int terrainStatsGlobalMinSurface = int.MaxValue;
    private static int terrainStatsGlobalMaxSurface = int.MinValue;
    private static float terrainStatsGlobalMinBaseHeight = float.MaxValue;
    private static float terrainStatsGlobalMaxBaseHeight = float.MinValue;
    private static float terrainStatsGlobalMinContinentalness = float.MaxValue;
    private static float terrainStatsGlobalMaxContinentalness = float.MinValue;
    private static float terrainStatsGlobalMinContinentalness01 = float.MaxValue;
    private static float terrainStatsGlobalMaxContinentalness01 = float.MinValue;
    private static float terrainStatsGlobalMinTemperature01 = float.MaxValue;
    private static float terrainStatsGlobalMaxTemperature01 = float.MinValue;
    private static float terrainStatsGlobalMinHumidity01 = float.MaxValue;
    private static float terrainStatsGlobalMaxHumidity01 = float.MinValue;
    private static float terrainStatsGlobalMinErosion01 = float.MaxValue;
    private static float terrainStatsGlobalMaxErosion01 = float.MinValue;
    private static float terrainStatsGlobalMinPeaksValleys01 = float.MaxValue;
    private static float terrainStatsGlobalMaxPeaksValleys01 = float.MinValue;
    private static float terrainStatsGlobalMinWeirdness = float.MaxValue;
    private static float terrainStatsGlobalMaxWeirdness = float.MinValue;
    private static readonly int[] terrainStatsBiomeColumnCounts = new int[256];

    private static bool IsTerrainStatsEnabled()
    {
#if !DEBUG
        return false;
#else
        var v = Environment.GetEnvironmentVariable("SPYRO_TERRAIN_STATS");
        if (string.IsNullOrWhiteSpace(v))
        {
            OpenRender.Log.Info("TerrainStats: disabled (set SPYRO_TERRAIN_STATS=1 to enable biome/height histograms)");
            return false;
        }
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    #endif
    }

    private void RecordTerrainStatsIfEnabled(int chunkIndex, ChunkData chunkData)
    {
        if (!TerrainStatsEnabled) return;

        var localMinSurface = int.MaxValue;
        var localMaxSurface = int.MinValue;
        for (var i = 0; i < chunkData.SurfaceHeights.Length; i++)
        {
            var h = chunkData.SurfaceHeights[i];
            if (h < 0) continue;
            if (h < localMinSurface) localMinSurface = h;
            if (h > localMaxSurface) localMaxSurface = h;
        }

        if (localMinSurface == int.MaxValue)
        {
            // No solid blocks in this chunk (?)
            return;
        }

        var localMinBaseHeight = float.MaxValue;
        var localMaxBaseHeight = float.MinValue;
        for (var i = 0; i < columnHeights.Length; i++)
        {
            var h = columnHeights[i];
            if (h < localMinBaseHeight) localMinBaseHeight = h;
            if (h > localMaxBaseHeight) localMaxBaseHeight = h;
        }

        var localMinCont = float.MaxValue;
        var localMaxCont = float.MinValue;
        for (var i = 0; i < columnContinentalness01.Length; i++)
        {
            var c = columnContinentalness01[i];
            if (c < localMinCont) localMinCont = c;
            if (c > localMaxCont) localMaxCont = c;
        }

        var localMinContRaw = float.MaxValue;
        var localMaxContRaw = float.MinValue;
        for (var i = 0; i < columnContinentalness.Length; i++)
        {
            var c = columnContinentalness[i];
            if (c < localMinContRaw) localMinContRaw = c;
            if (c > localMaxContRaw) localMaxContRaw = c;
        }

        var localMinTemp01 = float.MaxValue;
        var localMaxTemp01 = float.MinValue;
        var localMinHum01 = float.MaxValue;
        var localMaxHum01 = float.MinValue;
        var localMinErosion01 = float.MaxValue;
        var localMaxErosion01 = float.MinValue;
        var localMinPv01 = float.MaxValue;
        var localMaxPv01 = float.MinValue;
        var localMinWeird = float.MaxValue;
        var localMaxWeird = float.MinValue;

        var temp01 = climateCache.Temperature01;
        var hum01 = climateCache.Humidity01;
        var erosion01 = climateCache.Erosion01;
        var pv01 = climateCache.PeaksValleys01;
        var weird = climateCache.Weirdness;
        for (var i = 0; i < temp01.Length; i++)
        {
            var t = temp01[i];
            if (t < localMinTemp01) localMinTemp01 = t;
            if (t > localMaxTemp01) localMaxTemp01 = t;

            var h = hum01[i];
            if (h < localMinHum01) localMinHum01 = h;
            if (h > localMaxHum01) localMaxHum01 = h;

            var e = erosion01[i];
            if (e < localMinErosion01) localMinErosion01 = e;
            if (e > localMaxErosion01) localMaxErosion01 = e;

            var p = pv01[i];
            if (p < localMinPv01) localMinPv01 = p;
            if (p > localMaxPv01) localMaxPv01 = p;

            var w = weird[i];
            if (w < localMinWeird) localMinWeird = w;
            if (w > localMaxWeird) localMaxWeird = w;
        }

        lock (TerrainStatsLock)
        {
            terrainStatsChunks++;

            if (localMinSurface < terrainStatsGlobalMinSurface) terrainStatsGlobalMinSurface = localMinSurface;
            if (localMaxSurface > terrainStatsGlobalMaxSurface) terrainStatsGlobalMaxSurface = localMaxSurface;
            if (localMinBaseHeight < terrainStatsGlobalMinBaseHeight) terrainStatsGlobalMinBaseHeight = localMinBaseHeight;
            if (localMaxBaseHeight > terrainStatsGlobalMaxBaseHeight) terrainStatsGlobalMaxBaseHeight = localMaxBaseHeight;
            if (localMinContRaw < terrainStatsGlobalMinContinentalness) terrainStatsGlobalMinContinentalness = localMinContRaw;
            if (localMaxContRaw > terrainStatsGlobalMaxContinentalness) terrainStatsGlobalMaxContinentalness = localMaxContRaw;
            if (localMinCont < terrainStatsGlobalMinContinentalness01) terrainStatsGlobalMinContinentalness01 = localMinCont;
            if (localMaxCont > terrainStatsGlobalMaxContinentalness01) terrainStatsGlobalMaxContinentalness01 = localMaxCont;
            if (localMinTemp01 < terrainStatsGlobalMinTemperature01) terrainStatsGlobalMinTemperature01 = localMinTemp01;
            if (localMaxTemp01 > terrainStatsGlobalMaxTemperature01) terrainStatsGlobalMaxTemperature01 = localMaxTemp01;
            if (localMinHum01 < terrainStatsGlobalMinHumidity01) terrainStatsGlobalMinHumidity01 = localMinHum01;
            if (localMaxHum01 > terrainStatsGlobalMaxHumidity01) terrainStatsGlobalMaxHumidity01 = localMaxHum01;
            if (localMinErosion01 < terrainStatsGlobalMinErosion01) terrainStatsGlobalMinErosion01 = localMinErosion01;
            if (localMaxErosion01 > terrainStatsGlobalMaxErosion01) terrainStatsGlobalMaxErosion01 = localMaxErosion01;
            if (localMinPv01 < terrainStatsGlobalMinPeaksValleys01) terrainStatsGlobalMinPeaksValleys01 = localMinPv01;
            if (localMaxPv01 > terrainStatsGlobalMaxPeaksValleys01) terrainStatsGlobalMaxPeaksValleys01 = localMaxPv01;
            if (localMinWeird < terrainStatsGlobalMinWeirdness) terrainStatsGlobalMinWeirdness = localMinWeird;
            if (localMaxWeird > terrainStatsGlobalMaxWeirdness) terrainStatsGlobalMaxWeirdness = localMaxWeird;

            if (currentChunkBiome is not null)
            {
                for (var i = 0; i < currentChunkBiome.ColumnBiomes.Length; i++)
                {
                    var biome = (int)currentChunkBiome.ColumnBiomes[i];
                    if ((uint)biome < (uint)terrainStatsBiomeColumnCounts.Length)
                    {
                        terrainStatsBiomeColumnCounts[biome]++;
                    }
                }
            }

            // Log periodically to avoid spam.
            // 128 chunks ~= a few frames of streaming at startup.
            if (terrainStatsChunks - terrainStatsLastLoggedAt < 128) return;
            terrainStatsLastLoggedAt = terrainStatsChunks;

            var top = new List<(BiomeId biome, int count)>(16);
            for (var i = 0; i < terrainStatsBiomeColumnCounts.Length; i++)
            {
                var count = terrainStatsBiomeColumnCounts[i];
                if (count <= 0) continue;
                top.Add(((BiomeId)i, count));
            }

            top.Sort(static (a, b) => b.count.CompareTo(a.count));
            if (top.Count > 8) top.RemoveRange(8, top.Count - 8);

            var topText = string.Join(", ", top.Select(x => $"{x.biome}:{x.count}"));

            long totalBiomeColumns = 0;
            for (var i = 0; i < terrainStatsBiomeColumnCounts.Length; i++)
            {
                totalBiomeColumns += terrainStatsBiomeColumnCounts[i];
            }

            var histogram = new List<(BiomeId biome, int count)>(16);
            for (var i = 0; i < terrainStatsBiomeColumnCounts.Length; i++)
            {
                var count = terrainStatsBiomeColumnCounts[i];
                if (count <= 0) continue;
                histogram.Add(((BiomeId)i, count));
            }

            histogram.Sort(static (a, b) => b.count.CompareTo(a.count));
            var histText = string.Join(", ", histogram.Select(x =>
            {
                var pct = totalBiomeColumns > 0 ? (100.0 * x.count / totalBiomeColumns) : 0.0;
                return $"{x.biome}:{x.count}({pct:F1}%)";
            }));

            OpenRender.Log.Info(
                $"TerrainStats: chunks={terrainStatsChunks} water={VoxelHelper.WaterLevel} " +
                $"surface[min,max]=[{terrainStatsGlobalMinSurface},{terrainStatsGlobalMaxSurface}] " +
                $"baseHeight[min,max]=[{terrainStatsGlobalMinBaseHeight:F1},{terrainStatsGlobalMaxBaseHeight:F1}] " +
                $"contRaw[min,max]=[{terrainStatsGlobalMinContinentalness:F2},{terrainStatsGlobalMaxContinentalness:F2}] " +
                $"cont01[min,max]=[{terrainStatsGlobalMinContinentalness01:F2},{terrainStatsGlobalMaxContinentalness01:F2}] " +
                $"temp01[min,max]=[{terrainStatsGlobalMinTemperature01:F2},{terrainStatsGlobalMaxTemperature01:F2}] " +
                $"hum01[min,max]=[{terrainStatsGlobalMinHumidity01:F2},{terrainStatsGlobalMaxHumidity01:F2}] " +
                $"erosion01[min,max]=[{terrainStatsGlobalMinErosion01:F2},{terrainStatsGlobalMaxErosion01:F2}] " +
                $"pv01[min,max]=[{terrainStatsGlobalMinPeaksValleys01:F2},{terrainStatsGlobalMaxPeaksValleys01:F2}] " +
                $"weird[min,max]=[{terrainStatsGlobalMinWeirdness:F2},{terrainStatsGlobalMaxWeirdness:F2}] " +
                $"topBiomes=[{topText}] hist=[{histText}] (lastChunk={chunkIndex})");
        }
    }
#endif

    private void GenerateCollisionData(
        ChunkData chunkData,
        ChunkCollisionData collision,
        int[] spanPairs,
        BlockId[] spanTypes,
        byte[] spanCounts)
    {
        profiler.BeginStep(TerrainGenerationProfiler.Step.CollisionGeneration);

        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                var spanBase = columnIndex * ChunkCollisionData.MaxSpansPerColumn;
                var pairBase = columnIndex * ChunkCollisionData.MaxSpansPerColumn * 2;
                var spanCount = 0;
                var inSpan = false;
                var spanStart = 0;
                var spanBlock = BlockId.Air;

                for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
                {
                    var block = chunkData.GetBlock(lx, y, lz);

                    if (!block.IsAir())
                    {
                        if (!inSpan)
                        {
                            inSpan = true;
                            spanStart = y;
                            spanBlock = block;
                        }
                        else if (block != spanBlock)
                        {
                            if (TryCommitSpan(columnIndex, spanBase, pairBase, spanTypes, spanPairs, spanCount, spanStart, y - 1, spanBlock, collision))
                            {
                                spanCount++;
                            }
                            spanStart = y;
                            spanBlock = block;
                        }
                    }
                    else if (inSpan)
                    {
                        if (TryCommitSpan(columnIndex, spanBase, pairBase, spanTypes, spanPairs, spanCount, spanStart, y - 1, spanBlock, collision))
                        {
                            spanCount++;
                        }
                        inSpan = false;
                    }
                }

                if (inSpan)
                {
                    if (TryCommitSpan(columnIndex, spanBase, pairBase, spanTypes, spanPairs, spanCount, spanStart, VoxelHelper.ChunkYSize - 1, spanBlock, collision))
                    {
                        spanCount++;
                    }
                }

                var recorded = (byte)Math.Min(spanCount, ChunkCollisionData.MaxSpansPerColumn);
                spanCounts[columnIndex] = recorded;
                collision.SpanCounts[columnIndex] = recorded;
            }
        }
        profiler.EndStep(TerrainGenerationProfiler.Step.CollisionGeneration);
    }
    
    /// <summary>
    /// Compute the surface height for a column (highest non-air block Y coordinate).
    /// Returns -1 if the column is entirely air.
    /// </summary>
    private static int ComputeSurfaceHeight(ChunkData chunkData, int lx, int lz)
    {
        for (var y = VoxelHelper.ChunkYSize - 1; y >= 0; y--)
        {
            var block = chunkData.GetBlock(lx, y, lz);
            if (!block.IsAir())
            {
                return y;
            }
        }
        return -1; // Column is entirely air/transparent
    }

    private void PrepareChunkCaches(int chunkX, int chunkZ)
    {
        // ============================================================
        // STAGE 1: Climate Sampling (Biome Assignment First)
        // ============================================================
        // Sample all climate noise ONCE using SIMD batching.
        // These cached values are used by BOTH biome selection AND height calculation.
        profiler.BeginStep(TerrainGenerationProfiler.Step.ClimateSampling);
        climateCache.SampleForChunk(chunkX, chunkZ, config);
        BuildColumnCoordinates(chunkX, chunkZ);
        profiler.EndStep(TerrainGenerationProfiler.Step.ClimateSampling);
        
        // ============================================================
        // STAGE 1 (continued): Biome Selection BEFORE Height
        // ============================================================
        // Select biome for each column using climate values.
        // This is the CORRECT Minecraft order: biomes drive terrain shape.
        profiler.BeginStep(TerrainGenerationProfiler.Step.BiomeSelection);
        SelectBiomesFromClimate();
        profiler.EndStep(TerrainGenerationProfiler.Step.BiomeSelection);
        
        // ============================================================
        // STAGE 2: Biome-Driven Height Calculation
        // ============================================================
        // Calculate terrain height using biome properties (BaseHeight, HeightVariation, etc.)
        // Uses cached climate values (PV, Erosion) - NO re-sampling.
        profiler.BeginStep(TerrainGenerationProfiler.Step.HeightCalculation);
        BuildColumnHeightsFromBiomes();
        profiler.EndStep(TerrainGenerationProfiler.Step.HeightCalculation);
        
        // ============================================================
        // Water Body Detection
        // ============================================================
        // Compute water body info using heights and biomes
        BuildColumnWaterBodies();
        
        // ============================================================
        // STAGE 5: 3D Noise for Caves/Overhangs
        // ============================================================
        profiler.BeginStep(TerrainGenerationProfiler.Step.Noise3DSampling);
        BuildColumnVolumes();
        BuildCaveMaskVolume();
        profiler.EndStep(TerrainGenerationProfiler.Step.Noise3DSampling);
    }
    
    /// <summary>
    /// STAGE 1: Select biomes for each column using climate values.
    /// This happens BEFORE height calculation - biomes drive terrain shape.
    /// 
    /// Uses cached climate values from ChunkClimateCache (no re-sampling).
    /// </summary>
    private void SelectBiomesFromClimate()
    {
        // Initialize biome data structure
        currentChunkBiome ??= new ChunkBiomeData();
        
        var cont01 = climateCache.Continentalness01;
        var temp01 = climateCache.Temperature01;
        var humid01 = climateCache.Humidity01;
        var erosion01 = climateCache.Erosion01;
        var pv01 = climateCache.PeaksValleys01;
        
        // Copy climate values to local arrays for compatibility
        var cachedCont01 = climateCache.Continentalness01;
        var cachedErosion = climateCache.Erosion;
        var cachedPeaks = climateCache.PeaksValleys;
        var cachedWarpX = climateCache.WarpX;
        var cachedWarpZ = climateCache.WarpZ;
        
        for (var i = 0; i < ColumnCount; i++)
        {
            columnContinentalness01[i] = cachedCont01[i];
            columnContinentalness[i] = climateCache.Continentalness[i];
            columnErosion[i] = cachedErosion[i];
            columnPeaks[i] = cachedPeaks[i];
            columnWarpX[i] = cachedWarpX[i];
            columnWarpZ[i] = cachedWarpZ[i];
        }
        
        // Select biome for each column based on climate values ONLY
        // Note: We don't have height yet - biomes are selected from climate parameters
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                
                // Select biome using ONLY climate parameters (Minecraft-style)
                // Biome selection happens BEFORE height calculation - biomes DRIVE terrain shape
                var biome = biomeSelector.Select(
                    cont01[columnIndex],
                    temp01[columnIndex],
                    humid01[columnIndex],
                    erosion01[columnIndex],
                    pv01[columnIndex]);
                
                currentChunkBiome.SetBiomeAt(lx, lz, biome);
                
                // Cache biome definition for height calculation
                biomeById.TryGetValue((int)biome, out var biomeDef);
                columnBiomes[columnIndex] = biomeDef;
            }
        }

        PopulateLegacyCellClimateFromCache();
    }

    /// <summary>
    /// Populate the 4x4 climate grid in <see cref="ChunkBiomeData"/> directly from the per-column
    /// <see cref="ChunkClimateCache"/>.
    ///
    /// This keeps debug queries (interpolated climate) consistent with the actual generation
    /// pipeline, and avoids redundant noise sampling.
    /// </summary>
    private void PopulateLegacyCellClimateFromCache()
    {
        if (currentChunkBiome == null)
        {
            return;
        }

        var temp01 = climateCache.Temperature01;
        var humid01 = climateCache.Humidity01;
        var cont = climateCache.Continentalness;
        var erosion = climateCache.Erosion;
        var pv = climateCache.PeaksValleys;
        var weird = climateCache.Weirdness;

        for (var cellZ = 0; cellZ < ChunkBiomeData.GridSize; cellZ++)
        {
            for (var cellX = 0; cellX < ChunkBiomeData.GridSize; cellX++)
            {
                var cellIndex = cellZ * ChunkBiomeData.GridSize + cellX;

                // Sample at cell center (4x4 cells, center at +2,+2)
                var lx = cellX * ChunkBiomeData.BlocksPerCell + (ChunkBiomeData.BlocksPerCell / 2);
                var lz = cellZ * ChunkBiomeData.BlocksPerCell + (ChunkBiomeData.BlocksPerCell / 2);
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;

                currentChunkBiome.Temperature[cellIndex] = temp01[columnIndex];
                currentChunkBiome.Humidity[cellIndex] = humid01[columnIndex];
                currentChunkBiome.Continentalness[cellIndex] = cont[columnIndex];
                currentChunkBiome.Erosion[cellIndex] = erosion[columnIndex];
                currentChunkBiome.PeaksValleys[cellIndex] = pv[columnIndex];
                currentChunkBiome.Weirdness[cellIndex] = weird[columnIndex];
                currentChunkBiome.BiomeIds[cellIndex] = currentChunkBiome.GetBiomeAt(lx, lz);
            }
        }
    }
    
    /// <summary>
    /// STAGE 2: Calculate terrain heights using biome properties.
    /// This is the core of the Minecraft-style pipeline - biomes DRIVE terrain shape.
    /// 
    /// Uses cached climate values (PV, Erosion) from Stage 1 - NO re-sampling.
    /// </summary>
    private void BuildColumnHeightsFromBiomes()
    {
        var shaping = config.TerrainShaping;
        var cachedErosion01 = climateCache.Erosion01;
        var cachedWeirdness = climateCache.Weirdness;
        
        // Sample additional detail noise for cliffs (still needed for mountain detail)
        var xSpan = columnWorldX.AsSpan();
        var zSpan = columnWorldZ.AsSpan();
        SampleFbm2D(xSpan, zSpan, terrainParams.CliffFrequency, terrainParams.Seed + shaping.CliffNoiseSeedOffset, shaping.CliffNoiseOctaves, shaping.CliffNoisePersistence, shaping.CliffNoiseLacunarity, columnCliff);
        
       
        var temp01 = climateCache.Temperature01;
        var humid01 = climateCache.Humidity01;
        var pv01 = climateCache.PeaksValleys01;

        for (var i = 0; i < ColumnCount; i++)
        {
            var biome = columnBiomes[i];
            var cont01 = columnContinentalness01[i];
            var pv = columnPeaks[i];
            var erosion01 = cachedErosion01[i];
            var weirdness = cachedWeirdness[i];
            var absWeirdness = MathF.Abs(weirdness);
            var isOceanBiome = biome != null && (biome.Id == (int)BiomeId.Ocean || biome.Id == (int)BiomeId.DeepOcean);
            //var slopeValue = columnSlope[i];
            
            
            float baseHeight;

            // Macro elevation comes from the height spline (continentalness-driven).
            // Biome height is blended in to control local character (flat/jagged), not absolute elevation.
            var macroHeight = densityEvaluator.CalculateSplineHeight(cont01, pv01[i], erosion01, heightSpline);
            
            if (biome != null)
            {
                // Calculate 3D factor from weirdness + erosion
                column3DFactor[i] = densityEvaluator.Calculate3DFactor(absWeirdness, erosion01);

                // ALWAYS use weighted biome properties to calculate height
                // This blends heights between ALL biomes (including Ocean->Beach) to avoid cliffs
                baseHeight = 0f;
                biomeSelector.SelectWeighted(
                    cont01, 
                    temp01[i], 
                    humid01[i], 
                    erosion01, 
                    pv01[i], 
                    weightedBiomes);

                if (weightedBiomes.Count > 0)
                {
                    foreach (var (b, weight) in weightedBiomes)
                    {
                        var biomeHeight = densityEvaluator.CalculateBiomeHeight(b, pv, erosion01, cont01);
                        var blended = TerrainDensityEvaluator.BlendMacroAndBiomeHeight(macroHeight, b, biomeHeight);
                        baseHeight += blended * weight;
                    }
                }
                else
                {
                    // Fallback if no biome selected (shouldn't happen)
                    var biomeHeight = densityEvaluator.CalculateBiomeHeight(biome, pv, erosion01, cont01);
                    baseHeight = TerrainDensityEvaluator.BlendMacroAndBiomeHeight(macroHeight, biome, biomeHeight);
                }
            }
            else
            {
                // Fallback to spline-based calculation
                baseHeight = macroHeight;
                column3DFactor[i] = Calculate3DFactor(absWeirdness, erosion01, shaping);
            }

            // Use continuous threshold for land/ocean distinction, ignoring hard biome classification.
            // This prevents mode-switching artifacts where "Ocean" biome wins by 0.1% but triggers different logic.
            var isLandColumn = cont01 >= terrainParams.OceanThreshold && !isOceanBiome;

            // Apply additional terrain detail for land areas
            if (isLandColumn)
            {
                var roughness = 1f - erosion01;
                var coastDist = (cont01 - terrainParams.OceanThreshold) / (1f - terrainParams.OceanThreshold);
                var effectiveCoastDist = shaping.CoastDistanceBase + coastDist * shaping.CoastDistanceMultiplier;
                var coastBlend = Smoothstep(terrainParams.OceanThreshold, terrainParams.OceanThreshold + Math.Max(0.01f, shaping.CoastalZoneWidth), cont01);
                
                // Weirdness terrain variety
                // Multiply by coastBlend to ensure weirdness starts at 0 at the coast line
                // This prevents the ~8 block jump where weirdness suddenly kicked in at 30% strength
                var weirdnessInfluence = weirdness * shaping.WeirdnessAmplitude * 
                    (shaping.WeirdnessInfluenceBase + roughness * shaping.WeirdnessInfluenceRoughness) * effectiveCoastDist * coastBlend;
                
                if (absWeirdness > shaping.ExtremeWeirdnessThreshold)
                {
                    var extremeBoost = (absWeirdness - shaping.ExtremeWeirdnessThreshold) / (1f - shaping.ExtremeWeirdnessThreshold);
                    weirdnessInfluence += MathF.Sign(weirdness) * extremeBoost * shaping.ExtremeWeirdnessBoost * (shaping.WeirdnessRoughnessBase + roughness * shaping.WeirdnessRoughnessMultiplier);
                }
                baseHeight += weirdnessInfluence;
                
                var minLandHeight = VoxelHelper.WaterLevel + shaping.MinLandHeightOffset + coastDist * shaping.MinLandHeightCoastMultiplier;
                var coastalMinHeight = Lerp(VoxelHelper.WaterLevel + MathF.Min(0.2f, shaping.BeachHeightOffset), minLandHeight, coastBlend);
                if (baseHeight < coastalMinHeight)
                {
                    baseHeight = coastalMinHeight;
                }

                baseHeight = ApplyCoastalLandSmoothing(baseHeight, cont01, shaping);
            }
            else
            {
                // Ocean columns ease into the shoreline instead of forming vertical cliffs
                baseHeight = ApplyOceanShoreSmoothing(baseHeight, cont01, shaping);
            }
            
            columnHeights[i] = baseHeight;
            var rounded = (int)MathF.Round(baseHeight);
            
            // Guard: land should not be BELOW sea level (but allow sea-level beaches).
            if (isLandColumn && rounded < (int)VoxelHelper.WaterLevel)
            {
                rounded = (int)VoxelHelper.WaterLevel;
                columnHeights[i] = rounded;
            }
            columnHeightInts[i] = Math.Clamp(rounded, 0, VoxelHelper.ChunkYSize - 1);
        }
    }

    private float ApplyOceanShoreSmoothing(float baseHeight, float continentalness01, TerrainShapingConfig shaping)
    {
        var distanceToCoast = MathF.Max(0f, terrainParams.OceanThreshold - continentalness01);
        var smoothingRange = Math.Max(0.0001f, shaping.UnderwaterCoastalSmoothingDistance);
        var blend = 1f - Math.Clamp(distanceToCoast / smoothingRange, 0f, 1f);

        var maxDepth = MathF.Max(0f, shaping.MaxOceanHeightOffset);
        var depthBlend = Math.Clamp(distanceToCoast / (smoothingRange * 2f), 0f, 1f);
        var shallowDepth = maxDepth * depthBlend;
        var shallowTarget = VoxelHelper.WaterLevel - shallowDepth;

        if (blend > 0f)
        {
            baseHeight = Lerp(baseHeight, shallowTarget, blend * blend);
        }

        var maxOceanHeight = VoxelHelper.WaterLevel - 0.05f;
        return MathF.Min(baseHeight, maxOceanHeight);
    }

    private float ApplyCoastalLandSmoothing(float baseHeight, float continentalness01, TerrainShapingConfig shaping)
    {
        var coastStart = terrainParams.OceanThreshold;
        var coastEnd = terrainParams.OceanThreshold + Math.Max(0.01f, shaping.CoastalZoneWidth);
        var blend = Smoothstep(coastStart, coastEnd, continentalness01);
        var beachHeight = VoxelHelper.WaterLevel + MathF.Min(0.2f, shaping.BeachHeightOffset);
        return Lerp(beachHeight, baseHeight, blend);
    }
    
    /// <summary>
    /// STAGE 3: Compute water body info for each column using the aquifer system.
    /// This is the SINGLE SOURCE OF TRUTH for water - used by both biome selection AND block generation.
    /// 
    /// The aquifer system uses deterministic coordinate-based noise lookup:
    /// - Ocean biomes: Use global sea level (VoxelHelper.WaterLevel)
    /// - Inland water bodies: Use local aquifer noise for varied water levels
    /// - Water is automatically "contained" by solid terrain (density > 0)
    /// 
    /// CRITICAL: This replaces the old terrain-based lake detection which was broken:
    /// - Old system evaluated noise per-column creating random water patches
    /// - New aquifer system uses coherent noise regions for natural water bodies
    /// </summary>
    private void BuildColumnWaterBodies()
    {
        var cont01 = climateCache.Continentalness01;
        var aquifer01 = climateCache.AquiferNoise01;
        
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                var continentalness01 = cont01[columnIndex];
                var terrainHeight = columnHeights[columnIndex];
                var biomeId = currentChunkBiome?.GetBiomeAt(lx, lz) ?? BiomeId.Plains;
                
                // STAGE 3: Use aquifer system for deterministic water level lookup
                columnWaterBody[columnIndex] = aquiferSystem.GetWaterBodyInfo(
                    biomeId,
                    terrainHeight,
                    continentalness01,
                    aquifer01[columnIndex]);
            }
        }
        
        ComputeOceanAdjacency();

        // Beach is a derived biome based on proximity to ocean water (not climate).
        ApplyBeachBiomeOverride();
    }

    /// <summary>
    /// Compute distance to nearest ocean column for beach width calculation.
    /// Uses a flood-fill approach to find minimum Manhattan distance to any ocean column.
    /// Also computes per-column beach threshold using noise for natural variation.
    /// 
    /// Beach width varies based on:
    /// - Base beach width from config (BeachMaxWidth)
    /// - Noise modulation for organic, irregular coastlines
    /// - Only ocean water creates wide beaches (rivers/lakes remain 1-block)
    /// </summary>
    private void ComputeOceanAdjacency()
    {
        // Initialize distances: 0 for ocean, MaxValue for land
        for (var i = 0; i < ColumnCount; i++)
        {
            columnOceanDistance[i] = columnWaterBody[i].IsOcean ? 0f : float.MaxValue;
        }
        
        // Multi-pass flood fill to compute minimum distance to ocean
        // Each pass propagates distance from ocean outward
        var maxBeachSearchRadius = Math.Clamp((int)MathF.Ceiling(config.BeachMaxWidth), 1, VoxelHelper.ChunkSideSize - 1);
        for (var pass = 0; pass < maxBeachSearchRadius; pass++)
        {
            var changed = false;
            for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
            {
                for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
                {
                    var idx = lz * VoxelHelper.ChunkSideSize + lx;
                    var currentDist = columnOceanDistance[idx];
                    
                    // Check 4 neighbors and update if shorter path found
                    if (lz > 0)
                    {
                        var neighborDist = columnOceanDistance[(lz - 1) * VoxelHelper.ChunkSideSize + lx];
                        if (neighborDist + 1f < currentDist)
                        {
                            columnOceanDistance[idx] = neighborDist + 1f;
                            changed = true;
                        }
                    }
                    if (lz < VoxelHelper.ChunkSideSize - 1)
                    {
                        var neighborDist = columnOceanDistance[(lz + 1) * VoxelHelper.ChunkSideSize + lx];
                        if (neighborDist + 1f < currentDist)
                        {
                            columnOceanDistance[idx] = neighborDist + 1f;
                            changed = true;
                        }
                    }
                    if (lx > 0)
                    {
                        var neighborDist = columnOceanDistance[lz * VoxelHelper.ChunkSideSize + (lx - 1)];
                        if (neighborDist + 1f < currentDist)
                        {
                            columnOceanDistance[idx] = neighborDist + 1f;
                            changed = true;
                        }
                    }
                    if (lx < VoxelHelper.ChunkSideSize - 1)
                    {
                        var neighborDist = columnOceanDistance[lz * VoxelHelper.ChunkSideSize + (lx + 1)];
                        if (neighborDist + 1f < currentDist)
                        {
                            columnOceanDistance[idx] = neighborDist + 1f;
                            changed = true;
                        }
                    }
                }
            }
            if (!changed) break; // Converged early
        }
        
        // Compute per-column beach threshold using noise for organic coastlines
        // Beach appears where oceanDistance <= beachThreshold
        var baseBeachWidth = config.BeachMaxWidth;
        var beachNoiseStrength = config.BeachNoiseStrength;
        
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var idx = lz * VoxelHelper.ChunkSideSize + lx;
                var wx = columnWorldX[idx];
                var wz = columnWorldZ[idx];
                
                // Sample noise for beach width variation
                // Use domain-warped noise for more organic shapes
                var beachNoise = GetBeachNoise(wx, wz);
                
                // Beach threshold varies from ~1 block (minimum) to baseBeachWidth
                // Noise modulates the width: high noise = wider beach, low noise = narrower
                var t = Math.Clamp(0.5f + beachNoise * beachNoiseStrength, 0f, 1f);
                var threshold = 1f + (baseBeachWidth - 1f) * t;
                columnBeachThreshold[idx] = Math.Clamp(threshold, 1f, baseBeachWidth);
            }
        }
    }

    /// <summary>
    /// Apply Beach biome to columns that are close to ocean water.
    /// This prevents climate-only beach selection (which creates huge inland sand bands)
    /// and ensures beaches only appear near actual ocean columns.
    /// </summary>
    private void ApplyBeachBiomeOverride()
    {
        if (currentChunkBiome is null)
        {
            return;
        }

        if (!biomeById.TryGetValue((int)BiomeId.Beach, out var beachDef))
        {
            return;
        }

        // Restrict beach biome to near sea level; sand placement already checks shoreline,
        // but biome-level sand should not climb far up inland slopes.
        var maxBeachSurfaceY = (int)VoxelHelper.WaterLevel + (int)terrainParams.ShorelineRange + 4;

        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var idx = lz * VoxelHelper.ChunkSideSize + lx;

                if (columnWaterBody[idx].IsOcean)
                {
                    continue;
                }

                if (!IsWithinBeachDistance(idx))
                {
                    continue;
                }

                if (columnHeightInts[idx] > maxBeachSurfaceY)
                {
                    continue;
                }

                currentChunkBiome.SetBiomeAt(lx, lz, BiomeId.Beach);
                columnBiomes[idx] = beachDef;
            }
        }
    }
    
    /// <summary>
    /// Sample beach width noise at a world position.
    /// Returns a value roughly in [-0.5, 0.5] range for modulating beach width.
    /// Uses low-frequency noise with domain warp for organic, blobby coastline shapes.
    /// </summary>
    private float GetBeachNoise(float wx, float wz)
    {
        var scale = config.BeachNoiseScale;
        var seed = terrainParams.Seed + 8500u;
        
        // Apply domain warp for more organic shapes
        var warpX = ValueNoise2D(wx * scale * 0.7f, wz * scale * 0.7f, seed + 100u) * 20f;
        var warpZ = ValueNoise2D(wx * scale * 0.7f + 100f, wz * scale * 0.7f, seed + 200u) * 20f;
        
        // Sample main beach noise with warped coordinates
        var noise = ValueNoise2D((wx + warpX) * scale, (wz + warpZ) * scale, seed);
        
        // Return centered value
        return noise - 0.5f;
    }
    
    /// <summary>
    /// Simple 2D value noise for beach width variation.
    /// </summary>
    private static float ValueNoise2D(float x, float z, uint seed)
    {
        var xi = (int)MathF.Floor(x);
        var zi = (int)MathF.Floor(z);
        
        var fx = x - xi;
        var fz = z - zi;
        
        var c00 = Hash2D(xi, zi, seed);
        var c10 = Hash2D(xi + 1, zi, seed);
        var c01 = Hash2D(xi, zi + 1, seed);
        var c11 = Hash2D(xi + 1, zi + 1, seed);
        
        var u = Fade(fx);
        var v = Fade(fz);
        
        var x0 = Lerp(c00, c10, u);
        var x1 = Lerp(c01, c11, u);
        
        return Lerp(x0, x1, v);
    }
    
    /// <summary>
    /// Check if a column is within beach distance of ocean.
    /// Returns true if the column should be considered coastal (for Beach biome).
    /// </summary>
    private bool IsWithinBeachDistance(int columnIndex)
    {
        var distance = columnOceanDistance[columnIndex];
        var threshold = columnBeachThreshold[columnIndex];
        return distance > 0f && distance <= threshold; // distance > 0 excludes ocean itself
    }

    private void BuildColumnCoordinates(int chunkX, int chunkZ)
    {
        var baseX = chunkX * VoxelHelper.ChunkSideSize;
        var baseZ = chunkZ * VoxelHelper.ChunkSideSize;
        var idx = 0;
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            var worldZ = baseZ + lz;
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                columnWorldX[idx] = baseX + lx;
                columnWorldZ[idx] = worldZ;
                idx++;
            }
        }
    }

    /// <summary>
    /// Phase 4: Calculate the 3D factor from weirdness and erosion.
    /// This determines how much 3D features (overhangs, arches, floating islands) affect terrain.
    /// 
    /// High |weirdness| + low erosion = dramatic 3D (factor close to 1.0)
    /// Low |weirdness| + high erosion = pure 2D heightmap (factor close to Min3DFactor)
    /// 
    /// The factor is used to modulate overhang amplitude during terrain density calculation.
    /// </summary>
    /// <param name="absWeirdness">Absolute value of weirdness [0, 1].</param>
    /// <param name="erosion01">Erosion normalized to [0, 1].</param>
    /// <param name="shaping">Terrain shaping configuration.</param>
    /// <returns>3D factor [Min3DFactor, Max3DFactor] that modulates overhang strength.</returns>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float Calculate3DFactor(float absWeirdness, float erosion01, TerrainShapingConfig shaping)
    {
        // Weirdness contribution: ramp from 0 at low |W| to 1 at high |W|
        var weirdnessFactor = Smoothstep(shaping.Weirdness3DThresholdLow, shaping.Weirdness3DThresholdHigh, absWeirdness);
        
        // Erosion contribution: 1 at low erosion (rough terrain), 0 at high erosion (flat)
        var erosionFactor = 1f - Smoothstep(0f, shaping.Erosion3DThreshold, erosion01);
        
        // Combined factor: both high weirdness AND low erosion needed for maximum 3D
        var rawFactor = weirdnessFactor * erosionFactor;
        
        // Map to configured range [Min3DFactor, Max3DFactor]
        return Lerp(shaping.Min3DFactor, shaping.Max3DFactor, rawFactor);
    }

    /// <summary>
    /// Flatten a height value towards a target based on strength factor.
    /// </summary>
    private static float FlattenTowards(float height, float target, float strength) => Lerp(height, target, strength);

    private void BuildColumnVolumes()
    {
        // Minecraft-style optimization: sample noise at sparse intervals and trilinear interpolate.
        // This reduces 3D noise samples from 98,304 to 2,425 per noise type (~40x reduction).

        var baseX = currentChunkX * VoxelHelper.ChunkSideSize;
        var baseZ = currentChunkZ * VoxelHelper.ChunkSideSize;
        
        // Get Y-stretch factor for horizontal cave bias
        var spaghettiYStretch = config.Caves.SpaghettiYStretch;

        // Build sparse sample coordinates (5x5 grid at 4-block intervals)
        var sparseIdx = 0;
        for (var sz = 0; sz < SparseSamplesXZ; sz++)
        {
            var worldZ = baseZ + sz * SparseStep;
            for (var sx = 0; sx < SparseSamplesXZ; sx++)
            {
                sparseSampleX[sparseIdx] = baseX + sx * SparseStep;
                sparseSampleZ[sparseIdx] = worldZ;
                sparseIdx++;
            }
        }

        // Sample sparse 3D grid for each noise type
        for (var sy = 0; sy < SparseSamplesY; sy++)
        {
            var worldY = sy * SparseStep;
            var sliceOffset = sy * SparseSampleCount;
            
            // HORIZONTAL CAVE BIAS: Stretch Y coordinate for spaghetti caves
            // This makes caves prefer horizontal tunnels over vertical shafts
            var stretchedY = worldY * spaghettiYStretch;

            // Cheese caves (unchanged - large chambers use normal Y)
            SampleValueNoiseSliceSparse(
                sparseSliceScratch,
                sparseSampleX.AsSpan(),
                worldY,
                sparseSampleZ.AsSpan(),
                terrainParams.CheeseFrequency,
                terrainParams.Seed + 300u,
                octaves: 2, persistence: 0.6f, lacunarity: 1.9f);
            sparseSliceScratch.AsSpan().CopyTo(sparseCheeseGrid.AsSpan(sliceOffset, SparseSampleCount));

            // Spaghetti tunnels with Y-stretch for horizontal bias
            SampleValueNoiseSliceSparse(
                sparseSliceScratch,
                sparseSampleX.AsSpan(),
                stretchedY,  // Use stretched Y for horizontal tendency
                sparseSampleZ.AsSpan(),
                terrainParams.SpaghettiFrequency,
                terrainParams.Seed + 400u,
                octaves: 1, persistence: 1f, lacunarity: 2f);
            sparseSliceScratch.AsSpan().CopyTo(sparseSpaghettiA.AsSpan(sliceOffset, SparseSampleCount));

            SampleValueNoiseSliceSparse(
                sparseSliceScratch,
                sparseSampleX.AsSpan(),
                stretchedY,  // Use stretched Y for horizontal tendency
                sparseSampleZ.AsSpan(),
                terrainParams.SpaghettiFrequency,
                terrainParams.Seed + 500u,
                octaves: 1, persistence: 1f, lacunarity: 2f);
            sparseSliceScratch.AsSpan().CopyTo(sparseSpaghettiB.AsSpan(sliceOffset, SparseSampleCount));

            // Overhangs (unchanged)
            SampleValueNoiseSliceSparse(
                sparseSliceScratch,
                sparseSampleX.AsSpan(),
                worldY,
                sparseSampleZ.AsSpan(),
                terrainParams.OverhangFrequency,
                terrainParams.Seed + 2000u,
                octaves: 2, persistence: 0.55f, lacunarity: 2f);
            sparseSliceScratch.AsSpan().CopyTo(sparseOverhangGrid.AsSpan(sliceOffset, SparseSampleCount));
        }

        // Trilinear interpolate sparse samples into full-resolution volumes
        InterpolateSparseVolumes();
    }

    private void BuildCaveMaskVolume()
    {
        // Stage 5: Delegate cave carving to CaveCarver
        // This replaces the inline implementation with the improved volume-based entrance carving
        if (caveCarver == null)
        {
            Array.Clear(caveMaskVolume);
            return;
        }
        
        // Build isLandColumn array for CaveCarver
        Span<bool> isLandColumn = stackalloc bool[ColumnCount];
        for (var i = 0; i < ColumnCount; i++)
        {
            isLandColumn[i] = !columnWaterBody[i].IsOcean;
        }
        
        // Carve caves using the new improved system
        caveCarver.CarveChunk(
            currentChunkX, 
            currentChunkZ, 
            columnHeightInts, 
            isLandColumn,
            GetSurfaceHeightFloat);
        
        // Copy cave mask from CaveCarver to local buffer
        caveCarver.CaveMask.CopyTo(caveMaskVolume);
    }

    /// <summary>
    /// Interpolate sparse 3D noise samples into full-resolution volumes using optimized linear interpolation.
    /// </summary>
    private void InterpolateSparseVolumes()
    {
        var chunkY = VoxelHelper.ChunkYSize;

        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            // Find sparse Z indices and interpolation factor
            var sz0 = lz / SparseStep;
            var sz1 = Math.Min(sz0 + 1, SparseSamplesXZ - 1);
            var tz = (lz % SparseStep) / (float)SparseStep;

            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;

                // Find sparse X indices and interpolation factor
                var sx0 = lx / SparseStep;
                var sx1 = Math.Min(sx0 + 1, SparseSamplesXZ - 1);
                var tx = (lx % SparseStep) / (float)SparseStep;

                // Precompute XZ corner indices in sparse grid
                var idx00 = sz0 * SparseSamplesXZ + sx0;
                var idx10 = sz0 * SparseSamplesXZ + sx1;
                var idx01 = sz1 * SparseSamplesXZ + sx0;
                var idx11 = sz1 * SparseSamplesXZ + sx1;

                // Iterate through sparse Y layers
                for (var sy = 0; sy < SparseSamplesY - 1; sy++)
                {
                    var yOffset0 = sy * SparseSampleCount;
                    var yOffset1 = (sy + 1) * SparseSampleCount;

                    // Compute values at the 4 corners of the current sparse Y segment
                    // Cheese
                    var c0 = BilinearSample(sparseCheeseGrid, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var c1 = BilinearSample(sparseCheeseGrid, yOffset1, idx00, idx10, idx01, idx11, tx, tz);
                    
                    // Spaghetti A
                    var sa0 = BilinearSample(sparseSpaghettiA, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var sa1 = BilinearSample(sparseSpaghettiA, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    // Spaghetti B
                    var sb0 = BilinearSample(sparseSpaghettiB, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var sb1 = BilinearSample(sparseSpaghettiB, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    // Overhang
                    var oh0 = BilinearSample(sparseOverhangGrid, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var oh1 = BilinearSample(sparseOverhangGrid, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    // Fill the dense voxels between sy and sy+1
                    var yBase = sy * SparseStep;
                    for (var dy = 0; dy < SparseStep; dy++)
                    {
                        var y = yBase + dy;
                        if (y >= chunkY) break;

                        var ty = dy / (float)SparseStep;
                        var sliceOffset = columnIndex * chunkY + y;

                        // Linear interpolate along Y
                        var cheeseSample = (Lerp(c0, c1, ty) * 2f - 1f) * terrainParams.CheeseAmplitude;
                        cheeseVolume[sliceOffset] = Math.Clamp(cheeseSample, -1f, 1f);

                        var n1 = (Lerp(sa0, sa1, ty) * 2f - 1f);
                        var n2 = (Lerp(sb0, sb1, ty) * 2f - 1f);
                        var dist = MathF.Sqrt(n1 * n1 + n2 * n2);
                        var amp = Math.Clamp(terrainParams.SpaghettiAmplitude, 0.2f, 4f);
                        var ampT = (amp - 0.2f) / 3.8f;
                        var widthFactor = Lerp(2.8f, 1.1f, ampT);
                        var tunnelWidth = 1f - dist * widthFactor;
                        spaghettiVolume[sliceOffset] = Math.Clamp(tunnelWidth, -1f, 1f);

                        var overhangSample = (Lerp(oh0, oh1, ty) * 2f - 1f) * terrainParams.OverhangAmplitude;
                        overhangVolume[sliceOffset] = Math.Clamp(overhangSample, -1f, 1f);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Bilinear interpolation from 4 sparse grid corners on a single Y plane.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float BilinearSample(
        float[] grid,
        int yOffset,
        int idx00, int idx10, int idx01, int idx11,
        float tx, float tz)
    {
        var c00 = grid[yOffset + idx00];
        var c10 = grid[yOffset + idx10];
        var c01 = grid[yOffset + idx01];
        var c11 = grid[yOffset + idx11];

        var x0 = c00 + (c10 - c00) * tx;
        var x1 = c01 + (c11 - c01) * tx;

        return x0 + (x1 - x0) * tz;
    }

    private static void SampleValueNoiseSliceSparse(Span<float> destination, ReadOnlySpan<float> xCoords, float yCoord, ReadOnlySpan<float> zCoords, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity)
    {
        var count = destination.Length;
        for (var i = 0; i < count; i++)
        {
            var amplitude = 1f;
            var frequency = baseFrequency;
            var accum = 0f;
            var totalAmp = 0f;

            for (var octave = 0; octave < octaves; octave++)
            {
                var sample = ValueNoise3D(xCoords[i] * frequency, yCoord * frequency, zCoords[i] * frequency, seed + (uint)(octave * 1013));
                accum += sample * amplitude;
                totalAmp += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            destination[i] = totalAmp > 0f ? accum / totalAmp : 0f;
        }
    }

    private static Span<float> GetColumnVolumeSpan(float[] volume, int columnIndex)
        => volume.AsSpan(columnIndex * VoxelHelper.ChunkYSize, VoxelHelper.ChunkYSize);

    private Span<byte> GetColumnCaveMask(int columnIndex)
        => caveMaskVolume.AsSpan(columnIndex * VoxelHelper.ChunkYSize, VoxelHelper.ChunkYSize);

    private bool TryGetColumnIndex(int wx, int wz, out int columnIndex)
    {
        var localX = wx - currentChunkX * VoxelHelper.ChunkSideSize;
        var localZ = wz - currentChunkZ * VoxelHelper.ChunkSideSize;
        if ((uint)localX < VoxelHelper.ChunkSideSize && (uint)localZ < VoxelHelper.ChunkSideSize)
        {
            columnIndex = localZ * VoxelHelper.ChunkSideSize + localX;
            return true;
        }

        columnIndex = -1;
        return false;
    }

    private void SampleFbm2D(Span<float> xCoords, Span<float> zCoords, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity, Span<float> destination)
    {
        var length = destination.Length;
        destination.Clear();
        var amplitude = 1f;
        var frequency = baseFrequency;
        var totalAmplitude = 0f;
        var scratch = sampleScratch2D.AsSpan(0, length);

        for (var octave = 0; octave < octaves; octave++)
        {
            Noise.GradientNoise2D(xCoords[..length], zCoords[..length], scratch, frequency, frequency, amplitude, unchecked((int)(seed + (uint)(octave * 132))));
            for (var i = 0; i < length; i++)
            {
                destination[i] += scratch[i];
            }

            totalAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        if (totalAmplitude <= 0f)
        {
            return;
        }

        var inv = 1f / totalAmplitude;
        for (var i = 0; i < length; i++)
        {
            destination[i] *= inv;
        }
    }

    private void SampleFbm3D(Span<float> xCoords, Span<float> yCoords, Span<float> zCoords, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity, Span<float> destination)
    {
        var length = destination.Length;
        destination.Clear();
        var amplitude = 1f;
        var frequency = baseFrequency;
        var totalAmplitude = 0f;
        var scratch = scratch3DOutput.AsSpan(0, length);

        for (var octave = 0; octave < octaves; octave++)
        {
            Noise.GradientNoise3D(
                xCoords[..length],
                yCoords[..length],
                zCoords[..length],
                scratch,
                frequency,
                frequency,
                frequency,
                amplitude,
                unchecked((int)(seed + (uint)(octave * 132))));

            for (var i = 0; i < length; i++)
            {
                destination[i] += scratch[i];
            }

            totalAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        if (totalAmplitude <= 0f)
        {
            return;
        }

        var inv = 1f / totalAmplitude;
        for (var i = 0; i < length; i++)
        {
            destination[i] *= inv;
        }
    }

    private float SampleFbm2DSingle(Vector2 p, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity)
    {
        Span<float> x = stackalloc float[1];
        Span<float> z = stackalloc float[1];
        Span<float> output = stackalloc float[1];
        x[0] = p.X;
        z[0] = p.Y;
        SampleFbm2D(x, z, baseFrequency, seed, octaves, persistence, lacunarity, output);
        return output[0];
    }

    private float SampleFbm3DSingle(Vector3 p, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity)
    {
        Span<float> x = stackalloc float[1];
        Span<float> y = stackalloc float[1];
        Span<float> z = stackalloc float[1];
        Span<float> output = stackalloc float[1];
        x[0] = p.X;
        y[0] = p.Y;
        z[0] = p.Z;
        SampleFbm3D(x, y, z, baseFrequency, seed, octaves, persistence, lacunarity, output);
        return output[0];
    }

    private static float ValueNoise3D(float x, float y, float z, uint seed)
    {
        var xi = (int)MathF.Floor(x);
        var yi = (int)MathF.Floor(y);
        var zi = (int)MathF.Floor(z);

        var fx = x - xi;
        var fy = y - yi;
        var fz = z - zi;

        var c000 = Hash3(xi, yi, zi, seed);
        var c100 = Hash3(xi + 1, yi, zi, seed);
        var c010 = Hash3(xi, yi + 1, zi, seed);
        var c110 = Hash3(xi + 1, yi + 1, zi, seed);
        var c001 = Hash3(xi, yi, zi + 1, seed);
        var c101 = Hash3(xi + 1, yi, zi + 1, seed);
        var c011 = Hash3(xi, yi + 1, zi + 1, seed);
        var c111 = Hash3(xi + 1, yi + 1, zi + 1, seed);

        var u = Fade(fx);
        var v = Fade(fy);
        var w = Fade(fz);

        var x0 = Lerp(c000, c100, u);
        var x1 = Lerp(c010, c110, u);
        var x2 = Lerp(c001, c101, u);
        var x3 = Lerp(c011, c111, u);

        var y0 = Lerp(x0, x1, v);
        var y1 = Lerp(x2, x3, v);

        return Lerp(y0, y1, w);
    }

    private static float Fade(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Hash3(int x, int y, int z, uint seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647);
            h ^= seed;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777216f; // [0,1)
        }
    }

    private static bool TryCommitSpan(
        int columnIndex,
        int spanBase,
        int pairBase,
        BlockId[] spanTypes,
        int[] spanPairs,
        int spanIndex,
        int startY,
        int endY,
        BlockId block,
        ChunkCollisionData collision)
    {
        if (spanIndex >= ChunkCollisionData.MaxSpansPerColumn)
        {
            return false;
        }

        var spanId = spanBase + spanIndex;
        var pairId = pairBase + spanIndex * 2;
        spanTypes[spanId] = block;
        spanPairs[pairId] = startY;
        spanPairs[pairId + 1] = endY + 1; // store exclusive end for chunk consumption
        var dstIndex = columnIndex * ChunkCollisionData.MaxSpansPerColumn + spanIndex;
        collision.Spans[dstIndex] = new ColumnSpan
        {
            StartY = (short)startY,
            EndY = (short)endY,
            Block = (ushort)block  // Store full BlockId with flags
        };
        return true;
    }

    private int GenerateHeight(int wx, int wz)
    {
        if (TryGetColumnIndex(wx, wz, out var columnIndex))
        {
            return columnHeightInts[columnIndex];
        }

        var height = (int)MathF.Round(GetHeight(new Vector2(wx, wz)));
        return Math.Clamp(height, 0, VoxelHelper.ChunkYSize - 1);
    }

    private float GetSurfaceHeightFloat(int wx, int wz) 
        => TryGetColumnIndex(wx, wz, out var columnIndex) ? columnHeights[columnIndex] 
            : GetHeight(new Vector2(wx, wz));

    /// <summary>
    /// Generate the block type for a voxel at the given world position.
    /// Returns appropriate BlockId based on height, depth, biome, and cave systems.
    /// Uses biome-specific blocks (SurfaceBlock, SubsurfaceBlock, DeepBlock) from BiomeDefinition.
    /// 
    /// FIXED: Surface detection now works with 3D terrain by checking if the block above is air,
    /// rather than comparing y to 2D height. This correctly places grass/snow on overhangs.
    /// 
    /// SINGLE SOURCE OF TRUTH: Uses columnWaterBody[] for water body detection instead of
    /// recalculating isOceanArea/isLakeArea. This ensures consistency with biome selection.
    /// 
    /// WATER PLACEMENT RULES:
    /// - Water ONLY appears in columns where waterBody.HasWater is true (Ocean biomes only, lakes disabled)
    /// - columnHasAdjacentOcean is ONLY used for Beach biome selection, NOT water block placement
    /// - Caves are always dry - no water fills underground even if below global water level
    /// - Non-water biomes never get water blocks, even when digging below Y=35
    /// </summary>
    private BlockId GenerateBlock(
        int height,
        int y,
        int wx,
        int wz,
        float baseHeight,
        float continentalness01,
        int columnIndex,
        Span<float> overhangSlice,
        BiomeDefinition? biomeDef)
    {
        // === HARDCODED BOTTOM LAYERS ===
        // Y=0: Always lava (magma layer at the bottom of the world)
        // Y=1: Always bedrock (impenetrable foundation layer)
        // These layers are ONLY visible when there's air above them (e.g., in deep caves).
        // The mesh builder will cull faces between adjacent solid blocks automatically.
        if (y == 0)
        {
            return BlockId.Lava;
        }
        if (y == 1)
        {
            return BlockId.Bedrock;
        }

        // SINGLE SOURCE OF TRUTH: Use cached water body info computed in BuildColumnWaterBodies()
        // Water ONLY exists where the water body type explicitly says so (Ocean biomes)
        var waterBody = columnWaterBody[columnIndex];
        var hasWaterHere = waterBody.HasWater;
        var localWaterLevel = waterBody.WaterLevel;
        
        // Optimization: If we are significantly above the base height + overhang range,
        // the density will definitely be negative (air).
        // This avoids density calculations for the empty sky.
        if (y > baseHeight + terrainParams.OverhangHeightRange + 16)
        {
            // Water ONLY in columns that have water body (Ocean biomes)
            return hasWaterHere && y <= localWaterLevel ? BlockId.Water : BlockId.Air;
        }

        // 1. Calculate 3D Density for THIS voxel
        var density = GetTerrainDensity(continentalness01, baseHeight, overhangSlice[y], y, column3DFactor[columnIndex]);

        // 2. Density Check - If density is negative, it's air (or water in water body columns).
        if (density < 0f)
        {
            // Water fills air space ONLY in columns with water body
            return hasWaterHere && y <= localWaterLevel ? BlockId.Water : BlockId.Air;
        }

        // Block is solid - check cave carving
        var isLand = !waterBody.IsOcean;
        var caveMask = GetColumnCaveMask(columnIndex);
        if (isLand && y > 0 && caveMask[y] != 0)
        {
            // Cave carved this voxel - it becomes air
            // CRITICAL: No water in carved caves, even if below global water level
            // Caves are dry unless they breach into an ocean biome column
            return BlockId.Air;
        }

        // 4. Determine if this is a SURFACE block by checking if block above is air or carved by a cave
        bool isSurface;
        if (y < VoxelHelper.ChunkYSize - 1)
        {
            var densityAbove = GetTerrainDensity(continentalness01, baseHeight, overhangSlice[y + 1], y + 1, column3DFactor[columnIndex]);
            var caveAbove = isLand && caveMask[y + 1] != 0;
            isSurface = densityAbove < 0f || caveAbove;
        }
        else
        {
            isSurface = true;
        }

        // 5. Determine block type based on position and biome
        if (biomeDef == null)
        {
            // Fallback for missing biome definition
            return isSurface ? BlockId.Grass : BlockId.Stone;
        }

        // Underwater detection: A block is "underwater" only if THIS COLUMN has water
        // AND the terrain surface is below the local water level
        var isUnderwater = hasWaterHere && height < localWaterLevel;

        // Surface block - now correctly detected even on overhangs
        if (isSurface)
        {
            // CAVE FLOOR FIX: Deep cave floors get stone instead of biome grass
            // This prevents grass from appearing on cave floors in complete darkness
            var depthBelowSurface = height - y;
            if (depthBelowSurface > config.Caves.CaveFloorDepthThreshold)
            {
                // Deep underground cave floor - use stone instead of biome surface block
                return BlockId.Stone;
            }
            
            // Coastal beach override: within variable beach width of ocean, above water, within shoreline range
            // Note: Beach detection is distance-based for natural variation in beach width
            var isInBeachZone = IsWithinBeachDistance(columnIndex);
            var atBeachHeight = y >= VoxelHelper.WaterLevel && y <= VoxelHelper.WaterLevel + terrainParams.ShorelineRange;
            return !isUnderwater && isInBeachZone && atBeachHeight && !hasWaterHere
                ? BlockId.Sand
                : isUnderwater ? biomeDef.UnderwaterSurfaceBlock : biomeDef.SurfaceBlock;
        }

        // Subsurface blocks - check depth below the ACTUAL surface
        // For 3D terrain, we need to find how far below the nearest surface we are
        // Approximate by checking how many solid blocks are above us
        var solidAboveCount = 0;
        for (var checkY = y + 1; checkY < Math.Min(y + (int)terrainParams.SubsurfaceDepth + 2, VoxelHelper.ChunkYSize); checkY++)
        {
            var checkDensity = GetTerrainDensity(continentalness01, baseHeight, overhangSlice[checkY], checkY, column3DFactor[columnIndex]);
            if (checkDensity >= 0f)
            {
                solidAboveCount++;
            }
            else
            {
                break; // Found air, stop counting
            }
        }
        
        // If we're within subsurface depth of any surface above, use subsurface block
        // This handles both regular terrain and overhangs
        var effectiveDepth = solidAboveCount;
        if (effectiveDepth == 0)
        {
            // We're right below a surface (the surface check above passed)
            // Use traditional depth calculation as fallback
            effectiveDepth = Math.Max(0, height - y);
        }
        
        if (effectiveDepth <= terrainParams.SubsurfaceDepth)
        {
            // CAVE SUBSURFACE FIX: Deep cave subsurface layers get stone/dirt instead of biome blocks
            // This prevents SnowDirt from appearing deep inside caves in Alpine biomes
            var depthBelowTerrainSurface = height - y;
            if (depthBelowTerrainSurface > config.Caves.CaveFloorDepthThreshold)
            {
                // Deep underground - use generic subsurface (dirt) instead of biome-specific (e.g., SnowDirt)
                return BlockId.Dirt;
            }
            
            // Beach subsurface: within beach distance of ocean, at beach height, not underwater
            var isInBeachZone = IsWithinBeachDistance(columnIndex);
            var atBeachHeight = y >= VoxelHelper.WaterLevel && y <= VoxelHelper.WaterLevel + terrainParams.ShorelineRange;
            return !isUnderwater && isInBeachZone && atBeachHeight && !hasWaterHere
                ? BlockId.Sand
                : isUnderwater ? biomeDef.UnderwaterSubsurfaceBlock : biomeDef.SubsurfaceBlock;
        }

        // Deep blocks - try to generate ore in stone regions
        var deepBlock = biomeDef.DeepBlock;
        if (deepBlock == BlockId.Stone)
        {
            var oreBlock = TryGenerateOre(wx, y, wz);
            if (oreBlock != BlockId.Air)
            {
                return oreBlock;
            }
        }

        return deepBlock;
    }

    /// <summary>
    /// Attempts to generate an ore at the specified world position.
    /// Returns the ore BlockId if generated, otherwise Air (indicating no ore).
    /// Uses Minecraft-style depth distribution with per-ore probability curves.
    /// </summary>
    private BlockId TryGenerateOre(int wx, int y, int wz)
    {
        var oreTypes = config.Ores.OreTypes;
        if (oreTypes == null || oreTypes.Count == 0)
            return BlockId.Air;

        var baseSeed = terrainParams.Seed + config.Ores.SeedOffset;

        // Check each ore type
        foreach (var oreDef in oreTypes)
        {
            // Convert Y ranges relative to water level for negative values
            var minY = oreDef.MinY < 0 ? VoxelHelper.WaterLevel + oreDef.MinY : oreDef.MinY;
            var maxY = oreDef.MaxY < 0 ? VoxelHelper.WaterLevel + oreDef.MaxY : oreDef.MaxY;
            var peakY = oreDef.PeakY < 0 ? VoxelHelper.WaterLevel + oreDef.PeakY : oreDef.PeakY;

            // Check if within Y range
            if (y < minY || y > maxY)
                continue;

            // Calculate spawn probability based on distribution type
            var probability = oreDef.Rarity;
            if (oreDef.DistributionType == OreDistribution.Triangle)
            {
                // Triangle distribution: peaks at PeakY, falls off linearly
                if (y <= peakY)
                {
                    var range = peakY - minY;
                    probability *= range > 0 ? (y - minY) / (float)range : 1f;
                }
                else
                {
                    var range = maxY - peakY;
                    probability *= range > 0 ? (maxY - y) / (float)range : 1f;
                }
            }

            // Use 3D hash for deterministic ore placement
            var oreSeed = baseSeed + (uint)oreDef.OreBlock.GetId();
            var hash = OreHash3D(wx, y, wz, oreSeed);

            if (hash < probability)
            {
                return oreDef.OreBlock;
            }
        }

        return BlockId.Air;
    }

    /// <summary>
    /// 3D hash function for ore generation. Returns a value in [0,1).
 /// </summary>
    private static float OreHash3D(int x, int y, int z, uint seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647);
            h ^= seed;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777216f;
        }
    }

    #region Terrain Functions

    private float GetHeight(Vector2 p)
    {
        var continentalness = GetContinentalness(p);
        var erosion = GetErosion(p);
        var peaks = GetPeaksValleys(p);
        var tC = continentalness * 0.5f + 0.5f;
        var baseHeight = SampleHeightSpline(tC) + VoxelHelper.WaterLevel;

        if (tC >= terrainParams.OceanThreshold)
        {
            var ruggedness = 1f - erosion * 0.5f - 0.5f;
            baseHeight += peaks * 20f * ruggedness;

            if (tC > terrainParams.MountainThreshold)
            {
                var cliffNoise = SampleFbm2DSingle(p, terrainParams.CliffFrequency, terrainParams.Seed + 1500u, 4, 0.6f, 2.5f);
                cliffNoise = MathF.Abs(cliffNoise);
                var mountainness = Smoothstep(terrainParams.MountainThreshold, 0.95f, tC);
                baseHeight += cliffNoise * terrainParams.CliffAmplitude * mountainness;
            }
        }

        return baseHeight;
    }

    /// <summary>
    /// Calculate terrain density for 3D terrain features (overhangs, arches).
    /// Negative density = air, positive density = solid.
    /// Phase 4: Uses 3D factor from weirdness to modulate overhang strength.
    /// </summary>
    private float GetTerrainDensity(float continentalness01, float baseHeight, float overhangNoise, float sampleY, float factor3D)
    {
        var tC = continentalness01;
        var density = baseHeight - sampleY;
        var shaping = config.TerrainShaping;

        // Enable overhangs at LOWER continentalness for more dramatic terrain everywhere
        // Overhangs appear in highlands and mountains, not just extreme mountains
        if (tC > shaping.OverhangStartThreshold &&
            sampleY > baseHeight - terrainParams.OverhangDepthRange &&
            sampleY < baseHeight + terrainParams.OverhangHeightRange)
        {
            // Mountainness factor: gradual increase from start to full
            var mountainness = Smoothstep(shaping.OverhangStartThreshold, shaping.OverhangFullThreshold, tC);
            var heightFactor = 1f - MathF.Abs((sampleY - baseHeight) / terrainParams.OverhangFalloffRange);
            heightFactor = Math.Clamp(heightFactor, 0f, 1f);
            
            // Phase 4: Modulate overhang amplitude by 3D factor
            // High |weirdness| + low erosion → factor3D near 1.0 → full overhang effect
            density += overhangNoise * terrainParams.OverhangAmplitude * shaping.OverhangMultiplier * mountainness * heightFactor * factor3D;
        }

        return density;
    }

    /// <summary>
    /// Calculate slope at a world position using consistent height sampling.
    /// IMPORTANT: Uses GetHeight() for ALL samples (including in-chunk) to ensure
    /// consistent height calculations at chunk boundaries. This fixes grid-pattern
    /// cave artifacts caused by height discontinuities when slope was computed
    /// using cached heights (from BuildColumnFieldCaches) for in-chunk positions
    /// but GetHeight() for out-of-chunk positions.
    /// </summary>
    private float GetSlope(int wx, int wz)
    {
        // Use GetHeight() consistently for all samples to avoid discontinuities at chunk edges
        // GetHeight() uses the simplified terrain formula, but it's consistent across all positions
        var p = new Vector2(wx, wz);
        var h0 = GetHeight(p);
        var h1 = GetHeight(new Vector2(wx + 1, wz));
        var h2 = GetHeight(new Vector2(wx, wz + 1));
        var h3 = GetHeight(new Vector2(wx - 1, wz));
        var h4 = GetHeight(new Vector2(wx, wz - 1));

        var dx = Math.Max(Math.Abs(h1 - h0), Math.Abs(h3 - h0));
        var dz = Math.Max(Math.Abs(h2 - h0), Math.Abs(h4 - h0));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Sample entrance noise to create coherent, roundish cave entrance shapes.
    /// Uses 2D noise so entrance shape is consistent across Y levels (like looking at a hillside).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private float GetEntranceNoise(int wx, int wz)
    {
        var caveParams = config.Caves;
        var freq = caveParams.EntranceNoiseFrequency;
        
        // Use simple 2D value noise for entrance clustering
        // This creates blob-like entrance shapes on hillsides
        var nx = wx * freq;
        var nz = wz * freq;
        var seed = terrainParams.Seed + 7000u;
        
        // Simple 2D value noise
        var xi = (int)MathF.Floor(nx);
        var zi = (int)MathF.Floor(nz);

        var fx = nx - xi;
        var fz = nz - zi;

        var c00 = Hash2D(xi, zi, seed);
        var c10 = Hash2D(xi + 1, zi, seed);
        var c01 = Hash2D(xi, zi + 1, seed);
        var c11 = Hash2D(xi + 1, zi + 1, seed);

        var u = Fade(fx);
        var v = Fade(fz);
        
        var x0 = Lerp(c00, c10, u);
        var x1 = Lerp(c01, c11, u);

        return Lerp(x0, x1, v);
    }
    
    /// <summary>
    /// 2D hash function for entrance noise. Returns a value in [0,1).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float Hash2D(int x, int z, uint seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + z * 668265263);
            h ^= seed;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777216f;
        }
    }
    
    private float GetContinentalness(Vector2 p)
    {
        var warped = p * terrainParams.WarpScale;
        var warp = DomainWarp(warped, terrainParams.Seed);
        return SampleFbm2DSingle(p + warp, terrainParams.ContinentalnessScale, terrainParams.Seed, 3, 0.5f, 2f);
    }

    private float GetErosion(Vector2 p)
        => SampleFbm2DSingle(p, terrainParams.ErosionScale, terrainParams.Seed + 100u, 3, 0.5f, 2f);

    private float GetPeaksValleys(Vector2 p)
    {
        var n = SampleFbm2DSingle(p, terrainParams.PeaksValleysScale, terrainParams.Seed + 200u, 3, 0.5f, 2f);
        return 1f - MathF.Abs(n);
    }

    #endregion

    #region Noise Helpers

    private float SampleHeightSpline(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var scaled = t * (HeightSplineResolution - 1);
        var i = (int)MathF.Floor(scaled);
        var frac = scaled - i;
        var a = heightSpline[i];
        var b = heightSpline[Math.Min(i + 1, HeightSplineResolution - 1)];
        return a + (b - a) * frac;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (Math.Abs(edge1 - edge0) < float.Epsilon)
        {
            return x >= edge1 ? 1f : 0f;
        }

        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private Vector2 DomainWarp(Vector2 p, uint seed)
    {
        var qx = SampleFbm2DSingle(p, 1f, seed, 2, 0.5f, 2f);
        var qy = SampleFbm2DSingle(p + new Vector2(5.2f, 1.3f), 1f, seed, 2, 0.5f, 2f);
        var warp = new Vector2(qx, qy) * terrainParams.WarpStrength;
        return warp;
    }

    /// <summary>
    /// Log performance statistics for terrain generation.
    /// Call periodically (e.g., every 100 chunks or on demand via F3 key).
    /// </summary>
    public void LogPerformanceStats()
    {
        profiler.LogStatistics();
#if DEBUG
        if (climateCache.IsValid)
        {
            OpenRender.Log.Debug($"ClimateCache last sample time: {climateCache.LastSampleTimeMs:F2}ms");
        }
#endif
    }

    #endregion

    internal readonly record struct ChunkGenerationResult(
        ChunkCollisionData Collision,
        int[] SpanPairs,
        byte[] SpanCounts,
        BlockId[] SpanTypes)
    {
        public bool IsEmpty { get; init; } = false;
    }
}