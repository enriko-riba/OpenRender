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
    // Adjusted for larger terrain features and better ocean generation
    public float ContinentalnessScale { get; set; } = 1f / 800f; // Reduced frequency for larger continents/oceans (was 1/200)
    public float ErosionScale { get; set; } = 1f / 300f; // Larger erosion patterns (was 1/150)
    public float RidgeScale { get; set; } = 1f / 120f; // Larger ridge features (was 1/80)

    // Domain warp - increased for more natural terrain flow
    public float WarpScale { get; set; } = 1f / 150f; // Larger warp patterns (was 1/100)
    public float WarpStrength { get; set; } = 60f; // Increased warp intensity (was 40)

    // Climate - adjusted for better biome distribution
    public float BaseTemperature { get; set; } = 0.5f; // Centered temperature (was 0.6)
    public float LapseRate { get; set; } = 0.002f; // Slightly increased altitude cooling (was 0.0018)
    public float BaseHumidity { get; set; } = 0.5f; // Centered humidity (was 0.55)
    public float CoastDrying { get; set; } = 0.3f; // Increased inland drying (was 0.25)
    public float ClimateScale { get; set; } = 1f / 15000f; // Slightly larger climate zones (was 1/12000)
    public float ClimateWarp { get; set; } = 1f / 25000f; // Slightly larger climate warp (was 1/22000)

    // Height mapping
    public Spline1D HeightSpline { get; set; } = Spline1D.DefaultHeightSpline();
    public Spline1D? ErosionToSlopeSpline { get; set; } = null;

    // Biomes
    public List<BiomeDefinition> Biomes { get; set; } = BiomeDefinition.DefaultSet();

    // Regions and caves
    public BiomeRegionParams BiomeRegions { get; set; } = BiomeRegionParams.Default();
    public CaveParams Caves { get; set; } = CaveParams.Default();

    // Sea level adjusted for deeper ocean system (was 70)
    // New height spline has ocean floor at -80 to -20, so sea level at 35 creates proper ocean depth
    public float SeaLevel { get; set; } = 35f;

    // --- Terrain Shaping Parameters ---
    // These control how terrain features are generated and placed
    
    /// <summary>
    /// Continentalness threshold separating ocean from land (NEW: Biome system).
    /// Example: 0.35 means C < 0.35 is ocean, C >= 0.35 is land.
    /// - Lower values (0.30): More ocean, less land
    /// - Higher values (0.40): Less ocean, more land
    /// CRITICAL: Must match the coast transition in your height spline!
    /// </summary>
    public float OceanThreshold { get; set; } = 0.35f;
    
    /// <summary>
    /// Continentalness threshold for deep ocean biome variant.
    /// Example: 0.20 means C < 0.20 is deep ocean, 0.20-0.35 is regular ocean.
    /// </summary>
    public float DeepOceanThreshold { get; set; } = 0.20f;
    
    /// <summary>
    /// Elevation threshold (in blocks) where Alpine biome begins.
    /// Example: 200 means elevation > 200 forces Alpine biome regardless of climate.
    /// - Lower values (150): Alpine starts earlier, more snowy peaks
    /// - Higher values (250): Alpine only on highest peaks
    /// </summary>
    public float AlpineElevation { get; set; } = 200f;
    
    /// <summary>
    /// Continentalness range around OceanThreshold for coast transition zones.
    /// Example: 0.05 means coast is from C=0.30 to C=0.40 (±0.05 around 0.35).
    /// Used for Beach/Shore biomes that need to appear near water-land boundary.
    /// </summary>
    public float CoastRange { get; set; } = 0.05f;
    
    /// <summary>
    /// Coast threshold for humidity calculation (continentalness value where coast is detected).
    /// Example: 0.35 means C=0.35 is considered coast for biome humidity calculations.
    /// - Lower values (0.30): Coast detection happens earlier, more inland drying
    /// - Higher values (0.40): Coast detection happens later, wetter interiors
    /// Should match the coast region in your height spline (steep transition zone).
    /// </summary>
    public float CoastThreshold { get; set; } = 0.35f;
    
    /// <summary>
    /// Continentalness threshold where mountains begin (for cliff/overhang generation).
    /// Example: 0.75 means mountains start appearing at C > 0.75.
    /// - Lower values (0.65): Mountains appear in more areas, more dramatic terrain
    /// - Higher values (0.85): Mountains only in highest continentalness, flatter world
    /// </summary>
    public float MountainThreshold { get; set; } = 0.75f;
    
    /// <summary>
    /// Frequency of cliff noise (inverse of feature size in blocks).
    /// Example: 1/40 = 0.025 means cliff features repeat every ~40 blocks.
    /// - Lower values (1/60 = 0.0167): Larger, smoother cliff faces
    /// - Higher values (1/25 = 0.04): More jagged, frequent cliff details
    /// </summary>
    public float CliffFrequency { get; set; } = 1f / 40f;
    
    /// <summary>
    /// Height amplitude of cliff variations in blocks.
    /// Example: 40 means cliffs can vary up to 40 blocks in height.
    /// - Lower values (25): Gentler, less dramatic cliffs
    /// - Higher values (60): Very dramatic, towering cliff faces
    /// </summary>
    public float CliffAmplitude { get; set; } = 40f;
    
    /// <summary>
    /// Frequency of 3D overhang noise (inverse of feature size).
    /// Example: 1/60 = 0.0167 means overhang features ~60 blocks wide.
    /// - Lower values (1/80 = 0.0125): Larger, smoother overhangs
    /// - Higher values (1/40 = 0.025): Smaller, more frequent overhangs
    /// </summary>
    public float OverhangFrequency { get; set; } = 1f / 60f;
    
    /// <summary>
    /// Strength/amplitude of overhang displacement in blocks.
    /// Example: 15 means overhangs can extend up to 15 blocks.
    /// - Lower values (8): Subtle overhangs, barely noticeable
    /// - Higher values (25): Extreme overhangs, floating islands
    /// </summary>
    public float OverhangAmplitude { get; set; } = 15f;
    
    /// <summary>
    /// Range in blocks around sea level where shoreline detection occurs.
    /// Example: 2 means y <= SeaLevel + 2 checks for nearby water to place sand.
    /// - Lower values (1): Narrow shoreline, sharp transition
    /// - Higher values (4): Wide shoreline, more beach area
    /// </summary>
    public float ShorelineRange { get; set; } = 2f;
    
    /// <summary>
    /// Depth in blocks of the subsurface dirt layer below grass/surface.
    /// Example: 2 means 2 blocks of dirt below the surface before stone.
    /// - Lower values (1): Thin topsoil, stone closer to surface
    /// - Higher values (4): Thick soil layer, more digging before stone
    /// </summary>
    public float SubsurfaceDepth { get; set; } = 2f;
    
    /// <summary>
    /// Distance in blocks below surface where caves fade to prevent surface breaches.
    /// Example: 5 means caves attenuate from depth 0 to 5.
    /// - Lower values (3): Caves reach surface more easily, more cave entrances
    /// - Higher values (8): Caves stay deep, fewer natural entrances
    /// </summary>
    public float CaveDepthFade { get; set; } = 5f;
    
    /// <summary>
    /// Slope threshold range for cave breach detection.
    /// Example: Min=0.5, Max=2.0 means gentle slopes (0.5) to steep cliffs (2.0) allow breaches.
    /// - Lower min (0.3): Caves breach even on gentle slopes
    /// - Higher max (3.0): Only very steep cliffs allow breaches
    /// </summary>
    public SlopeRange CaveSlopeFade { get; set; } = new() { Min = 0.5f, Max = 2.0f };
    
    /// <summary>
    /// Additional blocks below sea level where cave flooding extends inland.
    /// Example: 8 means caves up to y = SeaLevel + 8 are flooded if near coast.
    /// - Lower values (4): Less flooding, more dry caves near coast
    /// - Higher values (12): More flooding, wetter caves, fewer air pockets
    /// </summary>
    public float CaveFloodingExtension { get; set; } = 8f;

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
    /// Build a 2D LUT of biome ids (temp x humidity → biomeId). 
    /// Returns a row-major byte array of size res*res.
    /// Values are indices into Biomes list (0..Biomes.Count-1). Out-of-range maps to DEFAULT_FALLBACK_BIOME_ID.
    /// 
    /// IMPORTANT: This LUT only contains LAND biomes (AllowedTerrain == LandOnly).
    /// Ocean and Alpine biomes are handled via hardcoded checks in the shader (Phase 1 & 2).
    /// The shader only samples this LUT for land areas after ocean/alpine checks.
    /// </summary>
    public byte[] BuildBiomeIdLut(int resolution = 256)
    {
        if (resolution <= 1) resolution = 2;
        var data = new byte[resolution * resolution];

        // Filter to only LAND biomes (exclude Ocean and Alpine which are hardcoded in shader)
        var landBiomes = Biomes.Where(b => b.AllowedTerrain == TerrainType.LandOnly).ToList();
        
        if (landBiomes.Count == 0)
        {
            // Fallback: fill LUT with default biome ID
            Array.Fill(data, (byte)BiomeDefinition.DEFAULT_FALLBACK_BIOME_ID);
            return data;
        }

        for (var y = 0; y < resolution; y++)
        {
            var h = y / (float)(resolution - 1); // humidity in [0,1]
            for (var x = 0; x < resolution; x++)
            {
                var t = x / (float)(resolution - 1); // temperature in [0,1]
                var id = BiomeDefinition.SelectBestBiomeId(landBiomes, t, h);
                if (id < 0) id = BiomeDefinition.DEFAULT_FALLBACK_BIOME_ID;
                data[x + y * resolution] = (byte)landBiomes[id].Id; // Use actual biome ID, not list index
            }
        }
        return data;
    }

    /// <summary>
    /// Struct matching the std430 layout in the shader.
    /// Pack carefully to match GLSL alignment: vec4 = 16-byte aligned, vec3 uses 16 bytes, etc.
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
        public float uCaveThreshold; public float uCurlScale; public float uCurlStrength; 
        
        // Terrain shaping params
        public float uCoastThreshold;          // Coast detection for humidity
        public float uMountainThreshold;       // Where mountains start for cliff/overhang gen
        public float uCliffFreq;               // Cliff noise frequency
        public float uCliffAmp;                // Cliff height amplitude
        public float uOverhangFreq;            // 3D overhang noise frequency
        public float uOverhangAmp;             // Overhang displacement amplitude
        public float uShorelineRange;          // Shoreline detection range
        public float uSubsurfaceDepth;         // Dirt layer thickness
        public float uCaveDepthFade;           // Cave surface attenuation distance
        public float uCaveSlopeFadeMin;        // Cave slope breach min threshold
        public float uCaveSlopeFadeMax;        // Cave slope breach max threshold
        public float uCaveFloodExt;            // Cave flooding extension above sea level
        
        // NEW: Ocean/Land/Altitude thresholds for biome system
        public float uOceanThreshold;          // C < this = ocean
        public float uDeepOceanThreshold;      // C < this = deep ocean
        public float uAlpineElevation;         // elevation > this = alpine
        public float uCoastRange;              // +/- range around OceanThreshold for coast
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
            uCoastThreshold = CoastThreshold,
            uMountainThreshold = MountainThreshold,
            uCliffFreq = CliffFrequency,
            uCliffAmp = CliffAmplitude,
            uOverhangFreq = OverhangFrequency,
            uOverhangAmp = OverhangAmplitude,
            uShorelineRange = ShorelineRange,
            uSubsurfaceDepth = SubsurfaceDepth,
            uCaveDepthFade = CaveDepthFade,
            uCaveSlopeFadeMin = CaveSlopeFade.Min,
            uCaveSlopeFadeMax = CaveSlopeFade.Max,
            uCaveFloodExt = CaveFloodingExtension,
            uOceanThreshold = OceanThreshold,
            uDeepOceanThreshold = DeepOceanThreshold,
            uAlpineElevation = AlpineElevation,
            uCoastRange = CoastRange,
        };
    }
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
/// Range helper for slope thresholds (used for cave breach detection).
/// </summary>
public struct SlopeRange
{
    public float Min { get; set; }
    public float Max { get; set; }
    
