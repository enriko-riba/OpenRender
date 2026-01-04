using NoiseDotNet;
using DarkVox.Server.World;
using DarkVox.Shared.World;

namespace DarkVox.Server.World.Generation;

/// <summary>
/// Stateless helper for sampling climate noise.
/// Uses buffers from GenerationContext to avoid allocations.
/// </summary>
internal static class ClimateSampler
{
    public const int ColumnCount = VoxelHelper.ChunkSideSizeSquare;

    private static float ApplyContrast01(float value01, float contrastPower)
    {
        var raw = Math.Clamp(value01, 0f, 1f);
        var centered = raw - 0.5f;
        var sign = MathF.Sign(centered);
        var magnitude = MathF.Abs(centered) * 2f;
        var stretched = MathF.Pow(magnitude, Math.Max(0.01f, contrastPower));
        return Math.Clamp(sign * stretched * 0.5f + 0.5f, 0f, 1f);
    }

    /// <summary>
    /// Sample all climate parameters for a chunk using SIMD-batched noise operations.
    /// </summary>
    public static void SampleForChunk(int chunkX, int chunkZ, TerrainConfig config, GenerationContext ctx)
    {
        var terrainParams = config.GetGenerationParams();
        var seed = terrainParams.Seed;

        // Step 1: Build world coordinates for all 256 columns
        BuildWorldCoordinates(chunkX, chunkZ, ctx.WorldX, ctx.WorldZ);

        // Step 2: Sample domain warp noise (used to distort other noise)
        SampleDomainWarp(config.WarpScale, config.WarpStrength, seed, ctx);

        // Step 3: Sample all 6 climate parameters using SIMD batching
        SampleContinentalness(config.Continentalness, seed, ctx);
        SampleErosion(config.Erosion, seed, ctx);
        SamplePeaksValleys(config.PeaksValleys, seed, ctx);
        SampleTemperature(config, config.Temperature, seed, ctx);
        SampleHumidity(config, config.Humidity, seed, ctx);
        SampleWeirdness(config.Weirdness, seed, ctx);
        
        // Step 4: Sample aquifer noise (Phase 3: Deterministic water levels)
        SampleAquiferNoise(config.AquiferNoise, seed, ctx);
    }

    private static void BuildWorldCoordinates(int chunkX, int chunkZ, float[] worldX, float[] worldZ)
    {
        var baseX = chunkX * VoxelHelper.ChunkSideSize;
        var baseZ = chunkZ * VoxelHelper.ChunkSideSize;
        var idx = 0;
        
        for (var lz = 0; lz < VoxelHelper.ChunkSideSize; lz++)
        {
            var worldZVal = baseZ + lz;
            for (var lx = 0; lx < VoxelHelper.ChunkSideSize; lx++)
            {
                worldX[idx] = baseX + lx;
                worldZ[idx] = worldZVal;
                idx++;
            }
        }
    }

    private static void SampleDomainWarp(float warpScale, float warpStrength, uint seed, GenerationContext ctx)
    {
        var warpInputX = ctx.Scratch1.AsSpan();
        var warpInputZ = ctx.Scratch2.AsSpan();

        // Scale coordinates for warp sampling
        for (var i = 0; i < ColumnCount; i++)
        {
            warpInputX[i] = ctx.WorldX[i] * warpScale;
            warpInputZ[i] = ctx.WorldZ[i] * warpScale;
        }

        // Sample warp X
        SampleFbm2DBatched(warpInputX, warpInputZ, 1f, seed, 2, 0.5f, 2f, ctx.WarpX, ctx);

        // Sample warp Z with offset coordinates
        for (var i = 0; i < ColumnCount; i++)
        {
            ctx.Scratch3[i] = warpInputX[i] + 5.2f;
            warpInputZ[i] += 1.3f;
        }
        SampleFbm2DBatched(ctx.Scratch3.AsSpan(), warpInputZ, 1f, seed, 2, 0.5f, 2f, ctx.WarpZ, ctx);

        // Apply warp strength
        for (var i = 0; i < ColumnCount; i++)
        {
            ctx.WarpX[i] *= warpStrength;
            ctx.WarpZ[i] *= warpStrength;
        }
    }

