using SpyroGame.Noise;
using System.Runtime.CompilerServices;

namespace SpyroGame.World;

public class TerrainBuilder
{
    /// <summary>
    /// Normalized height data in range [0..1]
    /// </summary>
    private readonly float[] heightData;
    private readonly float[] ridgeData;    // extra ridged layer
    private readonly int seed;

    private readonly WorldFields Fields;
    private readonly Biome[] Biomes;
    private float elevOffset, elevScale;
    private GenParams P;


    public TerrainBuilder(int seed)
    {
        this.seed = seed;

        var worldSize = VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize;

        // High-res, domain-warped base noise for the whole world.
        // Base continental + macro terrain
        heightData = NoiseData.CreateDomainWarped(
            0, 0, worldSize,
            baseFreq: 1f / 180f,
            warpFreq: 1f / 900f,
            warpAmp: 8f,
            seedBase: seed ^ 0x12345, out var hRange);
        var range = hRange.max - hRange.min;
        if (range < 1e-6f) range = 1f;
        for (var i = 0; i < heightData.Length; i++)
            heightData[i] = (heightData[i] - hRange.min) / range * 2f - 1f; // normalize to [-1,1]

        // Ridge detail layer – keep stronger for cliffs
        ridgeData = NoiseData.CreateDomainWarped(
            0, 0, worldSize,
            baseFreq: 1f / 220f,
            warpFreq: 1f / 550f,
            warpAmp: 14f,
            seedBase: seed ^ 0x5A17C3, out var rRange);
        range = rRange.max - rRange.min;
        if (range < 1e-6f) range = 1f;
        for (var i = 0; i < ridgeData.Length; i++)
            ridgeData[i] = (ridgeData[i] - rRange.min) / range * 2f - 1f; // normalize to [-1,1]

        // Low-res global fields (continentalness / erosion / temp / humidity)
        Fields = new WorldFields(worldSize, texSize: 1024,
            cf: 1f / 15000f, ef: 1f / 4000f, tf: 1f / 5000f, hf: 1f / 5000f, seed);

        // Biome table (tiny) – tweak to taste
        Biomes =
        [
            new Biome{ Name="Beach", C=0.35f, E=0.6f,  T=0.7f, H=0.6f,  Top=BlockType.Sand },       // Beach
            new Biome{ Name="Plains", C=0.55f, E=0.7f,  T=0.6f, H=0.5f,  Top=BlockType.GrassDirt },  // Plains
            new Biome{ Name="Taiga", C=0.60f, E=0.6f,  T=0.3f, H=0.6f,  Top=BlockType.GrassDirt },  // Taiga
            new Biome{ Name="Alpine", C=0.85f, E=0.3f,  T=0.2f, H=0.4f,  Top=BlockType.Snow },       // Alpine/Snow
            new Biome{ Name="Highlands", C=0.75f, E=0.4f,  T=0.5f, H=0.3f,  Top=BlockType.Gravel }      // Highlands/Gravel
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
    private static float Normalize01(float v, float lo, float hi) => (v - lo) / MathF.Max(hi - lo, 1e-6f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saturate(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);

    private float HeightRaw(int wx, int wz, float baseVal)
    {
        float C = 0.5f * (Fields.Sample(Fields.C, wx, wz) + 1f);
        float E = 0.5f * (Fields.Sample(Fields.E, wx, wz) + 1f);

        float fbm = 0.5f * (baseVal + 1f);                           // [0,1]
        float rid = 1f - MathF.Abs(ridgeData[WorldIndex(wx, wz)]);   // [0,1]
        rid = MathF.Pow(Saturate(rid), P.RidgePow);

        float ruggedWeight = MathF.Pow(1f - E, 1.6f) * Lerp(0.5f, 1.2f, C);
        float relief = Lerp(MathF.Pow(fbm, P.SmoothPow), rid, ruggedWeight);

        float seaBias = Lerp(P.SeaMin, P.SeaMax, C);
        float mountainK = Lerp(P.MtnLow, P.MtnHigh, C);

        float h = seaBias + (relief - 0.5f) * mountainK;

        // --- Carve connected valleys (band near 0 of anisotropic noise) ---
        float r = NoiseData.SampleGradientNoise2D(wx * 0.0009f, wz * 0.00045f, 1f, 1f, seed ^ 0xBEEF);
        float band = 1f - Smooth01(MathF.Abs(r));                 // high near river "centerlines"
        float riverDepth = Lerp(0.02f, 0.14f, C) * Lerp(1.0f, 0.5f, E); // inland & low erosion → deeper
        h -= band * riverDepth;                                    // carve elongated, connected valleys

        h += (rid - 0.5f) * (C * C) * 0.45f;          // peak boost
        h -= (1f - C) * (1f - C) * 1.5f;              // deep ocean basins

        return h; // UNNORMALIZED
    }

    private void CalibrateElevation(int worldSize, int stride = 8)
    {
        // sample raw h sparsely
        var samples = new List<float>((worldSize / stride) * (worldSize / stride));
        for (int z = 0; z < worldSize; z += stride)
            for (int x = 0; x < worldSize; x += stride)
            {
                float baseVal = heightData[WorldIndex(x, z)];
                samples.Add(HeightRaw(x, z, baseVal));
            }

        samples.Sort();
        // robust quantiles → map [qLo..qHi] → [0..1]
        float qLo = samples[(int)(samples.Count * 0.10f)];  // 10th percentile
        float qHi = samples[(int)(samples.Count * 0.98f)];  // 98th percentile
        elevOffset = qLo;
        elevScale = 1f / MathF.Max(qHi - qLo, 1e-6f);

        //  Ensure target ocean coverage
        float targetWater01 = VoxelHelper.WaterLevel / (float)(VoxelHelper.ChunkYSize - 1); // ~0.275
        float desiredOceanFrac = 0.40f;

        // estimate current ocean fraction from the sampled set
        int below = 0;
        foreach (var s in samples)
        {
            float s01 = Saturate((s - elevOffset) * elevScale);
            if (s01 < targetWater01) below++;
        }
        float frac = below / (float)samples.Count;

        // shift offset to hit desired
        float deltaFrac = desiredOceanFrac - frac;
        // small proportional shift (tune 0.25f if needed)
        elevOffset -= deltaFrac * (1f / elevScale) * 0.25f;
    }

    private float Height01At(int gx, int gz)
    {
        var baseVal = heightData[WorldIndex(gx, gz)];   // [-1,1]
        return Height01At(gx, gz, baseVal);            // [0,1]
    }

    // baseVal in [-1,1] (from heightData); returns normalized height in [0,1]
    private float Height01At(int wx, int wz, float baseVal)
    {
        var h = HeightRaw(wx, wz, baseVal);

        // map [qLo..qHi] → [0..1], with gentle clipping
        var h01 = Saturate((h - elevOffset) * elevScale);
        
        //flatten near the waterline
        float wl01 = VoxelHelper.WaterLevel / (float)(VoxelHelper.ChunkYSize - 1);
        float d = h01 - wl01;                 // height above water
        if (d < 0.08f)                        // ~10 blocks band
        {
            float k = Smooth01((d + 0.08f) / 0.08f); // 0 underwater .. 1 above band
                                                     // pull towards a flat shelf near water; keeps gentle beaches & lake shores
            h01 = wl01 + d * Lerp(0.20f, 1.0f, k);
        }


        // terraces (optional) operate on h01
        if (P.TerraceStep > 0f)
        {
            var step = P.TerraceStep;
            var baseT = MathF.Floor(h01 / step) * step;
            var t = (h01 - baseT) / step;
            var terraced = baseT + Smooth01(t) * step;
            var strength = Lerp(P.TerraceStrength, 0f, 1f - /*erosion*/ 0.5f); // or use E if you want
            h01 = Lerp(h01, terraced, strength);
        }

        return h01;
    }


    // fast accessors
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int WorldIndex(int gx, int gz) => gx + gz * (VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize);

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



    /// <summary>
    /// Raw height data in range [0, 1].
    /// </summary>
    public float[] HeightData => heightData;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="globalX"></param>
    /// <param name="globalZ"></param>
    /// <returns></returns>
    public int GetHeightNormalizedGlobal(int globalX, int globalZ)
    {
        var h01 = Height01At(globalX, globalZ); // [0,1]
        h01 = MathF.Round(h01 * (VoxelHelper.ChunkYSize - 1));
        return Math.Clamp((int)h01, 0, VoxelHelper.MaxBlockPositionY);
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
    public static BlockType GenerateChunkBlockType(int maxHeight, int x, int y, int z)
    {
        var blockAltitude = y;

        BlockType bt;
        if (blockAltitude > maxHeight)
        {
            bt = BlockType.None;
        }
        else if (blockAltitude == maxHeight)
        {
            bt = BlockType.GrassDirt;
        }
        else if (blockAltitude < maxHeight && blockAltitude >= maxHeight - 2)
        {
            bt = BlockType.Dirt;
        }
        else if (blockAltitude < maxHeight - 2)
        {
            bt = BlockType.Rock;
        }
        else
        {
            bt = BlockType.None;
        }

        //  special cases
        if (blockAltitude <= (int)VoxelHelper.WaterLevel)
        {
            if ((bt == BlockType.None) && blockAltitude == (int)VoxelHelper.WaterLevel)
            {
                bt = BlockType.WaterLevel;
            }
            else if ((bt != BlockType.None) && (blockAltitude >= VoxelHelper.WaterLevel - 1))
            {
                bt = BlockType.Sand;
            }
            else if ((bt != BlockType.None) && (blockAltitude < VoxelHelper.WaterLevel - 1))
            {
                //  replace top layer underwater solid blocks with bedrock
                bt = BlockType.BedRock;
            }
            else if (blockAltitude < 3)
            {
                bt = BlockType.BedRock;
            }
        }

        return bt;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SampleFields01(int gx, int gz, out float C, out float E, out float T, out float H)
    {
        C = 0.5f * (Fields.Sample(Fields.C, gx, gz) + 1f);
        E = 0.5f * (Fields.Sample(Fields.E, gx, gz) + 1f);
        T = 0.5f * (Fields.Sample(Fields.T, gx, gz) + 1f);
        H = 0.5f * (Fields.Sample(Fields.H, gx, gz) + 1f);
    }

    // simple altitude+temperature biased classifier
    public string ClassifyBiome(float C, float E, float T, float H, float height01, float slope01)
    {
        // altitude temperature lapse (cooler higher up)
        float effectiveT = Math.Clamp(T - height01 * 0.5f, 0f, 1f);

        if (height01 > 0.80f || (effectiveT < 0.25f && slope01 > 0.25f))
            return "Alpine";
        if (height01 > 0.65f && slope01 > 0.35f)
            return "Highlands";
        if (height01 < (VoxelHelper.WaterLevel / (float)(VoxelHelper.ChunkYSize - 1)) + 0.02f && C < 0.45f)
            return "Beach";
        if (effectiveT < 0.35f)
            return "Taiga";
        if (E > 0.65f && slope01 < 0.2f)
            return "Plains";

        // fallback: nearest centroid by L1 distance
        Biome best = Biomes[0];
        float bestD = float.MaxValue;
        foreach (var b in Biomes)
        {
            float d = MathF.Abs(b.C - C) + MathF.Abs(b.E - E) + MathF.Abs(b.T - effectiveT) + MathF.Abs(b.H - H);
            if (d < bestD) { bestD = d; best = b; }
        }
        return best.Name;
    }
}
