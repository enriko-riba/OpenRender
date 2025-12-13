using NoiseDotNet;
using System.Diagnostics;

namespace SpyroGame.World;

/// <summary>
/// Pre-allocated cache for climate noise values sampled once per chunk.
/// All 2D climate parameters are sampled in a single SIMD-batched pass and reused
/// throughout terrain generation, avoiding redundant noise calls.
/// 
/// This is the foundation of the Minecraft-style terrain generation pipeline where:
/// 1. Climate is sampled ONCE per column (256 columns per chunk)
/// 2. Height calculation uses cached climate values (no noise calls)
/// 3. Biome selection uses cached climate values (no noise calls)
/// 4. Block generation uses cached heights + interpolated 3D noise (no 2D noise calls)
/// </summary>
/// <remarks>
/// Performance target: &lt;2ms for all 6 climate parameters × 256 columns using SIMD batching.
/// This class is designed to be reused across chunks (call <see cref="SampleForChunk"/> per chunk).
/// </remarks>
internal sealed class ChunkClimateCache
{
    /// <summary>Number of columns per chunk (16×16).</summary>
    public const int ColumnCount = VoxelHelper.ChunkSideSizeSquare;

    // Pre-allocated arrays for 6 climate parameters (all in [-1, 1] range)
    private readonly float[] _continentalness = new float[ColumnCount];
    private readonly float[] _erosion = new float[ColumnCount];
    private readonly float[] _peaksValleys = new float[ColumnCount];
    private readonly float[] _temperature = new float[ColumnCount];
    private readonly float[] _humidity = new float[ColumnCount];
    private readonly float[] _weirdness = new float[ColumnCount];
    private readonly float[] _aquiferNoise = new float[ColumnCount];  // Phase 3: Aquifer water level noise

    // Normalized versions [0, 1] for convenience
    private readonly float[] _continentalness01 = new float[ColumnCount];
    private readonly float[] _erosion01 = new float[ColumnCount];
    private readonly float[] _peaksValleys01 = new float[ColumnCount];
    private readonly float[] _temperature01 = new float[ColumnCount];
    private readonly float[] _humidity01 = new float[ColumnCount];
    private readonly float[] _weirdness01 = new float[ColumnCount];
    private readonly float[] _aquiferNoise01 = new float[ColumnCount];  // Phase 3: Normalized aquifer noise

    // Domain warp values (cached separately as they're used to warp other noise)
    private readonly float[] _warpX = new float[ColumnCount];
    private readonly float[] _warpZ = new float[ColumnCount];

    // World coordinates for this chunk's columns
    private readonly float[] _worldX = new float[ColumnCount];
    private readonly float[] _worldZ = new float[ColumnCount];

    // Scratch buffers for intermediate calculations (avoid allocations)
    private readonly float[] _scratch1 = new float[ColumnCount];
    private readonly float[] _scratch2 = new float[ColumnCount];
    private readonly float[] _scratch3 = new float[ColumnCount];
    
    // Dedicated scratch buffers for SampleFbm2DBatched (separate from caller scratch buffers)
    private readonly float[] _noiseScratchOctave = new float[ColumnCount];
    private readonly float[] _noiseScratchX = new float[ColumnCount];
    private readonly float[] _noiseScratchZ = new float[ColumnCount];

    // Current chunk coordinates
    private int _chunkX;
    private int _chunkZ;
    private bool _isValid;

    private static float ApplyContrast01(float value01, float contrastPower)
    {
        var raw = Math.Clamp(value01, 0f, 1f);
        var centered = raw - 0.5f;
        var sign = MathF.Sign(centered);
        var magnitude = MathF.Abs(centered) * 2f;
        var stretched = MathF.Pow(magnitude, Math.Max(0.01f, contrastPower));
        return Math.Clamp(sign * stretched * 0.5f + 0.5f, 0f, 1f);
    }

#if DEBUG
    private readonly Stopwatch _stepTimer = new();
    private long _lastSampleTicks;
    
    /// <summary>Gets the time spent on the last SampleForChunk call in milliseconds.</summary>
    public double LastSampleTimeMs => _lastSampleTicks * 1000.0 / Stopwatch.Frequency;
#endif

    /// <summary>Gets the raw continentalness values [-1, 1].</summary>
    public ReadOnlySpan<float> Continentalness => _continentalness;
    
    /// <summary>Gets normalized continentalness values [0, 1].</summary>
    public ReadOnlySpan<float> Continentalness01 => _continentalness01;
    
