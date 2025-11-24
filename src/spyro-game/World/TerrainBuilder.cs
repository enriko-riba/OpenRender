using SpyroGame.Noise;
using System.Runtime.CompilerServices;

namespace SpyroGame.World;

public class TerrainBuilder
{
    private readonly struct DomainWarpSettings(float baseFreq, float warpFreq, float warpAmp, int seed)
    {
        public float BaseFreq { get; } = baseFreq;
        public float WarpFreq { get; } = warpFreq;
        public float WarpAmp { get; } = warpAmp;
        public int Seed { get; } = seed;
    }

    private readonly DomainWarpSettings baseNoiseSettings;
    private readonly DomainWarpSettings ridgeNoiseSettings;
    private readonly int seed;

    private readonly WorldFields Fields;
    private readonly Biome[] Biomes;
    private float elevOffset, elevScale;
    private GenParams P;


    public TerrainBuilder(int seed)
    {
        this.seed = seed;

        var worldSize = VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize;

        // High-res, domain-warped base noise for the whole world (sampled on demand per chunk).
        baseNoiseSettings = new DomainWarpSettings(
            baseFreq: 1f / 180f,
            warpFreq: 1f / 900f,
            warpAmp: 8f,
            seed: seed ^ 0x12345);

        // Ridge detail layer – keep stronger for cliffs (sampled on demand per chunk).
        ridgeNoiseSettings = new DomainWarpSettings(
            baseFreq: 1f / 220f,
            warpFreq: 1f / 550f,
            warpAmp: 14f,
            seed: seed ^ 0x5A17C3);

        // Low-res global fields (continentalness / erosion / temp / humidity)
        Fields = new WorldFields(worldSize, texSize: 1024,
            cf: 1f / 15000f, ef: 1f / 4000f, tf: 1f / 5000f, hf: 1f / 5000f, seed);

        // Biome table (tiny)
        // IMPORTANT: Keep docs/terrain/terrain_generation_architecture.md in sync when adjusting this list.
        Biomes =
        [
            new Biome{ Name="Ocean",      C=0.25f, E=0.6f,  T=0.6f,  H=0.85f, Top=BlockType.WaterLevel },
            new Biome{ Name="Beach",      C=0.35f, E=0.6f,  T=0.65f, H=0.55f, Top=BlockType.Sand },
            new Biome{ Name="Swamp",      C=0.45f, E=0.5f,  T=0.65f, H=0.85f, Top=BlockType.Dirt },
            new Biome{ Name="Plains",     C=0.55f, E=0.7f,  T=0.55f, H=0.5f,  Top=BlockType.GrassDirt },
            new Biome{ Name="Savanna",    C=0.60f, E=0.6f,  T=0.6f,  H=0.35f, Top=BlockType.GrassDirt },
            new Biome{ Name="Desert",     C=0.50f, E=0.4f,  T=0.75f, H=0.2f,  Top=BlockType.Sand },
            new Biome{ Name="Rainforest", C=0.60f, E=0.55f, T=0.7f,  H=0.85f, Top=BlockType.Grass },
            new Biome{ Name="Taiga",      C=0.65f, E=0.6f,  T=0.3f,  H=0.6f,  Top=BlockType.GrassDirt },
            new Biome{ Name="Tundra",     C=0.70f, E=0.5f,  T=0.25f, H=0.35f, Top=BlockType.Snow },
            new Biome{ Name="Highlands",  C=0.75f, E=0.45f, T=0.45f, H=0.4f,  Top=BlockType.Gravel },
            new Biome{ Name="Alpine",     C=0.85f, E=0.35f, T=0.2f,  H=0.45f, Top=BlockType.Snow }
        ];

        P = new GenParams
        {
            SeaMin = -0.55f,
            SeaMax = 0.30f,
            MtnLow = 0.30f,
            MtnHigh = 2.20f,
            TerraceStep = 0.025f,
            TerraceStrength = 0.30f,
            RidgePow = 1.9f,
            SmoothPow = 0.8f
        };

        CalibrateElevation(worldSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Smooth01(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3 - 2 * x); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saturate(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);

    private float SampleBaseNoise(int wx, int wz)
    {
        var sample = NoiseData.SampleDomainWarped(wx, wz, baseNoiseSettings.BaseFreq, baseNoiseSettings.WarpFreq, baseNoiseSettings.WarpAmp, baseNoiseSettings.Seed);
        return Math.Clamp(sample, -1f, 1f);
    }

    private float SampleRidgeNoise(int wx, int wz)
    {
        var sample = NoiseData.SampleDomainWarped(wx, wz, ridgeNoiseSettings.BaseFreq, ridgeNoiseSettings.WarpFreq, ridgeNoiseSettings.WarpAmp, ridgeNoiseSettings.Seed);
        return Math.Clamp(sample, -1f, 1f);
    }

    private float HeightRaw(int wx, int wz, float baseVal)
    {
        var C = 0.5f * (Fields.Sample(Fields.C, wx, wz) + 1f);
        var E = 0.5f * (Fields.Sample(Fields.E, wx, wz) + 1f);

        var fbm = 0.5f * (baseVal + 1f);                      // [0,1]
        var rid = 1f - MathF.Abs(SampleRidgeNoise(wx, wz));   // [0,1]
        rid = MathF.Pow(Saturate(rid), P.RidgePow);

        var ruggedWeight = MathF.Pow(1f - E, 1.6f) * Lerp(0.5f, 1.2f, C);
        var relief = Lerp(MathF.Pow(fbm, P.SmoothPow), rid, ruggedWeight);

        var seaBias = Lerp(P.SeaMin, P.SeaMax, C);
        var mountainK = Lerp(P.MtnLow, P.MtnHigh, C);

        var h = seaBias + (relief - 0.5f) * mountainK;

        // --- Carve connected valleys (band near 0 of anisotropic noise) ---
        var r = NoiseData.SampleGradientNoise2D(wx * 0.0009f, wz * 0.00045f, 1f, 1f, seed ^ 0xBEEF);
        var band = 1f - Smooth01(MathF.Abs(r));                 // high near river "centerlines"
        var riverDepth = Lerp(0.02f, 0.14f, C) * Lerp(1.0f, 0.5f, E); // inland & low erosion → deeper
        h -= band * riverDepth;                                    // carve elongated, connected valleys

        h += (rid - 0.5f) * (C * C) * 0.45f;          // peak boost
        h -= (1f - C) * (1f - C) * 1.5f;              // deep ocean basins

        return h; // UNNORMALIZED
    }

    private void CalibrateElevation(int worldSize, int stride = 8)
    {
        // sample raw h sparsely
        var samples = new List<float>((worldSize / stride) * (worldSize / stride));
        for (var z = 0; z < worldSize; z += stride)
            for (var x = 0; x < worldSize; x += stride)
            {
                var baseVal = SampleBaseNoise(x, z);
                samples.Add(HeightRaw(x, z, baseVal));
            }

        samples.Sort();
        // robust quantiles → map [qLo..qHi] → [0..1]
        var qLo = samples[(int)(samples.Count * 0.10f)];  // 10th percentile
        var qHi = samples[(int)(samples.Count * 0.98f)];  // 98th percentile
        elevOffset = qLo;
        elevScale = 1f / MathF.Max(qHi - qLo, 1e-6f);

        //  Ensure target ocean coverage
        var targetWater01 = VoxelHelper.WaterLevel / (float)(VoxelHelper.ChunkYSize - 1); // ~0.275
        var desiredOceanFrac = 0.40f;

        // estimate current ocean fraction from the sampled set
        var below = 0;
        foreach (var s in samples)
        {
            var s01 = Saturate((s - elevOffset) * elevScale);
            if (s01 < targetWater01) below++;
        }
        var frac = below / (float)samples.Count;

        // shift offset to hit desired
        var deltaFrac = desiredOceanFrac - frac;
        // small proportional shift (tune 0.25f if needed)
        elevOffset -= deltaFrac * (1f / elevScale) * 0.25f;
    }

    private float Height01At(int gx, int gz)
    {
        return (float)GenerateHeight(gx, gz) / (VoxelHelper.ChunkYSize - 1);
    }

    public float EstimateSlope01(int gx, int gz)
    {
        var W = VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize;
        var X0 = Math.Clamp(gx - 1, 0, W - 1);
        var X1 = Math.Clamp(gx + 1, 0, W - 1);
        var Z0 = Math.Clamp(gz - 1, 0, W - 1);
        var Z1 = Math.Clamp(gz + 1, 0, W - 1);
        var hx0 = Height01At(X0, gz);
        var hx1 = Height01At(X1, gz);
        var hz0 = Height01At(gx, Z0);
        var hz1 = Height01At(gx, Z1);
        var dx = (hx1 - hx0) * 0.5f;
        var dz = (hz1 - hz0) * 0.5f;
        return Math.Clamp(MathF.Sqrt(dx * dx + dz * dz), 0f, 1f); // 0 flat .. 1 steep
    }

    public readonly record struct ChunkGenerationData(BlockType[] BlockTypes, int[] ColumnHeights, ColumnInfo[] Columns, uint[] BlockAttributes);

    public ChunkGenerationData BuildChunkData(int chunkIndex)
    {
        var blockTypes = new BlockType[VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize];
        var columnHeights = new int[VoxelHelper.ChunkSideSizeSquare];
        var columns = new ColumnInfo[VoxelHelper.ChunkSideSizeSquare];

        var worldX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var worldZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        var baseX = worldX * VoxelHelper.ChunkSideSize;
        var baseZ = worldZ * VoxelHelper.ChunkSideSize;

        var columnIndex = 0;
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            var gz = baseZ + z;
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++, columnIndex++)
            {
                var gx = baseX + x;
                var height = GetHeightNormalizedGlobal(gx, gz);
                columnHeights[columnIndex] = height;

                SampleFields01(gx, gz, out var C, out var E, out var T, out var H);
                var height01 = height / (float)(VoxelHelper.ChunkYSize - 1);
                var slope01 = EstimateSlope01(gx, gz);
                var biome = ClassifyBiome(C, E, T, H, height01, slope01);
                columns[columnIndex] = new ColumnInfo(biome, C, E, T, H, (byte)height, height01);

                for (var y = 0; y <= VoxelHelper.MaxBlockPositionY; y++)
                {
                    var blockIndex = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
                    blockTypes[blockIndex] = GenerateChunkBlockType(height, x, y, z);
                }
            }
        }

        return new ChunkGenerationData(blockTypes, columnHeights, columns, new uint[blockTypes.Length]);
    }

