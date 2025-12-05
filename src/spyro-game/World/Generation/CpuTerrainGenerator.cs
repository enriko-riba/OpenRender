using NoiseDotNet;
using OpenRender;
using System.Numerics;

namespace SpyroGame.World;

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
    private readonly float[] scratch2DA = new float[ColumnCount];
    private readonly float[] scratch2DB = new float[ColumnCount];
    private readonly float[] scratch2DC = new float[ColumnCount];
    private readonly float[] scratch2DOutput = new float[ColumnCount];
    private readonly float[] sampleScratch2D = new float[ColumnCount];

    private readonly float[] cheeseVolume = new float[ColumnHeightWords];
    private readonly float[] spaghettiVolume = new float[ColumnHeightWords];
    private readonly float[] overhangVolume = new float[ColumnHeightWords];
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
        
        climateCache.Invalidate();
    }

    /// <summary>
    /// Gets the biome data for the last generated chunk. 
    /// Call after GenerateChunk to retrieve the biome grid.
    /// </summary>
    public ChunkBiomeData? GetLastChunkBiomeData() => currentChunkBiome;

    /// <summary>
    /// Generate voxel descriptors and collision spans for the specified chunk.
    /// </summary>
    public ChunkGenerationResult GenerateChunk(int chunkIndex, ChunkData chunkData, IReadOnlyDictionary<int, BlockId>? edits = null)
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

        var collision = new ChunkCollisionData();
        var spanPairs = new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        var spanTypes = new BlockId[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
        var spanCounts = new byte[VoxelHelper.ChunkSideSizeSquare];

        FillChunk(chunkIndex, chunkData, collision, spanPairs, spanTypes, spanCounts, edits);

        profiler.EndStep(TerrainGenerationProfiler.Step.Total);
        profiler.FinalizeChunk();
        
        return new ChunkGenerationResult(collision, spanPairs, spanCounts, spanTypes);
    }

    private void FillChunk(
        int chunkIndex,
        ChunkData chunkData,
        ChunkCollisionData collision,
        int[] spanPairs,
        BlockId[] spanTypes,
        byte[] spanCounts,
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
                var cheeseSlice = GetColumnVolumeSpan(cheeseVolume, columnIndex);
                var spaghettiSlice = GetColumnVolumeSpan(spaghettiVolume, columnIndex);
                var overhangSlice = GetColumnVolumeSpan(overhangVolume, columnIndex);
                var spanBase = columnIndex * ChunkCollisionData.MaxSpansPerColumn;
                var pairBase = columnIndex * ChunkCollisionData.MaxSpansPerColumn * 2;
                var spanCount = 0;
                var inSpan = false;
                var spanStart = 0;
                var spanBlock = BlockId.Air;

                // Optimization: Pre-calculate biome and slope for the column
                // This avoids 384 lookups per column
                var biomeId = currentChunkBiome?.GetBiomeAt(lx, lz) ?? BiomeId.Plains;
                var biomeDef = GetBiomeDefinition((int)biomeId);
                var slope = GetSlope(worldX, worldZ);

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
                        cheeseSlice,
                        spaghettiSlice,
                        overhangSlice,
                        biomeDef,
                        slope);
                    var localIndex = y * VoxelHelper.ChunkSideSizeSquare + columnIndex;

                    if (edits != null && edits.TryGetValue(localIndex, out var editedBlock))
                    {
                        block = editedBlock;
                    }

                    // Palette lookup
                    byte paletteIndex = chunkData.GetOrAddPaletteEntry(block);
                    chunkData.VoxelData[localIndex] = paletteIndex;

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
                
                // Performance optimization: Compute surface height for this column (highest opaque block)
                // This is used by lighting to quickly skip underground air columns
                chunkData.SurfaceHeights[columnIndex] = ComputeSurfaceHeight(chunkData, lx, lz);
            }
        }
        
        profiler.EndStep(TerrainGenerationProfiler.Step.BlockGeneration);

        // ChunkCollisionData.Spans populated inside TryCommitSpan
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
        // Phase 1: Sample climate cache - SINGLE POINT for all 2D climate noise
        // The climate cache samples: Continentalness, Erosion, PeaksValleys, Temperature, Humidity, Weirdness
        // using SIMD-batched operations. All subsequent code reads from this cache.
        profiler.BeginStep(TerrainGenerationProfiler.Step.ClimateSampling);
        climateCache.SampleForChunk(chunkX, chunkZ, config);
        BuildColumnCoordinates(chunkX, chunkZ);
        profiler.EndStep(TerrainGenerationProfiler.Step.ClimateSampling);
        
        // Phase 1: Height calculation - uses cached climate values, samples additional detail noise
        profiler.BeginStep(TerrainGenerationProfiler.Step.HeightCalculation);
        BuildColumnFieldCaches();
        profiler.EndStep(TerrainGenerationProfiler.Step.HeightCalculation);
        
        profiler.BeginStep(TerrainGenerationProfiler.Step.BiomeSelection);
        UpdateBiomeDataFromTerrainValues(); // Fix: Use terrain's continentalness for biomes
        profiler.EndStep(TerrainGenerationProfiler.Step.BiomeSelection);
        
        profiler.BeginStep(TerrainGenerationProfiler.Step.Noise3DSampling);
        BuildColumnVolumes();
        profiler.EndStep(TerrainGenerationProfiler.Step.Noise3DSampling);
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
        // Select biome for every column based on its actual terrain height.
        // This eliminates the 4x4 blocky biome borders.
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                // Use integer height for biome selection to match block placement logic
                // This prevents "dry ocean" bugs where float height < 35 but int height = 35
                var terrainHeight = (float)columnHeightInts[columnIndex];
                
                // Select biome using per-column climate values and actual terrain height
                var biome = biomeSelector.SelectPrimary(
                    cont01[columnIndex],
                    temp01[columnIndex],
                    humid01[columnIndex],
                    erosion01[columnIndex],
                    pv01[columnIndex],
                    terrainHeight);
                
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

            // Cheese caves
            SampleValueNoiseSliceSparse(
                sparseSliceScratch,
                sparseSampleX.AsSpan(),
                worldY,
                sparseSampleZ.AsSpan(),
                terrainParams.CheeseFrequency,
                terrainParams.Seed + 300u,
                octaves: 2, persistence: 0.6f, lacunarity: 1.9f);
            sparseSliceScratch.AsSpan().CopyTo(sparseCheeseGrid.AsSpan(sliceOffset, SparseSampleCount));

            // Spaghetti tunnels (two noise channels)
            SampleValueNoiseSliceSparse(
                sparseSliceScratch,
                sparseSampleX.AsSpan(),
                worldY,
                sparseSampleZ.AsSpan(),
                terrainParams.SpaghettiFrequency,
                terrainParams.Seed + 400u,
                octaves: 1, persistence: 1f, lacunarity: 2f);
            sparseSliceScratch.AsSpan().CopyTo(sparseSpaghettiA.AsSpan(sliceOffset, SparseSampleCount));

            SampleValueNoiseSliceSparse(
                sparseSliceScratch,
                sparseSampleX.AsSpan(),
                worldY,
                sparseSampleZ.AsSpan(),
                terrainParams.SpaghettiFrequency,
                terrainParams.Seed + 500u,
                octaves: 1, persistence: 1f, lacunarity: 2f);
            sparseSliceScratch.AsSpan().CopyTo(sparseSpaghettiB.AsSpan(sliceOffset, SparseSampleCount));

            // Overhangs
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

    private void SampleValueNoiseSliceSparse(Span<float> destination, ReadOnlySpan<float> xCoords, float yCoord, ReadOnlySpan<float> zCoords, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity)
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

        var x00 = Lerp(c000, c100, u);
        var x10 = Lerp(c010, c110, u);
        var x01 = Lerp(c001, c101, u);
        var x11 = Lerp(c011, c111, u);

        var y0 = Lerp(x00, x10, v);
        var y1 = Lerp(x01, x11, v);

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

    /// <summary>
    /// Generate the block type for a voxel at the given world position.
    /// Returns appropriate BlockId based on height, depth, biome, and cave systems.
    /// Uses biome-specific blocks (SurfaceBlock, SubsurfaceBlock, DeepBlock) from BiomeDefinition.
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
        Span<float> cheeseSlice,
        Span<float> spaghettiSlice,
        Span<float> overhangSlice,
        BiomeDefinition? biomeDef,
        float slope)
    {
        // Optimization: If we are significantly above the base height + overhang range,
        // the density will definitely be negative (air).
        // This avoids density calculations for the empty sky.
        if (y > baseHeight + terrainParams.OverhangHeightRange + 16)
        {
            return y <= VoxelHelper.WaterLevel ? BlockId.Water : BlockId.Air;
        }

        // 1. Calculate 3D Density
        // This replaces the simple "y > height" check with a full density check.
        // It allows for overhangs, arches, and floating islands where the 3D noise is strong enough.
        var density = GetTerrainDensity(continentalness01, baseHeight, overhangSlice[y], y, column3DFactor[columnIndex]);

        // 2. Density Check
        // If density is negative, it's air (or water).
        if (density < 0f)
        {
            return y <= VoxelHelper.WaterLevel ? BlockId.Water : BlockId.Air;
        }

        var tC = continentalness01;
        var isLand = tC >= terrainParams.OceanThreshold;

        // 3. Cave Systems (Cheese & Spaghetti)
        // Only apply caves if we have solid terrain
        if (isLand && y > 0)
        {
            var depth = height - y;
            // Slope is now pre-calculated per column
            if (IsCave(depth, slope, cheeseSlice[y], spaghettiSlice[y]))
            {
                return y <= VoxelHelper.WaterLevel && y <= VoxelHelper.WaterLevel + terrainParams.CaveFloodExtension ? BlockId.Water : BlockId.Air;
            }
        }

        // Get the biome for this position to determine block types
        // IMPORTANT: Block types are fully determined by biome - no overrides!
        // This ensures deterministic block types for inventory/block-breaking.
        // BiomeDefinition is now passed in from FillChunk (optimization)
        
        // If biome definition is missing, log warning and use fallback
        // This should not happen in production - all BiomeIds should have definitions
        if (biomeDef == null)
        {
            // Log.Warn($"Missing BiomeDefinition, using fallback blocks");
        }

        var isOceanBiome = (BiomeId?)biomeDef?.Id is BiomeId.Ocean or BiomeId.DeepOcean;
        var isUnderwater = y <= VoxelHelper.WaterLevel && (isOceanBiome || height < VoxelHelper.WaterLevel);

        // Surface block - determined entirely by biome
        // Note: With 3D terrain, 'height' is the 2D surface. Floating islands (y > height)
        // will fall through to DeepBlock (Stone), which is acceptable for now.
        if (y == height)
        {
            if (isUnderwater)
            {
                return biomeDef?.UnderwaterSurfaceBlock ?? BlockId.Gravel;
            }
            return biomeDef?.SurfaceBlock ?? BlockId.Grass;
        }

        // Subsurface blocks - use biome's subsurface block
        var depthBelowSurface = height - y;
        if (depthBelowSurface <= terrainParams.SubsurfaceDepth && depthBelowSurface >= 0)
        {
            if (isUnderwater)
            {
                return biomeDef?.UnderwaterSubsurfaceBlock ?? BlockId.Stone;
            }
            return biomeDef?.SubsurfaceBlock ?? BlockId.Dirt;
        }

        // Deep blocks - try to generate ore in stone regions
        var deepBlock = biomeDef?.DeepBlock ?? BlockId.Stone;
        if (deepBlock == BlockId.Stone)
        {
            // Only generate ores in stone blocks
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

    private float GetSlope(int wx, int wz)
    {
        var h0 = GenerateHeight(wx, wz);
        var h1 = GenerateHeight(wx + 1, wz);
        var h2 = GenerateHeight(wx, wz + 1);
        var h3 = GenerateHeight(wx - 1, wz);
        var h4 = GenerateHeight(wx, wz - 1);

        var dx = Math.Max(Math.Abs(h1 - h0), Math.Abs(h3 - h0));
        var dz = Math.Max(Math.Abs(h2 - h0), Math.Abs(h4 - h0));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private bool IsNearWater(int wx, int wz)
    {
        for (var dz = -3; dz <= 3; dz += 3)
        {
            for (var dx = -3; dx <= 3; dx += 3)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }

                var h = GenerateHeight(wx + dx, wz + dz);
                if (h <= VoxelHelper.WaterLevel)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsCave(int depth, float slope, float cheeseDensity, float spaghettiDensity)
    {
        var depthAtten = Smoothstep(0f, terrainParams.CaveDepthFade, depth);
        var slopeAtten = Smoothstep(terrainParams.CaveSlopeFadeMin, terrainParams.CaveSlopeFadeMax, slope);
        var attenuation = Math.Clamp(Math.Max(depthAtten, slopeAtten * 1.2f), 0f, 1f);

        return cheeseDensity * attenuation > terrainParams.CaveCarveThreshold ? true : spaghettiDensity * attenuation > terrainParams.CaveCarveThreshold;
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
        BlockId[] SpanTypes);
}