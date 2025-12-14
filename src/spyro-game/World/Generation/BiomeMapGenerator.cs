using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SpyroGame.World.Generation;

/// <summary>
/// Generates color-coded biome maps for debugging terrain generation.
/// Outputs BMP files showing biome distribution across the world.
/// </summary>
public static class BiomeMapGenerator
{
    /// <summary>
    /// Color palette for each biome type.
    /// Colors chosen to be visually distinct and meaningful.
    /// </summary>
    private static readonly Dictionary<BiomeId, Rgba32> BiomeColors = new()
    {
        [BiomeId.Ocean] = new Rgba32(0, 0, 139),        // Dark blue
        [BiomeId.DeepOcean] = new Rgba32(0, 0, 80),     // Very dark blue
        [BiomeId.Beach] = new Rgba32(238, 214, 175),    // Sandy beige
        [BiomeId.Plains] = new Rgba32(141, 179, 96),    // Light green
        [BiomeId.Savanna] = new Rgba32(189, 183, 107),  // Khaki/tan
        [BiomeId.Desert] = new Rgba32(237, 201, 175),   // Sand color
        [BiomeId.Rainforest] = new Rgba32(34, 139, 34), // Forest green
        [BiomeId.Taiga] = new Rgba32(47, 79, 79),       // Dark slate gray
        [BiomeId.Tundra] = new Rgba32(176, 196, 222),   // Light steel blue
        [BiomeId.Highlands] = new Rgba32(107, 142, 35), // Olive drab
        [BiomeId.Alpine] = new Rgba32(255, 250, 250),   // Snow white
        [BiomeId.River] = new Rgba32(65, 105, 225),     // Royal blue
        [BiomeId.Swamp] = new Rgba32(85, 107, 47),      // Dark olive green
        [BiomeId.Lake] = new Rgba32(70, 130, 180),      // Steel blue
        [BiomeId.Unknown] = new Rgba32(255, 0, 255),    // Magenta (error)
    };

    /// <summary>
    /// Generate a biome map centered on the given world position.
    /// Each pixel represents one block column in the world.
    /// </summary>
    /// <param name="config">Terrain configuration.</param>
    /// <param name="centerX">Center X world coordinate.</param>
    /// <param name="centerZ">Center Z world coordinate.</param>
    /// <param name="radiusBlocks">Radius in blocks from center (map will be 2*radius x 2*radius).</param>
    /// <param name="outputPath">Output file path for the BMP.</param>
    /// <param name="includeClimateOverlay">If true, adds climate data as overlay.</param>
    public static void GenerateBiomeMap(
        TerrainConfig config,
        int centerX,
        int centerZ,
        int radiusBlocks,
        string outputPath,
        bool includeClimateOverlay = false) =>
        // Use downsampled version for efficiency (1 pixel = 4x4 blocks)
        GenerateBiomeMapDownsampled(config, centerX, centerZ, radiusBlocks, outputPath, 4, includeClimateOverlay);

