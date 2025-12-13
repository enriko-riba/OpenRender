namespace SpyroGame.World;

/// <summary>
/// Generates biome data for chunks using Minecraft-style multi-parameter climate system.
/// Biomes are determined by: Continentalness, Temperature, Humidity, Erosion, Peaks/Valleys, Weirdness.
/// Check the TERRAIN_ARCHITECTURE.md document for details.
/// </summary>
public sealed class BiomeGenerator(TerrainConfig config)
{
    private readonly uint seed = (uint)config.Seed;

    // Climate noise parameters - use config values for consistency with CpuTerrainGenerator
    private float ContinentalnessScale => config.Continentalness.BaseScale;
    private float TemperatureScale => config.Temperature.BaseScale;
    private float HumidityScale => config.Humidity.BaseScale;
    private float ErosionScale => config.Erosion.BaseScale;
    private float PeaksValleysScale => config.PeaksValleys.BaseScale;
    private float WeirdnessScale => config.Weirdness.BaseScale;

    // Warp parameters - use config values for consistency with CpuTerrainGenerator
    private float WarpScale => config.WarpScale;
    private float WarpStrength => config.WarpStrength;

    /// <summary>
    /// Generate biome data for a chunk including 3D cave biomes.
    /// </summary>
    public ChunkBiomeData GenerateChunkBiomes(int chunkX, int chunkZ)
    {
        var data = new ChunkBiomeData();
        var baseX = chunkX * VoxelHelper.ChunkSideSize;
        var baseZ = chunkZ * VoxelHelper.ChunkSideSize;

        // Generate surface biomes (4×4 grid)
        for (var cellZ = 0; cellZ < ChunkBiomeData.GridSize; cellZ++)
        {
            for (var cellX = 0; cellX < ChunkBiomeData.GridSize; cellX++)
            {
                var cellIndex = cellZ * ChunkBiomeData.GridSize + cellX;

                // Sample at cell center with slight randomization to break grid alignment
                // This prevents biome borders from appearing as straight lines along axes
                var baseCellX = baseX + cellX * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                var baseCellZ = baseZ + cellZ * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;

                // Add small per-cell jitter (±0.25 blocks equivalent) to break grid patterns
                // Using cell coordinates as seed ensures consistency across chunk boundaries
                var jitterX = GradientNoise2D(baseCellX * 0.25f, baseCellZ * 0.25f, seed + 2000u) * 2.0f;
                var jitterZ = GradientNoise2D(baseCellX * 0.25f + 100f, baseCellZ * 0.25f + 100f, seed + 2001u) * 2.0f;
                var worldX = baseCellX + jitterX;
                var worldZ = baseCellZ + jitterZ;

                // Apply domain warp for more organic shapes
                var warpX = SampleNoise2D(worldX, worldZ, WarpScale, seed + 1000u);
                var warpZ = SampleNoise2D(worldX + 5.2f, worldZ + 1.3f, WarpScale, seed + 1001u);
                var warpedX = worldX + warpX * WarpStrength;
                var warpedZ = worldZ + warpZ * WarpStrength;

                // Sample climate parameters
                var continentalness = SampleNoise2D(warpedX, warpedZ, ContinentalnessScale, seed);
                var temperature = SampleNoise2D(worldX, worldZ, TemperatureScale, seed + 100u);
                var humidity = SampleNoise2D(worldX, worldZ, HumidityScale, seed + 200u);
                var erosion = SampleNoise2D(worldX, worldZ, ErosionScale, seed + 300u);
                var peaksValleys = SampleNoise2D(worldX, worldZ, PeaksValleysScale, seed + 400u);
                var weirdness = SampleNoise2D(worldX, worldZ, WeirdnessScale, seed + 500u);

                // Store raw values
                data.Continentalness[cellIndex] = continentalness;
                data.Erosion[cellIndex] = erosion;
                data.PeaksValleys[cellIndex] = peaksValleys;
                data.Weirdness[cellIndex] = weirdness;

                // Temperature and humidity need to be in [0,1] range for biome selection
                // Also apply altitude-based temperature reduction (estimated from continentalness + PV)
                var temp01 = temperature * 0.5f + 0.5f;
                var humid01 = humidity * 0.5f + 0.5f;

                // Estimate altitude effect on temperature
                var estimatedAltitude = GetEstimatedAltitude(continentalness, erosion, peaksValleys, weirdness);
                var altitudeAboveWater = Math.Max(0, estimatedAltitude - VoxelHelper.WaterLevel);
                temp01 -= altitudeAboveWater * config.LapseRate;
                temp01 = Math.Clamp(temp01, 0f, 1f);

                // Continental drying effect
                var cont01 = continentalness * 0.5f + 0.5f;
                if (cont01 > config.CoastThreshold)
                {
                    humid01 -= (cont01 - config.CoastThreshold) * config.CoastDrying;
                    humid01 = Math.Clamp(humid01, 0f, 1f);
                }

                data.Temperature[cellIndex] = temp01;
                data.Humidity[cellIndex] = humid01;

                // Select biome based on all parameters
                data.BiomeIds[cellIndex] = SelectBiome(
                    continentalness, temp01, humid01, erosion, peaksValleys, weirdness,
                    estimatedAltitude);
            }
        }

        // Generate per-column biomes (16x16) for accurate borders
        // This populates the ColumnBiomes array which is the primary source for rendering and gameplay
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                var worldX = baseX + lx;
                var worldZ = baseZ + lz;

                // Apply domain warp (same as 4x4 grid)
                var warpX = SampleNoise2D(worldX, worldZ, WarpScale, seed + 1000u);
                var warpZ = SampleNoise2D(worldX + 5.2f, worldZ + 1.3f, WarpScale, seed + 1001u);
                var warpedX = worldX + warpX * WarpStrength;
                var warpedZ = worldZ + warpZ * WarpStrength;

                // Sample climate parameters
                var continentalness = SampleNoise2D(warpedX, warpedZ, ContinentalnessScale, seed);
                var temperature = SampleNoise2D(worldX, worldZ, TemperatureScale, seed + 100u);
                var humidity = SampleNoise2D(worldX, worldZ, HumidityScale, seed + 200u);
                var erosion = SampleNoise2D(worldX, worldZ, ErosionScale, seed + 300u);
                var peaksValleys = SampleNoise2D(worldX, worldZ, PeaksValleysScale, seed + 400u);
                var weirdness = SampleNoise2D(worldX, worldZ, WeirdnessScale, seed + 500u);

                // Process temperature/humidity
                var temp01 = temperature * 0.5f + 0.5f;
                var humid01 = humidity * 0.5f + 0.5f;

                var estimatedAltitude = GetEstimatedAltitude(continentalness, erosion, peaksValleys, weirdness);
                var altitudeAboveWater = Math.Max(0, estimatedAltitude - VoxelHelper.WaterLevel);
                temp01 -= altitudeAboveWater * config.LapseRate;
                temp01 = Math.Clamp(temp01, 0f, 1f);

                var cont01 = continentalness * 0.5f + 0.5f;
                if (cont01 > config.CoastThreshold)
                {
                    humid01 -= (cont01 - config.CoastThreshold) * config.CoastDrying;
                    humid01 = Math.Clamp(humid01, 0f, 1f);
                }

                var biomeId = SelectBiome(continentalness, temp01, humid01, erosion, peaksValleys, weirdness, estimatedAltitude);
                data.SetBiomeAt(lx, lz, biomeId);
            }
        }

        // Phase 3: Cave biomes disabled for performance - they're not currently used.
        // Surface biome selection in CpuTerrainGenerator uses BiomeSelector which
        // reads climate values from ChunkClimateCache, making this separate biome
        // generation mostly redundant for surface blocks. Re-enable when implementing
        // underground biome-dependent features (special cave decorations, etc.).
        // GenerateCaveBiomes(data, baseX, baseZ);

        return data;
    }

    /// <summary>
    /// Generate 3D cave biomes for underground biome variation.
    /// Uses Minecraft-style 4×4×24 grid (16-block cells in Y).
    /// </summary>
    private void GenerateCaveBiomes(ChunkBiomeData data, int baseX, int baseZ)
    {
        for (var cellY = 0; cellY < ChunkBiomeData.GridSizeY; cellY++)
        {
            var worldY = cellY * ChunkBiomeData.BlocksPerCellY + ChunkBiomeData.BlocksPerCellY / 2;

            for (var cellZ = 0; cellZ < ChunkBiomeData.GridSize; cellZ++)
            {
                for (var cellX = 0; cellX < ChunkBiomeData.GridSize; cellX++)
                {
                    var worldX = baseX + cellX * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                    var worldZ = baseZ + cellZ * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;

                    // Get surface climate values for this XZ position
                    var surfaceIndex = cellZ * ChunkBiomeData.GridSize + cellX;
                    var temp01 = data.Temperature[surfaceIndex];
                    var humid01 = data.Humidity[surfaceIndex];
                    var cont01 = data.Continentalness[surfaceIndex] * 0.5f + 0.5f;

                    // Select cave biome based on 3D position and climate
                    var caveBiome = SelectCaveBiome(worldX, worldY, worldZ, temp01, humid01, cont01);
                    data.SetCaveBiomeAt(cellX, cellY, cellZ, caveBiome);
                }
            }
        }
    }

    /// <summary>
    /// Select cave biome based on 3D world position and surface climate.
    /// </summary>
    private CaveBiomeId SelectCaveBiome(float worldX, float worldY, float worldZ, float temp01, float humid01, float cont01)
    {
        // Use 3D noise for cave biome variation
        const float caveBiomeScale = 1f / 120f; // ~120 block cave biome regions
        var caveBiomeNoise = SampleNoise3D(worldX, worldY, worldZ, caveBiomeScale, seed + 5000u);
        var caveBiomeValue = caveBiomeNoise * 0.5f + 0.5f; // [0, 1]

        // Secondary noise for variety
        var varietyNoise = SampleNoise3D(worldX + 100f, worldY + 50f, worldZ + 100f, caveBiomeScale * 1.5f, seed + 5001u);
        //var varietyValue = varietyNoise * 0.5f + 0.5f;

        // Depth-based selection (deep = different biomes)
        var isDeep = worldY < 30; // Below Y=30 is "deep" underground
                                  //var isVeryDeep = worldY < 10; // Below Y=10 is "very deep"

        // Ocean areas get ocean caves
        if (cont01 < config.OceanThreshold)
        {
            return CaveBiomeId.OceanCave;
        }

        // Very deep areas - chance for deep dark (disabled per user request - skip this)
        // if (isVeryDeep && varietyValue > 0.85f)
        // {
        //     return CaveBiomeId.DeepDark;
        // }

        // Cold biomes get frozen caves
        if (temp01 < 0.25f && caveBiomeValue > 0.6f)
        {
            return CaveBiomeId.FrozenCave;
        }

        // Humid biomes get lush caves
        if (humid01 > 0.6f && caveBiomeValue > 0.4f && !isDeep)
        {
            return CaveBiomeId.LushCave;
        }

        // Dry biomes get dripstone caves
        if (humid01 < 0.4f && caveBiomeValue > 0.5f)
        {
            return CaveBiomeId.DripstoneCave;
        }

        // Default to standard cave
        return CaveBiomeId.StandardCave;
    }

    /// <summary>
    /// Sample 3D noise for cave biome selection.
    /// </summary>
    private static float SampleNoise3D(float x, float y, float z, float frequency, uint seed)
    {
        // Simple 3D FBM with 2 octaves for performance
        var amplitude = 1f;
        var freq = frequency;
        var sum = 0f;
        var totalAmp = 0f;

        for (var octave = 0; octave < 2; octave++)
        {
            sum += GradientNoise3D(x * freq, y * freq, z * freq, seed + (uint)(octave * 1000)) * amplitude;
            totalAmp += amplitude;
            amplitude *= 0.5f;
            freq *= 2f;
        }

        return sum / totalAmp;
    }

    /// <summary>
    /// Simple 3D gradient noise implementation.
    /// </summary>
    private static float GradientNoise3D(float x, float y, float z, uint seed)
    {
        var xi = (int)MathF.Floor(x);
        var yi = (int)MathF.Floor(y);
        var zi = (int)MathF.Floor(z);

        var fx = x - xi;
        var fy = y - yi;
        var fz = z - zi;

        // Fade curves
        var u = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        var v = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
        var w = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);

        // Hash corners and compute gradients
        var g000 = Grad3D(Hash3D(xi, yi, zi, seed), fx, fy, fz);
        var g100 = Grad3D(Hash3D(xi + 1, yi, zi, seed), fx - 1f, fy, fz);
        var g010 = Grad3D(Hash3D(xi, yi + 1, zi, seed), fx, fy - 1f, fz);
        var g110 = Grad3D(Hash3D(xi + 1, yi + 1, zi, seed), fx - 1f, fy - 1f, fz);
        var g001 = Grad3D(Hash3D(xi, yi, zi + 1, seed), fx, fy, fz - 1f);
        var g101 = Grad3D(Hash3D(xi + 1, yi, zi + 1, seed), fx - 1f, fy, fz - 1f);
        var g011 = Grad3D(Hash3D(xi, yi + 1, zi + 1, seed), fx, fy - 1f, fz - 1f);
        var g111 = Grad3D(Hash3D(xi + 1, yi + 1, zi + 1, seed), fx - 1f, fy - 1f, fz - 1f);

        // Trilinear interpolation
        var x00 = g000 + u * (g100 - g000);
        var x10 = g010 + u * (g110 - g010);
        var x01 = g001 + u * (g101 - g001);
        var x11 = g011 + u * (g111 - g011);

        var y0 = x00 + v * (x10 - x00);
        var y1 = x01 + v * (x11 - x01);

        return y0 + w * (y1 - y0);
    }

    private static float Grad3D(uint hash, float x, float y, float z)
    {
        var h = hash & 15;
        var u = h < 8 ? x : y;
        var v = h < 4 ? y : (h is 12 or 14 ? x : z);
        return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -v : v);
    }

    private static uint Hash3D(int x, int y, int z, uint seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647);
            h ^= seed;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    /// <summary>
    /// Select biome based on Minecraft-style multi-parameter system.
    /// Uses ONLY noise parameters (primarily continentalness) for biome selection.
    /// This matches Minecraft 1.18+ where biomes are determined by noise, not actual terrain height.
    /// The terrain height is DERIVED from the same noise, creating natural consistency.
    /// </summary>
    private BiomeId SelectBiome(float continentalness, float temperature, float humidity,
        float erosion, float pv, float weirdness, float estimatedAltitude)
    {
        var cont01 = continentalness * 0.5f + 0.5f;
        var erosion01 = erosion * 0.5f + 0.5f;
        var pv01 = pv * 0.5f + 0.5f;

        // ============ OCEAN BIOMES (Minecraft-style: ONLY use continentalness) ============
        // In Minecraft 1.18+, ocean biomes are determined PURELY by continentalness noise.
        // The terrain height is ALSO derived from continentalness (via height spline),
        // so low continentalness → Ocean biome AND low terrain (underwater).
        // This creates natural consistency without checking actual height.
        if (cont01 < config.DeepOceanThreshold)
        {
            return BiomeId.DeepOcean;
        }

        if (cont01 < config.OceanThreshold)
        {
            return BiomeId.Ocean;
        }

        // ============ COASTAL BIOMES ============
        // Beach: in the coastal zone (just above ocean threshold) with high erosion (flat coasts)
        // Minecraft-style: Use ONLY noise parameters, not estimated altitude
        // The height spline ensures coastal continentalness produces near-water-level terrain
        var beachUpperThreshold = config.OceanThreshold + config.CoastRange + 0.07f;
        if (cont01 < beachUpperThreshold && erosion01 > 0.5f)
        {
            return BiomeId.Beach;
        }

        // ============ ALPINE BIOME ============
        // Minecraft-style: Use estimated altitude from noise parameters
        // (This is acceptable since we're using the same spline that shapes terrain)
        var altitudeAboveWater = estimatedAltitude - VoxelHelper.WaterLevel;
        if (altitudeAboveWater > config.AlpineElevation || temperature < 0.12f)
        {
            return BiomeId.Alpine;
        }

        // ============ MOUNTAIN/HIGHLANDS ============
        // High peaks with low erosion = dramatic mountains
        if (pv01 > 0.7f && erosion01 < 0.4f)
        {
            return temperature < 0.35f ? BiomeId.Alpine : BiomeId.Highlands;
        }

        // ============ CLIMATE-BASED LAND BIOMES ============

        // Hot biomes (temperature > 0.65)
        if (temperature > 0.65f)
        {
            if (humidity < 0.30f) return BiomeId.Desert;
            if (humidity < 0.55f) return BiomeId.Savanna;
            return BiomeId.Rainforest;
        }

        // Cold biomes (temperature < 0.35)
        if (temperature < 0.35f)
        {
            if (humidity > 0.50f) return BiomeId.Taiga;
            return BiomeId.Tundra;
        }

        // Temperate biomes
        if (humidity > 0.55f)
        {
            // Wet areas near coast could be swamp
            if (cont01 < 0.50f && erosion01 > 0.6f)
            {
                return BiomeId.Swamp;
            }
            return BiomeId.Taiga; // Dense forest
        }

        // Default temperate
        if (pv01 > 0.55f || erosion01 < 0.45f)
        {
            return BiomeId.Highlands; // Rolling hills
        }

        return BiomeId.Plains;
    }

    /// <summary>
    /// Estimate terrain altitude from climate parameters.
    /// Uses the actual height spline from config to match CpuTerrainGenerator's output.
    /// </summary>
    private float GetEstimatedAltitude(float continentalness, float erosion, float pv, float weirdness)
    {
        var cont01 = continentalness * 0.5f + 0.5f;
        var erosion01 = erosion * 0.5f + 0.5f;
        var pv01 = pv * 0.5f + 0.5f;

        // Use the SAME height spline as CpuTerrainGenerator for consistency
        // HeightSpline maps cont01 [0,1] -> height offset from water level
        var baseHeight = config.HeightSpline.Evaluate(cont01) + VoxelHelper.WaterLevel;

        // Only add terrain variation if above ocean threshold (land areas)
        if (cont01 >= config.OceanThreshold)
        {
            // Add peaks/valleys contribution (scaled by inverse erosion)
            // This matches the terrain shaping in CpuTerrainGenerator
            var ruggedness = 1f - erosion01 * 0.5f;
            var peakContribution = pv01 * 20f * ruggedness;
            baseHeight += peakContribution;

            // Mountain areas get additional cliff contribution
            if (cont01 > config.MountainThreshold)
            {
                var mountainness = (cont01 - config.MountainThreshold) / (1f - config.MountainThreshold);
                baseHeight += mountainness * 40f * pv01;
            }
        }

        return baseHeight;
    }

    /// <summary>
    /// Sample 2D gradient noise with FBM.
    /// </summary>
    private static float SampleNoise2D(float x, float z, float frequency, uint seed)
    {
        // Simple FBM with 3 octaves
        var amplitude = 1f;
        var freq = frequency;
        var sum = 0f;
        var totalAmp = 0f;

        for (var octave = 0; octave < 3; octave++)
        {
            sum += GradientNoise2D(x * freq, z * freq, seed + (uint)(octave * 1000)) * amplitude;
            totalAmp += amplitude;
            amplitude *= 0.5f;
            freq *= 2f;
        }

        return sum / totalAmp;
    }

    /// <summary>
    /// Simple 2D gradient noise implementation.
    /// </summary>
    private static float GradientNoise2D(float x, float z, uint seed)
    {
        var xi = (int)MathF.Floor(x);
        var zi = (int)MathF.Floor(z);

        var fx = x - xi;
        var fz = z - zi;

        // Fade curves
        var u = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        var v = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);

        // Hash corners and compute gradients
        var g00 = Grad2D(Hash2D(xi, zi, seed), fx, fz);
        var g10 = Grad2D(Hash2D(xi + 1, zi, seed), fx - 1f, fz);
        var g01 = Grad2D(Hash2D(xi, zi + 1, seed), fx, fz - 1f);
        var g11 = Grad2D(Hash2D(xi + 1, zi + 1, seed), fx - 1f, fz - 1f);

        // Bilinear interpolation
        var x0 = g00 + u * (g10 - g00);
        var x1 = g01 + u * (g11 - g01);
        return x0 + v * (x1 - x0);
    }

    private static float Grad2D(uint hash, float x, float z)
    {
        var h = hash & 7;
        var u = h < 4 ? x : z;
        var v = h < 4 ? z : x;
        return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -v : v);
    }

    private static uint Hash2D(int x, int z, uint seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + z * 668265263);
            h ^= seed;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