    public SlopeRange()
    {
        Min = 0.5f;
        Max = 2.0f;
    }
}

/// <summary>
/// Terrain type constraint for biome placement.
/// Determines where a biome can appear based on ocean/land/altitude.
/// </summary>
public enum TerrainType
{
    /// <summary>No terrain restriction - biome can appear anywhere if climate matches</summary>
    Any = 0,
    /// <summary>Only in ocean areas (C < OceanThreshold)</summary>
    OceanOnly = 1,
    /// <summary>Only on land areas (C >= OceanThreshold)</summary>
    LandOnly = 2,
    /// <summary>Only near coast transition (|C - OceanThreshold| < CoastRange)</summary>
    CoastOnly = 3,
    /// <summary>Only at high elevation (elevation > AlpineElevation)</summary>
    MountainOnly = 4
}

/// <summary>
/// Defines a biome: name, climate comfort ranges, terrain constraints, and texture set mapping.
/// </summary>
public sealed class BiomeDefinition
{
    // Hardcoded biome IDs for shader use (must match shader constants)
    public const int OCEAN_BIOME_ID = 0;
    public const int ALPINE_BIOME_ID = 9;
    public const int DEFAULT_FALLBACK_BIOME_ID = 2; // Plains
    
    public int Id { get; set; }
    public string Name { get; set; } = "Unnamed";