    /// <summary>
    /// Generate a downsampled biome map for faster generation of large areas.
    /// Uses dominant biome selection within each cell.
    /// </summary>
    /// <param name="config">Terrain configuration.</param>
    /// <param name="centerX">Center X world coordinate.</param>
    /// <param name="centerZ">Center Z world coordinate.</param>
    /// <param name="radiusBlocks">Radius in blocks from center.</param>
    /// <param name="outputPath">Output file path for the BMP.</param>
    /// <param name="cellSize">Blocks per pixel (e.g., 4 = 4x4 blocks per pixel).</param>
    /// <param name="includeClimateOverlay">If true, adds climate data as overlay.</param>
    public static void GenerateBiomeMapDownsampled(
        TerrainConfig config,
        int centerX,
        int centerZ,
        int radiusBlocks,
        string outputPath,
        int cellSize = 4,
        bool includeClimateOverlay = false,
        bool mirrorX = false)
    {
        var sizeInBlocks = radiusBlocks * 2;
        var imageSize = sizeInBlocks / cellSize;
        var startX = centerX - radiusBlocks;
        var startZ = centerZ - radiusBlocks;

        var biomeSelector = new BiomeSelector(config.Biomes);
        //biomeSelector.UpdateConfig(config);

        using var image = new Image<Rgba32>(imageSize, imageSize);
        var climateCache = new ChunkClimateCache();

        OpenRender.Log.Info($"Generating downsampled biome map: {imageSize}x{imageSize} ({cellSize}x{cellSize} blocks/pixel) centered at ({centerX}, {centerZ})...");

        var biomeStats = new Dictionary<BiomeId, int>();
        foreach (var biome in Enum.GetValues<BiomeId>())
        {
            biomeStats[biome] = 0;
        }

        // Biome vote counter for dominant biome selection
        Span<int> biomeVotes = stackalloc int[16]; // Max 16 biome types

        var chunksPerSide = (sizeInBlocks + VoxelHelper.ChunkSideSize - 1) / VoxelHelper.ChunkSideSize + 1;
        var startChunkX = startX / VoxelHelper.ChunkSideSize;
        var startChunkZ = startZ / VoxelHelper.ChunkSideSize;

        for (var cz = 0; cz < chunksPerSide; cz++)
        {
            for (var cx = 0; cx < chunksPerSide; cx++)
            {
                var chunkX = startChunkX + cx;
                var chunkZ = startChunkZ + cz;

                climateCache.SampleForChunk(chunkX, chunkZ, config);

                var chunkWorldX = chunkX * VoxelHelper.ChunkSideSize;
                var chunkWorldZ = chunkZ * VoxelHelper.ChunkSideSize;

                var cont01 = climateCache.Continentalness01;
                var temp01 = climateCache.Temperature01;
                var humid01 = climateCache.Humidity01;
                var erosion01 = climateCache.Erosion01;
                var pv01 = climateCache.PeaksValleys01;

                // Process cells within this chunk
                for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz += cellSize)
                {
                    var worldZ = chunkWorldZ + lz;
                    var imageZ = (worldZ - startZ) / cellSize;
                    if (imageZ < 0 || imageZ >= imageSize) continue;

                    for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx += cellSize)
                    {
                        var worldX = chunkWorldX + lx;
                        var imageX = (worldX - startX) / cellSize;
                        if (imageX < 0 || imageX >= imageSize) continue;

                        // Find dominant biome in this cell by voting
                        biomeVotes.Clear();
                        var avgTemp = 0f;

                        for (var dz = 0; dz < cellSize && lz + dz < VoxelHelper.ChunkSideSize; dz++)
                        {
                            for (var dx = 0; dx < cellSize && lx + dx < VoxelHelper.ChunkSideSize; dx++)
                            {
                                var columnIndex = (lz + dz) * VoxelHelper.ChunkSideSize + (lx + dx);
                                var biome = biomeSelector.Select(
                                    cont01[columnIndex],
                                    temp01[columnIndex],
                                    humid01[columnIndex],
                                    erosion01[columnIndex],
                                    pv01[columnIndex]);

                                var biomeIdx = (int)biome;
                                if (biomeIdx < biomeVotes.Length)
                                    biomeVotes[biomeIdx]++;

                                avgTemp += temp01[columnIndex];
                            }
                        }

                        // Find winning biome
                        var maxVotes = 0;
                        var dominantBiome = BiomeId.Plains;
                        for (var i = 0; i < biomeVotes.Length; i++)
                        {
                            if (biomeVotes[i] > maxVotes)
                            {
                                maxVotes = biomeVotes[i];
                                dominantBiome = (BiomeId)i;
                            }
                        }

                        var color = BiomeColors.GetValueOrDefault(dominantBiome, BiomeColors[BiomeId.Unknown]);

                        if (includeClimateOverlay)
                        {
                            avgTemp /= cellSize * cellSize;
                            var tempTint = (byte)(avgTemp * 30);
                            color = new Rgba32(
                                (byte)Math.Min(255, color.R + tempTint),
                                color.G,
                                (byte)Math.Min(255, color.B + (30 - tempTint)));
                        }

                        var writeX = mirrorX ? (imageSize - 1 - imageX) : imageX;
                        image[writeX, imageSize - 1 - imageZ] = color;
                        biomeStats[dominantBiome]++;
                    }
                }
            }
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        image.SaveAsBmp(outputPath);

