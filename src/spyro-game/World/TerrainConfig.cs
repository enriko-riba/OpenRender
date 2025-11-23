using System;
using System.Collections.Generic;

namespace SpyroGame.World;

/// <summary>
/// CPU-only terrain configuration bucket. Holds all tweakable parameters and small data tables
/// (splines, biome defs, region + cave params). No generation logic here.
/// Upload/bake these into GPU buffers/textures elsewhere.
/// </summary>
public sealed class TerrainConfig
{
    // World & seeds
    public int Seed { get; set; } = 1337;
    public float WorldScale { get; set; } = 1.0f;

    // Macro field scales (world-space → noise frequencies)
    public float ContinentalnessScale { get; set; } = 1f / 20000f;
    public float ErosionScale { get; set; } = 1f / 7000f;
    public float RidgeScale { get; set; } = 1f / 1800f;

    // Domain warp
    public float WarpScale { get; set; } = 1f / 9000f;
    public float WarpStrength { get; set; } = 120f;

    // Climate
    public float BaseTemperature { get; set; } = 0.6f;
    public float LapseRate { get; set; } = 0.0018f; // temp drop per unit height
    public float BaseHumidity { get; set; } = 0.55f;
    public float CoastDrying { get; set; } = 0.25f;
    public float ClimateScale { get; set; } = 1f / 12000f;
    public float ClimateWarp { get; set; } = 1f / 22000f;

    // Height mapping
    public Spline1D HeightSpline { get; set; } = Spline1D.DefaultHeightSpline();
    public Spline1D? ErosionToSlopeSpline { get; set; } = null;

    // Biomes
    public List<BiomeDefinition> Biomes { get; set; } = BiomeDefinition.DefaultSet();

    // Regions and caves
    public BiomeRegionParams BiomeRegions { get; set; } = BiomeRegionParams.Default();
    public CaveParams Caves { get; set; } = CaveParams.Default();

    // Optional sea level for climate lapse calculations (world units)
    public float SeaLevel { get; set; } = 70f;

    // --- Helper baking functions for GPU uploads ---

    /// <summary>
    /// Bake the height spline to a float array (to upload as a 1D R32F texture).
    /// Input x domain assumed [0,1]. Output is raw float values (meters or units you choose).
    /// </summary>
    public float[] BakeHeightSplineLut(int samples = 256)
        => HeightSpline.Bake(samples);

    /// <summary>
    /// Build a 2D LUT of biome ids (temp x humidity → biomeId). Returns a row-major byte array of size res*res.
    /// Values are indices into Biomes list (0..Biomes.Count-1). Out-of-range maps to 0.
    /// </summary>
    public byte[] BuildBiomeIdLut(int resolution = 256)
    {
        if (resolution <= 1) resolution = 2;
        var data = new byte[resolution * resolution];

        for (int y = 0; y < resolution; y++)
        {
            float h = y / (float)(resolution - 1); // humidity in [0,1]
            for (int x = 0; x < resolution; x++)
            {
                float t = x / (float)(resolution - 1); // temperature in [0,1]
                int id = BiomeDefinition.SelectBestBiomeId(Biomes, t, h);
                if (id < 0) id = 0;
                data[x + y * resolution] = (byte)id;
            }
        }
        return data;
    }
}

/// <summary>
/// Minimal geology layers used for biome texture selection. Independent of in-world block types.
/// </summary>
public enum GeologyLayer : byte
{
    Air = 0,
    Water = 1,
    Surface = 2,
    Subsurface = 3,
    DeepSubsurface = 4,
    UnderwaterSurface = 5,
    UnderwaterSubsurface = 6,
    ShoreLine = 7,
}

/// <summary>
/// Range helper for climate comfort bands.
/// </summary>
public readonly record struct Range(float Min, float Max)
{
    public bool Contains(float v) => v >= Min && v <= Max;
    public float Center => (Min + Max) * 0.5f;
}

/// <summary>
/// Mapping from geology layer to texture index inside a texture array/atlas.
/// </summary>
public sealed class BiomeTextureSet
{
    private readonly int[] indices;