    /// <summary>Gets the raw erosion values [-1, 1].</summary>
    public ReadOnlySpan<float> Erosion => _erosion;
    
    /// <summary>Gets normalized erosion values [0, 1].</summary>
    public ReadOnlySpan<float> Erosion01 => _erosion01;
    
    /// <summary>Gets the raw peaks/valleys values (transformed ridge noise) [0, 1].</summary>
    public ReadOnlySpan<float> PeaksValleys => _peaksValleys;
    
    /// <summary>Gets normalized peaks/valleys values [0, 1].</summary>
    public ReadOnlySpan<float> PeaksValleys01 => _peaksValleys01;
    
    /// <summary>Gets the raw temperature values [-1, 1].</summary>
    public ReadOnlySpan<float> Temperature => _temperature;
    
    /// <summary>Gets normalized temperature values [0, 1].</summary>
    public ReadOnlySpan<float> Temperature01 => _temperature01;
    
    /// <summary>Gets the raw humidity values [-1, 1].</summary>
    public ReadOnlySpan<float> Humidity => _humidity;
    
    /// <summary>Gets normalized humidity values [0, 1].</summary>
    public ReadOnlySpan<float> Humidity01 => _humidity01;
    
    /// <summary>Gets the raw weirdness values [-1, 1].</summary>
    public ReadOnlySpan<float> Weirdness => _weirdness;
    
    /// <summary>Gets normalized weirdness values [0, 1].</summary>
    public ReadOnlySpan<float> Weirdness01 => _weirdness01;
    
    /// <summary>Gets the raw aquifer noise values [-1, 1]. Phase 3: Aquifer water levels.</summary>
    public ReadOnlySpan<float> AquiferNoise => _aquiferNoise;
    
    /// <summary>Gets normalized aquifer noise values [0, 1]. Phase 3: Aquifer water levels.</summary>
    public ReadOnlySpan<float> AquiferNoise01 => _aquiferNoise01;
    
    /// <summary>Gets the domain warp X offsets.</summary>
    public ReadOnlySpan<float> WarpX => _warpX;
    
    /// <summary>Gets the domain warp Z offsets.</summary>
    public ReadOnlySpan<float> WarpZ => _warpZ;
    
    /// <summary>Gets the world X coordinates for all columns.</summary>
    public ReadOnlySpan<float> WorldX => _worldX;
    
    /// <summary>Gets the world Z coordinates for all columns.</summary>
    public ReadOnlySpan<float> WorldZ => _worldZ;

    /// <summary>Gets whether the cache contains valid data for a chunk.</summary>
    public bool IsValid => _isValid;
    
    /// <summary>Gets the chunk X coordinate for the cached data.</summary>
    public int ChunkX => _chunkX;
    
    /// <summary>Gets the chunk Z coordinate for the cached data.</summary>
    public int ChunkZ => _chunkZ;

    /// <summary>
    /// Sample all climate parameters for a chunk using SIMD-batched noise operations.
    /// This is the ONLY place where 2D climate noise is sampled.
    /// </summary>
    /// <param name="chunkX">Chunk X coordinate.</param>
    /// <param name="chunkZ">Chunk Z coordinate.</param>
    /// <param name="config">Terrain configuration with noise parameters.</param>
    public void SampleForChunk(int chunkX, int chunkZ, TerrainConfig config)
    {
#if DEBUG
        _stepTimer.Restart();
#endif
        _chunkX = chunkX;
        _chunkZ = chunkZ;
        _isValid = false;

        var terrainParams = config.GetGenerationParams();
        var seed = terrainParams.Seed;

        // Step 1: Build world coordinates for all 256 columns
        BuildWorldCoordinates(chunkX, chunkZ);

        // Step 2: Sample domain warp noise (used to distort other noise)
        // Note: Warp settings are currently global in TerrainConfig, but could be moved to a NoiseLayer too
        SampleDomainWarp(config.WarpScale, config.WarpStrength, seed);

        // Step 3: Sample all 6 climate parameters using SIMD batching
        // Each parameter is sampled with domain-warped coordinates where applicable
        SampleContinentalness(config.Continentalness, seed);
        SampleErosion(config.Erosion, seed);
        SamplePeaksValleys(config.PeaksValleys, seed);
        SampleTemperature(config, config.Temperature, seed);
        SampleHumidity(config, config.Humidity, seed);
        SampleWeirdness(config.Weirdness, seed);
        
        // Step 4: Sample aquifer noise (Phase 3: Deterministic water levels)
        SampleAquiferNoise(config.AquiferNoise, seed);

        _isValid = true;

#if DEBUG
        _lastSampleTicks = _stepTimer.ElapsedTicks;
#endif
    }

