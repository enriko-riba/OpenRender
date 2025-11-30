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
    
    // Climate noise parameters
    private const float ContinentalnessScale = 1f / 800f;
    private const float TemperatureScale = 1f / 400f;
    private const float HumidityScale = 1f / 350f;
    private const float ErosionScale = 1f / 300f;
    private const float PeaksValleysScale = 1f / 150f;
    private const float WeirdnessScale = 1f / 200f;
    
    // Warp parameters for more organic shapes
    private const float WarpScale = 1f / 150f;
    private const float WarpStrength = 40f;
    
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
                
                // Sample at cell center
                var worldX = baseX + cellX * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                var worldZ = baseZ + cellZ * ChunkBiomeData.BlocksPerCell + ChunkBiomeData.BlocksPerCell / 2;
                
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
    /// </summary>
    private BiomeId SelectBiome(float continentalness, float temperature, float humidity,
        float erosion, float pv, float weirdness, float estimatedAltitude)
    {
        var cont01 = continentalness * 0.5f + 0.5f;
        var erosion01 = erosion * 0.5f + 0.5f;
        var pv01 = pv * 0.5f + 0.5f;
        
        // ============ OCEAN BIOMES ============
        // Deep ocean: very low continentalness
        if (cont01 < 0.20f)
        {
            return BiomeId.DeepOcean;
        }
        
        // Regular ocean
        if (cont01 < 0.35f)
        {
            return BiomeId.Ocean;
        }
        
        // ============ COASTAL BIOMES ============
        // Beach: near ocean with high erosion (flat coasts)
        if (cont01 < 0.42f && erosion01 > 0.5f)
        {
            return BiomeId.Beach;
        }
        
        // ============ ALPINE BIOME ============
        // High altitude OR very cold temperature
        var altitudeAboveWater = estimatedAltitude - VoxelHelper.WaterLevel;
        if (altitudeAboveWater > config.AlpineElevation || temperature < 0.15f)
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
    /// Used to affect temperature before actual terrain generation.
    /// </summary>
    private float GetEstimatedAltitude(float continentalness, float erosion, float pv, float weirdness)
    {
        var cont01 = continentalness * 0.5f + 0.5f;
        var erosion01 = erosion * 0.5f + 0.5f;
        var pv01 = pv * 0.5f + 0.5f;
        var weird01 = weirdness * 0.5f + 0.5f;
        
        // Base height from continentalness
        float baseHeight;
        if (cont01 < 0.35f)
        {
            // Ocean
            baseHeight = VoxelHelper.WaterLevel - 30f;
        }
        else if (cont01 < 0.50f)
        {
            // Coastal
            baseHeight = VoxelHelper.WaterLevel + 10f;
        }
        else if (cont01 < 0.70f)
        {
            // Inland
            baseHeight = VoxelHelper.WaterLevel + 40f;
        }
        else
        {
            // Mountain base
            baseHeight = VoxelHelper.WaterLevel + 80f;
        }
        
        // Add peaks/valleys contribution (scaled by inverse erosion)
        var peakContribution = pv01 * 100f * (1f - erosion01 * 0.7f);
        
        // Weirdness can add dramatic height changes
        var weirdContribution = weird01 > 0.7f ? (weird01 - 0.7f) * 150f : 0f;
        
        return baseHeight + peakContribution + weirdContribution;
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