    public BiomeTextureSet()
    {
        indices = new int[Enum.GetValues(typeof(GeologyLayer)).Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = -1; // -1 = not set
    }

    public int this[GeologyLayer layer]
    {
        get => indices[(int)layer];
        set => indices[(int)layer] = value;
    }
}

/// <summary>
/// Defines a biome: name, climate comfort ranges, and texture set mapping.
/// </summary>
public sealed class BiomeDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "Unnamed";

    // Climate comfort bands in normalized [0,1]
    public Range Temperature { get; set; } = new(0.4f, 0.6f);
    public Range Humidity { get; set; } = new(0.4f, 0.6f);

    public BiomeTextureSet Textures { get; set; } = new();

    public static int SelectBestBiomeId(List<BiomeDefinition> biomes, float temperature01, float humidity01)
    {
        if (biomes.Count == 0) return -1;

        // Prefer those where both temp and humidity are inside; otherwise nearest by L1 to centers
        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < biomes.Count; i++)
        {
            var b = biomes[i];
            bool tIn = b.Temperature.Contains(temperature01);
            bool hIn = b.Humidity.Contains(humidity01);
            float score = tIn && hIn ? 0f : MathF.Abs(b.Temperature.Center - temperature01) + MathF.Abs(b.Humidity.Center - humidity01);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    public static List<BiomeDefinition> DefaultSet()
    {
        // Minimal default set; texture indices are placeholders
        return new List<BiomeDefinition>
        {
            new BiomeDefinition{ Id=0, Name="Ocean",      Temperature=new(0.55f,0.75f), Humidity=new(0.7f,1.0f),    Textures = DefaultTextures(water:true)},
            new BiomeDefinition{ Id=1, Name="Beach",      Temperature=new(0.55f,0.75f), Humidity=new(0.35f,0.65f),   Textures = DefaultTextures(sand:true)},
            new BiomeDefinition{ Id=2, Name="Plains",     Temperature=new(0.45f,0.65f), Humidity=new(0.4f,0.65f),    Textures = DefaultTextures(grass:true)},
            new BiomeDefinition{ Id=3, Name="Savanna",    Temperature=new(0.6f,0.8f),   Humidity=new(0.25f,0.55f),   Textures = DefaultTextures(grass:true)},
            new BiomeDefinition{ Id=4, Name="Desert",     Temperature=new(0.65f,1.0f),  Humidity=new(0.0f,0.35f),    Textures = DefaultTextures(sand:true)},
            new BiomeDefinition{ Id=5, Name="Rainforest", Temperature=new(0.55f,0.8f),  Humidity=new(0.7f,1.0f),     Textures = DefaultTextures(grass:true)},
            new BiomeDefinition{ Id=6, Name="Taiga",      Temperature=new(0.2f,0.45f),  Humidity=new(0.45f,0.75f),   Textures = DefaultTextures(snow:false, grass:true)},
            new BiomeDefinition{ Id=7, Name="Tundra",     Temperature=new(0.0f,0.35f),  Humidity=new(0.2f,0.6f),     Textures = DefaultTextures(snow:true)},
            new BiomeDefinition{ Id=8, Name="Highlands",  Temperature=new(0.25f,0.6f),  Humidity=new(0.25f,0.75f),   Textures = DefaultTextures(rock:true)},
            new BiomeDefinition{ Id=9, Name="Alpine",     Temperature=new(0.0f,0.3f),   Humidity=new(0.2f,0.7f),     Textures = DefaultTextures(snow:true, rock:true)},
        };
    }

    private static BiomeTextureSet DefaultTextures(bool water=false, bool sand=false, bool grass=false, bool snow=false, bool rock=false)
    {
        var t = new BiomeTextureSet();
        if (water) { t[GeologyLayer.Water] = 0; t[GeologyLayer.ShoreLine] = 1; }
        if (sand)  { t[GeologyLayer.Surface] = 2; t[GeologyLayer.Subsurface] = 3; }
        if (grass) { t[GeologyLayer.Surface] = 4; t[GeologyLayer.Subsurface] = 5; }
        if (rock)  { t[GeologyLayer.Surface] = 6; t[GeologyLayer.DeepSubsurface] = 7; }
        if (snow)  { t[GeologyLayer.Surface] = 8; }
        return t;
    }
}

/// <summary>
/// Region grouping for biomes across chunks.
/// </summary>
public sealed class BiomeRegionParams
{
    /// <summary>Desired cell size in chunks (X/Z). Example: 4 = 4x4 chunks per region.</summary>
    public float CellSizeChunks { get; set; } = 4f;
    /// <summary>Jitter strength [0..1] applied to cell centers.</summary>
    public float JitterStrength { get; set; } = 0.35f;
    /// <summary>Feather width in world units used to blend between neighboring regions.</summary>
    public float FeatherWidth { get; set; } = 12f;
    /// <summary>Max number of neighboring regions to mix.</summary>
    public int MaxRegionMix { get; set; } = 3;

