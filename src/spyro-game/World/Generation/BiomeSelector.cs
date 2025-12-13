namespace SpyroGame.World;

/// <summary>
/// Minecraft-style biome selector using ONLY climate parameters (C/T/H/E/PV).
/// 
/// ARCHITECTURE PRINCIPLE: Biomes are selected FIRST based on global noise parameters.
/// The biome's properties then DRIVE terrain shape (BaseHeight, HeightVariation).
/// Water is a DOWNSTREAM effect - ocean biomes have low BaseHeight, producing terrain below water level.
/// 
/// Selection is purely data-driven using BiomeDefinition ranges:
/// - Continentalness: Primary filter (ocean vs land vs mountain)
/// - Temperature: Climate filter
/// - Humidity: Climate filter
/// - Priority: Tiebreaker when multiple biomes match
/// 
/// This selector does NOT use:
/// - WaterBodyType (determined AFTER biome selection)
/// - Actual terrain height (biome drives height, not vice versa)
/// - Any hardcoded terrain type checks (all encoded in Continentalness ranges)
/// </summary>
internal sealed class BiomeSelector
{
    private readonly BiomeDefinition[] _biomes;
    private readonly int _biomeCount;
    private const float WeightFalloff = 6f;
    
    /// <summary>
    /// Creates a new biome selector with biome definitions sorted by priority.
    /// </summary>
    public BiomeSelector(IReadOnlyList<BiomeDefinition> biomes)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        _biomes = [.. biomes.OrderByDescending(b => b.Priority)];
        _biomeCount = _biomes.Length;
    }
    
    
    /// <summary>
    /// Select biome using ONLY climate parameters.
    /// This is the core Minecraft-style biome selection - purely data-driven.
    /// </summary>
    /// <param name="continentalness01">Continentalness [0,1]. Low = ocean, high = inland.</param>
    /// <param name="temperature01">Temperature [0,1]. Low = cold, high = hot.</param>
    /// <param name="humidity01">Humidity [0,1]. Low = dry, high = wet.</param>
    /// <param name="erosion01">Erosion [0,1]. Low = rough terrain, high = flat (reserved for future).</param>
    /// <param name="peaksValleys01">Peaks/Valleys [0,1]. Ridge noise (reserved for future).</param>
    /// <returns>Selected BiomeId based purely on climate ranges.</returns>
    public BiomeId Select(
        float continentalness01,
        float temperature01,
        float humidity01,
        float erosion01,
        float peaksValleys01)
    {
        var bestBiomeId = BiomeId.Plains;
        var bestScore = float.MaxValue;
        
        for (var i = 0; i < _biomeCount; i++)
        {
            var biome = _biomes[i];
            
            // Skip Lake/River - they're handled separately (not by climate)
            if (biome.Id is (int)BiomeId.Lake or (int)BiomeId.River)
                continue;
            
            // PRIMARY FILTER: Continentalness range
            // This replaces all the old TerrainType checks
            if (!biome.Continentalness.Contains(continentalness01))
                continue;
            
            // Climate distance scoring for Temperature and Humidity
            var tempDist = biome.Temperature.Contains(temperature01) 
                ? 0f 
                : MathF.Min(MathF.Abs(temperature01 - biome.Temperature.Min),
                            MathF.Abs(temperature01 - biome.Temperature.Max));
            
            var humidDist = biome.Humidity.Contains(humidity01)
                ? 0f
                : MathF.Min(MathF.Abs(humidity01 - biome.Humidity.Min),
                            MathF.Abs(humidity01 - biome.Humidity.Max));
            
            // Continentalness distance (for tiebreaking within valid range)
            var contDist = MathF.Abs(continentalness01 - biome.Continentalness.Center) * 0.5f;
            
            // Priority bonus (higher priority = lower score)
            var priorityBonus = (100 - biome.Priority) * 0.01f;
            
            // Combined score: climate fit + continentalness fit + priority
            var score = tempDist + humidDist + contDist + priorityBonus;
            
            if (score < bestScore)
            {
                bestScore = score;
                bestBiomeId = (BiomeId)biome.Id;
            }
        }
        
        return bestBiomeId;
    }

    /// <summary>
    /// Selects multiple biomes with weights for blending.
    /// Allows smooth transitions between biomes by removing hard cutoffs.
    /// </summary>
    public void SelectWeighted(
        float continentalness01,
        float temperature01,
        float humidity01,
        float erosion01,
        float peaksValleys01,
        List<(BiomeDefinition Biome, float Weight)> results)
    {
        results.Clear();
        var totalWeight = 0f;
        
        for (var i = 0; i < _biomeCount; i++)
        {
            var biome = _biomes[i];
            
            // Skip Lake/River - they're handled separately
            if (biome.Id is (int)BiomeId.Lake or (int)BiomeId.River)
                continue;
            
            // Calculate distance to Continentalness range
            // Instead of hard cutoff, we apply a penalty for being outside
            float contDist;
            var halfWidth = (biome.Continentalness.Max - biome.Continentalness.Min) * 0.5f;
            
            if (biome.Continentalness.Contains(continentalness01))
            {
                // Inside: 0 at center, increasing to halfWidth * 0.5 at edge
                contDist = MathF.Abs(continentalness01 - biome.Continentalness.Center) * 0.5f;
            }
            else
            {
                // Penalty grows rapidly outside the range
                var distMin = MathF.Abs(continentalness01 - biome.Continentalness.Min);
                var distMax = MathF.Abs(continentalness01 - biome.Continentalness.Max);
                var dist = MathF.Min(distMin, distMax);

                // Continuous penalty: start from edge value and increase.
                // NOTE: Keep this fairly steep so far-out biomes don't bleed into each other.
                contDist = (halfWidth * 0.5f) + (dist * 2.0f);
            }
            
            // Climate distance scoring
            var tempDist = biome.Temperature.Contains(temperature01) 
                ? 0f 
                : MathF.Min(MathF.Abs(temperature01 - biome.Temperature.Min),
                            MathF.Abs(temperature01 - biome.Temperature.Max));
            
            var humidDist = biome.Humidity.Contains(humidity01)
                ? 0f
                : MathF.Min(MathF.Abs(humidity01 - biome.Humidity.Min),
                            MathF.Abs(humidity01 - biome.Humidity.Max));
            
            // Priority bonus (higher priority = lower score)
            // REDUCED impact of priority to allow smoother blending
            // Was (100 - priority) * 0.01f -> range 0.0 to 1.0
            // Now (100 - priority) * 0.001f -> range 0.0 to 0.1
            var priorityBonus = (100 - biome.Priority) * 0.001f;
            
            // Combined score
            var score = tempDist + humidDist + contDist + priorityBonus;
            
            // Convert score to weight using Gaussian-like falloff for smooth blending
            // Previous 1/score method caused singularities and sharp transitions
            var weight = MathF.Exp(-score * WeightFalloff);
            
            results.Add((biome, weight));
            totalWeight += weight;
        }
        
        // Normalize weights and filter insignificant ones
        if (totalWeight > 0)
        {
            for (var i = 0; i < results.Count; i++)
            {
                var (b, w) = results[i];
                results[i] = (b, w / totalWeight);
            }
            
            // Keep all contributors.
            // Truncating to top-N can create hard discontinuities (contour-like cliff walls)
            // when the Nth/N+1th contributors swap order, followed by renormalization.
            results.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        }
    }
}