    // Climate comfort bands in normalized [0,1]
    public Range Temperature { get; set; } = new(0.4f, 0.6f);
    public Range Humidity { get; set; } = new(0.4f, 0.6f);
    
    // NEW: Elevation constraints (in blocks)
    /// <summary>Minimum elevation for this biome (-1000 = no minimum)</summary>
    public float MinElevation { get; set; } = float.MinValue;
    /// <summary>Maximum elevation for this biome (9999 = no maximum)</summary>
    public float MaxElevation { get; set; } = float.MaxValue;
    
    // NEW: Terrain type restriction
    /// <summary>Where this biome can appear (ocean/land/mountain/etc.)</summary>
    public TerrainType AllowedTerrain { get; set; } = TerrainType.Any;
    
    // NEW: Selection priority (higher = checked first, 0 = lowest)
    /// <summary>Biome selection priority (100=hardcoded like Ocean, 50=normal, 0=fallback)</summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Texture paths indexed by GeologyLayer enum.
    /// Index 0 = Air (unused), 1 = Water, 2 = Surface, etc.
    /// </summary>
    public List<string> TexturePaths { get; set; } = [];

    public BiomeDefinition() { }

    public BiomeDefinition(int id, string name, Range temperature, Range humidity, List<string> texturePaths,
        int priority = 0, TerrainType terrainType = TerrainType.Any, float minElevation = float.MinValue, float maxElevation = float.MaxValue)
    {
        Id = id;
        Name = name;
        Temperature = temperature;
        Humidity = humidity;
        TexturePaths = texturePaths;
        Priority = priority;
        AllowedTerrain = terrainType;
        MinElevation = minElevation;
        MaxElevation = maxElevation;
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
        // Default texture paths
        // Index mapping:
        // 0: Air (unused)
        // 1: Water
        // 2: Surface
        // 3: Subsurface
        // 4: DeepSubsurface
        // 5: UnderwaterSurface
        // 6: UnderwaterSubsurface
        // 7: ShoreLine

        // NEW ARCHITECTURE: Biomes are organized by PRIORITY and TERRAIN TYPE
        // Priority levels:
        // - 100: Hardcoded ocean biomes (checked first in shader)
        // - 90: Hardcoded alpine biome (altitude override in shader)
        // - 50: Normal climate-based land biomes
        // - 0: Fallback biome (if nothing else matches)
        
        return
        [
            // PRIORITY 100: OCEAN BIOMES (checked first in shader via hardcoded logic)
            new (OCEAN_BIOME_ID, "Ocean", 
                new(0.0f, 1.0f), new(0.0f, 1.0f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 100, 
                terrainType: TerrainType.OceanOnly),
            
            // PRIORITY 90: ALPINE BIOME (checked second in shader via altitude override)
            new (ALPINE_BIOME_ID, "Alpine", 
                new(0.0f, 1.0f), new(0.0f, 1.0f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/Alpine/snow.png", "Resources/voxel/Alpine/snow-dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 90, 
                terrainType: TerrainType.MountainOnly,
                minElevation: 200f),
            
            // PRIORITY 50: LAND BIOMES (checked via climate LUT in shader)
            // These are ONLY used on land (C >= OceanThreshold) and below Alpine elevation
            
            // Cold land biomes
            new (6, "Taiga", 
                new(0.25f, 0.5f), new(0.5f, 1.0f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/Taiga/grass-dirt.png", "Resources/voxel/Taiga/dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
            
            // Temperate land biomes
            new (8, "Highlands", 
                new(0.25f, 0.5f), new(0.0f, 0.5f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
            
            new (DEFAULT_FALLBACK_BIOME_ID, "Plains", 
                new(0.5f, 0.75f), new(0.33f, 0.66f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/Plains/grass.png", "Resources/voxel/Plains/dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
            
            new (1, "Beach", 
                new(0.5f, 0.75f), new(0.0f, 0.33f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/sand.png", "Resources/voxel/sand.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
            
            new (7, "Tundra", 
                new(0.5f, 0.75f), new(0.66f, 1.0f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
            
            // Warm/Hot land biomes
            new (5, "Rainforest", 
                new(0.75f, 1.0f), new(0.5f, 0.66f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
            
            new (3, "Savanna", 
                new(0.75f, 1.0f), new(0.25f, 0.5f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/grass-dirt.png", "Resources/voxel/dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
            
            new (4, "Desert", 
                new(0.75f, 1.0f), new(0.0f, 0.25f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/sand.png", "Resources/voxel/sand.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
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

    // Increased threshold by ~15% to reduce cave frequency (0.75 -> 0.86)
    // Higher threshold = fewer caves since density must be higher to carve
    public float CarveThreshold { get; set; } = 0.86f; // Reduced cave frequency by 15% (was 0.75)

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
        public float X { get; set; }
        public float Y { get; set; }
        
        // Parameterless constructor for JSON deserialization
        public Point()
        {
            X = 0f;
            Y = 0f;
        }
        
        // Parameterized constructor for convenience
        public Point(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    // CRITICAL FIX: Add setter for JSON deserialization
    public List<Point> Points { get; set; } = [];

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
        // Enhanced height mapping for dramatic terrain features
        // Continentalness input: 0.0 = deep ocean, 1.0 = mountains
        
        // Deep ocean basin (extended and deeper)
        s.Add(0.00f, -80f);  // Very deep ocean trenches
        s.Add(0.15f, -50f);  // Deep ocean
        s.Add(0.25f, -20f);  // Shallow ocean
        
        // Steep coastal cliffs - dramatic transition from ocean to land
        s.Add(0.32f, -5f);   // Continental shelf
        s.Add(0.38f, 35f);   // Steep cliff rise (40m elevation change over short distance)
        
        // Land regions
        s.Add(0.50f, 50f);   // Coastal plains
        s.Add(0.65f, 80f);   // Inland plains/hills
        
        // Mountain regions with cliffs
        s.Add(0.75f, 120f);  // Foothills
        s.Add(0.85f, 180f);  // Mountain slopes
        s.Add(0.95f, 250f);  // High mountains
        s.Add(1.00f, 320f);  // Mountain peaks
        
        s.Sort();
        return s;
    }
}
