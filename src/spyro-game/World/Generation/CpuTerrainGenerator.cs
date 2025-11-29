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
    private readonly float[] lineX3D = new float[VoxelHelper.ChunkYSize];
    private readonly float[] lineY3D = new float[VoxelHelper.ChunkYSize];
    private readonly float[] lineZ3D = new float[VoxelHelper.ChunkYSize];
    private readonly float[] scratch3DA = new float[VoxelHelper.ChunkYSize];
    private readonly float[] scratch3DB = new float[VoxelHelper.ChunkYSize];
    private readonly float[] scratch3DOutput = new float[VoxelHelper.ChunkYSize];

    private int currentChunkX;
    private int currentChunkZ;

    public CpuTerrainGenerator(TerrainConfig config)
    {
        UpdateConfig(config);
        for (var i = 0; i < lineY3D.Length; i++)
        {
            lineY3D[i] = i;
        }
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
        var ySpan = lineY3D.AsSpan();
        for (var columnIndex = 0; columnIndex < ColumnCount; columnIndex++)
        {
            var xValue = columnWorldX[columnIndex];
            var zValue = columnWorldZ[columnIndex];
            for (var y = 0; y < chunkY; y++)
            {
                lineX3D[y] = xValue;
                lineZ3D[y] = zValue;
            }

            var sliceOffset = columnIndex * chunkY;
            var cheeseSlice = cheeseVolume.AsSpan(sliceOffset, chunkY);
            SampleFbm3D(lineX3D.AsSpan(), ySpan, lineZ3D.AsSpan(), terrainParams.CheeseFrequency, terrainParams.Seed + 300u, 2, 0.5f, 2f, cheeseSlice);

            var n1 = scratch3DA.AsSpan();
            var n2 = scratch3DB.AsSpan();
            SampleFbm3D(lineX3D.AsSpan(), ySpan, lineZ3D.AsSpan(), terrainParams.SpaghettiFrequency, terrainParams.Seed + 400u, 2, 0.5f, 2f, n1);
            SampleFbm3D(lineX3D.AsSpan(), ySpan, lineZ3D.AsSpan(), terrainParams.SpaghettiFrequency, terrainParams.Seed + 500u, 2, 0.5f, 2f, n2);

            var spaghettiSlice = spaghettiVolume.AsSpan(sliceOffset, chunkY);
            for (var y = 0; y < chunkY; y++)
            {
                var dist = MathF.Sqrt(n1[y] * n1[y] + n2[y] * n2[y]);
                spaghettiSlice[y] = 1f - dist * 4f;
            }

            var overhangSlice = overhangVolume.AsSpan(sliceOffset, chunkY);
            SampleFbm3D(lineX3D.AsSpan(), ySpan, lineZ3D.AsSpan(), terrainParams.OverhangFrequency, terrainParams.Seed + 2000u, 3, 0.5f, 2f, overhangSlice);
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


