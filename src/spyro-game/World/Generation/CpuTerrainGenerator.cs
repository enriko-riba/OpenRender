using System;
using System.Collections.Generic;
using System.Numerics;
using NoiseDotNet;

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

    private int currentChunkX;
    private int currentChunkZ;

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
    }

    /// <summary>
    /// Generate voxel descriptors and collision spans for the specified chunk.
    /// </summary>
    public ChunkGenerationResult GenerateChunk(int chunkIndex, Span<uint> destination, IReadOnlyDictionary<int, BlockDescriptor>? edits = null)
    {
        if (destination.Length < VoxelHelper.ChunkVoxelCount)
        {
            throw new ArgumentException($"Destination span must contain at least {VoxelHelper.ChunkVoxelCount} voxels", nameof(destination));
        }

        var collision = new ChunkCollisionData();
        var spanPairs = new int[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn * 2];
        var spanTypes = new byte[VoxelHelper.ChunkSideSizeSquare * ChunkCollisionData.MaxSpansPerColumn];
        var spanCounts = new byte[VoxelHelper.ChunkSideSizeSquare];

        FillChunk(chunkIndex, destination, collision, spanPairs, spanTypes, spanCounts, edits);

        return new ChunkGenerationResult(collision, spanPairs, spanCounts, spanTypes);
    }

    private void FillChunk(
        int chunkIndex,
        Span<uint> voxels,
        ChunkCollisionData collision,
        int[] spanPairs,
        byte[] spanTypes,
        byte[] spanCounts,
        IReadOnlyDictionary<int, BlockDescriptor>? edits)
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
                var spanDescriptor = BlockDescriptor.Air;

                for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
                {
                    var descriptor = GenerateBlockDescriptor(
                        height,
                        y,
                        worldX,
                        worldZ,
                        baseHeight,
                        continentalness01,
                        cheeseSlice,
                        spaghettiSlice,
                        overhangSlice);
                    var localIndex = y * VoxelHelper.ChunkSideSizeSquare + columnIndex;

                    if (edits != null && edits.TryGetValue(localIndex, out var editedDescriptor))
                    {
                        descriptor = editedDescriptor;
                    }

                    voxels[localIndex] = (uint)descriptor;

                    if (descriptor != BlockDescriptor.Air)
                    {
                        if (!inSpan)
                        {
                            inSpan = true;
                            spanStart = y;
                            spanDescriptor = descriptor;
                        }
                        else if (descriptor != spanDescriptor)
                        {
                            if (TryCommitSpan(columnIndex, spanBase, pairBase, spanTypes, spanPairs, spanCount, spanStart, y - 1, spanDescriptor, collision))
                            {
                                spanCount++;
                            }
                            spanStart = y;
                            spanDescriptor = descriptor;
                        }
                    }
                    else if (inSpan)
                    {
                        if (TryCommitSpan(columnIndex, spanBase, pairBase, spanTypes, spanPairs, spanCount, spanStart, y - 1, spanDescriptor, collision))
                        {
                            spanCount++;
                        }
                        inSpan = false;
                    }
                }

                if (inSpan)
                {
                    if (TryCommitSpan(columnIndex, spanBase, pairBase, spanTypes, spanPairs, spanCount, spanStart, VoxelHelper.ChunkYSize - 1, spanDescriptor, collision))
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

        for (var i = 0; i < ColumnCount; i++)
        {
            var tC = columnContinentalness01[i];
            var baseHeight = SampleHeightSpline(tC) + VoxelHelper.WaterLevel;

            if (tC >= terrainParams.OceanThreshold)
            {
                var ruggedness = 1f - columnErosion[i] * 0.5f - 0.5f;
                baseHeight += columnPeaks[i] * 20f * ruggedness;

                if (tC > terrainParams.MountainThreshold)
                {
                    var mountainness = Smoothstep(terrainParams.MountainThreshold, 0.95f, tC);
                    baseHeight += MathF.Abs(columnCliff[i]) * terrainParams.CliffAmplitude * mountainness;
                }
            }

            columnHeights[i] = baseHeight;
            var rounded = (int)MathF.Round(baseHeight);
            columnHeightInts[i] = Math.Clamp(rounded, 0, VoxelHelper.ChunkYSize - 1);
        }
    }

    private void BuildColumnVolumes()
    {
        var chunkY = VoxelHelper.ChunkYSize;
        var columnCount = ColumnCount;
        var xSpan = columnWorldX.AsSpan();
        var zSpan = columnWorldZ.AsSpan();
        var cheeseRow = scratch2DB.AsSpan();
        var spaghettiRowA = scratch2DC.AsSpan();
        var spaghettiRowB = scratch2DOutput.AsSpan();
        var overhangRow = sampleScratch2D.AsSpan();

        for (var y = 0; y < chunkY; y++)
        {
            SampleValueNoiseSlice(cheeseRow, xSpan, y, zSpan, terrainParams.CheeseFrequency, terrainParams.Seed + 300u, octaves: 2, persistence: 0.6f, lacunarity: 1.9f);
            SampleValueNoiseSlice(spaghettiRowA, xSpan, y, zSpan, terrainParams.SpaghettiFrequency, terrainParams.Seed + 400u, octaves: 1, persistence: 1f, lacunarity: 2f);
            SampleValueNoiseSlice(spaghettiRowB, xSpan, y, zSpan, terrainParams.SpaghettiFrequency, terrainParams.Seed + 500u, octaves: 1, persistence: 1f, lacunarity: 2f);
            SampleValueNoiseSlice(overhangRow, xSpan, y, zSpan, terrainParams.OverhangFrequency, terrainParams.Seed + 2000u, octaves: 2, persistence: 0.55f, lacunarity: 2f);

            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var sliceOffset = columnIndex * chunkY + y;

                var cheeseSample = (cheeseRow[columnIndex] * 2f - 1f) * terrainParams.CheeseAmplitude;
                cheeseVolume[sliceOffset] = Math.Clamp(cheeseSample, -1f, 1f);

                var n1 = spaghettiRowA[columnIndex] * 2f - 1f;
                var n2 = spaghettiRowB[columnIndex] * 2f - 1f;
                var dist = MathF.Sqrt(n1 * n1 + n2 * n2);
                var amp = Math.Clamp(terrainParams.SpaghettiAmplitude, 0.2f, 4f);
                var ampT = (amp - 0.2f) / 3.8f;
                var widthFactor = Lerp(2.8f, 1.1f, ampT);
                var tunnelWidth = 1f - dist * widthFactor;
                spaghettiVolume[sliceOffset] = Math.Clamp(tunnelWidth, -1f, 1f);

                var overhangSample = (overhangRow[columnIndex] * 2f - 1f) * terrainParams.OverhangAmplitude;
                overhangVolume[sliceOffset] = Math.Clamp(overhangSample, -1f, 1f);
            }
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

    private void SampleValueNoiseSlice(Span<float> destination, ReadOnlySpan<float> xCoords, float yCoord, ReadOnlySpan<float> zCoords, float baseFrequency, uint seed, int octaves, float persistence, float lacunarity)
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
        byte[] spanTypes,
        int[] spanPairs,
        int spanIndex,
        int startY,
        int endY,
        BlockDescriptor descriptor,
        ChunkCollisionData collision)
    {
        if (spanIndex >= ChunkCollisionData.MaxSpansPerColumn)
        {
            return false;
        }

        var spanId = spanBase + spanIndex;
        var pairId = pairBase + spanIndex * 2;
        spanTypes[spanId] = (byte)descriptor;
        spanPairs[pairId] = startY;
        spanPairs[pairId + 1] = endY + 1; // store exclusive end for chunk consumption
        var dstIndex = columnIndex * ChunkCollisionData.MaxSpansPerColumn + spanIndex;
        collision.Spans[dstIndex] = new ColumnSpan
        {
            StartY = (short)startY,
            EndY = (short)endY,
            BlockDescriptor = (byte)descriptor
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

    private BlockDescriptor GenerateBlockDescriptor(
        int height,
        int y,
        int wx,
        int wz,
        float baseHeight,
        float continentalness01,
        Span<float> cheeseSlice,
        Span<float> spaghettiSlice,
        Span<float> overhangSlice)
    {
        if (y > height)
        {
            return y <= VoxelHelper.WaterLevel ? BlockDescriptor.Water : BlockDescriptor.Air;
        }

        var tC = continentalness01;
        var isLand = tC >= terrainParams.OceanThreshold;

        if (isLand && tC > terrainParams.MountainThreshold && y > height - 50 && y > VoxelHelper.WaterLevel + 20)
        {
            var density = GetTerrainDensity(continentalness01, baseHeight, overhangSlice[y], y);
            if (density < 0f)
            {
                return BlockDescriptor.Air;
            }
        }

        if (isLand && y > 0)
        {
            var depth = height - y;
            var slope = depth < 15 ? GetSlope(wx, wz) : 0f;
            if (IsCave(depth, slope, cheeseSlice[y], spaghettiSlice[y]))
            {
                return y <= VoxelHelper.WaterLevel && y <= VoxelHelper.WaterLevel + terrainParams.CaveFloodExtension ? BlockDescriptor.Water : BlockDescriptor.Air;
            }
        }

        if (y == height)
        {
            return y < VoxelHelper.WaterLevel
                ? y >= VoxelHelper.WaterLevel - 1 ? BlockDescriptor.ShoreLine : BlockDescriptor.UnderwaterSubsurface
                : y <= VoxelHelper.WaterLevel + terrainParams.ShorelineRange && IsNearWater(wx, wz) ? BlockDescriptor.ShoreLine : BlockDescriptor.Surface;
        }

        var depthBelowSurface = height - y;
        return depthBelowSurface <= terrainParams.SubsurfaceDepth ? BlockDescriptor.Subsurface : BlockDescriptor.DeepSubsurface;
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
        byte[] SpanTypes);
}


