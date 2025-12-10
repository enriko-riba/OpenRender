using NoiseDotNet;
using System.Numerics;

namespace SpyroGame.World.Generation;

/// <summary>
/// Fully CPU-based terrain generator that mirrors the GLSL pipeline.
/// Produces voxel descriptors and collision spans for a chunk.
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
    private readonly float[] columnSlope = new float[ColumnCount];
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

    // Biome generation
    private BiomeGenerator? biomeGenerator;
    private BiomeSelector? biomeSelector;  // Phase 3: Allocation-free biome selection
    private ChunkBiomeData? currentChunkBiome;

    // Phase 0 Infrastructure: Climate cache and performance profiling
    private readonly ChunkClimateCache climateCache = new();
    private readonly TerrainGenerationProfiler profiler = new();
    
    // Stage 2: Biome-driven terrain density evaluation
    private TerrainDensityEvaluator? densityEvaluator;
    
    // Stage 3: Deterministic aquifer system for water level lookup
    private AquiferSystem? aquiferSystem;
    
    // Per-column biome definitions cache (avoids repeated lookups)
    private readonly BiomeDefinition?[] columnBiomes = new BiomeDefinition?[ColumnCount];

    /// <summary>Gets the performance profiler for terrain generation.</summary>
    public TerrainGenerationProfiler Profiler => profiler;

    /// <summary>Gets the climate cache for the current chunk.</summary>
    public ChunkClimateCache ClimateCache => climateCache;

    public CpuTerrainGenerator(TerrainConfig config)
    {
        UpdateConfig(config);
    }

    public void UpdateConfig(TerrainConfig newConfig)
    {
        config = newConfig ?? throw new ArgumentNullException(nameof(newConfig));
        terrainParams = config.GetGenerationParams();
        var baked = config.BakeHeightSplineLut(HeightSplineResolution);
        Array.Copy(baked, heightSpline, HeightSplineResolution);
        biomeGenerator = new BiomeGenerator(config);
        
        // Phase 3: Initialize allocation-free BiomeSelector
        biomeSelector = new BiomeSelector(config.Biomes);
        biomeSelector.UpdateConfig(config);
        
        // Stage 2: Initialize biome-driven density evaluator
        densityEvaluator = new TerrainDensityEvaluator(config);
        
        // Stage 3: Initialize aquifer system for deterministic water levels
        aquiferSystem = new AquiferSystem(config);
        
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

        // Generate biome data for this chunk
        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        currentChunkBiome = biomeGenerator?.GenerateChunkBiomes(chunkX, chunkZ) ?? new ChunkBiomeData();

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
    public ChunkGenerationResult DecorateChunk(ChunkData chunkData, ChunkBiomeData biomeData, int chunkIndex)
    {
        profiler.BeginStep(TerrainGenerationProfiler.Step.Total);

        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        
        // Use provided biome data
        currentChunkBiome = biomeData;
        currentChunkBiome ??= biomeGenerator?.GenerateChunkBiomes(chunkX, chunkZ) ?? new ChunkBiomeData();

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
                var biomeDef = GetBiomeDefinition((int)biomeId);

                for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
                {
                    var block = GenerateBlock(
                        height,
                        y,
                        worldX,
                        worldZ,
                        lx,
                        lz,
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

        // ChunkCollisionData.Spans populated inside TryCommitSpan
    }

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
        BuildColumnSlopes();
        profiler.EndStep(TerrainGenerationProfiler.Step.HeightCalculation);
        
        // ============================================================
        // Water Body Detection
        // ============================================================
        // Compute water body info using heights and biomes
        BuildColumnWaterBodies();
        RefineWaterBodiesFromBiomes();
        
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
        if (biomeSelector == null) return;
        
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
                columnBiomes[columnIndex] = GetBiomeDefinition((int)biome);
            }
        }
        
        // Update legacy 4x4 cell data for climate interpolation
        UpdateLegacyBiomeCellData();
    }
    
    /// <summary>
    /// Estimate initial height from continentalness for biome selection.
    /// This is used before biome-driven height calculation is complete.
    /// </summary>
    private float EstimateInitialHeight(float continentalness01)
    {
        // Use height spline as initial estimate
        return SampleHeightSpline(continentalness01) + VoxelHelper.WaterLevel;
    }
    
    /// <summary>
    /// Update legacy 4x4 cell data for backward compatibility with climate interpolation systems.
    /// </summary>
    private void UpdateLegacyBiomeCellData()
    {
        if (currentChunkBiome == null) return;
        
        var temp01 = climateCache.Temperature01;
        var humid01 = climateCache.Humidity01;
        
        for (var cellZ = 0; cellZ < ChunkBiomeData.GridSize; cellZ++)
        {
            for (var cellX = 0; cellX < ChunkBiomeData.GridSize; cellX++)
            {
                var cellIndex = cellZ * ChunkBiomeData.GridSize + cellX;

                var centerLocalX = cellX * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                var centerLocalZ = cellZ * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                var centerColumnIndex = centerLocalZ * VoxelHelper.ChunkSideSize + centerLocalX;

                currentChunkBiome.Continentalness[cellIndex] = climateCache.Continentalness[centerColumnIndex];
                currentChunkBiome.Erosion[cellIndex] = climateCache.Erosion[centerColumnIndex];
                currentChunkBiome.PeaksValleys[cellIndex] = climateCache.PeaksValleys[centerColumnIndex];
                currentChunkBiome.Temperature[cellIndex] = temp01[centerColumnIndex];
                currentChunkBiome.Humidity[cellIndex] = humid01[centerColumnIndex];
                
                currentChunkBiome.BiomeIds[cellIndex] = currentChunkBiome.GetBiomeAt(centerLocalX, centerLocalZ);
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
        SampleFbm2D(xSpan, zSpan, terrainParams.CliffFrequency, terrainParams.Seed + 1500u, 4, 0.6f, 2.5f, columnCliff);
        
        for (var i = 0; i < ColumnCount; i++)
        {
            var biome = columnBiomes[i];
            var cont01 = columnContinentalness01[i];
            var pv = columnPeaks[i];
            var erosion01 = cachedErosion01[i];
            var weirdness = cachedWeirdness[i];
            var absWeirdness = MathF.Abs(weirdness);
            
            float baseHeight;
            
            if (biome != null && densityEvaluator != null)
            {
                // STAGE 2: Use biome properties to calculate height
                baseHeight = densityEvaluator.CalculateBiomeHeight(biome, pv, erosion01, cont01);
                
                // Calculate 3D factor from weirdness + erosion
                column3DFactor[i] = densityEvaluator.Calculate3DFactor(absWeirdness, erosion01);
            }
            else
            {
                // Fallback to spline-based calculation
                baseHeight = SampleHeightSpline(cont01) + VoxelHelper.WaterLevel;
                column3DFactor[i] = Calculate3DFactor(absWeirdness, erosion01, shaping);
            }
            
            // Apply additional terrain detail for land areas
            if (cont01 >= terrainParams.OceanThreshold)
            {
                var roughness = 1f - erosion01;
                var coastDist = (cont01 - terrainParams.OceanThreshold) / (1f - terrainParams.OceanThreshold);
                var effectiveCoastDist = 0.3f + coastDist * 0.7f;
                
                // Weirdness terrain variety
                var weirdnessInfluence = weirdness * shaping.WeirdnessAmplitude * 
                    (shaping.WeirdnessInfluenceBase + roughness * shaping.WeirdnessInfluenceRoughness) * effectiveCoastDist;
                
                if (absWeirdness > shaping.ExtremeWeirdnessThreshold)
                {
                    var extremeBoost = (absWeirdness - shaping.ExtremeWeirdnessThreshold) / (1f - shaping.ExtremeWeirdnessThreshold);
                    weirdnessInfluence += MathF.Sign(weirdness) * extremeBoost * shaping.ExtremeWeirdnessBoost * (0.5f + roughness * 0.5f);
                }
                baseHeight += weirdnessInfluence;
                
                // Mountain/cliff features
                if (cont01 > shaping.MountainStartThreshold)
                {
                    var mountainFactor = Smoothstep(shaping.MountainStartThreshold, 0.75f, cont01);
                    var cliffAmplitude = terrainParams.CliffAmplitude * shaping.CliffAmplitudeMultiplier;
                    var cliffStrength = MathF.Abs(columnCliff[i]) * (0.3f + roughness * 0.7f) * mountainFactor;
                    baseHeight += cliffStrength * cliffAmplitude;
                }
                
                // Ensure minimum land height
                var minLandHeight = VoxelHelper.WaterLevel + 1f + coastDist * 3f;
                if (baseHeight < minLandHeight)
                {
                    baseHeight = minLandHeight;
                }
            }
            
            columnHeights[i] = baseHeight;
            var rounded = (int)MathF.Round(baseHeight);
            
            // Guard: land must be above sea level
            if (cont01 >= terrainParams.OceanThreshold && rounded < (int)VoxelHelper.WaterLevel + 1)
            {
                rounded = (int)VoxelHelper.WaterLevel + 1;
                columnHeights[i] = rounded;
            }
            columnHeightInts[i] = Math.Clamp(rounded, 0, VoxelHelper.ChunkYSize - 1);
        }
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
        // Initialize aquifer system from climate cache
        if (aquiferSystem != null && climateCache.IsValid)
        {
            aquiferSystem.InitializeFromClimateCache(climateCache);
        }
        
        var cont01 = climateCache.Continentalness01;
        
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                var continentalness01 = cont01[columnIndex];
                var terrainHeight = columnHeights[columnIndex];
                var biomeId = currentChunkBiome?.GetBiomeAt(lx, lz) ?? BiomeId.Plains;
                
                if (aquiferSystem != null && aquiferSystem.IsValid)
                {
                    // STAGE 3: Use aquifer system for deterministic water level lookup
                    columnWaterBody[columnIndex] = aquiferSystem.GetWaterBodyInfo(
                        columnIndex,
                        biomeId,
                        terrainHeight,
                        continentalness01);
                }
                else
                {
                    // Fallback: Simple ocean detection based on continentalness
                    if (continentalness01 < terrainParams.OceanThreshold && terrainHeight < VoxelHelper.WaterLevel)
                    {
                        columnWaterBody[columnIndex] = WaterBodyInfo.Ocean();
                    }
                    else
                    {
                        columnWaterBody[columnIndex] = WaterBodyInfo.None;
                    }
                }
            }
        }
        
        ComputeOceanAdjacency();
    }

    /// <summary>
    /// After biome selection, enforce that ocean water only exists where the biome is Ocean/DeepOcean.
    /// Global sea level is applied only for these biome columns. Lakes remain terrain-relative.
    /// </summary>
    private void RefineWaterBodiesFromBiomes()
    {
        if (currentChunkBiome == null) return;

        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var idx = lz * VoxelHelper.ChunkSideSize + lx;
                var biome = currentChunkBiome.GetBiomeAt(lx, lz);
                if (biome is BiomeId.Ocean or BiomeId.DeepOcean)
                {
                    // Ocean columns use global sea level if terrain height is below it
                    var baseHeight = columnHeights[idx];
                    columnWaterBody[idx] = baseHeight < VoxelHelper.WaterLevel
                        ? WaterBodyInfo.Ocean()
                        : WaterBodyInfo.None;
                }
                else
                {
                    // Non-ocean biomes have no ocean water
                    columnWaterBody[idx] = WaterBodyInfo.None;
                }
            }
        }

        // Recompute adjacency since oceans may have changed
        ComputeOceanAdjacency();
    }

    private void BuildColumnSlopes()
    {
        for (var i = 0; i < ColumnCount; i++)
        {
            var wx = (int)columnWorldX[i];
            var wz = (int)columnWorldZ[i];
            columnSlope[i] = GetSlope(wx, wz);
        }
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
        const int maxBeachSearchRadius = 12; // Maximum beach width to consider
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
        var beachNoiseScale = config.BeachNoiseScale;
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
                var threshold = 1f + (baseBeachWidth - 1f) * (0.5f + beachNoise * beachNoiseStrength);
                columnBeachThreshold[idx] = Math.Max(1f, threshold);
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

    private void BuildColumnFieldCaches()
    {
        // Phase 2: Configurable terrain shaping using TerrainShapingConfig
        // All magic numbers replaced with documented config values
        // If-else chains replaced with continuous spline-based blending
        
        var xSpan = columnWorldX.AsSpan();
        var zSpan = columnWorldZ.AsSpan();
        var shaping = config.TerrainShaping;

        // Copy climate values from cache to local arrays for compatibility with existing code
        var cachedCont = climateCache.Continentalness;
        var cachedCont01 = climateCache.Continentalness01;
        var cachedErosion = climateCache.Erosion;
        var cachedErosion01 = climateCache.Erosion01;
        var cachedPeaks = climateCache.PeaksValleys;
        var cachedWarpX = climateCache.WarpX;
        var cachedWarpZ = climateCache.WarpZ;
        var cachedWeirdness = climateCache.Weirdness;
        
        for (var i = 0; i < ColumnCount; i++)
        {
            columnContinentalness[i] = cachedCont[i];
            columnContinentalness01[i] = cachedCont01[i];
            columnErosion[i] = cachedErosion[i];
            columnPeaks[i] = cachedPeaks[i];
            columnWarpX[i] = cachedWarpX[i];
            columnWarpZ[i] = cachedWarpZ[i];
        }

        // Sample detail noise using config-driven scales
        SampleFbm2D(xSpan, zSpan, terrainParams.CliffFrequency, terrainParams.Seed + 1500u, 4, 0.6f, 2.5f, columnCliff);

        var terrainType = scratch2DA.AsSpan();
        var cliffiness = scratch2DB.AsSpan();
        SampleFbm2D(xSpan, zSpan, shaping.TerrainTypeNoiseScale, terrainParams.Seed + 3000u, 2, 0.5f, 2f, terrainType);
        SampleFbm2D(xSpan, zSpan, shaping.CliffNoiseScale, terrainParams.Seed + 3100u, 3, 0.6f, 2.2f, cliffiness);

        for (var i = 0; i < ColumnCount; i++)
        {
            var tC = columnContinentalness01[i];
            var baseHeight = SampleHeightSpline(tC) + VoxelHelper.WaterLevel;

            if (tC >= terrainParams.OceanThreshold)
            {
                var erosion01 = cachedErosion01[i];
                var weirdness = cachedWeirdness[i];
                var absWeirdness = MathF.Abs(weirdness);
                var peaks = columnPeaks[i];
                var cliff = columnCliff[i];

                // === PHASE 4: Calculate 3D Factor from Weirdness + Erosion ===
                column3DFactor[i] = Calculate3DFactor(absWeirdness, erosion01, shaping);

                // Distance from coast (0 = at coast, 1 = deep inland)
                var coastDist = (tC - terrainParams.OceanThreshold) / (1f - terrainParams.OceanThreshold);
                
                // Minimum distance factor to prevent complete flattening near coast
                // This ensures SOME terrain variation even close to water
                var effectiveCoastDist = 0.3f + coastDist * 0.7f;  // Range [0.3, 1.0]

                // === COASTAL ZONE - Beach flattening only (very narrow) ===
                if (coastDist < shaping.CoastalZoneWidth)
                {
                    // Very narrow beach zone for quick transition to varied terrain
                    var beachFactor = coastDist / shaping.CoastalZoneWidth;
                    var beachLevel = VoxelHelper.WaterLevel + shaping.BeachHeightOffset;
                    baseHeight = Lerp(beachLevel, baseHeight, beachFactor * beachFactor);
                }

                // === TERRAIN VARIATION - Controlled by erosion ===
                // Low erosion = rough terrain with peaks and variation
                // High erosion = smooth, worn terrain
                var roughness = 1f - erosion01;  // 1.0 = rough, 0.0 = smooth
                
                // Peaks/valleys contribution - DRAMATICALLY INCREASED amplitude
                // Uses effectiveCoastDist to maintain variation near coast
                var peakAmplitude = shaping.PeakAmplitudeHills * 1.5f;  // 50% boost
                var peakContribution = (peaks - 0.5f) * 2f * peakAmplitude * (0.4f + roughness * 0.6f) * effectiveCoastDist;
                baseHeight += peakContribution;
                
                // === WEIRDNESS TERRAIN VARIETY (DRAMATICALLY ENHANCED) ===
                // Weirdness creates unusual terrain variations:
                // - Positive weirdness: unexpected elevated terrain (plateaus, mesas)
                // - Negative weirdness: unexpected lowlands (basins, depressions)
                var weirdnessAmplitude = shaping.WeirdnessAmplitude;
                var weirdnessInfluence = weirdness * weirdnessAmplitude * (shaping.WeirdnessInfluenceBase + roughness * shaping.WeirdnessInfluenceRoughness) * effectiveCoastDist;
                
                // Extra boost for extreme weirdness values - creates DRAMATIC terrain
                if (absWeirdness > shaping.ExtremeWeirdnessThreshold)
                {
                    var extremeBoost = (absWeirdness - shaping.ExtremeWeirdnessThreshold) / (1f - shaping.ExtremeWeirdnessThreshold);
                    weirdnessInfluence += MathF.Sign(weirdness) * extremeBoost * shaping.ExtremeWeirdnessBoost * (0.5f + roughness * 0.5f);
                }
                baseHeight += weirdnessInfluence;

                // === MOUNTAIN/CLIFF FEATURES - LOWER threshold, HIGHER amplitudes ===
                // Mountains start at lower continentalness for more terrain variety
                if (tC > shaping.MountainStartThreshold)
                {
                    var mountainFactor = Smoothstep(shaping.MountainStartThreshold, 0.75f, tC);  // Full effect at 0.75
                    
                    // Mountain height boost - INCREASED
                    var mountainBoost = shaping.MountainHeightBoost * shaping.MountainHeightBoostMultiplier;
                    baseHeight += mountainBoost * mountainFactor;
                    
                    // Cliff features - varies with roughness AND weirdness
                    var cliffAmplitude = terrainParams.CliffAmplitude * shaping.CliffAmplitudeMultiplier;
                    var cliffStrength = MathF.Abs(cliff) * (0.3f + roughness * 0.7f) * mountainFactor * (0.5f + absWeirdness * 0.5f);
                    baseHeight += cliffStrength * cliffAmplitude;
                    
                    // Valley carving - DEEPER valleys
                    if (peaks < 0.35f && roughness > 0.5f)
                    {
                        var valleyDepth = (0.35f - peaks) * shaping.ValleyDepthMultiplier * roughness * mountainFactor;
                        baseHeight -= valleyDepth;
                    }
                    
                    // Ridgeline peaks for high peaks + low erosion
                    if (peaks > 0.7f && roughness > 0.6f)
                    {
                        var ridgeBoost = (peaks - 0.7f) * shaping.RidgeBoostMultiplier * roughness * mountainFactor;
                        baseHeight += ridgeBoost;
                    }
                }
                
                // === ROLLING TERRAIN in coastal/lowland areas ===
                if (tC < shaping.MountainStartThreshold && tC > terrainParams.OceanThreshold + 0.03f)
                {
                    // More aggressive variation in plains
                    var plainsFactor = 1f - Smoothstep(0.40f, shaping.MountainStartThreshold, tC);
                    var plainsAmplitude = shaping.PeakAmplitudePlains * shaping.PlainsAmplitudeMultiplier;
                    var plainsVariation = (peaks - 0.5f) * plainsAmplitude * plainsFactor * (0.4f + roughness * 0.6f);
                    baseHeight += plainsVariation;
                }

                // === EROSION SMOOTHING - MINIMAL for maximum drama ===
                // Only apply light smoothing in very high erosion areas
                if (erosion01 > shaping.ErosionSmoothingThreshold)
                {
                    var smoothingFactor = (erosion01 - shaping.ErosionSmoothingThreshold) / (1f - shaping.ErosionSmoothingThreshold) * shaping.ErosionSmoothingFactor;
                    var smoothTarget = SampleHeightSpline(tC) + VoxelHelper.WaterLevel + shaping.ErosionSmoothingHeightOffset;
                    baseHeight = Lerp(baseHeight, smoothTarget, smoothingFactor);
                }
                
                // === CRITICAL: MINIMUM HEIGHT FOR LAND ===
                // Land terrain (continentalness >= OceanThreshold) MUST be at or above water level.
                // Without this, terrain modifiers (peaks, weirdness, valleys) can push land below
                // water level, creating "floating water" without proper shores.
                // The minimum height increases slightly inland to ensure natural coastlines.
                var minLandHeight = VoxelHelper.WaterLevel + 1f + coastDist * 3f;  // Y=36 at coast, rising inland
                if (baseHeight < minLandHeight)
                {
                    baseHeight = minLandHeight;
                }
            }
            else
            {
                // === OCEAN ZONE ===
                // Add significant underwater variation for interesting sea floor
                var oceanVariation = (columnPeaks[i] - 0.5f) * shaping.OceanVariationAmplitude;
                baseHeight += oceanVariation;
                
                column3DFactor[i] = shaping.Min3DFactor;
            }

            columnHeights[i] = baseHeight;
            var rounded = (int)MathF.Round(baseHeight);
            // CRITICAL GUARD: Land must be at least one block above sea level.
            // Prevents underwater land columns near coast causing missing beaches.
            if (cachedCont01[i] >= terrainParams.OceanThreshold && rounded < (int)VoxelHelper.WaterLevel + 1)
            {
                rounded = (int)VoxelHelper.WaterLevel + 1;
                columnHeights[i] = rounded;
            }
            columnHeightInts[i] = Math.Clamp(rounded, 0, VoxelHelper.ChunkYSize - 1);
        }
    }

    /// <summary>
    /// Phase 2 (Properly Unified): Single-pass height calculation.
    /// 
    /// The height spline is the PRIMARY and ONLY driver of base terrain height.
    /// All other factors (erosion, peaks, cliffs) are MODIFIERS that add detail.
    /// 
    /// This eliminates the layered Apply* methods that caused conflicting height calculations.
    /// </summary>
    /// <remarks>
    /// Height formula:
    ///   finalHeight = HeightSpline(continentalness) + WaterLevel
    ///               + peakVariation × roughness × inlandFactor
    ///               + mountainBoost × mountainFactor
    ///               + cliffDetail × mountainFactor × roughness
    ///               - erosionSmoothing
    /// 
    /// Key principles:
    /// - Continentalness ALONE determines base height via spline
    /// - Roughness (1 - erosion) controls terrain drama
    /// - Cliffs ONLY appear in mountains, not near coast
    /// - No competing height calculation systems
    /// </remarks>

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
    /// Phase 3: Select biomes using BiomeSelector with climate cache values.
    /// This uses SIMD-sampled climate values from ChunkClimateCache directly,
    /// eliminating the need for BiomeGenerator's separate noise sampling.
    /// Biome selection happens AFTER terrain height is computed so ocean/land
    /// classification matches actual terrain surface.
    /// 
    /// FIXED: Now computes per-column biome IDs for voxel-resolution borders
    /// instead of 4x4 cell sampling which caused blocky biome boundaries.
    /// </summary>
    private void UpdateBiomeDataFromTerrainValues()
    {
        if (currentChunkBiome == null || biomeSelector == null) return;

        // Get climate cache spans for direct access (no allocations)
        var cont01 = climateCache.Continentalness01;
        var temp01 = climateCache.Temperature01;
        var humid01 = climateCache.Humidity01;
        var erosion01 = climateCache.Erosion01;
        var pv01 = climateCache.PeaksValleys01;

        // === PER-COLUMN BIOME SELECTION (voxel-resolution borders) ===
        // Select biome for every column based on its actual terrain height AND water body type.
        // This eliminates the 4x4 blocky biome borders and ensures biome matches water presence.
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                // Select biome using ONLY climate parameters (Minecraft-style)
                var biome = biomeSelector.Select(
                    cont01[columnIndex],
                    temp01[columnIndex],
                    humid01[columnIndex],
                    erosion01[columnIndex],
                    pv01[columnIndex]);
                
                currentChunkBiome.SetBiomeAt(lx, lz, biome);
            }
        }

        // === LEGACY 4x4 CELL DATA (for climate interpolation) ===
        // Keep the 4x4 grid for backward compatibility with climate interpolation systems.
        for (var cellZ = 0; cellZ < ChunkBiomeData.GridSize; cellZ++)
        {
            for (var cellX = 0; cellX < ChunkBiomeData.GridSize; cellX++)
            {
                var cellIndex = cellZ * ChunkBiomeData.GridSize + cellX;

                // Sample at cell center for climate values
                var centerLocalX = cellX * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                var centerLocalZ = cellZ * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                var centerColumnIndex = centerLocalZ * VoxelHelper.ChunkSideSize + centerLocalX;

                // Update climate data (used for interpolation by other systems)
                currentChunkBiome.Continentalness[cellIndex] = climateCache.Continentalness[centerColumnIndex];
                currentChunkBiome.Erosion[cellIndex] = climateCache.Erosion[centerColumnIndex];
                currentChunkBiome.PeaksValleys[cellIndex] = climateCache.PeaksValleys[centerColumnIndex];
                currentChunkBiome.Temperature[cellIndex] = temp01[centerColumnIndex];
                currentChunkBiome.Humidity[cellIndex] = humid01[centerColumnIndex];
                
                // Cell biome is the most common biome in the cell (use center as representative)
                currentChunkBiome.BiomeIds[cellIndex] = currentChunkBiome.GetBiomeAt(centerLocalX, centerLocalZ);
            }
        }
    }

    // NOTE: Phase 2 Cleanup - The following methods were REMOVED because they created
    // competing height calculation systems that caused atoll patterns and cliff rings:
    // - ApplyBiomeHeightModulation() - had its own ocean/land height logic
    // - CalculateBiomeWeight() - attempted biome-based height blending
    // - CalculateRangeMatch() - helper for biome weight calculation
    //
    // Height is now calculated ONCE in BuildColumnFieldCaches() using:
    //   HeightSpline(continentalness) + erosion/peaks modifiers
    //
    // Biome selection (BiomeSelector) is separate and uses terrain HEIGHT to classify.

    /// <summary>
    /// Gets a biome definition by its ID from the config.
    /// </summary>
    private BiomeDefinition? GetBiomeDefinition(int biomeId)
    {
        foreach (var biome in config.Biomes)
        {
            if (biome.Id == biomeId)
            {
                return biome;
            }
        }
        return null;
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
        Array.Clear(caveMaskVolume);

        // === PASS 1: Generate base cave mask ===
        for (var columnIndex = 0; columnIndex < ColumnCount; columnIndex++)
        {
            var isLandColumn = !columnWaterBody[columnIndex].IsOcean;
            var cheeseSlice = GetColumnVolumeSpan(cheeseVolume, columnIndex);
            var spaghettiSlice = GetColumnVolumeSpan(spaghettiVolume, columnIndex);
            BuildBaseCaveMask(columnIndex, cheeseSlice, spaghettiSlice, isLandColumn);
        }

        // === PASS 2: Find and carve cave entrances ===
        // Scan the entire chunk to find the BEST entrance candidate, then carve it.
        // This prevents sieve effects by ensuring only ONE entrance per chunk region.
        CarveCaveEntrances();
    }

    /// <summary>
    /// Scan the chunk for cave entrance candidates and carve proper tunnel entrances.
    /// Uses a region-based approach: divide chunk into regions, find best candidate per region,
    /// then carve a proper ellipsoid tunnel for each selected entrance.
    /// </summary>
    private void CarveCaveEntrances()
    {
        // Divide chunk into regions (e.g., 8x8 blocks each = 4 regions in a 16x16 chunk)
        const int regionSize = 8;
        const int regionsPerSide = VoxelHelper.ChunkSideSize / regionSize;
        
        for (var rz = 0; rz < regionsPerSide; rz++)
        {
            for (var rx = 0; rx < regionsPerSide; rx++)
            {
                // Deterministic check if this region should have an entrance
                var regionWx = currentChunkX * VoxelHelper.ChunkSideSize + rx * regionSize;
                var regionWz = currentChunkZ * VoxelHelper.ChunkSideSize + rz * regionSize;
                var regionHash = Hash2D(regionWx / regionSize, regionWz / regionSize, terrainParams.Seed + 9000u);
                
                // ~15% of regions with qualifying caves get entrances
                if (regionHash > 0.15f)
                {
                    continue;
                }
                
                // Find the best entrance candidate in this region
                var bestCandidate = FindBestEntranceCandidate(rx * regionSize, rz * regionSize, regionSize);
                
                if (bestCandidate.HasValue)
                {
                    var (lx, lz, caveY, surfaceY, dirX, dirZ, slope) = bestCandidate.Value;
                    CarveEntranceTunnel(lx, lz, caveY, surfaceY, dirX, dirZ, slope);
                }
            }
        }
    }

    /// <summary>
    /// Find the best cave entrance candidate within a region.
    /// Returns the candidate with the best combination of slope, cave proximity, and position.
    /// </summary>
    private (int lx, int lz, int caveY, int surfaceY, int dirX, int dirZ, float slope)? FindBestEntranceCandidate(
        int startLx, int startLz, int size)
    {
        float bestScore = 0;
        (int lx, int lz, int caveY, int surfaceY, int dirX, int dirZ, float slope)? best = null;
        
        for (var dz = 0; dz < size; dz++)
        {
            var lz = startLz + dz;
            if (lz >= VoxelHelper.ChunkSideSize) continue;
            
            for (var dx = 0; dx < size; dx++)
            {
                var lx = startLx + dx;
                if (lx >= VoxelHelper.ChunkSideSize) continue;
                
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                
                // Skip ocean columns
                if (columnWaterBody[columnIndex].IsOcean)
                {
                    continue;
                }
                
                var mask = GetColumnCaveMask(columnIndex);
                var surfaceHeight = columnHeightInts[columnIndex];
                
                // Find highest cave voxel
                var highestCaveY = -1;
                for (var y = Math.Min(surfaceHeight - 3, VoxelHelper.ChunkYSize - 1); y >= 0; y--)
                {
                    if (mask[y] != 0)
                    {
                        highestCaveY = y;
                        break;
                    }
                }
                
                if (highestCaveY < 0)
                {
                    continue;
                }
                
                // Check roof thickness
                var roofThickness = surfaceHeight - highestCaveY;
                if (roofThickness is < 3 or > 20)
                {
                    continue;
                }
                
                // Compute slope and direction
                var (slope, dirX, dirZ) = ComputeSlopeAndDirection(lx, lz);
                
                // Need sufficient slope and a valid direction
                if (slope < 0.8f || (dirX == 0 && dirZ == 0))
                {
                    continue;
                }
                
                // Score this candidate: prefer steeper slopes and thinner roofs
                var slopeScore = Math.Min(slope / 3f, 1f); // Max score at slope 3+
                var roofScore = 1f - (roofThickness - 3f) / 17f; // Thinner = better
                var score = slopeScore * 0.6f + roofScore * 0.4f;
                
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (lx, lz, highestCaveY, surfaceHeight, dirX, dirZ, slope);
                }
            }
        }
        
        return best;
    }

    /// <summary>
    /// Carve a proper cave entrance tunnel using an expanding ellipsoid.
    /// The entrance FLOOR starts at the cave floor level and gradually RISES toward the terrain surface.
    /// This creates a walkable ramp from inside the cave to outside.
    /// Continue carving until the entrance floor reaches terrain surface level.
    /// </summary>
    private void CarveEntranceTunnel(int startLx, int startLz, int caveY, int surfaceY, int dirX, int dirZ, float slope)
    {
        // Ellipsoid dimensions - wide enough to walk through
        var radiusH = 2.0f;   // Horizontal radius (width)
        var radiusV = 2.5f;   // Vertical radius (height above floor)
        
        // Growth per step - entrance gets wider as it approaches the outside
        const float radiusGrowthH = 0.2f;
        const float radiusGrowthV = 0.15f;
        
        // Normalize direction for consistent step size
        var dirLength = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
        if (dirLength < 0.001f) return;
        var normDirX = dirX / dirLength;
        var normDirZ = dirZ / dirLength;
        
        // CRITICAL: Start at the CAVE FLOOR level
        // The entrance floor must connect seamlessly to the cave floor
        var floorY = (float)caveY;
        var currentX = startLx + 0.5f;
        var currentZ = startLz + 0.5f;
        
        // Calculate how much we need to rise per step to reach the surface
        // We want a gentle walkable slope (roughly 0.3-0.5 blocks rise per horizontal block)
        var horizontalDistanceToSurface = MathF.Max(5f, (surfaceY - caveY) / slope);
        var risePerStep = (surfaceY - caveY) / horizontalDistanceToSurface;
        risePerStep = Math.Clamp(risePerStep, 0.25f, 0.6f); // Keep it walkable
        
        // Carve until the FLOOR reaches terrain surface level
        const int maxSteps = 30;
        for (var step = 0; step < maxSteps; step++)
        {
            // Get the surface height at current XZ position
            var centerLx = (int)currentX;
            var centerLz = (int)currentZ;
            
            // Bounds check
            if (centerLx < 0 || centerLx >= VoxelHelper.ChunkSideSize ||
                centerLz < 0 || centerLz >= VoxelHelper.ChunkSideSize)
            {
                break;
            }
            
            var colIdx = centerLz * VoxelHelper.ChunkSideSize + centerLx;
            var localSurface = columnHeightInts[colIdx];
            
            // Carve ellipsoid centered vertically above the floor
            // The ellipsoid center is at floorY + radiusV (so the bottom touches the floor)
            var centerY = floorY + radiusV;
            CarveEllipsoid(currentX, centerY, currentZ, radiusH, radiusV);
            
            // Check if the FLOOR has reached or exceeded the terrain surface
            // This means we've created a complete walkable path from cave to surface
            if (floorY >= localSurface)
            {
                // Carve one more larger ellipsoid to create a nice cave mouth opening
                CarveEllipsoid(currentX, centerY, currentZ, radiusH + 1f, radiusV + 0.5f);
                break;
            }
            
            // Move HORIZONTALLY in the downhill direction (toward the hillside)
            currentX += normDirX * 1.0f;
            currentZ += normDirZ * 1.0f;
            
            // RISE the floor gradually - this creates the walkable ramp
            floorY += risePerStep;
            
            // Grow the ellipsoid for expanding cave mouth
            radiusH += radiusGrowthH;
            radiusV += radiusGrowthV;
        }
    }

    /// <summary>
    /// Carve a proper cave entrance tunnel using an expanding ellipsoid.
    /// The entrance FLOOR starts at the cave floor level and gradually RISES toward the terrain surface.
    /// This creates a walkable ramp from inside the cave to outside.
    /// Continue carving until the entrance floor reaches terrain surface level.
    /// </summary>
    private void CarveEllipsoid(float centerX, float centerY, float centerZ, float radiusH, float radiusV)
    {
        var minX = Math.Max(0, (int)(centerX - radiusH - 1));
        var maxX = Math.Min(VoxelHelper.ChunkSideSize - 1, (int)(centerX + radiusH + 1));
        var minZ = Math.Max(0, (int)(centerZ - radiusH - 1));
        var maxZ = Math.Min(VoxelHelper.ChunkSideSize - 1, (int)(centerZ + radiusH + 1));
        var minY = Math.Max(0, (int)(centerY - radiusV - 1));
        var maxY = Math.Min(VoxelHelper.ChunkYSize - 1, (int)(centerY + radiusV + 1));
        
        var radiusHSq = radiusH * radiusH;
        var radiusVSq = radiusV * radiusV;
        
        for (var lz = minZ; lz <= maxZ; lz++)
        {
            var dz = lz + 0.5f - centerZ;
            var dzNormSq = (dz * dz) / radiusHSq;
            
            for (var lx = minX; lx <= maxX; lx++)
            {
                var dx = lx + 0.5f - centerX;
                var dxNormSq = (dx * dx) / radiusHSq;
                
                // Early exit if outside in XZ plane
                if (dxNormSq + dzNormSq > 1f)
                {
                    continue;
                }
                
                var colIdx = lz * VoxelHelper.ChunkSideSize + lx;
                var localSurface = columnHeightInts[colIdx];
                var mask = GetColumnCaveMask(colIdx);
                
                for (var y = minY; y <= maxY; y++)
                {
                    // Don't carve above terrain surface
                    if (y > localSurface)
                    {
                        continue;
                    }
                    
                    var dy = y + 0.5f - centerY;
                    var dyNormSq = (dy * dy) / radiusVSq;
                    
                    // Ellipsoid equation
                    if (dxNormSq + dyNormSq + dzNormSq <= 1f)
                    {
                        mask[y] = 1;
                    }
                }
            }
        }
    }

    private void BuildBaseCaveMask(int columnIndex, Span<float> cheeseSlice, Span<float> spaghettiSlice, bool isLand)
    {
        var mask = GetColumnCaveMask(columnIndex);
        if (!isLand)
        {
            mask.Clear();
            return;
        }

        var surfaceHeight = columnHeightInts[columnIndex];
        var minCaveDepth = Math.Max(4, config.Caves.MinBreachCaveDepth);

        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            var depth = surfaceHeight - y;
            if (depth < minCaveDepth)
            {
                continue;
            }

            var combinedDensity = MathF.Max(cheeseSlice[y], spaghettiSlice[y]);
            var depthAtten = Smoothstep(minCaveDepth, minCaveDepth + terrainParams.CaveDepthFade, depth);
            if (combinedDensity * depthAtten > terrainParams.CaveCarveThreshold)
            {
                mask[y] = 1;
            }
        }
    }

    /// <summary>
    /// Compute slope magnitude and downhill direction from cached terrain heights.
    /// Returns (slope, dx, dz) where dx/dz is the direction of steepest descent.
    /// </summary>
    private (float slope, int dx, int dz) ComputeSlopeAndDirection(int lx, int lz)
    {
        var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
        var h0 = columnHeightInts[columnIndex];
        
        var bestDrop = 0f;
        var bestDx = 0;
        var bestDz = 0;
        
        // Check all 8 neighbors to find steepest descent
        ReadOnlySpan<(int dx, int dz)> offsets = [
            (-1, 0), (1, 0), (0, -1), (0, 1),
            (-1, -1), (-1, 1), (1, -1), (1, 1)
        ];
        
        foreach (var (dx, dz) in offsets)
        {
            var nlx = lx + dx;
            var nlz = lz + dz;
            
            if (nlx < 0 || nlx >= VoxelHelper.ChunkSideSize || 
                nlz < 0 || nlz >= VoxelHelper.ChunkSideSize)
            {
                continue;
            }
            
            var neighborIdx = nlz * VoxelHelper.ChunkSideSize + nlx;
            var drop = h0 - columnHeightInts[neighborIdx];
            
            // Normalize diagonal distances
            var dist = (dx != 0 && dz != 0) ? 1.414f : 1f;
            var normalizedDrop = drop / dist;
            
            if (normalizedDrop > bestDrop)
            {
                bestDrop = normalizedDrop;
                bestDx = dx;
                bestDz = dz;
            }
        }
        
        return (bestDrop, bestDx, bestDz);
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
    {
        if (TryGetColumnIndex(wx, wz, out var columnIndex))
        {
            return columnHeights[columnIndex];
        }

        return GetHeight(new Vector2(wx, wz));
    }

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
        int localX,
        int localZ,
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
            if (hasWaterHere && y <= localWaterLevel)
            {
                return BlockId.Water;
            }
            return BlockId.Air;
        }

        // 1. Calculate 3D Density for THIS voxel
        var density = GetTerrainDensity(continentalness01, baseHeight, overhangSlice[y], y, column3DFactor[columnIndex]);

        // 2. Density Check - If density is negative, it's air (or water in water body columns).
        if (density < 0f)
        {
            // Water fills air space ONLY in columns with water body
            if (hasWaterHere && y <= localWaterLevel)
            {
                return BlockId.Water;
            }
            return BlockId.Air;
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
            if (!isUnderwater && isInBeachZone && atBeachHeight && !hasWaterHere)
            {
                return BlockId.Sand;
            }
            return isUnderwater ? biomeDef.UnderwaterSurfaceBlock : biomeDef.SurfaceBlock;
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
            if (!isUnderwater && isInBeachZone && atBeachHeight && !hasWaterHere)
            {
                return BlockId.Sand;
            }
            return isUnderwater ? biomeDef.UnderwaterSubsurfaceBlock : biomeDef.SubsurfaceBlock;
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
            // SIGNIFICANTLY INCREASED: Multiplier for dramatic overhangs
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