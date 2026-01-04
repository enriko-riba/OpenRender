using NoiseDotNet;
using System.Numerics;
using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using DarkVox.World;

namespace DarkVox.Server.World.Generation;

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
    private const int SparseSamplesXZ = VoxelHelper.ChunkSideSize / SparseStep + 1;  // 5 samples: 0,4,8,12,16
    private const int SparseSamplesY = VoxelHelper.ChunkYSize / SparseStep + 1;      // 97 samples: 0,4,8,...,384
    private const int SparseSampleCount = SparseSamplesXZ * SparseSamplesXZ;         // 25 samples per Y slice
    private const int SparseVolumeSize = SparseSampleCount * SparseSamplesY;         // 25 * 97 = 2425 total

    private TerrainConfig config = null!;
    private TerrainConfig.TerrainGenerationParams terrainParams;
    private readonly float[] heightSpline = new float[HeightSplineResolution];

    // Biome selection (Stage 1)
    private BiomeSelector biomeSelector = null!;  // Set in UpdateConfig

    // Phase 0 Infrastructure: Performance profiling
    private readonly TerrainGenerationProfiler profiler = new();

    // Stage 2: Biome-driven terrain density evaluation
    private TerrainDensityEvaluator densityEvaluator = null!;  // Set in UpdateConfig

    // Stage 3: Deterministic aquifer system for water level lookup
    private AquiferSystem aquiferSystem = null!;  // Set in UpdateConfig

    // Stage 5: Cave carving system with volume-based entrances
    private CaveCarver caveCarver = null!;  // Set in UpdateConfig

    // Cached biome definitions for fast lookups
    private readonly Dictionary<int, BiomeDefinition> biomeById = new(32);

    /// <summary>Gets the performance profiler for terrain generation.</summary>
    public TerrainGenerationProfiler Profiler => profiler;

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
    }

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

        using var ctx = new GenerationContext();
        var chunkBiomeData = new ChunkBiomeData();

        // 1. Generate base terrain voxels
        FillChunk(chunkIndex, chunkData, edits, ctx, chunkBiomeData);

        // Safety: Recalculate surface heights from actual voxel data to ensure mesher accuracy
        // This handles edge cases where FillChunk's tracking might diverge from actual blocks
        chunkData.RecalculateSurfaceHeights();

        var collision = new ChunkCollisionData();
        var spanPairs = new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        var spanTypes = new BlockId[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
        var spanCounts = new byte[VoxelHelper.ChunkSideSizeSquare];

        // Generate collision spans from base terrain
        GenerateCollisionData(chunkData, collision, spanPairs, spanTypes, spanCounts);

        profiler.EndStep(TerrainGenerationProfiler.Step.Total);
        profiler.FinalizeChunk();

        return new ChunkGenerationResult(collision, spanPairs, spanCounts, spanTypes, chunkBiomeData);
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
        var currentChunkBiome = biomeData ?? new ChunkBiomeData();
        if (biomeData is null)
        {
            using var ctx = new GenerationContext();
            PrepareChunkCaches(chunkX, chunkZ, ctx, currentChunkBiome);
        }

        // 2. Place vegetation (trees, flowers, etc.)
        profiler.BeginStep(TerrainGenerationProfiler.Step.Vegetation);
        var vegetationGen = new VegetationGenerator(config);
        vegetationGen.DecorateChunk(chunkData, currentChunkBiome, chunkX, chunkZ);
        profiler.EndStep(TerrainGenerationProfiler.Step.Vegetation);

        // Recalculate surface heights after decoration (trees may have raised the surface)
        chunkData.RecalculateSurfaceHeights();

        var collision = new ChunkCollisionData();
        var spanPairs = new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        var spanTypes = new BlockId[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
        var spanCounts = new byte[VoxelHelper.ChunkSideSizeSquare];

        // 3. Generate collision spans from final voxel data
        GenerateCollisionData(chunkData, collision, spanPairs, spanTypes, spanCounts);

        profiler.EndStep(TerrainGenerationProfiler.Step.Total);
        profiler.FinalizeChunk();

        return new ChunkGenerationResult(collision, spanPairs, spanCounts, spanTypes, currentChunkBiome);
    }

    /// <summary>
    /// Fills a chunk with terrain voxels using a column-first iteration strategy.
    /// 
    /// <para><b>Performance Optimizations:</b></para>
    /// <list type="bullet">
    ///   <item>Column-first (X,Z outer, Y inner) iteration to maximize cache locality for per-column data</item>
    ///   <item>Top-down Y iteration to track surface depth without O(N) lookups per voxel</item>
    ///   <item>Early-exit density calculation for voxels significantly above terrain height</item>
    ///   <item>Pre-cached biome, water body, and beach zone data per column (avoids 384× lookups)</item>
    ///   <item>Palette-based voxel storage to reduce memory bandwidth</item>
    /// </list>
    /// 
    /// <para><b>Complexity:</b> O(256 columns × ~height voxels) where height is terrain-dependent.</para>
    /// </summary>
    /// <param name="chunkIndex">The chunk's linear index in the world grid.</param>
    /// <param name="chunkData">Output chunk data to populate with voxels.</param>
    /// <param name="edits">Optional player block edits to apply on top of generated terrain.</param>
    /// <param name="ctx">Generation context containing pre-computed climate and noise data.</param>
    /// <param name="chunkBiomeData">Output biome data for this chunk.</param>
    private void FillChunk(
        int chunkIndex,
        ChunkData chunkData,
        IReadOnlyDictionary<int, BlockId>? edits,
        GenerationContext ctx,
        ChunkBiomeData chunkBiomeData)
    {
        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;

        PrepareChunkCaches(chunkX, chunkZ, ctx, chunkBiomeData);

        // Pre-cache common palette entries to avoid lookup overhead
        // Air is always index 0
        chunkData.GetOrAddPaletteEntry(BlockId.Air);

        profiler.BeginStep(TerrainGenerationProfiler.Step.BlockGeneration);

        // Pre-cache beach zone status for all 256 columns to avoid repeated lookups in inner Y loop
        // This saves ~384 IsWithinBeachDistance calls per column (surface + subsurface layers)
        Span<bool> columnIsBeachZone = stackalloc bool[ColumnCount];
        for (var i = 0; i < ColumnCount; i++)
        {
            columnIsBeachZone[i] = IsWithinBeachDistance(i, ctx);
        }

        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            var worldZ = chunkZ * VoxelHelper.ChunkSideSize + lz;
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var worldX = chunkX * VoxelHelper.ChunkSideSize + lx;
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                var height = ctx.ColumnHeightInts[columnIndex];
                var baseHeight = ctx.ColumnHeights[columnIndex];
                var continentalness01 = ctx.Continentalness01[columnIndex];
                var overhangSlice = GetColumnVolumeSpan(ctx.OverhangVolume, columnIndex);

                // Optimization: Pre-calculate biome and beach zone for the column
                // This avoids 384 lookups per column
                var biomeId = chunkBiomeData.GetBiomeAt(lx, lz);
                biomeById.TryGetValue((int)biomeId, out var biomeDef);
                var isBeachZone = columnIsBeachZone[columnIndex];

                // Optimization: Track state top-down to avoid redundant calculations
                var densityAbove = -1.0f; // Assumed air above world top
                var depthFromSurface = 0;
                var surfaceHeight = -1;

                // Iterate TOP-DOWN to track depth and surface state efficiently
                for (var y = VoxelHelper.ChunkYSize - 1; y >= 0; y--)
                {
                    // 1. Calculate 3D Density for THIS voxel
                    // Optimization: If we are significantly above the base height + overhang range,
                    // the density will definitely be negative (air).
                    float density;
                    if (y > baseHeight + terrainParams.OverhangHeightRange + 16)
                    {
                        density = -1.0f;
                    }
                    else
                    {
                        density = GetTerrainDensity(continentalness01, baseHeight, overhangSlice[y], y, ctx.Column3DFactor[columnIndex]);
                    }

                    // Track surface height (highest non-air block)
                    // Note: We check density >= 0 OR if it's a special bottom layer (bedrock/lava)
                    if (surfaceHeight == -1 && (density >= 0f || y <= 1))
                    {
                        surfaceHeight = y;
                    }

                    var block = GenerateBlock(
                        height,
                        y,
                        worldX,
                        worldZ,
                        columnIndex,
                        biomeDef,
                        density,
                        densityAbove,
                        depthFromSurface,
                        isBeachZone,
                        ctx);

                    var localIndex = y * VoxelHelper.ChunkSideSizeSquare + columnIndex;

                    if (edits != null && edits.TryGetValue(localIndex, out var editedBlock))
                    {
                        block = editedBlock;
                    }

                    // Palette lookup
                    var paletteIndex = chunkData.GetOrAddPaletteEntry(block);
                    chunkData.VoxelData[localIndex] = paletteIndex;

                    // Update state for next iteration (y-1)
                    // If current block is solid, depth increases. If air, depth resets.
                    if (density >= 0f)
                    {
                        depthFromSurface++;
                    }
                    else
                    {
                        depthFromSurface = 0;
                    }

                    densityAbove = density;
                }

                // Store computed surface height for this column
                // This is critical for the mesher to know how high to iterate
                chunkData.SurfaceHeights[columnIndex] = surfaceHeight >= 0 ? surfaceHeight : 0;
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
        // Initialize span counts to 0
        Array.Clear(spanCounts, 0, VoxelHelper.ChunkSideSizeSquare);

        // Note: We use local variables for span count and index to avoid bounds checks in the loop
        // Allocate enough space for MaxSpansPerColumn per column
        Span<byte> localSpanCounts = stackalloc byte[VoxelHelper.ChunkSideSizeSquare];
        // 256 columns * 32 spans * 2 ints * 4 bytes = 64KB (safe for stack)
        Span<int> localSpanPairs = stackalloc int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        Span<BlockId> localSpanTypes = stackalloc BlockId[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];

        // 1. Calculate initial spans from voxel data
        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
            {
                for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
                {
                    var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                    var localIndex = y * VoxelHelper.ChunkSideSizeSquare + columnIndex;

                    var paletteIndex = chunkData.VoxelData[localIndex];

                    // Skip air blocks (assuming air is always index 0)
                    if (paletteIndex == 0)
                    {
                        continue;
                    }

                    var currentSpanCount = localSpanCounts[columnIndex];

                    // Check if we can merge with the previous span
                    if (currentSpanCount > 0)
                    {
                        var prevPairIndex = (columnIndex * ChunkCollisionData.MaxSpansPerColumn + currentSpanCount - 1) * 2;
                        var prevTypeIndex = columnIndex * ChunkCollisionData.MaxSpansPerColumn + currentSpanCount - 1;

                        var prevEnd = localSpanPairs[prevPairIndex + 1];
                        var prevType = localSpanTypes[prevTypeIndex];
                        var currentType = chunkData.Palette[paletteIndex];

                        if (prevEnd == y && prevType == currentType)
                        {
                            // Extend previous span
                            localSpanPairs[prevPairIndex + 1] = y + 1;
                            continue;
                        }
                    }

                    // Start new span if we have space
                    if (currentSpanCount < ChunkCollisionData.MaxSpansPerColumn)
                    {
                        var basePairIndex = (columnIndex * ChunkCollisionData.MaxSpansPerColumn + currentSpanCount) * 2;
                        var baseTypeIndex = columnIndex * ChunkCollisionData.MaxSpansPerColumn + currentSpanCount;

                        localSpanPairs[basePairIndex] = y;
                        localSpanPairs[basePairIndex + 1] = y + 1;
                        localSpanTypes[baseTypeIndex] = chunkData.Palette[paletteIndex];

                        // Increment span count for the column
                        localSpanCounts[columnIndex]++;
                    }
                }
            }
        }

        // 2. Copy local span data to output arrays AND ChunkCollisionData.Spans
        for (var i = 0; i < VoxelHelper.ChunkSideSizeSquare; i++)
        {
            var count = localSpanCounts[i];
            spanCounts[i] = count;
            collision.SpanCounts[i] = count;

            if (count > 0)
            {
                var srcBaseIndex = i * ChunkCollisionData.MaxSpansPerColumn * 2;
                var srcTypeBaseIndex = i * ChunkCollisionData.MaxSpansPerColumn;

                var dstIndex = i * ChunkCollisionData.MaxSpansPerColumn * 2;
                var dstTypeIndex = i * ChunkCollisionData.MaxSpansPerColumn;
                var collisionOffset = i * ChunkCollisionData.MaxSpansPerColumn;

                // Copy span pairs and types to output arrays
                for (var j = 0; j < count; j++)
                {
                    var startY = localSpanPairs[srcBaseIndex + j * 2];
                    var endY = localSpanPairs[srcBaseIndex + j * 2 + 1];
                    var blockType = localSpanTypes[srcTypeBaseIndex + j];

                    spanPairs[dstIndex + j * 2] = startY;
                    spanPairs[dstIndex + j * 2 + 1] = endY;
                    spanTypes[dstTypeIndex + j] = blockType;

                    // Also populate ChunkCollisionData.Spans for CollisionManager
                    // Note: CollisionManager expects EndY to be INCLUSIVE, but we store it as EXCLUSIVE
                    // So we subtract 1 when storing to Spans
                    collision.Spans[collisionOffset + j] = new ColumnSpan
                    {
                        StartY = (short)startY,
                        EndY = (short)(endY - 1),  // Convert exclusive to inclusive
                        Block = (ushort)blockType
                    };
                }
            }
        }
    }

    private void PrepareChunkCaches(int chunkX, int chunkZ, GenerationContext ctx, ChunkBiomeData chunkBiomeData)
    {
        // ============================================================
        // STAGE 1: Climate Sampling (Biome Assignment First)
        // ============================================================
        // Sample all climate noise ONCE using SIMD batching.
        // These cached values are used by BOTH biome selection AND height calculation.
        profiler.BeginStep(TerrainGenerationProfiler.Step.ClimateSampling);
        ClimateSampler.SampleForChunk(chunkX, chunkZ, config, ctx);
        profiler.EndStep(TerrainGenerationProfiler.Step.ClimateSampling);

        // ============================================================
        // STAGE 1 (continued): Biome Selection BEFORE Height
        // ============================================================
        // Select biome for each column using climate values.
        // This is the CORRECT Minecraft order: biomes drive terrain shape.
        profiler.BeginStep(TerrainGenerationProfiler.Step.BiomeSelection);
        SelectBiomesFromClimate(ctx, chunkBiomeData);
        profiler.EndStep(TerrainGenerationProfiler.Step.BiomeSelection);

        // ============================================================
        // STAGE 2: Biome-Driven Height Calculation
        // ============================================================
        // Calculate terrain height using biome properties (BaseHeight, HeightVariation, etc.)
        // Uses cached climate values (PV, Erosion) - NO re-sampling.
        profiler.BeginStep(TerrainGenerationProfiler.Step.HeightCalculation);
        BuildColumnHeightsFromBiomes(ctx);
        profiler.EndStep(TerrainGenerationProfiler.Step.HeightCalculation);

        // ============================================================
        // Water Body Detection
        // ============================================================
        // Compute water body info using heights and biomes
        BuildColumnWaterBodies(ctx, chunkBiomeData);

        // ============================================================
        // STAGE 5: 3D Noise for Caves/Overhangs
        // ============================================================
        profiler.BeginStep(TerrainGenerationProfiler.Step.Noise3DSampling);
        BuildColumnVolumes(chunkX, chunkZ, ctx);
        BuildCaveMaskVolume(chunkX, chunkZ, ctx);
        profiler.EndStep(TerrainGenerationProfiler.Step.Noise3DSampling);
    }

    /// <summary>
    /// STAGE 1: Select biomes for each column using climate values.
    /// This happens BEFORE height calculation - biomes drive terrain shape.
    /// 
    /// Uses cached climate values from ChunkClimateCache (no re-sampling).
    /// </summary>
    private void SelectBiomesFromClimate(GenerationContext ctx, ChunkBiomeData chunkBiomeData)
    {
        var cont01 = ctx.Continentalness01;
        var temp01 = ctx.Temperature01;
        var humid01 = ctx.Humidity01;
        var erosion01 = ctx.Erosion01;
        var pv01 = ctx.PeaksValleys01;

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

                chunkBiomeData.SetBiomeAt(lx, lz, biome);

                // Cache biome definition for height calculation
                biomeById.TryGetValue((int)biome, out var biomeDef);
                ctx.ColumnBiomes[columnIndex] = biomeDef;
            }
        }

        PopulateLegacyCellClimate(ctx, chunkBiomeData);
    }

    /// <summary>
    /// Populate the 4x4 climate grid in <see cref="ChunkBiomeData"/> directly from the per-column
    /// climate data in <see cref="GenerationContext"/>.
    ///
    /// This keeps debug queries (interpolated climate) consistent with the actual generation
    /// pipeline, and avoids redundant noise sampling.
    /// </summary>
    private static void PopulateLegacyCellClimate(GenerationContext ctx, ChunkBiomeData chunkBiomeData)
    {
        var temp01 = ctx.Temperature01;
        var humid01 = ctx.Humidity01;
        var cont = ctx.Continentalness;
        var erosion = ctx.Erosion;
        var pv = ctx.PeaksValleys;
        var weird = ctx.Weirdness;

        for (var cellZ = 0; cellZ < ChunkBiomeData.GridSize; cellZ++)
        {
            for (var cellX = 0; cellX < ChunkBiomeData.GridSize; cellX++)
            {
                var cellIndex = cellZ * ChunkBiomeData.GridSize + cellX;

                // Sample at cell center (4x4 cells, center at +2,+2)
                var lx = cellX * ChunkBiomeData.BlocksPerCell + (ChunkBiomeData.BlocksPerCell / 2);
                var lz = cellZ * ChunkBiomeData.BlocksPerCell + (ChunkBiomeData.BlocksPerCell / 2);
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;

                chunkBiomeData.Temperature[cellIndex] = temp01[columnIndex];
                chunkBiomeData.Humidity[cellIndex] = humid01[columnIndex];
                chunkBiomeData.Continentalness[cellIndex] = cont[columnIndex];
                chunkBiomeData.Erosion[cellIndex] = erosion[columnIndex];
                chunkBiomeData.PeaksValleys[cellIndex] = pv[columnIndex];
                chunkBiomeData.Weirdness[cellIndex] = weird[columnIndex];
                chunkBiomeData.BiomeIds[cellIndex] = chunkBiomeData.GetBiomeAt(lx, lz);
            }
        }
    }

    /// <summary>
    /// STAGE 2: Calculate terrain heights using biome properties.
    /// This is the core of the Minecraft-style pipeline - biomes DRIVE terrain shape.
    /// 
    /// Uses cached climate values (PV, Erosion) from Stage 1 - NO re-sampling.
    /// </summary>
    private void BuildColumnHeightsFromBiomes(GenerationContext ctx)
    {
        var shaping = config.TerrainShaping;
        var cachedErosion01 = ctx.Erosion01;
        var cachedWeirdness = ctx.Weirdness;

        // Sample additional detail noise for cliffs (still needed for mountain detail)
        var xSpan = ctx.WorldX.AsSpan(0, ColumnCount);
        var zSpan = ctx.WorldZ.AsSpan(0, ColumnCount);
        SampleFbm2D(xSpan, zSpan, terrainParams.CliffFrequency, terrainParams.Seed + shaping.CliffNoiseSeedOffset, shaping.CliffNoiseOctaves, shaping.CliffNoisePersistence, shaping.CliffNoiseLacunarity, ctx.ColumnCliff.AsSpan(0, ColumnCount), ctx);


        var temp01 = ctx.Temperature01;
        var humid01 = ctx.Humidity01;
        var pv01 = ctx.PeaksValleys01;

        // Reused working set for weighted biome blending (avoid per-chunk allocations)
        var weightedBiomes = new List<(BiomeDefinition Biome, float Weight)>(8);

        for (var i = 0; i < ColumnCount; i++)
        {
            var biome = ctx.ColumnBiomes[i];
            var cont01 = ctx.Continentalness01[i];
            var pv = ctx.PeaksValleys[i];
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
                ctx.Column3DFactor[i] = densityEvaluator.Calculate3DFactor(absWeirdness, erosion01);

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
                ctx.Column3DFactor[i] = Calculate3DFactor(absWeirdness, erosion01, shaping);
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

            // CRITICAL: Clamp baseHeight to valid range to prevent solid pillars
            // Unclamped heights > ChunkYSize would cause density > 0 for entire column
            baseHeight = Math.Clamp(baseHeight, 0f, VoxelHelper.ChunkYSize - 1);
            
            ctx.ColumnHeights[i] = baseHeight;
            var rounded = (int)MathF.Round(baseHeight);

            // Guard: land should not be BELOW sea level (but allow sea-level beaches).
            if (isLandColumn && rounded < (int)VoxelHelper.WaterLevel)
            {
                rounded = (int)VoxelHelper.WaterLevel;
                ctx.ColumnHeights[i] = rounded;
            }
            ctx.ColumnHeightInts[i] = Math.Clamp(rounded, 0, VoxelHelper.ChunkYSize - 1);
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
    private void BuildColumnWaterBodies(GenerationContext ctx, ChunkBiomeData chunkBiomeData)
    {
        var cont01 = ctx.Continentalness01;
        var aquifer01 = ctx.AquiferNoise01;

        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;
                var continentalness01 = cont01[columnIndex];
                var terrainHeight = ctx.ColumnHeights[columnIndex];
                var biomeId = chunkBiomeData.GetBiomeAt(lx, lz);

                // STAGE 3: Use aquifer system for deterministic water level lookup
                ctx.ColumnWaterBody[columnIndex] = aquiferSystem.GetWaterBodyInfo(
                    biomeId,
                    terrainHeight,
                    continentalness01,
                    aquifer01[columnIndex]);
            }
        }

        ComputeOceanAdjacency(ctx);

        // Beach is a derived biome based on proximity to ocean water (not climate).
        ApplyBeachBiomeOverride(ctx, chunkBiomeData);
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
    private void ComputeOceanAdjacency(GenerationContext ctx)
    {
        // Initialize distances: 0 for ocean, MaxValue for land
        for (var i = 0; i < ColumnCount; i++)
        {
            ctx.ColumnOceanDistance[i] = ctx.ColumnWaterBody[i].IsOcean ? 0f : float.MaxValue;
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
                    var currentDist = ctx.ColumnOceanDistance[idx];

                    // Check 4 neighbors and update if shorter path found
                    if (lz > 0)
                    {
                        var neighborDist = ctx.ColumnOceanDistance[(lz - 1) * VoxelHelper.ChunkSideSize + lx];
                        if (neighborDist + 1f < currentDist)
                        {
                            ctx.ColumnOceanDistance[idx] = neighborDist + 1f;
                            changed = true;
                        }
                    }
                    if (lz < VoxelHelper.ChunkSideSize - 1)
                    {
                        var neighborDist = ctx.ColumnOceanDistance[(lz + 1) * VoxelHelper.ChunkSideSize + lx];
                        if (neighborDist + 1f < currentDist)
                        {
                            ctx.ColumnOceanDistance[idx] = neighborDist + 1f;
                            changed = true;
                        }
                    }
                    if (lx > 0)
                    {
                        var neighborDist = ctx.ColumnOceanDistance[lz * VoxelHelper.ChunkSideSize + (lx - 1)];
                        if (neighborDist + 1f < currentDist)
                        {
                            ctx.ColumnOceanDistance[idx] = neighborDist + 1f;
                            changed = true;
                        }
                    }
                    if (lx < VoxelHelper.ChunkSideSize - 1)
                    {
                        var neighborDist = ctx.ColumnOceanDistance[lz * VoxelHelper.ChunkSideSize + (lx + 1)];
                        if (neighborDist + 1f < currentDist)
                        {
                            ctx.ColumnOceanDistance[idx] = neighborDist + 1f;
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
                var wx = ctx.WorldX[idx];
                var wz = ctx.WorldZ[idx];

                // Sample noise for beach width variation
                // Use domain-warped noise for more organic shapes
                var beachNoise = GetBeachNoise(wx, wz);

                // Beach threshold varies from ~1 block (minimum) to baseBeachWidth
                // Noise modulates the width: high noise = wider beach, low noise = narrower
                var t = Math.Clamp(0.5f + beachNoise * beachNoiseStrength, 0f, 1f);
                var threshold = 1f + (baseBeachWidth - 1f) * t;
                ctx.ColumnBeachThreshold[idx] = Math.Clamp(threshold, 1f, baseBeachWidth);
            }
        }
    }

    /// <summary>
    /// Apply Beach biome to columns that are close to ocean water.
    /// This prevents climate-only beach selection (which creates huge inland sand bands)
    /// and ensures beaches only appear near actual ocean columns.
    /// </summary>
    private void ApplyBeachBiomeOverride(GenerationContext ctx, ChunkBiomeData chunkBiomeData)
    {
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

                if (ctx.ColumnWaterBody[idx].IsOcean)
                {
                    continue;
                }

                if (!IsWithinBeachDistance(idx, ctx))
                {
                    continue;
                }

                if (ctx.ColumnHeightInts[idx] > maxBeachSurfaceY)
                {
                    continue;
                }

                chunkBiomeData.SetBiomeAt(lx, lz, BiomeId.Beach);
                ctx.ColumnBiomes[idx] = beachDef;
            }
        }
    }

    /// <summary>
    /// Check if a column is within beach distance of ocean.
    /// Returns true if the column should be considered coastal (for Beach biome).
    /// </summary>
    private static bool IsWithinBeachDistance(int columnIndex, GenerationContext ctx)
    {
        var distance = ctx.ColumnOceanDistance[columnIndex];
        var threshold = ctx.ColumnBeachThreshold[columnIndex];
        return distance > 0f && distance <= threshold; // distance > 0 excludes ocean itself
    }

    private void BuildColumnVolumes(int chunkX, int chunkZ, GenerationContext ctx)
    {
        // Minecraft-style optimization: sample noise at sparse intervals and trilinear interpolate.
        // This reduces 3D noise samples from 98,304 to 2,425 per noise type (~40x reduction).


        var baseX = chunkX * VoxelHelper.ChunkSideSize;
        var baseZ = chunkZ * VoxelHelper.ChunkSideSize;

        // Get Y-stretch factor for horizontal cave bias
        var spaghettiYStretch = config.Caves.SpaghettiYStretch;

        // Build sparse sample coordinates (5x5 grid at 4-block intervals)
        var sparseIdx = 0;
        for (var sz = 0; sz < SparseSamplesXZ; sz++)
        {
            var worldZ = baseZ + sz * SparseStep;
            for (var sx = 0; sx < SparseSamplesXZ; sx++)
            {
                ctx.SparseSampleX[sparseIdx] = baseX + sx * SparseStep;
                ctx.SparseSampleZ[sparseIdx] = worldZ;
                sparseIdx++;
            }
        }

        // Prepare span references for SIMD noise sampling
        var xSpan = ctx.SparseSampleX.AsSpan(0, SparseSampleCount);
        var ySpan = ctx.SparseSampleY.AsSpan(0, SparseSampleCount);
        var zSpan = ctx.SparseSampleZ.AsSpan(0, SparseSampleCount);
        var scratchSpan = ctx.SparseSliceScratch.AsSpan(0, SparseSampleCount);

        // Sample sparse 3D grid for each noise type using SIMD-accelerated functions
        for (var sy = 0; sy < SparseSamplesY; sy++)
        {
            var worldY = sy * SparseStep;
            var sliceOffset = sy * SparseSampleCount;

            // HORIZONTAL CAVE BIAS: Stretch Y coordinate for spaghetti caves
            // This makes caves prefer horizontal tunnels over vertical shafts
            var stretchedY = worldY * spaghettiYStretch;

            // Cheese caves (2 octaves - large chambers use normal Y)
            SampleFbmNoiseSlice3D(
                ctx.SparseCheeseGrid.AsSpan(sliceOffset, SparseSampleCount),
                xSpan, ySpan, zSpan, scratchSpan,
                worldY,
                terrainParams.CheeseFrequency,
                terrainParams.Seed + 300u,
                octaves: 2, persistence: 0.6f, lacunarity: 1.9f);

            // Spaghetti tunnels A with Y-stretch for horizontal bias (single octave)
            SampleGradientNoiseSlice3D(
                ctx.SparseSpaghettiA.AsSpan(sliceOffset, SparseSampleCount),
                xSpan, ySpan, zSpan,
                stretchedY,
                terrainParams.SpaghettiFrequency,
                terrainParams.Seed + 400u,
                octaves: 1, persistence: 1f, lacunarity: 2f);

            // Spaghetti tunnels B with Y-stretch for horizontal bias (single octave)
            SampleGradientNoiseSlice3D(
                ctx.SparseSpaghettiB.AsSpan(sliceOffset, SparseSampleCount),
                xSpan, ySpan, zSpan,
                stretchedY,
                terrainParams.SpaghettiFrequency,
                terrainParams.Seed + 500u,
                octaves: 1, persistence: 1f, lacunarity: 2f);

            // Overhangs (2 octaves)
            SampleFbmNoiseSlice3D(
                ctx.SparseOverhangGrid.AsSpan(sliceOffset, SparseSampleCount),
                xSpan, ySpan, zSpan, scratchSpan,
                worldY,
                terrainParams.OverhangFrequency,
                terrainParams.Seed + 2000u,
                octaves: 2, persistence: 0.55f, lacunarity: 2f);
        }

        // Trilinear interpolate sparse samples into full-resolution volumes
        InterpolateSparseVolumes(ctx);
    }

    private void BuildCaveMaskVolume(int chunkX, int chunkZ, GenerationContext ctx)
    {
        // Stage 5: Delegate cave carving to CaveCarver
        // This replaces the inline implementation with the improved volume-based entrance carving
        if (caveCarver == null)
        {
            Array.Clear(ctx.CaveMaskVolume);
            return;
        }

        // Build isLandColumn array for CaveCarver
        Span<bool> isLandColumn = stackalloc bool[ColumnCount];
        for (var i = 0; i < ColumnCount; i++)
        {
            isLandColumn[i] = !ctx.ColumnWaterBody[i].IsOcean;
        }

        // Carve caves using the new improved system
        caveCarver.CarveChunk(
            chunkX,
            chunkZ,
            ctx.ColumnHeightInts,
            isLandColumn,
            (wx, wz) => GetSurfaceHeightFloat(wx, wz, chunkX, chunkZ, ctx));

        // Copy cave mask from CaveCarver to local buffer
        caveCarver.CaveMask.CopyTo(ctx.CaveMaskVolume);
    }


    /// <summary>
    /// Interpolates sparse 3D noise samples into full-resolution volumes using trilinear interpolation.
    /// 
    /// <para><b>Algorithm:</b> For each voxel in the 16×16×384 chunk:</para>
    /// <list type="number">
    ///   <item>Find the 8 surrounding sparse samples (2×2×2 corners of the containing cell)</item>
    ///   <item>Bilinearly interpolate the 4 XZ corners at both Y levels</item>
    ///   <item>Linearly interpolate between the two Y levels</item>
    /// </list>
    /// 
    /// <para><b>Performance Notes:</b></para>
    /// <list type="bullet">
    ///   <item>Processes 4 noise types (cheese, spaghetti A/B, overhang) per voxel</item>
    ///   <item>XZ indices and bilinear weights are computed once per column</item>
    ///   <item>Y interpolation weights are computed once per 4-voxel segment</item>
    ///   <item>Inner loop (dy) has only 4 iterations, limiting vectorization benefit</item>
    /// </list>
    /// 
    /// <para><b>Output Volumes:</b></para>
    /// <list type="bullet">
    ///   <item>CheeseVolume: Large cave chambers (applied directly)</item>
    ///   <item>SpaghettiVolume: Tunnel width from distance field of two noise channels</item>
    ///   <item>OverhangVolume: 3D terrain detail for cliff/overhang features</item>
    /// </list>
    /// </summary>
    /// <param name="ctx">Generation context containing sparse samples and output volumes.</param>
    private void InterpolateSparseVolumes(GenerationContext ctx)
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
                    var c0 = BilinearSample(ctx.SparseCheeseGrid, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var c1 = BilinearSample(ctx.SparseCheeseGrid, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    // Spaghetti A
                    var sa0 = BilinearSample(ctx.SparseSpaghettiA, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var sa1 = BilinearSample(ctx.SparseSpaghettiA, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    // Spaghetti B
                    var sb0 = BilinearSample(ctx.SparseSpaghettiB, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var sb1 = BilinearSample(ctx.SparseSpaghettiB, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

                    // Overhang
                    var oh0 = BilinearSample(ctx.SparseOverhangGrid, yOffset0, idx00, idx10, idx01, idx11, tx, tz);
                    var oh1 = BilinearSample(ctx.SparseOverhangGrid, yOffset1, idx00, idx10, idx01, idx11, tx, tz);

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
                        ctx.CheeseVolume[sliceOffset] = Math.Clamp(cheeseSample, -1f, 1f);

                        var n1 = (Lerp(sa0, sa1, ty) * 2f - 1f);
                        var n2 = (Lerp(sb0, sb1, ty) * 2f - 1f);
                        var dist = MathF.Sqrt(n1 * n1 + n2 * n2);
                        var amp = Math.Clamp(terrainParams.SpaghettiAmplitude, 0.2f, 4f);
                        var ampT = (amp - 0.2f) / 3.8f;
                        var widthFactor = Lerp(2.8f, 1.1f, ampT);
                        var tunnelWidth = 1f - dist * widthFactor;
                        ctx.SpaghettiVolume[sliceOffset] = Math.Clamp(tunnelWidth, -1f, 1f);

                        var overhangSample = (Lerp(oh0, oh1, ty) * 2f - 1f) * terrainParams.OverhangAmplitude;
                        ctx.OverhangVolume[sliceOffset] = Math.Clamp(overhangSample, -1f, 1f);
                    }
                }
            }
        }
    }

    private static void SampleFbm2D(Span<float> xCoords, Span<float> zCoords, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity, Span<float> destination, GenerationContext ctx)
    {
        var length = destination.Length;
        destination.Clear();
        var amplitude = 1f;
        var frequency = baseFrequency;
        var totalAmplitude = 0f;
        var scratch = ctx.SampleScratch2D.AsSpan(0, length);

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

    private static void SampleFbm3D(Span<float> xCoords, Span<float> yCoords, Span<float> zCoords, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity, Span<float> destination, GenerationContext ctx)
    {
        var length = destination.Length;
        destination.Clear();
        var amplitude = 1f;
        var frequency = baseFrequency;
        var totalAmplitude = 0f;
        var scratch = ctx.Scratch3DOutput.AsSpan(0, length);

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

    private float SampleFbm2DSingle(Vector2 p, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity, GenerationContext ctx)
    {
        Span<float> x = stackalloc float[1];
        Span<float> z = stackalloc float[1];
        Span<float> output = stackalloc float[1];
        x[0] = p.X;
        z[0] = p.Y;
        SampleFbm2D(x, z, baseFrequency, seed, octaves, persistence, lacunarity, output, ctx);
        return output[0];
    }

    private float SampleFbm3DSingle(Vector3 p, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity, GenerationContext ctx)
    {
        Span<float> x = stackalloc float[1];
        Span<float> y = stackalloc float[1];
        Span<float> z = stackalloc float[1];
        Span<float> output = stackalloc float[1];
        x[0] = p.X;
        y[0] = p.Y;
        z[0] = p.Z;
        SampleFbm3D(x, y, z, baseFrequency, seed, octaves, persistence, lacunarity, output, ctx);
        return output[0];
    }

    private float GetSurfaceHeightFloat(int wx, int wz, int chunkX, int chunkZ, GenerationContext ctx)
        => TryGetColumnIndex(wx, wz, chunkX, chunkZ, out var columnIndex) ? ctx.ColumnHeights[columnIndex]
            : GetHeight(new Vector2(wx, wz), ctx);

    /// <summary>
    /// Generates the block type for a single voxel based on terrain density, biome, and position.
    /// 
    /// <para><b>Performance Characteristics:</b></para>
    /// <list type="bullet">
    ///   <item>Called ~98,304 times per chunk (16×16×384 voxels)</item>
    ///   <item>Uses pre-computed density and depth state passed from FillChunk (avoids re-calculation)</item>
    ///   <item>Beach zone status pre-cached per column to avoid repeated distance checks</item>
    ///   <item>Ore generation uses fast hash instead of noise sampling</item>
    /// </list>
    /// 
    /// <para><b>Block Selection Priority:</b></para>
    /// <list type="number">
    ///   <item>Y=0: Lava (world bottom)</item>
    ///   <item>Y=1: Bedrock (impenetrable layer)</item>
    ///   <item>Negative density: Air or Water (if in water body)</item>
    ///   <item>Cave mask set: Air (carved cave)</item>
    ///   <item>Surface block: Biome surface or Sand (beach)</item>
    ///   <item>Subsurface: Biome subsurface or Sand (beach)</item>
    ///   <item>Deep: Stone with potential ore generation</item>
    /// </list>
    /// </summary>
    /// <param name="height">Pre-computed integer terrain height for this column.</param>
    /// <param name="y">Current Y coordinate being generated.</param>
    /// <param name="wx">World X coordinate.</param>
    /// <param name="wz">World Z coordinate.</param>
    /// <param name="columnIndex">Linear index of the column (0-255).</param>
    /// <param name="biomeDef">Cached biome definition for this column (may be null).</param>
    /// <param name="density">Pre-computed terrain density at this voxel.</param>
    /// <param name="densityAbove">Density of the voxel above (for surface detection).</param>
    /// <param name="depthFromSurface">Tracked depth below surface (for subsurface layers).</param>
    /// <param name="isBeachZone">Pre-cached flag indicating if column is within beach distance.</param>
    /// <param name="ctx">Generation context with cached column data.</param>
    /// <returns>The block type to place at this voxel position.</returns>
    private BlockId GenerateBlock(
        int height,
        int y,
        int wx,
        int wz,
        int columnIndex,
        BiomeDefinition? biomeDef,
        float density,
        float densityAbove,
        int depthFromSurface,
        bool isBeachZone,
        GenerationContext ctx)
    {
        // === HARDCODED BOTTOM LAYERS ===
        // Y=0: Always lava (magma layer at the bottom of the world)
        // Y=1: Always bedrock (impenetrable foundation layer)
        if (y == 0)
        {
            return BlockId.Lava;
        }
        if (y == 1)
        {
            return BlockId.Bedrock;
        }

        // SINGLE SOURCE OF TRUTH: Use cached water body info computed in BuildColumnWaterBodies()
        var waterBody = ctx.ColumnWaterBody[columnIndex];
        var hasWaterHere = waterBody.HasWater;
        var localWaterLevel = waterBody.WaterLevel;

        // 2. Density Check - If density is negative, it's air (or water in water body columns).
        if (density < 0f)
        {
            // Water fills air space ONLY in columns with water body
            return hasWaterHere && y <= localWaterLevel ? BlockId.Water : BlockId.Air;
        }

        // Block is solid - check cave carving
        var isLand = !waterBody.IsOcean;
        var caveMask = GetColumnCaveMask(ctx.CaveMaskVolume, columnIndex);
        if (isLand && y > 0 && caveMask[y] != 0)
        {
            // Cave carved this voxel - it becomes air
            return BlockId.Air;
        }

        // 4. Determine if this is a SURFACE block by checking if block above is air or carved by a cave
        // Use cached densityAbove to avoid re-calculation
        var caveAbove = isLand && y < VoxelHelper.ChunkYSize - 1 && caveMask[y + 1] != 0;
        var isSurface = densityAbove < 0f || caveAbove;

        // 5. Determine block type based on position and biome
        if (biomeDef == null)
        {
            return isSurface ? BlockId.Grass : BlockId.Stone;
        }

        // Underwater detection
        var isUnderwater = hasWaterHere && height < localWaterLevel;

        // Surface block
        if (isSurface)
        {
            // CAVE FLOOR FIX: Deep cave floors get stone instead of biome grass
            var depthBelowSurface = height - y;
            if (depthBelowSurface > config.Caves.CaveFloorDepthThreshold)
            {
                return BlockId.Stone;
            }

            // Coastal beach override - use pre-cached beach zone status
            var atBeachHeight = y >= VoxelHelper.WaterLevel && y <= VoxelHelper.WaterLevel + terrainParams.ShorelineRange;
            return !isUnderwater && isBeachZone && atBeachHeight && !hasWaterHere
                ? BlockId.Sand
                : isUnderwater ? biomeDef.UnderwaterSurfaceBlock : biomeDef.SurfaceBlock;
        }

        // Subsurface blocks - use tracked depthFromSurface
        // This avoids the O(N) loop that was previously here
        var effectiveDepth = depthFromSurface;
        if (effectiveDepth == 0)
        {
            // Fallback if tracking failed (shouldn't happen with correct top-down logic)
            effectiveDepth = Math.Max(0, height - y);
        }

        if (effectiveDepth <= terrainParams.SubsurfaceDepth)
        {
            // CAVE SUBSURFACE FIX
            var depthBelowTerrainSurface = height - y;
            if (depthBelowTerrainSurface > config.Caves.CaveFloorDepthThreshold)
            {
                return BlockId.Dirt;
            }

            // Beach subsurface - use pre-cached beach zone status
            var atBeachHeight = y >= VoxelHelper.WaterLevel && y <= VoxelHelper.WaterLevel + terrainParams.ShorelineRange;
            return !isUnderwater && isBeachZone && atBeachHeight && !hasWaterHere
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

    private float GetHeight(Vector2 p, GenerationContext ctx)
    {
        var continentalness = GetContinentalness(p, ctx);
        var erosion = GetErosion(p, ctx);
        var peaks = GetPeaksValleys(p, ctx);
        var tC = continentalness * 0.5f + 0.5f;
        var baseHeight = SampleHeightSpline(tC) + VoxelHelper.WaterLevel;

        if (tC >= terrainParams.OceanThreshold)
        {
            var ruggedness = 1f - erosion * 0.5f - 0.5f;
            baseHeight += peaks * 20f * ruggedness;

            if (tC > terrainParams.MountainThreshold)
            {
                var cliffNoise = SampleFbm2DSingle(p, terrainParams.CliffFrequency, terrainParams.Seed + 1500u, 4, 0.6f, 2.5f, ctx);
                cliffNoise = MathF.Abs(cliffNoise);
                var mountainness = Smoothstep(terrainParams.MountainThreshold, 0.95f, tC);
                baseHeight += cliffNoise * terrainParams.CliffAmplitude * mountainness;
            }
        }

        return baseHeight;
    }

    private Vector2 DomainWarp(Vector2 p, uint seed, GenerationContext ctx)
    {
        var qx = SampleFbm2DSingle(p, 1f, seed, 2, 0.5f, 2f, ctx);
        var qy = SampleFbm2DSingle(p + new Vector2(5.2f, 1.3f), 1f, seed, 2, 0.5f, 2f, ctx);
        var warp = new Vector2(qx, qy) * terrainParams.WarpStrength;
        return warp;
    }

    private float GetContinentalness(Vector2 p, GenerationContext ctx)
    {
        var warp = DomainWarp(p, terrainParams.Seed, ctx);
        var n = SampleFbm2DSingle(p + warp, config.Continentalness.BaseScale, terrainParams.Seed, config.Continentalness.Octaves, config.Continentalness.Persistence, config.Continentalness.Lacunarity, ctx);
        return Math.Clamp(n * config.Continentalness.OutputScale, -1f, 1f);
    }

    private float GetErosion(Vector2 p, GenerationContext ctx)
    {
        var warp = DomainWarp(p, terrainParams.Seed, ctx);
        var n = SampleFbm2DSingle(p + warp, config.Erosion.BaseScale, terrainParams.Seed + 200u, config.Erosion.Octaves, config.Erosion.Persistence, config.Erosion.Lacunarity, ctx);
        return Math.Clamp(n * config.Erosion.OutputScale, -1f, 1f);
    }

    private float GetPeaksValleys(Vector2 p, GenerationContext ctx)
    {
        var warp = DomainWarp(p, terrainParams.Seed, ctx);
        var n = SampleFbm2DSingle(p + warp, config.PeaksValleys.BaseScale, terrainParams.Seed + 400u, config.PeaksValleys.Octaves, config.PeaksValleys.Persistence, config.PeaksValleys.Lacunarity, ctx);
        n = Math.Clamp(n * config.PeaksValleys.OutputScale, -1f, 1f);

        if (config.PeaksValleys.UseRidged)
        {
            var ridge = 1f - MathF.Abs(n);
            ridge = Math.Clamp(ridge, 0f, 1f);
            return MathF.Pow(ridge, Math.Max(0.01f, config.PeaksValleys.RidgeSharpness));
        }
        return n * 0.5f + 0.5f;
    }

    private static Span<byte> GetColumnCaveMask(byte[] caveMaskVolume, int columnIndex)
        => caveMaskVolume.AsSpan(columnIndex * VoxelHelper.ChunkYSize, VoxelHelper.ChunkYSize);

    #region Helpers

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (Math.Abs(edge1 - edge0) < float.Epsilon)
        {
            return x >= edge1 ? 1f : 0f;
        }

        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// SIMD-accelerated 3D gradient noise sampling for a horizontal slice at constant Y.
    /// Uses NoiseDotNet's vectorized GradientNoise3D for ~8x speedup over scalar sampling.
    /// </summary>
    private static void SampleGradientNoiseSlice3D(
        Span<float> destination,
        Span<float> xCoords,
        Span<float> yCoords,
        Span<float> zCoords,
        float yCoord,
        float baseFrequency,
        uint seed,
        int octaves,
        float persistence,
        float lacunarity)
    {
        var count = destination.Length;

        // Fill Y coordinate buffer with constant value for this slice
        yCoords[..count].Fill(yCoord);

        destination.Clear();

        var amplitude = 1f;
        var frequency = baseFrequency;
        var totalAmplitude = 0f;

        for (var octave = 0; octave < octaves; octave++)
        {
            // Use SIMD-accelerated 3D gradient noise from NoiseDotNet
            Noise.GradientNoise3D(
                xCoords[..count],
                yCoords[..count],
                zCoords[..count],
                destination,
                frequency,
                frequency,
                frequency,
                amplitude,
                unchecked((int)(seed + (uint)(octave * 1013))));

            // Note: GradientNoise3D writes directly to destination with amplitude applied,
            // so for multi-octave FBM we need to accumulate differently.
            // Since GradientNoise3D overwrites the buffer, we need a scratch buffer for octaves > 1

            totalAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        // For single octave, GradientNoise3D output is in [-1, 1] range.
        // Normalize to [0, 1] for compatibility with existing terrain logic.
        if (totalAmplitude > 0f)
        {
            var inv = 1f / totalAmplitude;
            for (var i = 0; i < count; i++)
            {
                // GradientNoise3D returns [-1, 1], convert to [0, 1]
                destination[i] = (destination[i] * inv) * 0.5f + 0.5f;
            }
        }
    }

    /// <summary>
    /// SIMD-accelerated FBM (Fractal Brownian Motion) 3D noise sampling with multiple octaves.
    /// Properly accumulates octaves into destination buffer.
    /// </summary>
    private static void SampleFbmNoiseSlice3D(
        Span<float> destination,
        Span<float> xCoords,
        Span<float> yCoords,
        Span<float> zCoords,
        Span<float> scratchBuffer,
        float yCoord,
        float baseFrequency,
        uint seed,
        int octaves,
        float persistence,
        float lacunarity)
    {
        var count = destination.Length;

        // Fill Y coordinate buffer with constant value for this slice
        yCoords[..count].Fill(yCoord);

        destination.Clear();

        var amplitude = 1f;
        var frequency = baseFrequency;
        var totalAmplitude = 0f;

        for (var octave = 0; octave < octaves; octave++)
        {
            // Use SIMD-accelerated 3D gradient noise from NoiseDotNet
            Noise.GradientNoise3D(
                xCoords[..count],
                yCoords[..count],
                zCoords[..count],
                scratchBuffer[..count],
                frequency,
                frequency,
                frequency,
                1f,  // amplitude applied manually for proper accumulation
                unchecked((int)(seed + (uint)(octave * 1013))));

            // Accumulate octave contribution
            for (var i = 0; i < count; i++)
            {
                destination[i] += scratchBuffer[i] * amplitude;
            }

            totalAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        // Normalize and convert from [-1, 1] to [0, 1] range
        if (totalAmplitude > 0f)
        {
            var inv = 1f / totalAmplitude;
            for (var i = 0; i < count; i++)
            {
                destination[i] = (destination[i] * inv) * 0.5f + 0.5f;
            }
        }
    }

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

    private static bool TryGetColumnIndex(int wx, int wz, int chunkX, int chunkZ, out int columnIndex)
    {
        var localX = wx - chunkX * VoxelHelper.ChunkSideSize;
        var localZ = wz - chunkZ * VoxelHelper.ChunkSideSize;
        if ((uint)localX < VoxelHelper.ChunkSideSize && (uint)localZ < VoxelHelper.ChunkSideSize)
        {
            columnIndex = localZ * VoxelHelper.ChunkSideSize + localX;
            return true;
        }

        columnIndex = -1;
        return false;
    }

    private BlockId TryGenerateOre(int wx, int y, int wz)
    {
        var oreTypes = config.Ores.OreTypes;
        if (oreTypes == null || oreTypes.Count == 0)
            return BlockId.Air;

        var baseSeed = terrainParams.Seed + config.Ores.SeedOffset;

        foreach (var oreDef in oreTypes)
        {
            var minY = oreDef.MinY < 0 ? VoxelHelper.WaterLevel + oreDef.MinY : oreDef.MinY;
            var maxY = oreDef.MaxY < 0 ? VoxelHelper.WaterLevel + oreDef.MaxY : oreDef.MaxY;
            var peakY = oreDef.PeakY < 0 ? VoxelHelper.WaterLevel + oreDef.PeakY : oreDef.PeakY;

            if (y < minY || y > maxY)
                continue;

            var probability = oreDef.Rarity;
            if (oreDef.DistributionType == OreDistribution.Triangle)
            {
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

            var oreSeed = baseSeed + (uint)oreDef.OreBlock.GetId();
            var hash = OreHash3D(wx, y, wz, oreSeed);

            if (hash < probability)
            {
                return oreDef.OreBlock;
            }
        }

        return BlockId.Air;
    }

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

    private float SampleHeightSpline(float t)
    {
        var index = (int)(t * (HeightSplineResolution - 1));
        index = Math.Clamp(index, 0, HeightSplineResolution - 1);
        return heightSpline[index];
    }

    private static Span<float> GetColumnVolumeSpan(float[] volume, int columnIndex)
        => volume.AsSpan(columnIndex * VoxelHelper.ChunkYSize, VoxelHelper.ChunkYSize);

    private float GetTerrainDensity(float continentalness01, float baseHeight, float overhangNoise, float sampleY, float factor3D)
    {
        var tC = continentalness01;
        var density = baseHeight - sampleY;
        var shaping = config.TerrainShaping;

        if (tC > shaping.OverhangStartThreshold &&
            sampleY > baseHeight - terrainParams.OverhangDepthRange &&
            sampleY < baseHeight + terrainParams.OverhangHeightRange)
        {
            var mountainness = Smoothstep(shaping.OverhangStartThreshold, shaping.OverhangFullThreshold, tC);
            var heightFactor = 1f - MathF.Abs((sampleY - baseHeight) / terrainParams.OverhangFalloffRange);
            heightFactor = Math.Clamp(heightFactor, 0f, 1f);

            density += overhangNoise * terrainParams.OverhangAmplitude * shaping.OverhangMultiplier * mountainness * heightFactor * factor3D;
        }

        return density;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float Calculate3DFactor(float absWeirdness, float erosion01, TerrainShapingConfig shaping)
    {
        var weirdnessFactor = Smoothstep(shaping.Weirdness3DThresholdLow, shaping.Weirdness3DThresholdHigh, absWeirdness);
        var erosionFactor = 1f - Smoothstep(0f, shaping.Erosion3DThreshold, erosion01);
        var rawFactor = weirdnessFactor * erosionFactor;
        return Lerp(shaping.Min3DFactor, shaping.Max3DFactor, rawFactor);
    }

    private float ApplyCoastalLandSmoothing(float baseHeight, float continentalness01, TerrainShapingConfig shaping)
    {
        var coastStart = terrainParams.OceanThreshold;
        var coastEnd = terrainParams.OceanThreshold + Math.Max(0.01f, shaping.CoastalZoneWidth);
        var blend = Smoothstep(coastStart, coastEnd, continentalness01);
        var beachHeight = VoxelHelper.WaterLevel + MathF.Min(0.2f, shaping.BeachHeightOffset);
        return Lerp(beachHeight, baseHeight, blend);
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

    private float GetBeachNoise(float wx, float wz)
    {
        var scale = config.BeachNoiseScale;
        var seed = terrainParams.Seed + 8500u;

        var warpX = ValueNoise2D(wx * scale * 0.7f, wz * scale * 0.7f, seed + 100u) * 20f;
        var warpZ = ValueNoise2D(wx * scale * 0.7f + 100f, wz * scale * 0.7f, seed + 200u) * 20f;

        var noise = ValueNoise2D((wx + warpX) * scale, (wz + warpZ) * scale, seed);

        return noise - 0.5f;
    }

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

        var x0 = c00 + (c10 - c00) * u;
        var x1 = c01 + (c11 - c01) * u;

        return Lerp(x0, x1, v);
    }


    private static float Fade(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    private static float Hash2D(int x, int z, uint seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + z * 668265263);
            h ^= seed;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777216f;
        }
    }

    #endregion

    internal readonly record struct ChunkGenerationResult(
    ChunkCollisionData Collision,
    int[] SpanPairs,
    byte[] SpanCounts,
    BlockId[] SpanTypes,
    ChunkBiomeData? BiomeData)
    {
        public bool IsEmpty { get; init; } = false;
    }
}