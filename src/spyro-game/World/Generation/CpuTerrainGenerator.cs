using System;
using System.Collections.Generic;
using System.Numerics;
using NoiseDotNet;
using OpenRender;

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
    private ChunkBiomeData? currentChunkBiome;

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
    }

    /// <summary>
    /// Gets the biome data for the last generated chunk. 
    /// Call after GenerateChunk to retrieve the biome grid.
    /// </summary>
    public ChunkBiomeData? GetLastChunkBiomeData() => currentChunkBiome;

    /// <summary>
    /// Generate voxel descriptors and collision spans for the specified chunk.
    /// </summary>
    public ChunkGenerationResult GenerateChunk(int chunkIndex, Span<uint> destination, IReadOnlyDictionary<int, BlockId>? edits = null)
    {
        if (destination.Length < VoxelHelper.ChunkVoxelCount)
        {
            throw new ArgumentException($"Destination span must contain at least {VoxelHelper.ChunkVoxelCount} voxels", nameof(destination));
        }

        // Generate biome data for this chunk
        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        currentChunkBiome = biomeGenerator?.GenerateChunkBiomes(chunkX, chunkZ) ?? new ChunkBiomeData();

        var collision = new ChunkCollisionData();
        var spanPairs = new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        var spanTypes = new BlockId[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
        var spanCounts = new byte[VoxelHelper.ChunkSideSizeSquare];

        FillChunk(chunkIndex, destination, collision, spanPairs, spanTypes, spanCounts, edits);

        return new ChunkGenerationResult(collision, spanPairs, spanCounts, spanTypes);
    }

    private void FillChunk(
        int chunkIndex,
        Span<uint> voxels,
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
                        cheeseSlice,
                        spaghettiSlice,
                        overhangSlice);
                    var localIndex = y * VoxelHelper.ChunkSideSizeSquare + columnIndex;

                    if (edits != null && edits.TryGetValue(localIndex, out var editedBlock))
                    {
                        block = editedBlock;
                    }

                    voxels[localIndex] = (uint)block;

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

        // ChunkCollisionData.Spans populated inside TryCommitSpan
    }

    private void PrepareChunkCaches(int chunkX, int chunkZ)
    {
        BuildColumnCoordinates(chunkX, chunkZ);
        BuildColumnFieldCaches();
        UpdateBiomeDataFromTerrainValues(); // Fix: Use terrain's continentalness for biomes
        BuildColumnVolumes();
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
        var xSpan = columnWorldX.AsSpan();
        var zSpan = columnWorldZ.AsSpan();
        var warpInputX = scratch2DA.AsSpan();
        var warpInputZ = scratch2DB.AsSpan();
        var temp = scratch2DC.AsSpan();

        for (var i = 0; i < ColumnCount; i++)
        {
            warpInputX[i] = xSpan[i] * terrainParams.WarpScale;
            warpInputZ[i] = zSpan[i] * terrainParams.WarpScale;
        }

        SampleFbm2D(warpInputX, warpInputZ, 1f, terrainParams.Seed, 2, 0.5f, 2f, columnWarpX);

        for (var i = 0; i < ColumnCount; i++)
        {
            temp[i] = warpInputX[i] + 5.2f;
            warpInputZ[i] += 1.3f;
        }

        SampleFbm2D(temp, warpInputZ, 1f, terrainParams.Seed, 2, 0.5f, 2f, columnWarpZ);

        for (var i = 0; i < ColumnCount; i++)
        {
            columnWarpX[i] *= terrainParams.WarpStrength;
            columnWarpZ[i] *= terrainParams.WarpStrength;
        }

        var contX = scratch2DA.AsSpan();
        var contZ = scratch2DB.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            contX[i] = xSpan[i] + columnWarpX[i];
            contZ[i] = zSpan[i] + columnWarpZ[i];
        }

        SampleFbm2D(contX, contZ, terrainParams.ContinentalScale, terrainParams.Seed, 3, 0.5f, 2f, columnContinentalness);
        for (var i = 0; i < ColumnCount; i++)
        {
            columnContinentalness01[i] = columnContinentalness[i] * 0.5f + 0.5f;
        }

        SampleFbm2D(xSpan, zSpan, terrainParams.ErosionScale, terrainParams.Seed + 100u, 3, 0.5f, 2f, columnErosion);

        SampleFbm2D(xSpan, zSpan, terrainParams.RidgeScale, terrainParams.Seed + 200u, 3, 0.5f, 2f, scratch2DOutput.AsSpan());
        for (var i = 0; i < ColumnCount; i++)
        {
            columnPeaks[i] = 1f - MathF.Abs(scratch2DOutput[i]);
        }

        SampleFbm2D(xSpan, zSpan, terrainParams.CliffFrequency, terrainParams.Seed + 1500u, 4, 0.6f, 2.5f, columnCliff);

        // Sample additional coastal variation noise to break up the monotonous cliff pattern
        // Using larger wavelengths for smoother, more natural terrain
        var coastalVariation = scratch2DA.AsSpan();
        // Sample additional noise layers for terrain variety
        var terrainType = scratch2DA.AsSpan();      // Controls flat vs mountainous
        var cliffiness = scratch2DB.AsSpan();       // Controls cliff steepness
        SampleFbm2D(xSpan, zSpan, 1f / 400f, terrainParams.Seed + 3000u, 2, 0.5f, 2f, terrainType);
        SampleFbm2D(xSpan, zSpan, 1f / 150f, terrainParams.Seed + 3100u, 3, 0.6f, 2.2f, cliffiness);

        for (var i = 0; i < ColumnCount; i++)
        {
            var tC = columnContinentalness01[i];
            var baseHeight = SampleHeightSpline(tC) + VoxelHelper.WaterLevel;

            if (tC >= terrainParams.OceanThreshold)
            {
                var erosion = columnErosion[i];
                var erosion01 = erosion * 0.5f + 0.5f;
                
                // terrainType controls the character: <0.3 = flat plains, 0.3-0.7 = rolling hills, >0.7 = dramatic mountains
                var terrainTypeVal = terrainType[i] * 0.5f + 0.5f; // [0,1]
                var cliffVal = cliffiness[i];
                
                // Distance from coast (0 = at coast, 1 = deep inland)
                var coastDist = (tC - terrainParams.OceanThreshold) / (1f - terrainParams.OceanThreshold);
                
                // === COASTAL ZONE (0-20% inland) ===
                if (coastDist < 0.2f)
                {
                    var coastFactor = coastDist / 0.2f; // 0 at shore, 1 at end of coastal zone
                    
                    // Some coasts are beaches (low cliffiness), some are cliffs (high cliffiness)
                    var isCliffyCoast = cliffVal > 0.3f;
                    
                    if (!isCliffyCoast)
                    {
                        // Gentle beach - flatten toward water
                        var beachFlatten = (1f - coastFactor) * (1f - MathF.Abs(cliffVal));
                        baseHeight = Lerp(VoxelHelper.WaterLevel + 2f, baseHeight, coastFactor + beachFlatten * 0.5f);
                    }
                    else
                    {
                        // Cliffy coast - can have steep drop into water
                        var cliffStrength = (cliffVal - 0.3f) / 0.7f; // 0-1 for cliff intensity
                        baseHeight += cliffStrength * 15f * (1f - coastFactor);
                    }
                }
                
                // === TERRAIN TYPE MODULATION ===
                if (terrainTypeVal < 0.35f)
                {
                    // FLAT PLAINS - very little height variation
                    var flatness = (0.35f - terrainTypeVal) / 0.35f;
                    baseHeight += columnPeaks[i] * 5f * (1f - flatness);
                }
                else if (terrainTypeVal < 0.65f)
                {
                    // ROLLING HILLS - moderate variation
                    var hilliness = (terrainTypeVal - 0.35f) / 0.3f;
                    baseHeight += columnPeaks[i] * 25f * hilliness;
                    baseHeight += columnCliff[i] * 8f * hilliness;
                }
                else
                {
                    // DRAMATIC TERRAIN - mountains, cliffs, plateaus
                    var drama = (terrainTypeVal - 0.65f) / 0.35f;
                    
                    // Strong peaks and valleys
                    baseHeight += columnPeaks[i] * 50f * drama;
                    
                    // Cliff features - can create sudden height changes
                    var cliffContrib = MathF.Abs(columnCliff[i]) * 40f * drama;
                    
                    // Some areas get plateaus (flat tops), some get peaks
                    if (cliffVal > 0.5f)
                    {
                        // Sharp cliff edges
                        baseHeight += cliffContrib;
                    }
                    else if (cliffVal < -0.3f)
                    {
                        // Plateau - flat area then sudden drop
                        var plateauHeight = baseHeight + 30f * drama;
                        baseHeight = MathF.Max(baseHeight, plateauHeight - MathF.Abs(columnCliff[i]) * 60f);
                    }
                    else
                    {
                        // Gradual mountain slopes
                        baseHeight += cliffContrib * 0.5f;
                    }
                }
                
                // === MOUNTAIN ZONES (high continentalness) ===
                if (tC > terrainParams.MountainThreshold)
                {
                    var mountainness = Smoothstep(terrainParams.MountainThreshold, 0.92f, tC);
                    
                    // Mountains get dramatic height boost
                    baseHeight += 80f * mountainness;
                    
                    // Additional cliff detail in mountains
                    baseHeight += MathF.Abs(columnCliff[i]) * terrainParams.CliffAmplitude * mountainness;
                    
                    // Some mountain regions get extra peaks for alpine zones
                    if (terrainTypeVal > 0.5f)
                    {
                        baseHeight += columnPeaks[i] * 60f * mountainness;
                    }
                }
                
                // === EROSION SMOOTHING (applies globally) ===
                // High erosion areas are smoother - dampens all height variations
                var smoothingFactor = erosion01 * erosion01 * 0.3f;
                baseHeight = Lerp(baseHeight, SampleHeightSpline(tC) + VoxelHelper.WaterLevel + 20f, smoothingFactor);
                
                // === BIOME-BASED HEIGHT MODULATION ===
                // Use the stored biome data to further shape terrain
                baseHeight = ApplyBiomeHeightModulation(baseHeight, i);
            }

            columnHeights[i] = baseHeight;
            var rounded = (int)MathF.Round(baseHeight);
            columnHeightInts[i] = Math.Clamp(rounded, 0, VoxelHelper.ChunkYSize - 1);
        }
    }

    /// <summary>
    /// Updates the biome data using the terrain-computed continentalness values.
    /// This ensures biome selection uses the SAME noise values as terrain height.
    /// The biome data was pre-generated with separate noise sampling, but ocean/land
    /// classification must match the actual terrain height.
    /// </summary>
    private void UpdateBiomeDataFromTerrainValues()
    {
        if (currentChunkBiome == null) return;
        
        // For each 4x4 biome cell, update biome based on terrain values
        for (var cellZ = 0; cellZ < ChunkBiomeData.GridSize; cellZ++)
        {
            for (var cellX = 0; cellX < ChunkBiomeData.GridSize; cellX++)
            {
                var cellIndex = cellZ * ChunkBiomeData.GridSize + cellX;
                
                // Sample noise at cell center for climate values
                var centerLocalX = cellX * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                var centerLocalZ = cellZ * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                var centerColumnIndex = centerLocalZ * VoxelHelper.ChunkSideSize + centerLocalX;
                
                // Use terrain's actual continentalness (this is what determines terrain height!)
                var terrainCont = columnContinentalness[centerColumnIndex];
                var terrainCont01 = columnContinentalness01[centerColumnIndex];
                var terrainErosion = columnErosion[centerColumnIndex];
                var terrainPeaks = columnPeaks[centerColumnIndex];
                
                // Update the biome's stored values to match terrain
                currentChunkBiome.Continentalness[cellIndex] = terrainCont;
                currentChunkBiome.Erosion[cellIndex] = terrainErosion;
                currentChunkBiome.PeaksValleys[cellIndex] = terrainPeaks;
                
                // For ocean detection, find the MAXIMUM terrain height in the entire 4x4 cell
                // This prevents cells with some above-water blocks from being classified as ocean
                var maxTerrainHeight = float.MinValue;
                var cellStartX = cellX * ChunkBiomeData.BlocksPerCell;
                var cellStartZ = cellZ * ChunkBiomeData.BlocksPerCell;
                
                for (var dz = 0; dz < ChunkBiomeData.BlocksPerCell; dz++)
                {
                    for (var dx = 0; dx < ChunkBiomeData.BlocksPerCell; dx++)
                    {
                        var colIdx = (cellStartZ + dz) * VoxelHelper.ChunkSideSize + (cellStartX + dx);
                        maxTerrainHeight = MathF.Max(maxTerrainHeight, columnHeights[colIdx]);
                    }
                }
                
                // Re-select biome based on terrain's actual values
                // Use maxTerrainHeight so any above-water block prevents ocean classification
                var temp01 = currentChunkBiome.Temperature[cellIndex];
                var humid01 = currentChunkBiome.Humidity[cellIndex];
                var erosion01 = terrainErosion * 0.5f + 0.5f;
                var pv01 = terrainPeaks; // Already 0-1 range after ridge transform
                
                currentChunkBiome.BiomeIds[cellIndex] = SelectBiomeFromTerrainValues(
                    terrainCont01, temp01, humid01, erosion01, pv01, maxTerrainHeight);
            }
        }
    }

    /// <summary>
    /// Select biome using terrain-computed values including actual terrain height.
    /// </summary>
    private BiomeId SelectBiomeFromTerrainValues(
        float cont01, float temperature, float humidity, float erosion01, float pv01, float actualTerrainHeight)
    {
        // Use ACTUAL terrain height (after all modifiers) to determine ocean vs land
        // This is the definitive check - if terrain surface is below water, it's ocean
        var isUnderwater = actualTerrainHeight < VoxelHelper.WaterLevel;
        var altitudeAboveWater = actualTerrainHeight - VoxelHelper.WaterLevel;
        
        // ============ OCEAN BIOMES (based on actual terrain height) ============
        if (isUnderwater)
        {
            // Use continentalness to distinguish deep ocean from regular ocean
            if (cont01 < terrainParams.DeepOceanThreshold)
                return BiomeId.DeepOcean;
            return BiomeId.Ocean;
        }
        
        // ============ BEACH BIOME ============
        // Beach is determined primarily by HEIGHT, not just noise values.
        // Any terrain at or very close to water level (within ShorelineRange) is beach.
        // This ensures consistent sand/beach appearance at shorelines regardless of other biome factors.
        if (altitudeAboveWater <= terrainParams.ShorelineRange)
        {
            return BiomeId.Beach;
        }
        
        // ============ ALPINE BIOME ============
        // Use actual terrain height for altitude-based decisions
        if (altitudeAboveWater > terrainParams.AlpineElevation || temperature < 0.12f)
            return BiomeId.Alpine;
        
        // ============ MOUNTAIN/HIGHLANDS ============
        if (pv01 > 0.7f && erosion01 < 0.4f)
            return temperature < 0.35f ? BiomeId.Alpine : BiomeId.Highlands;
        
        // ============ CLIMATE-BASED LAND BIOMES ============
        if (temperature > 0.65f)
        {
            if (humidity < 0.30f) return BiomeId.Desert;
            if (humidity < 0.55f) return BiomeId.Savanna;
            return BiomeId.Rainforest;
        }
        
        if (temperature < 0.35f)
        {
            if (humidity > 0.50f) return BiomeId.Taiga;
            return BiomeId.Tundra;
        }
        
        // Temperate biomes
        if (humidity > 0.55f)
        {
            if (cont01 < 0.50f && erosion01 > 0.6f)
                return BiomeId.Swamp;
            return BiomeId.Taiga;
        }
        
        if (pv01 > 0.55f || erosion01 < 0.45f)
            return BiomeId.Highlands;
        
        return BiomeId.Plains;
    }

    /// <summary>
    /// Apply height modulation using biome definitions with smooth blending.
    /// 
    /// This follows Minecraft's approach:
    /// - Biome determines base_height and height_variation attributes
    /// - Climate parameters (C/E/PV) provide smooth blending weights
    /// - Adjacent biomes blend their height attributes at transitions
    /// - The continuous noise ensures no chunk boundary artifacts
    /// 
    /// KEY INSIGHT: We DON'T use discrete biome lookup for height calculation.
    /// Instead, we blend height attributes from ALL biomes weighted by how well
    /// the current climate values match each biome's preferred climate range.
    /// This ensures perfectly smooth transitions even at chunk boundaries.
    /// 
    /// IMPORTANT: For ocean areas, this method takes FULL CONTROL of height to avoid
    /// the atoll pattern caused by double-applying ocean logic.
    /// </summary>
    private float ApplyBiomeHeightModulation(float baseHeight, int columnIndex)
    {
        // Use the terrain generator's per-column climate values - these are CONTINUOUS
        // because they're sampled from world coordinates, not chunk-local data
        var c01 = columnContinentalness01[columnIndex];  // 0 = ocean, 1 = inland
        var erosion = columnErosion[columnIndex];        // Raw [-1,1] erosion value
        var pv = columnPeaks[columnIndex];               // 0-1 peaks value
        var e01 = erosion * 0.5f + 0.5f;                 // Normalize erosion to [0,1]
        
        // Get temperature and humidity for this column
        // We estimate them from continentalness and peaks (simplified)
        var temp01 = 0.5f + c01 * 0.15f - pv * 0.2f;  // Warmer inland, cooler at peaks
        var humid01 = 0.5f - (c01 - 0.5f) * 0.3f;     // Drier inland
        temp01 = Math.Clamp(temp01, 0f, 1f);
        humid01 = Math.Clamp(humid01, 0f, 1f);
        
        // === SIMPLIFIED OCEAN/LAND HEIGHT ===
        // Instead of blending biome height attributes (which caused atolls),
        // use continentalness DIRECTLY to control ocean vs land height.
        // This is the Minecraft approach: continentalness IS the primary height driver.
        
        // Ocean depth: deep ocean at low c01, shallow near coast
        // Land height: gradually increases with continentalness
        float targetHeight;
        
        if (c01 < 0.35f)
        {
            // === OCEAN ZONE ===
            // Continentalness 0.0 = deepest ocean, 0.35 = shallow ocean floor
            var oceanDepth = (0.35f - c01) / 0.35f;  // 1.0 at deep, 0.0 at coast
            var oceanFloor = VoxelHelper.WaterLevel - 5f - oceanDepth * 25f;  // 5-30 blocks below water
            
            // Add some underwater terrain variation
            var underwaterVariation = (pv - 0.5f) * 10f * (1f - oceanDepth);
            targetHeight = oceanFloor + underwaterVariation;
        }
        else if (c01 < 0.45f)
        {
            // === COASTAL TRANSITION ===
            // Smooth ramp from ocean floor to beach/low land
            var coastProgress = (c01 - 0.35f) / 0.1f;  // 0.0 at ocean edge, 1.0 at land
            var oceanFloor = VoxelHelper.WaterLevel - 5f;
            var beachLevel = VoxelHelper.WaterLevel + 3f;
            
            // Smooth transition from ocean to beach
            targetHeight = Lerp(oceanFloor, beachLevel, Smoothstep(0f, 1f, coastProgress));
            
            // Very minimal variation in coastal zone for smooth beaches
            var coastalVariation = (pv - 0.5f) * 4f * coastProgress;
            targetHeight += coastalVariation;
        }
        else
        {
            // === LAND ZONE ===
            // Use biome blending for land areas only
            var blendedBaseHeight = 0f;
            var blendedHeightVariation = 0f;
            var blendedPeaksInfluence = 0f;
            var blendedErosionSensitivity = 0f;
            var totalWeight = 0f;
            
            foreach (var biomeDef in config.Biomes)
            {
                // Skip ocean biomes for land calculation
                if (biomeDef.AllowedTerrain == TerrainType.OceanOnly) continue;
                
                // Calculate how well this biome matches the current climate
                var weight = CalculateBiomeWeight(biomeDef, c01, temp01, humid01, e01, pv, baseHeight);
                
                if (weight > 0.001f)
                {
                    blendedBaseHeight += biomeDef.BaseHeight * weight;
                    blendedHeightVariation += biomeDef.HeightVariation * weight;
                    blendedPeaksInfluence += biomeDef.PeaksInfluence * weight;
                    blendedErosionSensitivity += biomeDef.ErosionSensitivity * weight;
                    totalWeight += weight;
                }
            }
            
            // Normalize blended values
            if (totalWeight > 0.001f)
            {
                blendedBaseHeight /= totalWeight;
                blendedHeightVariation /= totalWeight;
                blendedPeaksInfluence /= totalWeight;
                blendedErosionSensitivity /= totalWeight;
            }
            else
            {
                // Fallback to plains-like defaults
                blendedBaseHeight = 12f;
                blendedHeightVariation = 8f;
                blendedPeaksInfluence = 0.2f;
                blendedErosionSensitivity = 0.8f;
            }
            
            // Calculate land height from blended biome attributes
            targetHeight = VoxelHelper.WaterLevel + blendedBaseHeight;
            
            // Apply peaks/valleys variation
            var pvOffset = (pv - 0.5f) * 2f;  // Convert 0-1 to -1..+1
            var pvContribution = pvOffset * blendedHeightVariation * blendedPeaksInfluence;
            targetHeight += pvContribution;
            
            // Erosion smoothing
            var erosionTarget = VoxelHelper.WaterLevel + blendedBaseHeight;
            var erosionStrength = e01 * blendedErosionSensitivity;
            targetHeight = Lerp(targetHeight, erosionTarget, erosionStrength * 0.4f);
            
            // Gradual height increase further inland (prevents flat land)
            var inlandBoost = (c01 - 0.45f) / 0.55f;  // 0 at coast, 1 at max inland
            targetHeight += inlandBoost * 10f * (1f - blendedErosionSensitivity);
        }
        
        // Blend with original noise-based height for high-frequency detail
        // Use less blending in ocean (we want clean ocean floors)
        var blendFactor = c01 < 0.4f ? 0.3f : 0.5f;
        var finalHeight = Lerp(baseHeight, targetHeight, 1f - blendFactor);
        
        return finalHeight;
    }
    
    /// <summary>
    /// Calculate how strongly a biome should influence the terrain at given climate values.
    /// Returns a weight [0,1] based on how well climate matches biome's preferred range.
    /// </summary>
    private float CalculateBiomeWeight(BiomeDefinition biome, float cont01, float temp01, 
        float humid01, float erosion01, float pv01, float currentHeight)
    {
        var weight = 1f;
        
        // === TERRAIN TYPE FILTERING ===
        // Ocean biomes only apply in low continentalness
        if (biome.AllowedTerrain == TerrainType.OceanOnly)
        {
            if (cont01 > 0.4f) return 0f;
            weight *= 1f - Smoothstep(0.25f, 0.4f, cont01);
        }
        // Mountain biomes only apply in high continentalness + high peaks
        else if (biome.AllowedTerrain == TerrainType.MountainOnly)
        {
            if (cont01 < 0.6f || pv01 < 0.5f) return 0f;
            weight *= Smoothstep(0.6f, 0.8f, cont01) * Smoothstep(0.5f, 0.75f, pv01);
        }
        // Land biomes avoid deep ocean
        else if (biome.AllowedTerrain == TerrainType.LandOnly)
        {
            if (cont01 < 0.35f) return 0f;
            weight *= Smoothstep(0.35f, 0.5f, cont01);
        }
        
        // === CLIMATE MATCHING ===
        // How well does temperature match this biome's range?
        var tempMatch = CalculateRangeMatch(temp01, biome.Temperature.Min, biome.Temperature.Max);
        weight *= tempMatch;
        
        // How well does humidity match this biome's range?
        var humidMatch = CalculateRangeMatch(humid01, biome.Humidity.Min, biome.Humidity.Max);
        weight *= humidMatch;
        
        // === PRIORITY BOOST ===
        // Higher priority biomes get a boost (Ocean=100, Alpine=90, others=50)
        weight *= 1f + biome.Priority * 0.005f;
        
        return weight;
    }
    
    /// <summary>
    /// Calculate how well a value matches a range, with smooth falloff.
    /// Returns 1.0 inside range, smoothly falls to 0 at distance 0.3 outside.
    /// </summary>
    private static float CalculateRangeMatch(float value, float min, float max)
    {
        const float falloffDistance = 0.25f;
        
        if (value >= min && value <= max)
        {
            return 1f;
        }
        
        if (value < min)
        {
            var dist = min - value;
            return 1f - Smoothstep(0f, falloffDistance, dist);
        }
        else
        {
            var dist = value - max;
            return 1f - Smoothstep(0f, falloffDistance, dist);
        }
    }

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
    private static float FlattenTowards(float height, float target, float strength)
    {
        return Lerp(height, target, strength);
    }

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
    /// Interpolate sparse 3D noise samples into full-resolution volumes using trilinear interpolation.
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

                for (var y = 0; y < chunkY; y++)
                {
                    var sliceOffset = columnIndex * chunkY + y;

                    // Find sparse Y indices and interpolation factor
                    var sy0 = y / SparseStep;
                    var sy1 = Math.Min(sy0 + 1, SparseSamplesY - 1);
                    var ty = (y % SparseStep) / (float)SparseStep;

                    var yOffset0 = sy0 * SparseSampleCount;
                    var yOffset1 = sy1 * SparseSampleCount;

                    // Trilinear interpolate cheese
                    var cheeseRaw = TrilinearSample(sparseCheeseGrid, idx00, idx10, idx01, idx11, yOffset0, yOffset1, tx, ty, tz);
                    var cheeseSample = (cheeseRaw * 2f - 1f) * terrainParams.CheeseAmplitude;
                    cheeseVolume[sliceOffset] = Math.Clamp(cheeseSample, -1f, 1f);

                    // Trilinear interpolate spaghetti channels
                    var n1Raw = TrilinearSample(sparseSpaghettiA, idx00, idx10, idx01, idx11, yOffset0, yOffset1, tx, ty, tz);
                    var n2Raw = TrilinearSample(sparseSpaghettiB, idx00, idx10, idx01, idx11, yOffset0, yOffset1, tx, ty, tz);
                    var n1 = n1Raw * 2f - 1f;
                    var n2 = n2Raw * 2f - 1f;
                    var dist = MathF.Sqrt(n1 * n1 + n2 * n2);
                    var amp = Math.Clamp(terrainParams.SpaghettiAmplitude, 0.2f, 4f);
                    var ampT = (amp - 0.2f) / 3.8f;
                    var widthFactor = Lerp(2.8f, 1.1f, ampT);
                    var tunnelWidth = 1f - dist * widthFactor;
                    spaghettiVolume[sliceOffset] = Math.Clamp(tunnelWidth, -1f, 1f);

                    // Trilinear interpolate overhang
                    var overhangRaw = TrilinearSample(sparseOverhangGrid, idx00, idx10, idx01, idx11, yOffset0, yOffset1, tx, ty, tz);
                    var overhangSample = (overhangRaw * 2f - 1f) * terrainParams.OverhangAmplitude;
                    overhangVolume[sliceOffset] = Math.Clamp(overhangSample, -1f, 1f);
                }
            }
        }
    }

    /// <summary>
    /// Trilinear interpolation from 8 sparse grid corners.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float TrilinearSample(
        float[] grid,
        int idx00, int idx10, int idx01, int idx11,
        int yOffset0, int yOffset1,
        float tx, float ty, float tz)
    {
        // Sample 8 corners
        var c000 = grid[yOffset0 + idx00];
        var c100 = grid[yOffset0 + idx10];
        var c010 = grid[yOffset0 + idx01];
        var c110 = grid[yOffset0 + idx11];
        var c001 = grid[yOffset1 + idx00];
        var c101 = grid[yOffset1 + idx10];
        var c011 = grid[yOffset1 + idx01];
        var c111 = grid[yOffset1 + idx11];

        // Interpolate along X
        var x00 = c000 + (c100 - c000) * tx;
        var x10 = c010 + (c110 - c010) * tx;
        var x01 = c001 + (c101 - c001) * tx;
        var x11 = c011 + (c111 - c011) * tx;

        // Interpolate along Z
        var z0 = x00 + (x10 - x00) * tz;
        var z1 = x01 + (x11 - x01) * tz;

        // Interpolate along Y
        return z0 + (z1 - z0) * ty;
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
        Span<float> cheeseSlice,
        Span<float> spaghettiSlice,
        Span<float> overhangSlice)
    {
        if (y > height)
        {
            return y <= VoxelHelper.WaterLevel ? BlockId.Water : BlockId.Air;
        }

        var tC = continentalness01;
        var isLand = tC >= terrainParams.OceanThreshold;

        if (isLand && tC > terrainParams.MountainThreshold && y > height - 50 && y > VoxelHelper.WaterLevel + 20)
        {
            var density = GetTerrainDensity(continentalness01, baseHeight, overhangSlice[y], y);
            if (density < 0f)
            {
                return BlockId.Air;
            }
        }

        if (isLand && y > 0)
        {
            var depth = height - y;
            var slope = depth < 15 ? GetSlope(wx, wz) : 0f;
            if (IsCave(depth, slope, cheeseSlice[y], spaghettiSlice[y]))
            {
                return y <= VoxelHelper.WaterLevel && y <= VoxelHelper.WaterLevel + terrainParams.CaveFloodExtension ? BlockId.Water : BlockId.Air;
            }
        }

        // Get the biome for this position to determine block types
        // IMPORTANT: Block types are fully determined by biome - no overrides!
        // This ensures deterministic block types for inventory/block-breaking.
        var biomeId = currentChunkBiome?.GetBiomeAt(localX, localZ) ?? BiomeId.Plains;
        var biomeDef = GetBiomeDefinition((int)biomeId);
        
        // If biome definition is missing, log warning and use fallback
        // This should not happen in production - all BiomeIds should have definitions
        if (biomeDef == null)
        {
            Log.Warn($"Missing BiomeDefinition for biomeId={biomeId}, using fallback blocks");
        }
        
        var isOceanBiome = biomeId == BiomeId.Ocean || biomeId == BiomeId.DeepOcean;
        var isUnderwater = y <= VoxelHelper.WaterLevel && (isOceanBiome || height < VoxelHelper.WaterLevel);
        
        // Surface block - determined entirely by biome
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
        if (depthBelowSurface <= terrainParams.SubsurfaceDepth)
        {
            if (isUnderwater)
            {
                return biomeDef?.UnderwaterSubsurfaceBlock ?? BlockId.Stone;
            }
            return biomeDef?.SubsurfaceBlock ?? BlockId.Dirt;
        }
        
        // Deep blocks - use biome's deep block
        return biomeDef?.DeepBlock ?? BlockId.Stone;
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

    private float GetTerrainDensity(float continentalness01, float baseHeight, float overhangNoise, float sampleY)
    {
        var tC = continentalness01;
        var density = baseHeight - sampleY;

        if (tC > terrainParams.MountainThreshold &&
            sampleY > baseHeight - terrainParams.OverhangDepthRange &&
            sampleY < baseHeight + terrainParams.OverhangHeightRange)
        {
            var mountainness = Smoothstep(terrainParams.MountainThreshold, 1f, tC);
            var heightFactor = 1f - MathF.Abs((sampleY - baseHeight) / terrainParams.OverhangFalloffRange);
            heightFactor = Math.Clamp(heightFactor, 0f, 1f);
            density += overhangNoise * terrainParams.OverhangAmplitude * mountainness * heightFactor;
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
        return SampleFbm2DSingle(p + warp, terrainParams.ContinentalScale, terrainParams.Seed, 3, 0.5f, 2f);
    }

    private float GetErosion(Vector2 p)
        => SampleFbm2DSingle(p, terrainParams.ErosionScale, terrainParams.Seed + 100u, 3, 0.5f, 2f);

    private float GetPeaksValleys(Vector2 p)
    {
        var n = SampleFbm2DSingle(p, terrainParams.RidgeScale, terrainParams.Seed + 200u, 3, 0.5f, 2f);
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

    #endregion

    internal readonly record struct ChunkGenerationResult(
        ChunkCollisionData Collision,
        int[] SpanPairs,
        byte[] SpanCounts,
        BlockId[] SpanTypes);
}


