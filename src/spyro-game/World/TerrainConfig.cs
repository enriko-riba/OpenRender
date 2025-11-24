namespace SpyroGame.World;

/// <summary>
/// CPU-only terrain configuration bucket. Holds all tweakable parameters and small data tables
/// (splines, biome defs, region + cave params). No generation logic here.
/// Upload/bake these into GPU buffers/textures elsewhere.
/// </summary>
public sealed class TerrainConfig
{
    // World & seeds
    public string WorldName { get; set; } = "default";
    public int Seed { get; set; } = 1337;
    public float WorldScale { get; set; } = 1.0f;

    // Macro field scales (world-space → noise frequencies)
    // Increased scales for testing/visibility in small world
    public float ContinentalnessScale { get; set; } = 1f / 200f; // Was 1/2000
    public float ErosionScale { get; set; } = 1f / 150f; // Was 1/1500
    public float RidgeScale { get; set; } = 1f / 80f; // Was 1/800

    // Domain warp
    public float WarpScale { get; set; } = 1f / 100f; // Was 1/1000
    public float WarpStrength { get; set; } = 40f; // Reduced strength for higher freq

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

    public static TerrainConfig Default() => new();

    public static TerrainConfig Load(string path)
    {
        if (!File.Exists(path)) return Default();
        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<TerrainConfig>(json) ?? Default();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to load TerrainConfig: {e.Message}");
            return Default();
        }
    }

    public void Save(string path)
    {
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var json = System.Text.Json.JsonSerializer.Serialize(this, options);
            File.WriteAllText(path, json);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to save TerrainConfig: {e.Message}");
        }
    }

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

        for (var y = 0; y < resolution; y++)
        {
            var h = y / (float)(resolution - 1); // humidity in [0,1]
            for (var x = 0; x < resolution; x++)
            {
                var t = x / (float)(resolution - 1); // temperature in [0,1]
                var id = BiomeDefinition.SelectBestBiomeId(Biomes, t, h);
                if (id < 0) id = 0;
                data[x + y * resolution] = (byte)id;
            }
        }
        return data;
    }

    /// <summary>
    /// Struct matching the std430 layout in the shader.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct GpuParams
    {
        public uint uSeed;
        public float uWorldScale;
        public float uMacroScale; // Unused in C# config but present in shader plan
        public float uContScale; public float uErodeScale; public float uRidgeScale;
        public float uWarpScale; public float uWarpStrength;
        public float uBaseTemp; public float uLapseRate; public float uBaseHum; public float uCoastDry;
        public float uClimateScale; public float uClimateWarp;
        public float uRegionCellSize; public float uRegionJitter; public float uRegionFeather; public uint uMaxRegionMix;
        // Cave params
        public float uCheeseFreq; public float uCheeseAmp; public float uSpaghettiFreq; public float uSpaghettiAmp;
        public float uCaveThreshold; public float uCurlScale; public float uCurlStrength; public float pad0;
    }

    public GpuParams GetGpuParams()
    {
        return new GpuParams
        {
            uSeed = (uint)Seed,
            uWorldScale = WorldScale,
            uMacroScale = 1.0f, // Default
            uContScale = ContinentalnessScale,
            uErodeScale = ErosionScale,
            uRidgeScale = RidgeScale,
            uWarpScale = WarpScale,
            uWarpStrength = WarpStrength,
            uBaseTemp = BaseTemperature,
            uLapseRate = LapseRate,
            uBaseHum = BaseHumidity,
            uCoastDry = CoastDrying,
            uClimateScale = ClimateScale,
            uClimateWarp = ClimateWarp,
            uRegionCellSize = BiomeRegions.CellSizeChunks,
            uRegionJitter = BiomeRegions.JitterStrength,
            uRegionFeather = BiomeRegions.FeatherWidth,
            uMaxRegionMix = (uint)BiomeRegions.MaxRegionMix,
            uCheeseFreq = Caves.CheeseFrequency,
            uCheeseAmp = Caves.CheeseAmplitude,
            uSpaghettiFreq = Caves.SpaghettiFrequency,
            uSpaghettiAmp = Caves.SpaghettiAmplitude,
            uCaveThreshold = Caves.CarveThreshold,
            uCurlScale = Caves.CurlScale,
            uCurlStrength = Caves.CurlStrength,
            pad0 = 0
        };
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
/// Defines a biome: name, climate comfort ranges, and texture set mapping.
/// </summary>
public sealed class BiomeDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "Unnamed";

    // Climate comfort bands in normalized [0,1]
    public Range Temperature { get; set; } = new(0.4f, 0.6f);
    public Range Humidity { get; set; } = new(0.4f, 0.6f);

    /// <summary>
    /// Texture paths indexed by GeologyLayer enum.
    /// Index 0 = Air (unused), 1 = Water, 2 = Surface, etc.
    /// </summary>
    public List<string> TexturePaths { get; set; } = [];

    public BiomeDefinition() { }

    public BiomeDefinition(int id, string name, Range temperature, Range humidity, List<string> texturePaths)
    {
        Id = id;
        Name = name;
        Temperature = temperature;
        Humidity = humidity;
        TexturePaths = texturePaths;
    }

    public static int SelectBestBiomeId(List<BiomeDefinition> biomes, float temperature01, float humidity01)
    {
        if (biomes.Count == 0) return -1;

        // Prefer those where both temp and humidity are inside; otherwise nearest by L1 to centers
        var best = -1;
        var bestScore = float.MaxValue;
        for (var i = 0; i < biomes.Count; i++)
        {
            var b = biomes[i];
            var tIn = b.Temperature.Contains(temperature01);
            var hIn = b.Humidity.Contains(humidity01);
            var score = tIn && hIn ? 0f : MathF.Abs(b.Temperature.Center - temperature01) + MathF.Abs(b.Humidity.Center - humidity01);
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
        // Default texture paths (currently shared across all biomes)
        // Index mapping:
        // 0: Air (unused)
        // 1: Water
        // 2: Surface
        // 3: Subsurface
        // 4: DeepSubsurface
        // 5: UnderwaterSurface
        // 6: UnderwaterSubsurface
        // 7: ShoreLine

        return
        [
            new(0, "Ocean",      new(0.55f,0.75f), new(0.7f,1.0f),    ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png",      "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png"]),
            new(1, "Beach",      new(0.55f,0.75f), new(0.35f,0.65f),  ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png",      "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png"]),
            new (2, "Plains",     new(0.45f,0.65f), new(0.4f,0.65f),   ["",
                "Resources/voxel/water.png",                // 1 - water
                "Resources/voxel/Plains/grass.png",         // 2 - surface
                "Resources/voxel/Plains/dirt.png",          // 3 - subsurface
                "Resources/voxel/rock.png",                 // 4 - deep subsurface
                "Resources/voxel/bedrock.png",              // 5 - UnderwaterSurface
                "Resources/voxel/rock.png",                 // 6 - UnderwaterSubsurface
                "Resources/voxel/sand.png"]),               // 7 - ShoreLine
            new (3, "Savanna",    new(0.6f,0.8f),   new(0.25f,0.55f),  ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png",      "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png"]),
            new (4, "Desert",     new(0.65f,1.0f),  new(0.0f,0.35f),   ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png",      "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png"]),
            new (5, "Rainforest", new(0.55f,0.8f),  new(0.7f,1.0f),    ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png",      "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png"]),
            new (6, "Taiga",      new(0.2f,0.45f),  new(0.45f,0.75f),  ["", 
                "Resources/voxel/water.png",                // 1 - water
                "Resources/voxel/Taiga/grass-dirt.png",     // 2 - surface
                "Resources/voxel/Taiga/dirt.png",           // 3 - subsurface
                "Resources/voxel/rock.png",                 // 4 - deep subsurface
                "Resources/voxel/bedrock.png",              // 5 - UnderwaterSurface
                "Resources/voxel/rock.png",                 // 6 - UnderwaterSubsurface
                "Resources/voxel/sand.png"]),               // 7 - ShoreLine
            new (7, "Tundra",     new(0.0f,0.35f),  new(0.2f,0.6f),    ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png",      "Resources/voxel/rock.png", "Resources/voxel/sand.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png"]),
            new (8, "Highlands",  new(0.25f,0.6f),  new(0.25f,0.75f),  ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png",      "Resources/voxel/rock.png", "Resources/voxel/sand.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png"]),
            new (9, "Alpine",     new(0.0f,0.3f),   new(0.2f,0.7f),    ["", "Resources/voxel/water.png", "Resources/voxel/snow.png",       "Resources/voxel/snow-dirt.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/sand.png"]),
        ];
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
    public float CheeseFrequency { get; set; } = 1f / 100f; // Much smoother (was 1/50)
    public float CheeseAmplitude { get; set; } = 1.0f;

    public float SpaghettiFrequency { get; set; } = 1f / 80f; // Much smoother (was 1/35)
    public float SpaghettiAmplitude { get; set; } = 0.8f;

    public float CarveThreshold { get; set; } = 0.75f; // Reduced density (was 0.72)

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
    public struct Point(float x, float y)
    {
        public float X { get; set; } = x; public float Y { get; set; } = y;
    }

    public List<Point> Points { get; } = [];

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
            var mid = (lo + hi) >> 1;
            if (x < Points[mid].X) hi = mid; else lo = mid;
        }
        var a = Points[lo];
        var b = Points[hi];
        var t = (x - a.X) / MathF.Max(1e-6f, (b.X - a.X));
        return a.Y + (b.Y - a.Y) * t;
    }

    /// <summary>
    /// Bake to uniform samples covering [0,1].</summary>
    public float[] Bake(int samples)
    {
        if (samples < 2) samples = 2;
        Sort();
        var arr = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            var x = i / (float)(samples - 1);
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
        s.Add(0.35f, 0f);  // coast
        s.Add(0.55f, 25f); // inland plains
        s.Add(0.75f, 60f); // hills
        s.Add(1.00f, 120f); // mountains
        s.Sort();
        return s;
    }
}