        var totalPixels = imageSize * imageSize;
        OpenRender.Log.Info($"Biome map saved to: {outputPath}");
        OpenRender.Log.Info("Biome distribution:");
        foreach (var (biome, count) in biomeStats.OrderByDescending(kv => kv.Value))
        {
            if (count > 0)
            {
                var percentage = count * 100.0 / totalPixels;
                OpenRender.Log.Info($"  {biome}: {count:N0} ({percentage:F1}%)");
            }
        }
    }

    /// <summary>
    /// Generate a full-resolution biome map (1 pixel = 1 block).
    /// Use for small areas or when precise boundaries are needed.
    /// </summary>
    public static void GenerateBiomeMapFullRes(
        TerrainConfig config,
        int centerX,
        int centerZ,
        int radiusBlocks,
        string outputPath,
        bool includeClimateOverlay = false)
    {
        var size = radiusBlocks * 2;
        var startX = centerX - radiusBlocks;
        var startZ = centerZ - radiusBlocks;

        var biomeSelector = new BiomeSelector(config.Biomes);
        //biomeSelector.UpdateConfig(config);

        using var image = new Image<Rgba32>(size, size);
        var climateCache = new ChunkClimateCache();

        OpenRender.Log.Info($"Generating full-res biome map: {size}x{size} centered at ({centerX}, {centerZ})...");

        var biomeStats = new Dictionary<BiomeId, int>();
        foreach (var biome in Enum.GetValues<BiomeId>())
        {
            biomeStats[biome] = 0;
        }

        var chunksPerSide = (size + VoxelHelper.ChunkSideSize - 1) / VoxelHelper.ChunkSideSize + 1;
        var startChunkX = startX / VoxelHelper.ChunkSideSize;
        var startChunkZ = startZ / VoxelHelper.ChunkSideSize;

        for (var cz = 0; cz < chunksPerSide; cz++)
        {
            for (var cx = 0; cx < chunksPerSide; cx++)
            {
                var chunkX = startChunkX + cx;
                var chunkZ = startChunkZ + cz;

                climateCache.SampleForChunk(chunkX, chunkZ, config);

                var chunkWorldX = chunkX * VoxelHelper.ChunkSideSize;
                var chunkWorldZ = chunkZ * VoxelHelper.ChunkSideSize;

                var cont01 = climateCache.Continentalness01;
                var temp01 = climateCache.Temperature01;
                var humid01 = climateCache.Humidity01;
                var erosion01 = climateCache.Erosion01;
                var pv01 = climateCache.PeaksValleys01;

                for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
                {
                    var worldZ = chunkWorldZ + lz;
                    var imageZ = worldZ - startZ;
                    if (imageZ < 0 || imageZ >= size) continue;

                    for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
                    {
                        var worldX = chunkWorldX + lx;
                        var imageX = worldX - startX;
                        if (imageX < 0 || imageX >= size) continue;

                        var columnIndex = lz * VoxelHelper.ChunkSideSize + lx;

                        var biome = biomeSelector.Select(
                            cont01[columnIndex],
                            temp01[columnIndex],
                            humid01[columnIndex],
                            erosion01[columnIndex],
                            pv01[columnIndex]);

                        var color = BiomeColors.GetValueOrDefault(biome, BiomeColors[BiomeId.Unknown]);

                        if (includeClimateOverlay)
                        {
                            var tempTint = (byte)(temp01[columnIndex] * 30);
                            color = new Rgba32(
                                (byte)Math.Min(255, color.R + tempTint),
                                color.G,
                                (byte)Math.Min(255, color.B + (30 - tempTint)));
                        }

                        image[imageX, size - 1 - imageZ] = color;
                        biomeStats[biome]++;
                    }
                }
            }
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        image.SaveAsBmp(outputPath);

        var totalPixels = size * size;
        OpenRender.Log.Info($"Biome map saved to: {outputPath}");
        OpenRender.Log.Info("Biome distribution:");
        foreach (var (biome, count) in biomeStats.OrderByDescending(kv => kv.Value))
        {
            if (count > 0)
            {
                var percentage = count * 100.0 / totalPixels;
                OpenRender.Log.Info($"  {biome}: {count:N0} ({percentage:F1}%)");
            }
        }
    }

    /// <summary>
    /// Generate a climate parameter map for debugging.
    /// Visualizes a specific climate parameter as a grayscale or colored image.
    /// </summary>
    public static void GenerateClimateMap(
        TerrainConfig config,
        int centerX,
        int centerZ,
        int radiusBlocks,
        string outputPath,
        ClimateParameter parameter,
        int cellSize = 1,
        bool mirrorX = false)
    {
        var sizeInBlocks = radiusBlocks * 2;
        var imageSize = sizeInBlocks / cellSize;
        var startX = centerX - radiusBlocks;
        var startZ = centerZ - radiusBlocks;

        var climateCache = new ChunkClimateCache();

        using var image = new Image<Rgba32>(imageSize, imageSize);

        OpenRender.Log.Info($"Generating {parameter} climate map: {imageSize}x{imageSize} ({cellSize}x{cellSize} blocks/pixel)...");

        var chunksPerSide = (sizeInBlocks + VoxelHelper.ChunkSideSize - 1) / VoxelHelper.ChunkSideSize + 1;
        var startChunkX = startX / VoxelHelper.ChunkSideSize;
        var startChunkZ = startZ / VoxelHelper.ChunkSideSize;

        for (var cz = 0; cz < chunksPerSide; cz++)
        {
            for (var cx = 0; cx < chunksPerSide; cx++)
            {
                var chunkX = startChunkX + cx;
                var chunkZ = startChunkZ + cz;

                climateCache.SampleForChunk(chunkX, chunkZ, config);

                var chunkWorldX = chunkX * VoxelHelper.ChunkSideSize;
                var chunkWorldZ = chunkZ * VoxelHelper.ChunkSideSize;

                var values = parameter switch
                {
                    ClimateParameter.Continentalness => climateCache.Continentalness01,
                    ClimateParameter.Temperature => climateCache.Temperature01,
                    ClimateParameter.Humidity => climateCache.Humidity01,
                    ClimateParameter.Erosion => climateCache.Erosion01,
                    ClimateParameter.PeaksValleys => climateCache.PeaksValleys01,
                    ClimateParameter.Weirdness => climateCache.Weirdness01,
                    _ => climateCache.Continentalness01
                };

                for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz += cellSize)
                {
                    var worldZ = chunkWorldZ + lz;
                    var imageZ = (worldZ - startZ) / cellSize;
                    if (imageZ < 0 || imageZ >= imageSize) continue;

                    for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx += cellSize)
                    {
                        var worldX = chunkWorldX + lx;
                        var imageX = (worldX - startX) / cellSize;
                        if (imageX < 0 || imageX >= imageSize) continue;

                        // Sample the center column for this cell to reduce cost.
                        var sampleLx = Math.Min(lx + cellSize / 2, VoxelHelper.ChunkSideSize - 1);
                        var sampleLz = Math.Min(lz + cellSize / 2, VoxelHelper.ChunkSideSize - 1);
                        var columnIndex = sampleLz * VoxelHelper.ChunkSideSize + sampleLx;
                        var value = values[columnIndex];

                        // Convert to color based on parameter type
                        var color = parameter switch
                        {
                            ClimateParameter.Temperature => TemperatureToColor(value),
                            ClimateParameter.Humidity => HumidityToColor(value),
                            ClimateParameter.Continentalness => ContinentalnessToColor(value, config.OceanThreshold),
                            _ => GrayscaleToColor(value)
                        };

                        var writeX = mirrorX ? (imageSize - 1 - imageX) : imageX;
                        image[writeX, imageSize - 1 - imageZ] = color;
                    }
                }
            }
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        image.SaveAsBmp(outputPath);
        OpenRender.Log.Info($"Climate map saved to: {outputPath}");
    }

    /// <summary>
    /// Generate a height map for debugging. Uses the same biome-driven height formula as terrain generation
    /// (biome BaseHeight + PV variation + WaterLevel), then color-codes height.
    /// </summary>
    public static void GenerateHeightMapDownsampled(
        TerrainConfig config,
        int centerX,
        int centerZ,
        int radiusBlocks,
        string outputPath,
        int cellSize = 2,
        bool mirrorX = false)
    {
        var sizeInBlocks = radiusBlocks * 2;
        var imageSize = sizeInBlocks / cellSize;
        var startX = centerX - radiusBlocks;
        var startZ = centerZ - radiusBlocks;

        var biomeSelector = new BiomeSelector(config.Biomes);
        var evaluator = new TerrainDensityEvaluator(config);

        var heightLut = config.BakeHeightSplineLut(256);

        var biomesById = config.Biomes.ToDictionary(b => (int)b.Id, b => b);
        var climateCache = new ChunkClimateCache();

        OpenRender.Log.Info($"Generating height map: {imageSize}x{imageSize} ({cellSize}x{cellSize} blocks/pixel) centered at ({centerX}, {centerZ})...");

        var heights = new float[imageSize * imageSize];
        var minHeight = float.MaxValue;
        var maxHeight = float.MinValue;

        var chunksPerSide = (sizeInBlocks + VoxelHelper.ChunkSideSize - 1) / VoxelHelper.ChunkSideSize + 1;
        var startChunkX = startX / VoxelHelper.ChunkSideSize;
        var startChunkZ = startZ / VoxelHelper.ChunkSideSize;

        for (var cz = 0; cz < chunksPerSide; cz++)
        {
            for (var cx = 0; cx < chunksPerSide; cx++)
            {
                var chunkX = startChunkX + cx;
                var chunkZ = startChunkZ + cz;

                climateCache.SampleForChunk(chunkX, chunkZ, config);

                var chunkWorldX = chunkX * VoxelHelper.ChunkSideSize;
                var chunkWorldZ = chunkZ * VoxelHelper.ChunkSideSize;

                var cont01 = climateCache.Continentalness01;
                var temp01 = climateCache.Temperature01;
                var humid01 = climateCache.Humidity01;
                var erosion01 = climateCache.Erosion01;
                var pv01 = climateCache.PeaksValleys01;

                for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz += cellSize)
                {
                    var worldZ = chunkWorldZ + lz;
                    var imageZ = (worldZ - startZ) / cellSize;
                    if (imageZ < 0 || imageZ >= imageSize) continue;

                    for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx += cellSize)
                    {
                        var worldX = chunkWorldX + lx;
                        var imageX = (worldX - startX) / cellSize;
                        if (imageX < 0 || imageX >= imageSize) continue;

                        // Use the center column for this cell.
                        var sampleLx = Math.Min(lx + cellSize / 2, VoxelHelper.ChunkSideSize - 1);
                        var sampleLz = Math.Min(lz + cellSize / 2, VoxelHelper.ChunkSideSize - 1);
                        var columnIndex = sampleLz * VoxelHelper.ChunkSideSize + sampleLx;

                        var biomeId = biomeSelector.Select(
                            cont01[columnIndex],
                            temp01[columnIndex],
                            humid01[columnIndex],
                            erosion01[columnIndex],
                            pv01[columnIndex]);

                        if (!biomesById.TryGetValue((int)biomeId, out var biome))
                        {
                            biome = config.Biomes.First();
                        }

                        var c01 = cont01[columnIndex];
                        var macroHeight = evaluator.CalculateSplineHeight(c01, pv01[columnIndex], erosion01[columnIndex], heightLut);
                        var biomeHeight = evaluator.CalculateBiomeHeight(biome, pv01[columnIndex], erosion01[columnIndex], c01);
                        var height = TerrainDensityEvaluator.BlendMacroAndBiomeHeight(macroHeight, biome, biomeHeight);

                        var idx = imageZ * imageSize + imageX;
                        heights[idx] = height;
                        if (height < minHeight) minHeight = height;
                        if (height > maxHeight) maxHeight = height;
                    }
                }
            }
        }

        using var image = new Image<Rgba32>(imageSize, imageSize);

        for (var imageZ = 0; imageZ < imageSize; imageZ++)
        {
            for (var imageX = 0; imageX < imageSize; imageX++)
            {
                var h = heights[imageZ * imageSize + imageX];
                var color = HeightToColor(h, VoxelHelper.WaterLevel);
                var writeX = mirrorX ? (imageSize - 1 - imageX) : imageX;
                image[writeX, imageSize - 1 - imageZ] = color;
            }
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        image.SaveAsBmp(outputPath);
        OpenRender.Log.Info($"Height map saved to: {outputPath} (height[min,max]=[{minHeight:F1},{maxHeight:F1}] water={VoxelHelper.WaterLevel} yMax={VoxelHelper.ChunkYSize - 1})");
    }

    private static float SampleHeightSpline(float[] heightLut, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var scaled = t * (heightLut.Length - 1);
        var i = (int)MathF.Floor(scaled);
        var frac = scaled - i;
        var a = heightLut[i];
        var b = heightLut[Math.Min(i + 1, heightLut.Length - 1)];
        return a + (b - a) * frac;
    }

    private static Rgba32 HeightToColor(float height, float waterLevel)
    {
        // Underwater: blue -> dark blue (deeper = darker)
        if (height < waterLevel)
        {
            var depth = Math.Clamp((waterLevel - height) / Math.Max(1f, waterLevel), 0f, 1f);
            // near water: brighter blue, deep: dark blue
            return LerpColor(new Rgba32(0, 0, 160), new Rgba32(0, 0, 20), depth);
        }

        // Above water: low = blue, then yellow, green, cyan, white at peaks
        var denom = Math.Max(1f, (VoxelHelper.ChunkYSize - 1) - waterLevel);
        var t = Math.Clamp((height - waterLevel) / denom, 0f, 1f);

        // Color stops: 0=Blue, 0.35=Yellow, 0.65=Green, 0.85=Cyan, 1=White
        return t <= 0.35f
            ? LerpColor(new Rgba32(0, 0, 255), new Rgba32(255, 255, 0), t / 0.35f)
            : t <= 0.65f
            ? LerpColor(new Rgba32(255, 255, 0), new Rgba32(0, 255, 0), (t - 0.35f) / 0.30f)
            : t <= 0.85f
            ? LerpColor(new Rgba32(0, 255, 0), new Rgba32(0, 255, 255), (t - 0.65f) / 0.20f)
            : LerpColor(new Rgba32(0, 255, 255), new Rgba32(255, 255, 255), (t - 0.85f) / 0.15f);
    }

    private static Rgba32 LerpColor(Rgba32 a, Rgba32 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Rgba32(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            255);
    }

    /// <summary>
    /// Generate all debug maps at once (biome + all climate parameters).
    /// </summary>
    public static void GenerateAllMaps(
        TerrainConfig config,
        int centerX,
        int centerZ,
        int radiusBlocks,
        string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var prefix = $"map_{timestamp}_c{centerX}_{centerZ}_r{radiusBlocks}";

        GenerateBiomeMap(config, centerX, centerZ, radiusBlocks,
            Path.Combine(outputDirectory, $"{prefix}_biomes.bmp"));

        GenerateClimateMap(config, centerX, centerZ, radiusBlocks,
            Path.Combine(outputDirectory, $"{prefix}_continentalness.bmp"),
            ClimateParameter.Continentalness);

        GenerateClimateMap(config, centerX, centerZ, radiusBlocks,
            Path.Combine(outputDirectory, $"{prefix}_temperature.bmp"),
            ClimateParameter.Temperature);

        GenerateClimateMap(config, centerX, centerZ, radiusBlocks,
            Path.Combine(outputDirectory, $"{prefix}_humidity.bmp"),
            ClimateParameter.Humidity);

        GenerateClimateMap(config, centerX, centerZ, radiusBlocks,
            Path.Combine(outputDirectory, $"{prefix}_erosion.bmp"),
            ClimateParameter.Erosion);

        GenerateClimateMap(config, centerX, centerZ, radiusBlocks,
            Path.Combine(outputDirectory, $"{prefix}_peaks.bmp"),
            ClimateParameter.PeaksValleys);

        OpenRender.Log.Info($"All debug maps saved to: {outputDirectory}");
    }

    private static Rgba32 TemperatureToColor(float t)
    {
        // Blue (cold) to red (hot)
        var r = (byte)(t * 255);
        var b = (byte)((1 - t) * 255);
        return new Rgba32(r, 100, b);
    }

    private static Rgba32 HumidityToColor(float h)
    {
        // Yellow (dry) to cyan (wet)
        var g = (byte)(h * 255);
        var b = (byte)(h * 200);
        return new Rgba32((byte)((1 - h) * 200), g, b);
    }

    private static Rgba32 ContinentalnessToColor(float c, float oceanThreshold)
    {
        if (c < oceanThreshold)
        {
            // Ocean: dark to medium blue
            var oceanDepth = c / oceanThreshold;
            return new Rgba32(0, 0, (byte)(80 + oceanDepth * 100));
        }
        else
        {
            // Land: green to brown to white (for mountains)
            var landHeight = (c - oceanThreshold) / (1 - oceanThreshold);
            if (landHeight < 0.5f)
            {
                // Low to mid land: green
                var green = (byte)(100 + landHeight * 2 * 100);
                return new Rgba32(50, green, 50);
            }
            else
            {
                // High land: brown to gray
                var gray = (byte)(100 + (landHeight - 0.5f) * 2 * 155);
                return new Rgba32(gray, gray, gray);
            }
        }
    }

    private static Rgba32 GrayscaleToColor(float v)
    {
        var gray = (byte)(v * 255);
        return new Rgba32(gray, gray, gray);
    }
}

/// <summary>
/// Climate parameters that can be visualized.
/// </summary>
public enum ClimateParameter
{
    Continentalness,
    Temperature,
    Humidity,
    Erosion,
    PeaksValleys,
    Weirdness
}