    // ------------------------------------------------------------------------
    // CPU implementation of GPU noise functions to ensure collision consistency
    // ------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(uint x, uint seed)
    {
        x += seed;
        x = ((x >> 16) ^ x) * 0x45d9f3bu;
        x = ((x >> 16) ^ x) * 0x45d9f3bu;
        x = (x >> 16) ^ x;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Noise2D(int x, int z, uint seed)
    {
        var n = Hash((uint)x + Hash((uint)z, seed), seed);
        return (float)n / uint.MaxValue * 2.0f - 1.0f;
    }

    private static float InterpolatedNoise(float x, float z, uint seed)
    {
        var ix = (int)MathF.Floor(x);
        var iz = (int)MathF.Floor(z);
        var fx = x - ix;
        var fz = z - iz;

        // Smooth the interpolation
        fx = fx * fx * (3.0f - 2.0f * fx);
        fz = fz * fz * (3.0f - 2.0f * fz);

        var v00 = Noise2D(ix, iz, seed);
        var v10 = Noise2D(ix + 1, iz, seed);
        var v01 = Noise2D(ix, iz + 1, seed);
        var v11 = Noise2D(ix + 1, iz + 1, seed);

        var v0 = Lerp(v00, v10, fx);
        var v1 = Lerp(v01, v11, fx);
        return Lerp(v0, v1, fz);
    }

    private static float MultiOctaveNoise(float x, float z, uint seed, int octaves)
    {
        var total = 0.0f;
        var frequency = 1.0f;
        var amplitude = 1.0f;
        var maxValue = 0.0f;

        for (var i = 0; i < octaves; i++)
        {
            total += InterpolatedNoise(x * frequency, z * frequency, seed + (uint)(i * 100));
            maxValue += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.0f;
        }

        return total / maxValue;
    }

    private int GenerateHeight(int wx, int wz)
    {
        // Base terrain (large features)
        var baseScale = 0.005f;  // 1/200
        var baseNoise = MultiOctaveNoise(wx * baseScale, wz * baseScale, (uint)seed, 4);

        // Detail noise (small features)
        var detailScale = 0.02f;  // 1/50
        var detailNoise = MultiOctaveNoise(wx * detailScale, wz * detailScale, (uint)seed + 1000u, 3);

        // Combine: base terrain + 30% detail
        var combined = baseNoise + detailNoise * 0.3f;

        // Map to height range: water level ±40 blocks = range of 80 blocks
        var h01 = combined * 0.5f + 0.5f;  // Map [-1,1] to [0,1]
        var height = (int)(VoxelHelper.WaterLevel + (h01 - 0.5f) * 80.0f);

        // Clamp to valid range
        return Math.Clamp(height, 0, VoxelHelper.ChunkYSize - 1);
    }

    private bool IsNearWater(int wx, int wz)
    {
        for (var dz = -3; dz <= 3; dz += 3)
        {
            for (var dx = -3; dx <= 3; dx += 3)
            {
                if (dx == 0 && dz == 0) continue;
                var h = GenerateHeight(wx + dx, wz + dz);
                if (h <= VoxelHelper.WaterLevel) return true;
            }
        }
        return false;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="globalX"></param>
    /// <param name="globalZ"></param>
    /// <returns></returns>
    public int GetHeightNormalizedGlobal(int globalX, int globalZ)
    {
        return GenerateHeight(globalX, globalZ);
    }

    public int GetHeightNormalizedChunkLocal(int chunkIndex, int x, int z)
    {
        //  get chunks world position
        var worldX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var worldZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        var cx = worldX * VoxelHelper.ChunkSideSize;
        var cz = worldZ * VoxelHelper.ChunkSideSize;

        //  get height data from global position
        var height = GetHeightNormalizedGlobal(x + cx, z + cz);

        return height;
    }

    /// <summary>
    /// Generates a block from chunk local coordinates.
    /// </summary>
    /// <param name="chunkIndex"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="z"></param>
    /// <returns></returns>
    public BlockType GenerateChunkBlockLocal(int chunkIndex, int x, int y, int z)
    {
        var height = GetHeightNormalizedChunkLocal(chunkIndex, x, z);
        return GenerateChunkBlockType(height, x, y, z);
    }

    /// <summary>
    /// Calculates the block type from height and local coordinates.
    /// </summary>
    /// <param name="maxHeight"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="z"></param>
    /// <returns></returns>
    public BlockType GenerateChunkBlockType(int maxHeight, int x, int y, int z)
    {
        return GenerateChunkBlockType(maxHeight, x, y, z, 0, 0);
    }

    public BlockType GenerateChunkBlockType(int maxHeight, int x, int y, int z, int wx, int wz)
    {
        var blockAltitude = y;

        if (blockAltitude > maxHeight)
        {
            if (blockAltitude <= (int)VoxelHelper.WaterLevel)
            {
                return BlockType.WaterLevel;
            }
            return BlockType.None;
        }

        // Solid blocks (y <= maxHeight)

        // Surface block
        if (blockAltitude == maxHeight)
        {
            if (blockAltitude < (int)VoxelHelper.WaterLevel)
            {
                // Underwater surface
                if (blockAltitude >= (int)VoxelHelper.WaterLevel - 1) return BlockType.Sand;
                return BlockType.BedRock;
            }
            else
            {
                // Above water surface
                if (blockAltitude <= (int)VoxelHelper.WaterLevel + 2)
                {
                    // Shoreline check
                    if (IsNearWater(wx, wz)) return BlockType.Sand;
                }
                return BlockType.GrassDirt;
            }
        }

        // Sub-surface
        var depth = maxHeight - blockAltitude;
        if (depth <= 2) return BlockType.Dirt;
        return BlockType.Rock;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SampleFields01(int gx, int gz, out float C, out float E, out float T, out float H)
    {
        C = 0.5f * (Fields.Sample(Fields.C, gx, gz) + 1f);
        E = 0.5f * (Fields.Sample(Fields.E, gx, gz) + 1f);
        T = 0.5f * (Fields.Sample(Fields.T, gx, gz) + 1f);
        H = 0.5f * (Fields.Sample(Fields.H, gx, gz) + 1f);

        var tempDetail = 0.5f * (Fields.Sample(Fields.TDetail, gx, gz) + 1f);
        var humDetail = 0.5f * (Fields.Sample(Fields.HDetail, gx, gz) + 1f);

        T = Saturate(Lerp(T, tempDetail, 0.45f));
        H = Saturate(Lerp(H, humDetail, 0.5f));

        // widen dynamic range around mid-point
        T = Saturate((T - 0.5f) * 1.25f + 0.5f);
        H = Saturate((H - 0.5f) * 1.3f + 0.5f);
    }

    // simple altitude+temperature biased classifier
    public string ClassifyBiome(float C, float E, float T, float H, float height01, float slope01)
    {
        var seaLevel01 = VoxelHelper.WaterLevel / (float)(VoxelHelper.ChunkYSize - 1);
        var blockStep01 = 1f / (VoxelHelper.ChunkYSize - 1);
        var aboveSea01 = MathF.Max(0f, height01 - seaLevel01);
        var belowSea01 = MathF.Max(0f, seaLevel01 - height01);
        var coastalBand = MathF.Abs(height01 - seaLevel01);

        // adjust temperature for altitude lapse (cooler higher up)
        var continental = Math.Clamp(C, 0f, 1f);
        var erosion = Math.Clamp(E, 0f, 1f);
        var temperature = Math.Clamp(T - aboveSea01 * 0.55f, 0f, 1f);
        var moisture = Math.Clamp(H, 0f, 1f);
        var aridity = 1f - moisture;
        var steep = slope01 > 0.48f;
        var rugged = slope01 > 0.32f;

        if (belowSea01 >= blockStep01 - 1e-4f)
            return "Ocean";

        if (coastalBand < blockStep01 * 3f && continental < 0.6f)
        {
            if (moisture > 0.65f && temperature > 0.45f)
                return "Swamp";
            if (aridity > 0.55f)
                return "Beach";
            return temperature < 0.35f ? "Taiga" : "Plains";
        }

        if (aboveSea01 > 0.45f)
        {
            if (temperature < 0.24f)
                return "Alpine";
            if (moisture < 0.4f)
                return "Tundra";
            return "Highlands";
        }

        if (steep && aboveSea01 > 0.05f && erosion < 0.55f)
            return "Highlands";

        if (temperature < 0.3f)
            return moisture > 0.5f ? "Taiga" : "Tundra";

        if (temperature > 0.64f && aridity > 0.55f && continental > 0.4f)
            return "Desert";

        if (temperature > 0.58f && moisture > 0.68f && erosion > 0.4f)
            return slope01 < 0.35f ? "Rainforest" : "Highlands";

        if (temperature > 0.52f && aridity > 0.4f && aridity < 0.7f && continental > 0.45f)
            return "Savanna";

        if (temperature > 0.45f && moisture > 0.62f && slope01 < 0.3f && erosion > 0.45f)
            return "Rainforest";

        if (rugged && aboveSea01 > 0.18f && erosion < 0.55f)
            return "Highlands";

        if (temperature < 0.42f)
            return moisture > 0.45f ? "Taiga" : "Tundra";

        // fallback: nearest centroid by L1 distance
        var best = Biomes[0];
        var bestD = float.MaxValue;
        foreach (var b in Biomes)
        {
            var d = MathF.Abs(b.C - C) + MathF.Abs(b.E - E) + MathF.Abs(b.T - temperature) + MathF.Abs(b.H - moisture);
            if (d < bestD) { bestD = d; best = b; }
        }
        return best.Name;
    }
}
