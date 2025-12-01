using System.Numerics;

namespace SpyroGame.World;

/// <summary>
/// Generates biome data for chunks using Minecraft-style multi-parameter climate system.
/// Biomes are determined by: Continentalness, Temperature, Humidity, Erosion, Peaks/Valleys, Weirdness.
/// </summary>
public sealed class BiomeGenerator
{
    private readonly TerrainConfig config;
    private readonly uint seed;
    
    // Climate noise parameters - use config values for consistency with CpuTerrainGenerator
    private float ContinentalnessScale => config.ContinentalnessScale;
    private const float TemperatureScale = 1f / 400f;
    private const float HumidityScale = 1f / 350f;
    private float ErosionScale => config.ErosionScale;
    private float PeaksValleysScale => config.RidgeScale;
    private const float WeirdnessScale = 1f / 200f;
    
    // Warp parameters - use config values for consistency with CpuTerrainGenerator
    private float WarpScale => config.WarpScale;
    private float WarpStrength => config.WarpStrength;
    
    public BiomeGenerator(TerrainConfig config)
    {
        this.config = config;
        this.seed = (uint)config.Seed;
    }
    
    /// <summary>
    /// Generate biome data for a chunk.
    /// </summary>
    public ChunkBiomeData GenerateChunkBiomes(int chunkX, int chunkZ)
    {
        var data = new ChunkBiomeData();
        var baseX = chunkX * VoxelHelper.ChunkSideSize;
        var baseZ = chunkZ * VoxelHelper.ChunkSideSize;
        
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
                var jitterX = GradientNoise2D(baseCellX * 0.25f, baseCellZ * 0.25f, seed + 2000u) * 0.5f;
                var jitterZ = GradientNoise2D(baseCellX * 0.25f + 100f, baseCellZ * 0.25f + 100f, seed + 2001u) * 0.5f;
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
        
        return data;
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