    /// <summary>
    /// Build world coordinates for all columns in the chunk.
    /// </summary>
    private void BuildWorldCoordinates(int chunkX, int chunkZ)
    {
        var baseX = chunkX * VoxelHelper.ChunkSideSize;
        var baseZ = chunkZ * VoxelHelper.ChunkSideSize;
        var idx = 0;
        
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            var worldZ = baseZ + lz;
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                _worldX[idx] = baseX + lx;
                _worldZ[idx] = worldZ;
                idx++;
            }
        }
    }

    /// <summary>
    /// Sample domain warp noise used to distort climate sampling coordinates.
    /// This creates more organic, less grid-aligned noise patterns.
    /// </summary>
    private void SampleDomainWarp(float warpScale, float warpStrength, uint seed)
    {
        var warpInputX = _scratch1.AsSpan();
        var warpInputZ = _scratch2.AsSpan();

        // Scale coordinates for warp sampling
        for (var i = 0; i < ColumnCount; i++)
        {
            warpInputX[i] = _worldX[i] * warpScale;
            warpInputZ[i] = _worldZ[i] * warpScale;
        }

        // Sample warp X
        SampleFbm2DBatched(warpInputX, warpInputZ, 1f, seed, 2, 0.5f, 2f, _warpX);

        // Sample warp Z with offset coordinates
        for (var i = 0; i < ColumnCount; i++)
        {
            _scratch3[i] = warpInputX[i] + 5.2f;
            warpInputZ[i] += 1.3f;
        }
        SampleFbm2DBatched(_scratch3.AsSpan(), warpInputZ, 1f, seed, 2, 0.5f, 2f, _warpZ);

        // Apply warp strength
        for (var i = 0; i < ColumnCount; i++)
        {
            _warpX[i] *= warpStrength;
            _warpZ[i] *= warpStrength;
        }
    }

    /// <summary>
    /// Sample continentalness using domain-warped coordinates.
    /// Controls ocean vs land distribution.
    /// </summary>
    private void SampleContinentalness(NoiseLayer layer, uint seed)
    {
        var warpedX = _scratch1.AsSpan();
        var warpedZ = _scratch2.AsSpan();

        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = _worldX[i] + _warpX[i];
            warpedZ[i] = _worldZ[i] + _warpZ[i];
        }

        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed, layer.Octaves, layer.Persistence, layer.Lacunarity, _continentalness);

        // Normalize to [0, 1] with contrast boost
        for (var i = 0; i < ColumnCount; i++)
        {
            // Apply layer scaling and clamp back into expected [-1, 1] domain.
            // This lets OutputScale widen the effective distribution without destabilizing downstream logic.
            var n = Math.Clamp(_continentalness[i] * layer.OutputScale, -1f, 1f);
            _continentalness[i] = n;

            // Contrast shaping helps local sampling reach both ocean/continent bands,
            // but too much contrast collapses midlands. Keep this modest.
            const float ContrastPower = 0.8f;
            _continentalness01[i] = ApplyContrast01(n * 0.5f + 0.5f, ContrastPower);
        }
    }

    /// <summary>
    /// Sample erosion noise. Controls terrain smoothness vs roughness.
    /// </summary>
    private void SampleErosion(NoiseLayer layer, uint seed)
    {
        var warpedX = _scratch1.AsSpan();
        var warpedZ = _scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = _worldX[i] + _warpX[i];
            warpedZ[i] = _worldZ[i] + _warpZ[i];
        }

        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 200u, layer.Octaves, layer.Persistence, layer.Lacunarity, _erosion);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(_erosion[i] * layer.OutputScale, -1f, 1f);
            _erosion[i] = n;
            _erosion01[i] = n * 0.5f + 0.5f;
        }
    }

    /// <summary>
    /// Sample peaks/valleys noise. Controls terrain ridges and valleys.
    /// </summary>
    private void SamplePeaksValleys(NoiseLayer layer, uint seed)
    {
        var warpedX = _scratch1.AsSpan();
        var warpedZ = _scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = _worldX[i] + _warpX[i];
            warpedZ[i] = _worldZ[i] + _warpZ[i];
        }

        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 400u, layer.Octaves, layer.Persistence, layer.Lacunarity, _scratch3);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(_scratch3[i] * layer.OutputScale, -1f, 1f);
            _scratch3[i] = n;

            if (layer.UseRidged)
            {
                // Ridged variant: strong peaks, few valleys (useful for ridge networks).
                var ridge = 1f - MathF.Abs(n);
                ridge = Math.Clamp(ridge, 0f, 1f);
                ridge = MathF.Pow(ridge, Math.Max(0.01f, layer.RidgeSharpness));
                _peaksValleys[i] = ridge;
                _peaksValleys01[i] = ridge;
            }
            else
            {
                // Signed variant mapped to [0,1]: supports both peaks and valleys.
                var pv01 = n * 0.5f + 0.5f;
                pv01 = Math.Clamp(pv01, 0f, 1f);
                _peaksValleys[i] = pv01;
                _peaksValleys01[i] = pv01;
            }
        }
    }

    /// <summary>
    /// Sample temperature noise. Affects biome climate zones.
    /// Uses domain-warped coordinates for organic biome borders.
    /// </summary>
    private void SampleTemperature(TerrainConfig config, NoiseLayer layer, uint seed)
    {
        var warpedX = _scratch1.AsSpan();
        var warpedZ = _scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = _worldX[i] + _warpX[i];
            warpedZ[i] = _worldZ[i] + _warpZ[i];
        }
        
        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 600u, layer.Octaves, layer.Persistence, layer.Lacunarity, _temperature);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(_temperature[i] * layer.OutputScale, -1f, 1f);
            _temperature[i] = n;

            // Map noise to [0,1] and apply a mild contrast curve so the field spans hot/cold
            // enough for rare biomes (rainforest/tundra) without per-chunk normalization.
            // BaseTemperature acts as a global offset around 0.5.
            const float ContrastPower = 0.8f;
            var noise01 = ApplyContrast01(n * 0.5f + 0.5f, ContrastPower);
            var t = (config.BaseTemperature - 0.5f) + noise01;
            _temperature01[i] = Math.Clamp(t, 0f, 1f);
        }
    }

    /// <summary>
    /// Sample humidity noise. Affects wet vs dry biomes.
    /// Uses domain-warped coordinates for organic biome borders.
    /// </summary>
    private void SampleHumidity(TerrainConfig config, NoiseLayer layer, uint seed)
    {
        var warpedX = _scratch1.AsSpan();
        var warpedZ = _scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = _worldX[i] + _warpX[i];
            warpedZ[i] = _worldZ[i] + _warpZ[i];
        }
        
        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 700u, layer.Octaves, layer.Persistence, layer.Lacunarity, _humidity);

        for (var i = 0; i < ColumnCount; i++)
        {
            // Base humidity plus noise, then apply continental drying beyond CoastThreshold.
            var n = Math.Clamp(_humidity[i] * layer.OutputScale, -1f, 1f);
            _humidity[i] = n;

            const float ContrastPower = 0.8f;
            var noise01 = ApplyContrast01(n * 0.5f + 0.5f, ContrastPower);
            var hum = (config.BaseHumidity - 0.5f) + noise01;

            var cont01 = _continentalness01[i];
            var inland = MathF.Max(0f, cont01 - config.CoastThreshold);
            hum -= inland * config.CoastDrying;

            _humidity01[i] = Math.Clamp(hum, 0f, 1f);
        }
    }

    /// <summary>
    /// Sample weirdness noise. Controls terrain "weirdness" for unique features.
    /// </summary>
    private void SampleWeirdness(NoiseLayer layer, uint seed)
    {
        SampleFbm2DBatched(_worldX.AsSpan(), _worldZ.AsSpan(), layer.BaseScale, seed + 800u, layer.Octaves, layer.Persistence, layer.Lacunarity, _weirdness);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(_weirdness[i] * layer.OutputScale, -1f, 1f);
            _weirdness[i] = n;
            _weirdness01[i] = n * 0.5f + 0.5f;
        }
    }

    /// <summary>
    /// Sample aquifer noise. Phase 3: Controls water level variation for inland water bodies.
    /// Uses low-frequency noise for coherent water level regions.
    /// </summary>
    private void SampleAquiferNoise(NoiseLayer layer, uint seed)
    {
        var warpedX = _scratch1.AsSpan();
        var warpedZ = _scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            // Moderate warp for aquifer - creates organic water body shapes
            warpedX[i] = _worldX[i] + _warpX[i] * 0.7f;
            warpedZ[i] = _worldZ[i] + _warpZ[i] * 0.7f;
        }
        
        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 1000u, layer.Octaves, layer.Persistence, layer.Lacunarity, _aquiferNoise);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(_aquiferNoise[i] * layer.OutputScale, -1f, 1f);
            _aquiferNoise[i] = n;
            _aquiferNoise01[i] = n * 0.5f + 0.5f;
        }
    }

    /// <summary>
    /// SIMD-batched 2D FBM noise sampling using NoiseDotNet.
    /// Processes all 256 columns efficiently using Vector256&lt;float&gt; internally.
    /// </summary>
    /// <remarks>
    /// NoiseDotNet's GradientNoise2D internally uses SIMD (AVX2/SSE) when available.
    /// This method accumulates multiple octaves of noise for FBM.
    /// </remarks>
    private void SampleFbm2DBatched(
        ReadOnlySpan<float> xCoords,
        ReadOnlySpan<float> zCoords,
        float baseFrequency,
        uint seed,
        int octaves,
        float persistence,
        float lacunarity,
        Span<float> output)
    {
        var length = output.Length;
        output.Clear();

        var amplitude = 1f;
        var frequency = baseFrequency;
        var totalAmplitude = 0f;
        
        // Use dedicated scratch buffers for noise sampling (separate from caller's scratch buffers)
        var octaveScratch = _noiseScratchOctave.AsSpan(0, length);
        
        // NoiseDotNet requires writable Spans, so copy input to dedicated scratch buffers
        var xCoordsWritable = _noiseScratchX.AsSpan(0, length);
        var zCoordsWritable = _noiseScratchZ.AsSpan(0, length);
        xCoords[..length].CopyTo(xCoordsWritable);
        zCoords[..length].CopyTo(zCoordsWritable);

        for (var octave = 0; octave < octaves; octave++)
        {
            // NoiseDotNet internally uses SIMD (Vector256<float>) when available
            Noise.GradientNoise2D(
                xCoordsWritable,
                zCoordsWritable,
                octaveScratch,
                frequency,
                frequency,
                amplitude,
                unchecked((int)(seed + (uint)(octave * 132))));

            // Accumulate into output
            for (var i = 0; i < length; i++)
            {
                output[i] += octaveScratch[i];
            }

            totalAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        // Normalize
        if (totalAmplitude > 0f)
        {
            var inv = 1f / totalAmplitude;
            for (var i = 0; i < length; i++)
            {
                output[i] *= inv;
            }
        }
    }

    /// <summary>
    /// Invalidate the cache. Call when chunk coordinates change or config is updated.
    /// </summary>
    public void Invalidate()
    {
        _isValid = false;
    }

    /// <summary>
    /// Get a single column's climate values by index.
    /// </summary>
    /// <param name="columnIndex">Column index (0-255).</param>
    /// <returns>Climate values for the column.</returns>
    public ColumnClimate GetColumnClimate(int columnIndex)
    {
        if ((uint)columnIndex >= ColumnCount)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));

        return new ColumnClimate
        {
            Continentalness = _continentalness[columnIndex],
            Continentalness01 = _continentalness01[columnIndex],
            Erosion = _erosion[columnIndex],
            Erosion01 = _erosion01[columnIndex],
            PeaksValleys = _peaksValleys[columnIndex],
            PeaksValleys01 = _peaksValleys01[columnIndex],
            Temperature = _temperature[columnIndex],
            Temperature01 = _temperature01[columnIndex],
            Humidity = _humidity[columnIndex],
            Humidity01 = _humidity01[columnIndex],
            Weirdness = _weirdness[columnIndex],
            Weirdness01 = _weirdness01[columnIndex],
            AquiferNoise = _aquiferNoise[columnIndex],
            AquiferNoise01 = _aquiferNoise01[columnIndex]
        };
    }
}

/// <summary>
/// Climate values for a single column. Used for debugging and single-point queries.
/// </summary>
internal readonly struct ColumnClimate
{
    public float Continentalness { get; init; }
    public float Continentalness01 { get; init; }
    public float Erosion { get; init; }
    public float Erosion01 { get; init; }
    public float PeaksValleys { get; init; }
    public float PeaksValleys01 { get; init; }
    public float Temperature { get; init; }
    public float Temperature01 { get; init; }
    public float Humidity { get; init; }
    public float Humidity01 { get; init; }
    public float Weirdness { get; init; }
    public float Weirdness01 { get; init; }
    public float AquiferNoise { get; init; }
    public float AquiferNoise01 { get; init; }
}