    private static void SampleContinentalness(NoiseLayer layer, uint seed, GenerationContext ctx)
    {
        var warpedX = ctx.Scratch1.AsSpan();
        var warpedZ = ctx.Scratch2.AsSpan();

        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = ctx.WorldX[i] + ctx.WarpX[i];
            warpedZ[i] = ctx.WorldZ[i] + ctx.WarpZ[i];
        }

        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed, layer.Octaves, layer.Persistence, layer.Lacunarity, ctx.Continentalness, ctx);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(ctx.Continentalness[i] * layer.OutputScale, -1f, 1f);
            ctx.Continentalness[i] = n;

            const float ContrastPower = 0.8f;
            ctx.Continentalness01[i] = ApplyContrast01(n * 0.5f + 0.5f, ContrastPower);
        }
    }

    private static void SampleErosion(NoiseLayer layer, uint seed, GenerationContext ctx)
    {
        var warpedX = ctx.Scratch1.AsSpan();
        var warpedZ = ctx.Scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = ctx.WorldX[i] + ctx.WarpX[i];
            warpedZ[i] = ctx.WorldZ[i] + ctx.WarpZ[i];
        }

        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 200u, layer.Octaves, layer.Persistence, layer.Lacunarity, ctx.Erosion, ctx);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(ctx.Erosion[i] * layer.OutputScale, -1f, 1f);
            ctx.Erosion[i] = n;
            ctx.Erosion01[i] = n * 0.5f + 0.5f;
        }
    }

    private static void SamplePeaksValleys(NoiseLayer layer, uint seed, GenerationContext ctx)
    {
        var warpedX = ctx.Scratch1.AsSpan();
        var warpedZ = ctx.Scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = ctx.WorldX[i] + ctx.WarpX[i];
            warpedZ[i] = ctx.WorldZ[i] + ctx.WarpZ[i];
        }

        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 400u, layer.Octaves, layer.Persistence, layer.Lacunarity, ctx.Scratch3, ctx);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(ctx.Scratch3[i] * layer.OutputScale, -1f, 1f);
            ctx.Scratch3[i] = n;

            if (layer.UseRidged)
            {
                var ridge = 1f - MathF.Abs(n);
                ridge = Math.Clamp(ridge, 0f, 1f);
                ridge = MathF.Pow(ridge, Math.Max(0.01f, layer.RidgeSharpness));
                ctx.PeaksValleys[i] = ridge;
                ctx.PeaksValleys01[i] = ridge;
            }
            else
            {
                var pv01 = n * 0.5f + 0.5f;
                pv01 = Math.Clamp(pv01, 0f, 1f);
                ctx.PeaksValleys[i] = pv01;
                ctx.PeaksValleys01[i] = pv01;
            }
        }
    }

    private static void SampleTemperature(TerrainConfig config, NoiseLayer layer, uint seed, GenerationContext ctx)
    {
        var warpedX = ctx.Scratch1.AsSpan();
        var warpedZ = ctx.Scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = ctx.WorldX[i] + ctx.WarpX[i];
            warpedZ[i] = ctx.WorldZ[i] + ctx.WarpZ[i];
        }
        
        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 600u, layer.Octaves, layer.Persistence, layer.Lacunarity, ctx.Temperature, ctx);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(ctx.Temperature[i] * layer.OutputScale, -1f, 1f);
            ctx.Temperature[i] = n;

            const float ContrastPower = 0.8f;
            var noise01 = ApplyContrast01(n * 0.5f + 0.5f, ContrastPower);
            var t = (config.BaseTemperature - 0.5f) + noise01;
            ctx.Temperature01[i] = Math.Clamp(t, 0f, 1f);
        }
    }

    private static void SampleHumidity(TerrainConfig config, NoiseLayer layer, uint seed, GenerationContext ctx)
    {
        var warpedX = ctx.Scratch1.AsSpan();
        var warpedZ = ctx.Scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = ctx.WorldX[i] + ctx.WarpX[i];
            warpedZ[i] = ctx.WorldZ[i] + ctx.WarpZ[i];
        }
        
        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 700u, layer.Octaves, layer.Persistence, layer.Lacunarity, ctx.Humidity, ctx);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(ctx.Humidity[i] * layer.OutputScale, -1f, 1f);
            ctx.Humidity[i] = n;

            const float ContrastPower = 0.8f;
            var noise01 = ApplyContrast01(n * 0.5f + 0.5f, ContrastPower);
            var hum = (config.BaseHumidity - 0.5f) + noise01;

            var cont01 = ctx.Continentalness01[i];
            var inland = MathF.Max(0f, cont01 - config.CoastThreshold);
            hum -= inland * config.CoastDrying;

            ctx.Humidity01[i] = Math.Clamp(hum, 0f, 1f);
        }
    }

    private static void SampleWeirdness(NoiseLayer layer, uint seed, GenerationContext ctx)
    {
        SampleFbm2DBatched(ctx.WorldX.AsSpan(), ctx.WorldZ.AsSpan(), layer.BaseScale, seed + 800u, layer.Octaves, layer.Persistence, layer.Lacunarity, ctx.Weirdness, ctx);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(ctx.Weirdness[i] * layer.OutputScale, -1f, 1f);
            ctx.Weirdness[i] = n;
            ctx.Weirdness01[i] = n * 0.5f + 0.5f;
        }
    }

    private static void SampleAquiferNoise(NoiseLayer layer, uint seed, GenerationContext ctx)
    {
        var warpedX = ctx.Scratch1.AsSpan();
        var warpedZ = ctx.Scratch2.AsSpan();
        for (var i = 0; i < ColumnCount; i++)
        {
            warpedX[i] = ctx.WorldX[i] + ctx.WarpX[i] * 0.7f;
            warpedZ[i] = ctx.WorldZ[i] + ctx.WarpZ[i] * 0.7f;
        }
        
        SampleFbm2DBatched(warpedX, warpedZ, layer.BaseScale, seed + 1000u, layer.Octaves, layer.Persistence, layer.Lacunarity, ctx.AquiferNoise, ctx);

        for (var i = 0; i < ColumnCount; i++)
        {
            var n = Math.Clamp(ctx.AquiferNoise[i] * layer.OutputScale, -1f, 1f);
            ctx.AquiferNoise[i] = n;
            ctx.AquiferNoise01[i] = n * 0.5f + 0.5f;
        }
    }

    private static void SampleFbm2DBatched(
        ReadOnlySpan<float> xCoords,
        ReadOnlySpan<float> zCoords,
        float baseFrequency,
        uint seed,
        int octaves,
        float persistence,
        float lacunarity,
        Span<float> output,
        GenerationContext ctx)
    {
        var length = ColumnCount;
        output.Clear();

        var amplitude = 1f;
        var frequency = baseFrequency;
        var totalAmplitude = 0f;
        
        var octaveScratch = ctx.NoiseScratchOctave.AsSpan(0, length);
        var xCoordsWritable = ctx.NoiseScratchX.AsSpan(0, length);
        var zCoordsWritable = ctx.NoiseScratchZ.AsSpan(0, length);
        xCoords[..length].CopyTo(xCoordsWritable);
        zCoords[..length].CopyTo(zCoordsWritable);

        for (var octave = 0; octave < octaves; octave++)
        {
            Noise.GradientNoise2D(
                xCoordsWritable,
                zCoordsWritable,
                octaveScratch,
                frequency,
                frequency,
                amplitude,
                unchecked((int)(seed + (uint)(octave * 132))));

            for (var i = 0; i < length; i++)
            {
                output[i] += octaveScratch[i];
            }

            totalAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        if (totalAmplitude > 0f)
        {
            var inv = 1f / totalAmplitude;
            for (var i = 0; i < length; i++)
            {
                output[i] *= inv;
            }
        }
    }
}