    public static BiomeRegionParams Default() => new();
}

/// <summary>
/// Cave shaping parameters used by the GPU density function.
/// </summary>
public sealed class CaveParams
{
    public float CheeseFrequency { get; set; } = 1f / 40f;
    public float CheeseAmplitude { get; set; } = 1.0f;

    public float SpaghettiFrequency { get; set; } = 1f / 28f;
    public float SpaghettiAmplitude { get; set; } = 0.8f;

    public float CarveThreshold { get; set; } = 0.55f;

    public float CurlScale { get; set; } = 1f / 120f;
    public float CurlStrength { get; set; } = 12f;

    public float RidgeCarve { get; set; } = 0.35f;

    public static CaveParams Default() => new();
}

/// <summary>
/// 1D spline defined by control points in [0,1] domain. Provides linear evaluation and LUT baking.
/// </summary>
public sealed class Spline1D
{
    public struct Point
    {
        public float X; // input in [0,1]
        public float Y; // output value (units defined by usage)
        public Point(float x, float y) { X = x; Y = y; }
    }

    public List<Point> Points { get; } = new();

    public void Clear() => Points.Clear();

    public void Add(float x, float y) => Points.Add(new Point(x, y));

    public void Sort()
    {
        Points.Sort((a, b) => a.X.CompareTo(b.X));
    }

    /// <summary>
    /// Evaluate with linear interpolation. If no points, returns 0. If one point, returns that Y.
    /// </summary>
    public float Evaluate(float x)
    {
        if (Points.Count == 0) return 0f;
        if (Points.Count == 1) return Points[0].Y;

        if (x <= Points[0].X) return Points[0].Y;
        if (x >= Points[^1].X) return Points[^1].Y;

        // binary search lower bound
        int lo = 0, hi = Points.Count - 1;
        while (lo + 1 < hi)
        {
            int mid = (lo + hi) >> 1;
            if (x < Points[mid].X) hi = mid; else lo = mid;
        }
        var a = Points[lo];
        var b = Points[hi];
        float t = (x - a.X) / MathF.Max(1e-6f, (b.X - a.X));
        return a.Y + (b.Y - a.Y) * t;
    }

    /// <summary>
    /// Bake to uniform samples covering [0,1].</summary>
    public float[] Bake(int samples)
    {
        if (samples < 2) samples = 2;
        Sort();
        var arr = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float x = i / (float)(samples - 1);
            arr[i] = Evaluate(x);
        }
        return arr;
    }

    public static Spline1D DefaultHeightSpline()
    {
        var s = new Spline1D();
        // Example mapping continentalness→height scale (units are arbitrary)
        s.Add(0.00f, -40f); // deep ocean
        s.Add(0.20f, -10f); // shallow
        s.Add(0.35f,  0f);  // coast
        s.Add(0.55f,  25f); // inland plains
        s.Add(0.75f,  60f); // hills
        s.Add(1.00f, 120f); // mountains
        s.Sort();
        return s;
    }
}
