namespace SpyroGame.World;

/// <summary>
/// Terrain configuration parameters bucket. Holds all tweakable parameters and small data tables
/// (splines, biome defs, region + cave params). No generation logic here.
/// Upload/bake these into GPU buffers/textures elsewhere.
/// </summary>
public sealed class TerrainConfig
{
    // World & seeds
    
    /// <summary>
    /// Gets or sets the world name used for save file identification.
    /// </summary>
    public string WorldName { get; set; } = "default";
    
    /// <summary>
    /// Gets or sets the random seed for procedural generation.
    /// Different seeds produce entirely different terrain layouts while maintaining the same characteristics.
    /// Range: Any integer value. Default: 1337
    /// </summary>
    public int Seed { get; set; } = 1337;
    
    /// <summary>
    /// Gets or sets the global world scale multiplier.
    /// Currently unused but reserved for future multi-scale world systems.
    /// Default: 1.0
    /// </summary>
    public float WorldScale { get; set; } = 1.0f;

    // Macro field scales (world-space → noise frequencies)
    // NOTE: These are now handled by NoiseLayer objects below, but kept here for reference if needed
    // or removed if fully replaced. The errors indicate they are duplicates of the Obsolete properties
    // I added later in the file. I should remove these original definitions.


    // Domain warp
    
    /// <summary>
    /// Gets or sets the domain warp noise frequency (inverse of wavelength in blocks).
    /// Controls the scale of terrain flow distortion applied before continentalness sampling.
    /// - 1/150 (default): Large-scale terrain flow (150-block warp patterns)
    /// - 1/250: Very gentle, continental-scale flow
    /// - 1/80: Aggressive, swirling terrain patterns
    /// Technical: Applies 2D FBM offset to input coordinates, creating natural curved features.
    /// </summary>
    public float WarpScale { get; set; } = 1f / 200f;
    
    /// <summary>
    /// Gets or sets the domain warp displacement strength in blocks.
    /// Controls how far terrain features are displaced by the warp effect.
    /// - 60 (default): Moderate displacement (60-block maximum shift)
    /// - 100: Strong displacement, highly organic flowing terrain
    /// - 30: Subtle displacement, more regular terrain grid
    /// Technical: Multiplies the warp noise output to determine actual coordinate offset.
    /// Step 5.2: Increased to 100 for more organic, irregular biome borders.
    /// </summary>
    public float WarpStrength { get; set; } = 100f;

    // Climate
    
    /// <summary>
    /// Gets or sets the base temperature at sea level before altitude and noise adjustments.
    /// Normalized range [0,1] where 0=frozen, 1=scorching.
    /// - 0.5 (default): Temperate baseline (Earth-like)
    /// - 0.7: Warmer world (more tropical biomes, less tundra)
    /// - 0.3: Colder world (more taiga/alpine, less desert)
    /// Technical: Starting point for temperature calculation before lapse rate and climate noise.
    /// </summary>
    public float BaseTemperature { get; set; } = 0.5f;
    
    /// <summary>
    /// Gets or sets the temperature decrease per block of altitude above sea level.
    /// Controls how quickly mountains become cold/snowy.
    /// - 0.002 (default): Moderate cooling (0.2 temp loss per 100 blocks altitude)
    /// - 0.003: Aggressive cooling (low snowline, alpine starts at ~150 blocks)
    /// - 0.001: Gentle cooling (high snowline, alpine starts at ~300 blocks)
    /// Technical: Implements adiabatic lapse rate. Applied as: temp -= LapseRate * max(0, altitude).
    /// </summary>
    public float LapseRate { get; set; } = 0.002f;
    
    /// <summary>
    /// Gets or sets the base humidity at coastlines before continental drying and noise adjustments.
    /// Normalized range [0,1] where 0=arid, 1=very humid.
    /// - 0.5 (default): Moderate baseline (balanced biome distribution)
    /// - 0.7: Humid world (more rainforest/taiga, less desert)
    /// - 0.3: Dry world (more desert/savanna, less rainforest)
    /// Technical: Starting point for humidity calculation before coast drying and climate noise.
    /// </summary>
    public float BaseHumidity { get; set; } = 0.5f;
    
    /// <summary>
    /// Gets or sets the humidity reduction rate with distance from coast.
    /// Controls the strength of continental interior drying effect.
    /// - 0.3 (default): Moderate drying (noticeable inland deserts)
    /// - 0.5: Strong drying (large inland deserts, coastal rainforests)
    /// - 0.1: Weak drying (humid interiors, small deserts)
    /// Technical: Applied as: hum -= (continentalness - coastThreshold) * CoastDrying.
    /// </summary>
    public float CoastDrying { get; set; } = 0.3f;

    // Climate parameters for Minecraft-style terrain (Phase 1)
    
    /// <summary>
    /// Continentalness noise configuration.
    /// Controls the size of oceans and continents.
    /// Scale balances large landmasses with ocean variety.
    /// </summary>
    public NoiseLayer Continentalness { get; set; } = new()
    {
        BaseScale = 1f / 1800f,  // Was 1/2500 - reduced for more ocean variety
        Octaves = 3,
        Persistence = 0.50f,     // Restored for more variation
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 1000f,
        DomainWarpStrength = 80f
    };

    /// <summary>
    /// Erosion noise configuration.
    /// Controls the scale of terrain roughness and flat areas.
    /// LARGER scale = bigger zones of flat vs rough terrain.
    /// </summary>
    public NoiseLayer Erosion { get; set; } = new()
    {
        BaseScale = 1f / 900f,   // Was 1/600 - larger = bigger flat plains zones
        Octaves = 3,             // Reduced from 4 for smoother transitions
        Persistence = 0.4f,      // Was 0.45 - smoother
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 600f,   // Was 1/400
        DomainWarpStrength = 60f       // Was 80 - less warping
    };

    /// <summary>
    /// Peaks/Valleys noise configuration.
    /// Controls the spacing of mountain ridges and valley systems.
    /// </summary>
    public NoiseLayer PeaksValleys { get; set; } = new()
    {
        BaseScale = 1f / 200f,   // Was 1/150 - larger features
        Octaves = 3,             // Reduced from 4
        Persistence = 0.5f,      // Was 0.55
        Lacunarity = 2.0f,
        UseRidged = true,
        RidgeSharpness = 1.5f    // Was 2.0 - softer ridges
    };

    /// <summary>
    /// Temperature noise configuration.
    /// Controls the size of temperature bands across the world.
    /// </summary>
    public NoiseLayer Temperature { get; set; } = new()
    {
        BaseScale = 1f / 8000f,
        Octaves = 2,
        Persistence = 0.4f,
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 5000f,
        DomainWarpStrength = 200f
    };

    /// <summary>
    /// Humidity noise configuration.
    /// Controls the size of wet/dry zones across the world.
    /// </summary>
    public NoiseLayer Humidity { get; set; } = new()
    {
        BaseScale = 1f / 5000f,
        Octaves = 2,
        Persistence = 0.45f,
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 3000f,
        DomainWarpStrength = 150f
    };

    /// <summary>
    /// Weirdness noise configuration.
    /// Controls terrain variety - high weirdness creates unusual terrain features.
    /// REDUCED influence for more normal, Earth-like terrain.
    /// </summary>
    public NoiseLayer Weirdness { get; set; } = new()
    {
        BaseScale = 1f / 300f,   // Was 1/300 - larger = more uniform areas
        Octaves = 2,             // Reduced from 3
        Persistence = 0.75f,      // Was 0.6
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 200f,   // Was 1/200
        DomainWarpStrength = 40f       // Was 50 - less distortion
    };

    // === LAKE SYSTEM (Phase 2) ===
    
    /// <summary>
    /// Lake noise configuration.
    /// Controls the distribution of lakes across the terrain.
    /// Only areas where noise exceeds LakeThreshold will have lakes.
    /// </summary>
    public NoiseLayer LakeNoise { get; set; } = new()
    {
        BaseScale = 1f / 600f,    // Large-scale lake distribution (~600 block features)
        Octaves = 2,
        Persistence = 0.5f,
        Lacunarity = 2.0f,
        DomainWarpScale = 1f / 400f,
        DomainWarpStrength = 40f
    };
    
    /// <summary>
    /// Noise threshold for lake placement [0, 1].
    /// Only areas where lake noise exceeds this threshold will have lakes.
    /// - 0.75 (default): ~25% of terrain can have lakes (sparse)
    /// - 0.85: ~15% of terrain (rare lakes)
    /// - 0.65: ~35% of terrain (frequent lakes)
    /// </summary>
    public float LakeThreshold { get; set; } = 0.75f;
    
    /// <summary>
    /// Maximum depth of lakes in blocks.
    /// Lakes fill depressions from terrain surface up to this depth.
    /// - 5 (default): Shallow lakes suitable for wading
    /// - 3: Very shallow ponds
    /// - 8: Deeper lakes for swimming
    /// </summary>
    public float LakeMaxDepth { get; set; } = 5f;
    
    /// <summary>
    /// Minimum continentalness for lake placement.
    /// Lakes only appear on land (above this threshold).
    /// Must match OceanThreshold to allow lakes at the land boundary.
    /// </summary>
    public float LakeMinContinentalness { get; set; } = 0.40f;

    // Legacy properties removed


    // Height mapping
    
    /// <summary>
    /// Gets or sets the height spline that maps continentalness [0,1] to elevation in blocks.
    /// Defines the terrain elevation profile from deep ocean to mountain peaks.
    /// The spline is sampled during terrain generation to determine base height before erosion/peaks are applied.
    /// Default spline: Ocean floor at -80 blocks, sea level at 0 (internal +35), mountain peaks at +320 blocks.
    /// </summary>
    public Spline1D HeightSpline { get; set; } = Spline1D.DefaultHeightSpline();
    
    /// <summary>
    /// Gets or sets the optional erosion-to-slope spline for advanced terrain shaping.
    /// Currently unused. Reserved for future erosion-dependent slope modulation.
    /// </summary>
    public Spline1D? ErosionToSlopeSpline { get; set; } = null;

    // Biomes
    
    /// <summary>
    /// Gets or sets the list of biome definitions used for terrain texturing and climate-based placement.
    /// Each biome defines temperature/humidity ranges, elevation constraints, and texture mappings.
    /// The biome system uses a combination of LUT sampling (for climate biomes) and hardcoded checks (ocean, alpine).
    /// Default: 10 biomes including Ocean, Beach, Plains, Desert, Savanna, Rainforest, Taiga, Tundra, Highlands, Alpine.
    /// </summary>
    public List<BiomeDefinition> Biomes { get; set; } = BiomeDefinition.DefaultSet();

    // Regions and caves
    
    /// <summary>
    /// Gets or sets the biome region parameters controlling how biomes are distributed and blended across chunks.
    /// Implements a Voronoi-based region system where each region has a dominant biome with feathered boundaries.
    /// See <see cref="BiomeRegionParams"/> for detailed parameters.
    /// </summary>
    public BiomeRegionParams BiomeRegions { get; set; } = BiomeRegionParams.Default();
    
    /// <summary>
    /// Gets or sets the cave generation parameters controlling cave frequency, size, and distribution.
    /// Implements a dual-system approach: "cheese" caves (large chambers) and "spaghetti" caves (tunnels).
    /// See <see cref="CaveParams"/> for detailed parameters.
    /// </summary>
    public CaveParams Caves { get; set; } = CaveParams.Default();
    
    /// <summary>
    /// Gets or sets the ore generation parameters controlling ore distribution by depth.
    /// Ores are scattered in stone regions based on Minecraft-style depth distribution.
    /// See <see cref="OreParams"/> for detailed parameters.
    /// </summary>
    public OreParams Ores { get; set; } = OreParams.Default();
    
    /// <summary>
    /// Gets or sets the terrain shaping configuration controlling height calculation.
    /// Phase 2 of Minecraft terrain pipeline: All height calculation parameters are here,
    /// replacing magic numbers with documented, configurable values.
    /// See <see cref="TerrainShapingConfig"/> for detailed parameters.
    /// </summary>
    public TerrainShapingConfig TerrainShaping { get; set; } = TerrainShapingConfig.Default();

    /// <summary>
    /// Gets or sets the Y-coordinate of the water level in blocks.
    /// Water blocks are placed at all Y positions <= SeaLevel where terrain surface is below this level.
    /// - 35 (default): Creates ~115-block deep oceans with current height spline
    /// - 50: Shallower oceans, more land exposed
    /// - 20: Deeper oceans, less land visible
    /// Technical: This is the absolute Y coordinate, not relative to terrain. Ocean floor can be much lower.
    /// </summary>
    public float SeaLevel { get; set; } = 35f;

    // --- Terrain Shaping Parameters ---
    
    /// <summary>
    /// Continentalness threshold separating ocean from land (NEW: Biome system).
    /// Example: 0.40 means C < 0.40 is ocean, C >= 0.40 is land.
    /// - Lower values (0.30): More ocean, less land
    /// - Higher values (0.50): Less ocean, more land
    /// CRITICAL: Must match the coast transition in your height spline!
    /// RAISED to 0.40 so more terrain is classified as ocean.
    /// </summary>
    public float OceanThreshold { get; set; } = 0.40f;
    
    /// <summary>
    /// Continentalness threshold for deep ocean biome variant.
    /// Example: 0.25 means C < 0.25 is deep ocean, 0.25-0.40 is regular ocean.
    /// </summary>
    public float DeepOceanThreshold { get; set; } = 0.25f;
    
    /// <summary>
    /// Elevation threshold (in blocks) where Alpine biome begins.
    /// Example: 200 means elevation > 200 forces Alpine biome regardless of climate.
    /// - Lower values (150): Alpine starts earlier, more snowy peaks
    /// - Higher values (250): Alpine only on highest peaks
    /// </summary>
    public float AlpineElevation { get; set; } = 150f;
    
    /// <summary>
    /// Continentalness range around OceanThreshold for coast transition zones.
    /// Example: 0.05 means coast is from C=0.30 to C=0.40 (±0.05 around 0.35).
    /// Used for Beach/Shore biomes that need to appear near water-land boundary.
    /// </summary>
    public float CoastRange { get; set; } = 0.05f;
    
    /// <summary>
    /// Coast threshold for humidity calculation (continentalness value where coast is detected).
    /// Example: 0.40 means C=0.40 is considered coast for biome humidity calculations.
    /// Should match the coast region in your height spline (steep transition zone).
    /// </summary>
    public float CoastThreshold { get; set; } = 0.40f;
    
    /// <summary>
    /// Continentalness threshold where mountains begin (for cliff/overhang generation).
    /// Example: 0.60 means mountains start appearing at C > 0.60.
    /// - Lower values (0.50): Mountains appear in more areas, more dramatic terrain
    /// - Higher values (0.85): Mountains only in highest continentalness, flatter world
    /// </summary>
    public float MountainThreshold { get; set; } = 0.55f;
    
    /// <summary>
    /// Frequency of cliff noise (inverse of feature size in blocks).
    /// Example: 1/40 = 0.025 means cliff features repeat every ~40 blocks.
    /// - Lower values (1/60 = 0.0167): Larger, smoother cliff faces
    /// - Higher values (1/25 = 0.04): More jagged, frequent cliff details
    /// </summary>
    public float CliffFrequency { get; set; } = 1f / 100f;
    
    /// <summary>
    /// Height amplitude of cliff variations in blocks.
    /// Example: 25 means cliffs can vary up to 25 blocks in height.
    /// - Lower values (15): Gentler, less dramatic cliffs
    /// - Higher values (40): Very dramatic, towering cliff faces
    /// </summary>
    public float CliffAmplitude { get; set; } = 25f;
    
    /// <summary>
    /// Frequency of 3D overhang noise (inverse of feature size).
    /// Example: 1/60 = 0.0167 means overhang features ~60 blocks wide.
    /// - Lower values (1/80 = 0.0125): Larger, smoother overhangs
    /// - Higher values (1/40 = 0.025): Smaller, more frequent overhangs
    /// </summary>
    public float OverhangFrequency { get; set; } = 1f / 60f;
    
    /// <summary>
    /// Strength/amplitude of overhang displacement in blocks.
    /// Example: 26 means overhangs can extend up to 26 blocks.
    /// - Lower values (12): Subtle overhangs
    /// - Higher values (40): Extreme overhangs, dramatic arches
    /// </summary>
    public float OverhangAmplitude { get; set; } = 26f;
    
    /// <summary>
    /// Range in blocks around sea level where shoreline detection occurs.
    /// Example: 2 means y <= SeaLevel + 2 checks for nearby water to place sand.
    /// - Lower values (1): Narrow shoreline, sharp transition
    /// - Higher values (4): Wide shoreline, more beach area
    /// </summary>
    public float ShorelineRange { get; set; } = 2f;
    
    // === BEACH SYSTEM (Variable Width) ===
    
    /// <summary>
    /// Maximum beach width in blocks around ocean edges.
    /// Beach width varies between 1 block (minimum) and this value based on noise.
    /// - 12 (default): Wide beaches with natural variation
    /// - 4: Narrow beaches
    /// - 16: Very wide beaches
    /// Only applies to ocean coastlines; rivers/lakes use 1-block adjacency.
    /// </summary>
    public float BeachMaxWidth { get; set; } = 12f;
    
    /// <summary>
    /// Frequency of beach width noise (inverse of feature size in blocks).
    /// Controls the scale of beach width variation along coastlines.
    /// - 1/60 (default): ~60 block features (blobby coastline variations)
    /// - 1/100: Larger, smoother beach width zones
    /// - 1/30: Smaller, more frequent width changes
    /// </summary>
    public float BeachNoiseScale { get; set; } = 1f / 60f;
    
    /// <summary>
    /// Strength of noise influence on beach width [0, 1].
    /// Controls how much the beach width varies from the mean.
    /// - 0.8 (default): Strong variation (beaches range from narrow to wide)
    /// - 0.5: Moderate variation
    /// - 1.0: Maximum variation
    /// </summary>
    public float BeachNoiseStrength { get; set; } = 0.8f;
    
    /// <summary>
    /// Depth in blocks of the subsurface layer below surface (e.g., dirt under grass).
    /// Example: 4 means 4 blocks of subsurface material below the surface before deep stone.
    /// - Lower values (2): Thin topsoil, stone closer to surface (can cause surface stone in steep areas)
    /// - Higher values (6): Thick soil layer, more digging before stone
    /// </summary>
    public float SubsurfaceDepth { get; set; } = 4f;
    
    /// <summary>
    /// Distance in blocks below surface where caves fade to prevent surface breaches.
    /// Example: 4 means caves attenuate from depth 0 to 4.
    /// - Lower values (3): Caves reach surface more easily, more cave entrances
    /// - Higher values (8): Caves stay deep, fewer natural entrances
    /// CHANGED: Reduced from 5 to 4 for more cave entrances near surface.
    /// </summary>
    public float CaveDepthFade { get; set; } = 4f;
    
    /// <summary>
    /// Slope threshold range for cave breach detection.
    /// Example: Min=0.25, Max=0.55 means gentler slopes allow breaches.
    /// - Lower min (0.2): Caves breach even on gentle slopes
    /// - Higher max (1.0): Only steeper slopes allow full breaches
    /// Technical: Applied with smoothstep(Min, Max, slope) for gradual attenuation.
    /// CHANGED: Reduced thresholds for more cave entrances on hillsides.
    /// </summary>
    public SlopeRange CaveSlopeFade { get; set; } = new() { Min = 0.25f, Max = 0.55f };
    
    /// <summary>
    /// Additional blocks below sea level where cave flooding extends inland.
    /// Example: 8 means caves up to y = SeaLevel + 8 are flooded if near coast.
    /// - Lower values (4): Less flooding, more dry caves near coast
    /// - Higher values (12): More flooding, wetter caves, fewer air pockets
    /// </summary>
    public float CaveFloodingExtension { get; set; } = 8f;
    
    /// <summary>
    /// Vertical range below terrain surface where overhang effects are applied (in blocks).
    /// Example: 65 means overhangs can form up to 65 blocks below the surface.
    /// - Lower values (40): Shallower overhangs, limited to near-surface
    /// - Higher values (90): Deeper overhangs, more dramatic caves
    /// </summary>
    public float OverhangDepthRange { get; set; } = 65f;
    
    /// <summary>
    /// Vertical range above terrain surface where overhang effects extend (in blocks).
    /// Example: 45 means overhangs can extend up to 45 blocks above the base surface.
    /// - Lower values (25): Smaller overhangs
    /// - Higher values (70): Larger, more dramatic floating formations
    /// </summary>
    public float OverhangHeightRange { get; set; } = 45f;
    
    /// <summary>
    /// Height factor denominator for overhang strength falloff.
    /// Example: 48 means overhang effect fades over 48-block vertical range.
    /// - Lower values (30): Sharper falloff, more concentrated overhangs
    /// - Higher values (65): Gentler falloff, more gradual overhang transitions
    /// </summary>
    public float OverhangFalloffRange { get; set; } = 48f;

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

    private readonly System.Text.Json.JsonSerializerOptions options = new() { WriteIndented = true };
    public void Save(string path)
    {
        try
        {
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
    /// Bakes the height spline to a float array for GPU upload as a 1D R32F texture.
    /// Samples the spline uniformly across [0,1] continentalness domain.
    /// </summary>
    /// <param name="samples">Number of samples to generate. Default: 256 (provides good interpolation quality).</param>
    /// <returns>Float array containing elevation values in blocks, indexed by normalized continentalness.</returns>
    public float[] BakeHeightSplineLut(int samples = 256)
        => HeightSpline.Bake(samples);

    /// <summary>
    /// Builds a 2D lookup table mapping (temperature, humidity) to biome IDs for GPU sampling.
    /// Returns a row-major byte array where each pixel represents a biome ID.
    /// IMPORTANT: This LUT only contains LAND biomes (AllowedTerrain == LandOnly).
    /// Ocean and Alpine biomes are handled via hardcoded checks in the shader (Phase 1 and Phase 2).
    /// The shader only samples this LUT for land areas after ocean/alpine checks.
    /// </summary>
    /// <param name="resolution">Resolution of the LUT (both width and height). Default: 256x256.</param>
    /// <returns>Byte array of size resolution² containing biome IDs (0-255).</returns>
    public byte[] BuildBiomeIdLut(int resolution = 256)
    {
        if (resolution <= 1) resolution = 2;
        var data = new byte[resolution * resolution];

        var landBiomes = Biomes.Where(b => b.AllowedTerrain == TerrainType.LandOnly).ToList();
        
        if (landBiomes.Count == 0)
        {
            Array.Fill(data, (byte)BiomeDefinition.DEFAULT_FALLBACK_BIOME_ID);
            return data;
        }

        for (var y = 0; y < resolution; y++)
        {
            var h = y / (float)(resolution - 1);
            for (var x = 0; x < resolution; x++)
            {
                var t = x / (float)(resolution - 1);
                var id = BiomeDefinition.SelectBestBiomeId(landBiomes, t, h);
                if (id < 0) id = BiomeDefinition.DEFAULT_FALLBACK_BIOME_ID;
                data[x + y * resolution] = (byte)landBiomes[id].Id;
            }
        }
        return data;
    }

    /// <summary>
    /// Packed terrain parameter block consumed by CPU generation (and the legacy SSBO upload path).
    /// Field order must stay in sync with terrain-common.glsl's std430 buffer.
    /// </summary>
    public struct TerrainGenerationParams
    {
        public uint Seed;
        public float WorldScale;
        public float MacroScale;
        public float ContinentalnessScale; public float ErosionScale; public float PeaksValleysScale;
        public float WarpScale; public float WarpStrength;
        public float BaseTemperature; public float TemperatureLapseRate; public float BaseHumidity; public float CoastDrying;
        
        // Phase 1: Climate caching parameters
        public float TemperatureScale; public float HumidityScale; public float WeirdnessScale;
        public float RegionCellSize; public float RegionJitter; public float RegionFeatherWidth; public uint MaxRegionMix;
        public float CheeseFrequency; public float CheeseAmplitude; public float SpaghettiFrequency; public float SpaghettiAmplitude;
        public float CaveCarveThreshold; public float CurlScale; public float CurlStrength;

        public float CoastThreshold;
        public float MountainThreshold;
        public float CliffFrequency;
        public float CliffAmplitude;
        public float OverhangFrequency;
        public float OverhangAmplitude;
        public float ShorelineRange;
        public float SubsurfaceDepth;
        public float CaveDepthFade;
        public float CaveSlopeFadeMin;
        public float CaveSlopeFadeMax;
        public float CaveFloodExtension;

        public float OceanThreshold;
        public float DeepOceanThreshold;
        public float AlpineElevation;
        public float CoastRange;

        public float OverhangDepthRange;
        public float OverhangHeightRange;
        public float OverhangFalloffRange;
    }

    public TerrainGenerationParams GetGenerationParams() => new()
    {
        Seed = (uint)Seed,
        WorldScale = WorldScale,
        MacroScale = 1.0f,
        ContinentalnessScale = Continentalness.BaseScale,
        ErosionScale = Erosion.BaseScale,
        PeaksValleysScale = PeaksValleys.BaseScale,
        WarpScale = WarpScale,
        WarpStrength = WarpStrength,
        BaseTemperature = BaseTemperature,
        TemperatureLapseRate = LapseRate,
        BaseHumidity = BaseHumidity,
        CoastDrying = CoastDrying,
        TemperatureScale = Temperature.BaseScale,
        HumidityScale = Humidity.BaseScale,
        WeirdnessScale = Weirdness.BaseScale,
        RegionCellSize = BiomeRegions.CellSizeChunks,
        RegionJitter = BiomeRegions.JitterStrength,
        RegionFeatherWidth = BiomeRegions.FeatherWidth,
        MaxRegionMix = (uint)BiomeRegions.MaxRegionMix,
        CheeseFrequency = Caves.CheeseFrequency,
        CheeseAmplitude = Caves.CheeseAmplitude,
        SpaghettiFrequency = Caves.SpaghettiFrequency,
        SpaghettiAmplitude = Caves.SpaghettiAmplitude,
        CaveCarveThreshold = Caves.CarveThreshold,
        CurlScale = Caves.CurlScale,
        CurlStrength = Caves.CurlStrength,
        CoastThreshold = CoastThreshold,
        MountainThreshold = MountainThreshold,
        CliffFrequency = CliffFrequency,
        CliffAmplitude = CliffAmplitude,
        OverhangFrequency = OverhangFrequency,
        OverhangAmplitude = OverhangAmplitude,
        ShorelineRange = ShorelineRange,
        SubsurfaceDepth = SubsurfaceDepth,
        CaveDepthFade = CaveDepthFade,
        CaveSlopeFadeMin = CaveSlopeFade.Min,
        CaveSlopeFadeMax = CaveSlopeFade.Max,
        CaveFloodExtension = CaveFloodingExtension,
        OceanThreshold = OceanThreshold,
        DeepOceanThreshold = DeepOceanThreshold,
        AlpineElevation = AlpineElevation,
        CoastRange = CoastRange,
        OverhangDepthRange = OverhangDepthRange,
        OverhangHeightRange = OverhangHeightRange,
        OverhangFalloffRange = OverhangFalloffRange,
    };
}

/// <summary>
/// Defines a normalized range with minimum and maximum bounds.
/// Used for climate comfort bands in biome definitions.
/// </summary>
/// <param name="Min">Minimum value of the range (inclusive). Range: [0,1].</param>
/// <param name="Max">Maximum value of the range (inclusive). Range: [0,1].</param>
public readonly record struct Range(float Min, float Max)
{
    /// <summary>
    /// Determines whether the specified value falls within this range (inclusive).
    /// </summary>
    /// <param name="v">Value to test.</param>
    /// <returns>True if v is between Min and Max (inclusive), false otherwise.</returns>
    public bool Contains(float v) => v >= Min && v <= Max;
    
    /// <summary>
    /// Gets the center point of this range.
    /// Used for distance calculations when selecting the best-matching biome.
    /// </summary>
    public float Center => (Min + Max) * 0.5f;
}

/// <summary>
/// Defines a slope range used for cave breach detection on terrain surfaces.
/// Slopes are calculated as the maximum height difference between neighboring blocks.
/// </summary>
public struct SlopeRange
{
    /// <summary>
    /// Gets or sets the minimum slope threshold for cave breaching.
    /// Slopes below this value will not allow caves to breach the surface.
    /// - 0.5 (default): Gentle slopes (0.5 block rise per block horizontal) start allowing breaches
    /// - 0.3: Even gentler slopes allow breaches (more cave entrances)
    /// - 1.0: Only moderate slopes allow breaches (fewer entrances)
    /// Technical: Applied with smoothstep(Min, Max, slope) for gradual attenuation.
    /// </summary>
    public float Min { get; set; }
    
    /// <summary>
    /// Gets or sets the maximum slope threshold for full cave breaching.
    /// Slopes above this value will have maximum cave breach probability.
    /// - 2.0 (default): Steep slopes (2 block rise per block horizontal) have full breach chance
    /// - 3.0: Only very steep cliffs allow full breaching
    /// - 1.5: Moderate slopes already allow full breaching
    /// Technical: Applied with smoothstep(Min, Max, slope) for gradual attenuation.
    /// </summary>
    public float Max { get; set; }
    
    /// <summary>
    /// Initializes a new instance of <see cref="SlopeRange"/> with default values.
    /// </summary>
    public SlopeRange()
    {
        Min = 0.5f;
        Max = 2.0f;
    }
}

/// <summary>
/// Defines terrain type constraints that determine where a biome can appear.
/// Used to ensure ocean biomes stay in water, alpine on mountains, etc.
/// </summary>
public enum TerrainType
{
    /// <summary>
    /// No terrain restriction. Biome can appear anywhere if climate conditions match.
    /// Used for flexible biomes that adapt to any elevation.
    /// </summary>
    Any = 0,
    
    /// <summary>
    /// Only appears in ocean areas where continentalness is below OceanThreshold.
    /// Used for underwater biomes like Ocean and Deep Ocean.
    /// Technical: Checked via hardcoded logic before LUT sampling.
    /// </summary>
    OceanOnly = 1,
    
    /// <summary>
    /// Only appears on land areas where continentalness is above OceanThreshold.
    /// Used for all terrestrial biomes (Plains, Desert, Forest, etc.).
    /// Technical: These biomes populate the climate LUT for land-only sampling.
    /// </summary>
    LandOnly = 2,
    
    /// <summary>
    /// Only appears near coast transition zones (continentalness near OceanThreshold).
    /// Used for shoreline biomes like Beach that require proximity to water.
    /// Technical: Checked via |C - OceanThreshold| < CoastRange.
    /// </summary>
    CoastOnly = 3,
    
    /// <summary>
    /// Only appears at high elevations above AlpineElevation threshold.
    /// Used for mountain peak biomes like Alpine that require altitude.
    /// Technical: Checked via hardcoded elevation override before LUT sampling.
    /// </summary>
    MountainOnly = 4
}

/// <summary>
/// Defines a biome with its climate requirements, elevation constraints, texture mappings, and placement priority.
/// Biomes are selected in the shader based on a combination of hardcoded checks (ocean, alpine) and
/// climate-based LUT sampling (temperature × humidity).
/// </summary>
public sealed class BiomeDefinition
{
    /// <summary>
    /// Hardcoded biome ID for Ocean. Must match shader constant OCEAN_BIOME_ID.
    /// </summary>
    public const int OCEAN_BIOME_ID = (int)BiomeId.Ocean;
    
    /// <summary>
    /// Hardcoded biome ID for Alpine. Must match shader constant ALPINE_BIOME_ID.
    /// </summary>
    public const int ALPINE_BIOME_ID = (int)BiomeId.Alpine;
    
    /// <summary>
    /// Default fallback biome ID used when LUT sampling fails. Must match shader constant DEFAULT_FALLBACK_BIOME_ID.
    /// </summary>
    public const int DEFAULT_FALLBACK_BIOME_ID = (int)BiomeId.Plains;
    
    /// <summary>
    /// Gets or sets the unique biome identifier used in shaders and save files.
    /// Must be unique across all biome definitions.
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// Gets or sets the human-readable biome name for debugging and UI display.
    /// </summary>
    public string Name { get; set; } = "Unnamed";

    /// <summary>
    /// Gets or sets the temperature comfort range [0,1] where this biome naturally occurs.
    /// 0=frozen, 0.25=cold, 0.5=temperate, 0.75=warm, 1.0=scorching.
    /// Used during LUT generation to map temperature/humidity coordinates to biome IDs.
    /// </summary>
    public Range Temperature { get; set; } = new(0.4f, 0.6f);
    
    /// <summary>
    /// Gets or sets the humidity comfort range [0,1] where this biome naturally occurs.
    /// 0=arid, 0.33=dry, 0.5=moderate, 0.66=humid, 1.0=very wet.
    /// Used during LUT generation to map temperature/humidity coordinates to biome IDs.
    /// </summary>
    public Range Humidity { get; set; } = new(0.4f, 0.6f);
    
    /// <summary>
    /// Gets or sets the minimum elevation in blocks where this biome can appear.
    /// Set to float.MinValue for no minimum constraint.
    /// Currently not actively enforced in shaders (reserved for future use).
    /// </summary>
    public float MinElevation { get; set; } = float.MinValue;
    
    /// <summary>
    /// Gets or sets the maximum elevation in blocks where this biome can appear.
    /// Set to float.MaxValue for no maximum constraint.
    /// Currently not actively enforced in shaders (reserved for future use).
    /// </summary>
    public float MaxElevation { get; set; } = float.MaxValue;
    
    /// <summary>
    /// Gets or sets the terrain type constraint determining where this biome can physically appear.
    /// Controls whether the biome is restricted to ocean, land, mountains, coasts, or has no restriction.
    /// See <see cref="TerrainType"/> for available options.
    /// </summary>
    public TerrainType AllowedTerrain { get; set; } = TerrainType.Any;
    
    /// <summary>
    /// Gets or sets the selection priority for this biome during shader evaluation.
    /// Higher priorities are checked first:
    /// - 100: Hardcoded checks (Ocean) processed before LUT
    /// - 90: Hardcoded overrides (Alpine) processed before LUT
    /// - 50: Normal climate biomes sampled from LUT
    /// - 0: Fallback biomes used when nothing else matches
    /// </summary>
    public int Priority { get; set; } = 0;

    // === HEIGHT GENERATION ATTRIBUTES ===
    
    /// <summary>
    /// Base elevation offset from water level for this biome.
    /// Positive = above water, negative = below water.
    /// Used during terrain height generation to shape the base terrain.
    /// </summary>
    public float BaseHeight { get; set; } = 10f;
    
    /// <summary>
    /// How much the terrain height varies within this biome.
    /// Low values = flat terrain (plains, beach), high values = dramatic hills/mountains (alpine).
    /// </summary>
    public float HeightVariation { get; set; } = 15f;
    
    /// <summary>
    /// Multiplier for how much peaks/valleys noise affects this biome's terrain.
    /// 0 = completely flat, 1 = full effect.
    /// </summary>
    public float PeaksInfluence { get; set; } = 0.5f;
    
    /// <summary>
    /// How much erosion smooths this biome's terrain.
    /// Higher values make the terrain smoother in high-erosion areas.
    /// </summary>
    public float ErosionSensitivity { get; set; } = 0.5f;

    // === BLOCK ASSIGNMENT (Minecraft-style) ===
    
    /// <summary>
    /// Block type for the topmost solid layer (e.g., Grass, Sand, Snow).
    /// </summary>
    public BlockId SurfaceBlock { get; set; } = BlockId.Grass;
    
    /// <summary>
    /// Block type for 2-4 blocks below surface (e.g., Dirt, Sand).
    /// </summary>
    public BlockId SubsurfaceBlock { get; set; } = BlockId.Dirt;
    
    /// <summary>
    /// Block type for deep underground (usually Stone).
    /// </summary>
    public BlockId DeepBlock { get; set; } = BlockId.Stone;
    
    /// <summary>
    /// Block type for underwater surface (ocean/river floor).
    /// </summary>
    public BlockId UnderwaterSurfaceBlock { get; set; } = BlockId.Gravel;
    
    /// <summary>
    /// Block type for underwater subsurface (below ocean floor).
    /// </summary>
    public BlockId UnderwaterSubsurfaceBlock { get; set; } = BlockId.Stone;

    // === VEGETATION ===
    
    /// <summary>
    /// List of vegetation rules for this biome.
    /// Each rule defines a type of vegetation (tree, flower, etc.) and its density.
    /// </summary>
    public List<VegetationRule> Vegetation { get; set; } = [];

    /// <summary>
    /// Initializes a new instance of <see cref="BiomeDefinition"/> with default values.
    /// </summary>
    public BiomeDefinition() { }

    /// <summary>
    /// Initializes a new instance of <see cref="BiomeDefinition"/> with specified parameters.
    /// </summary>
    public BiomeDefinition(int id, string name, Range temperature, Range humidity,
        int priority = 0, TerrainType terrainType = TerrainType.Any, float minElevation = float.MinValue, float maxElevation = float.MaxValue,
        float baseHeight = 10f, float heightVariation = 15f, float peaksInfluence = 0.5f, float erosionSensitivity = 0.5f,
        BlockId surfaceBlock = BlockId.Grass, BlockId subsurfaceBlock = BlockId.Dirt, BlockId deepBlock = BlockId.Stone,
        BlockId underwaterSurfaceBlock = BlockId.Gravel, BlockId underwaterSubsurfaceBlock = BlockId.Stone)
    {
        Id = id;
        Name = name;
        Temperature = temperature;
        Humidity = humidity;
        Priority = priority;
        AllowedTerrain = terrainType;
        MinElevation = minElevation;
        MaxElevation = maxElevation;
        BaseHeight = baseHeight;
        HeightVariation = heightVariation;
        PeaksInfluence = peaksInfluence;
        ErosionSensitivity = erosionSensitivity;
        SurfaceBlock = surfaceBlock;
        SubsurfaceBlock = subsurfaceBlock;
        DeepBlock = deepBlock;
        UnderwaterSurfaceBlock = underwaterSurfaceBlock;
        UnderwaterSubsurfaceBlock = underwaterSubsurfaceBlock;
    }

    /// <summary>
    /// Selects the best-matching biome from a list based on temperature and humidity values.
    /// Prefers biomes where both temperature and humidity fall within range.
    /// Falls back to nearest biome by L1 distance to range centers if no perfect match exists.
    /// </summary>
    /// <param name="biomes">List of biomes to select from.</param>
    /// <param name="temperature01">Temperature value [0,1].</param>
    /// <param name="humidity01">Humidity value [0,1].</param>
    /// <returns>Index of best-matching biome in the list, or -1 if list is empty.</returns>
    public static int SelectBestBiomeId(List<BiomeDefinition> biomes, float temperature01, float humidity01)
    {
        if (biomes.Count == 0) return -1;

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

    /// <summary>
    /// Creates the default set of 10 biomes with standard configurations.
    /// Includes: Ocean (0), Beach (1), Plains (2), Savanna (3), Desert (4), Rainforest (5),
    /// Taiga (6), Tundra (7), Highlands (8), Alpine (9).
    /// </summary>
    /// <returns>List of configured biome definitions.</returns>
    public static List<BiomeDefinition> DefaultSet()
    {
        return
        [
            // Ocean: below water, relatively flat but with some variation for underwater hills
            new (OCEAN_BIOME_ID, nameof(BiomeId.Ocean), 
                new(0.0f, 1.0f), new(0.0f, 1.0f),
                priority: 100, 
                terrainType: TerrainType.OceanOnly,
                baseHeight: -25f, heightVariation: 12f, peaksInfluence: 0.2f, erosionSensitivity: 0.7f,
                surfaceBlock: BlockId.Gravel, subsurfaceBlock: BlockId.Gravel, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Bedrock, underwaterSubsurfaceBlock: BlockId.Gravel),
            
            // DeepOcean: deeper underwater regions with gravel/stone floor
            new ((int)BiomeId.DeepOcean, nameof(BiomeId.DeepOcean), 
                new(0.0f, 1.0f), new(0.0f, 1.0f),
                priority: 100, 
                terrainType: TerrainType.OceanOnly,
                baseHeight: -30f, heightVariation: 8f, peaksInfluence: 0.1f, erosionSensitivity: 0.8f,
                surfaceBlock: BlockId.Gravel, subsurfaceBlock: BlockId.Gravel, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Gravel, underwaterSubsurfaceBlock: BlockId.Stone),
            
            // Alpine: very high, dramatic peaks and valleys
            new (ALPINE_BIOME_ID, nameof(BiomeId.Alpine), 
                new(0.0f, 1.0f), new(0.0f, 1.0f),
                priority: 90, 
                terrainType: TerrainType.MountainOnly,
                minElevation: 150f,
                baseHeight: 140f, heightVariation: 70f, peaksInfluence: 1.0f, erosionSensitivity: 0.2f,
                surfaceBlock: BlockId.Snow, subsurfaceBlock: BlockId.SnowDirt, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Gravel, underwaterSubsurfaceBlock: BlockId.Stone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.TreeSpruce, Density = 0.005f, AllowedSurfaceBlocks = [BlockId.Snow, BlockId.SnowDirt] } // Very sparse trees
                }
            },
            
            // Taiga: cold, wet - coniferous forests
            new ((int)BiomeId.Taiga, nameof(BiomeId.Taiga), 
                new(0.15f, 0.35f), new(0.5f, 0.8f),  // Cold + humid
                priority: 50, 
                terrainType: TerrainType.LandOnly,
                baseHeight: 35f, heightVariation: 25f, peaksInfluence: 0.6f, erosionSensitivity: 0.5f,
                surfaceBlock: BlockId.Podzol, subsurfaceBlock: BlockId.Dirt, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Gravel, underwaterSubsurfaceBlock: BlockId.Stone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.TreeSpruce, Density = 0.05f, AllowedSurfaceBlocks = [BlockId.Podzol, BlockId.Dirt, BlockId.Grass] },
                    new() { Type = VegetationType.Grass, Density = 0.1f, AllowedSurfaceBlocks = [BlockId.Podzol, BlockId.Dirt, BlockId.Grass] }
                }
            },
            
            // Highlands: cool, dry - elevated grasslands
            new ((int)BiomeId.Highlands, nameof(BiomeId.Highlands), 
                new(0.30f, 0.50f), new(0.25f, 0.50f),  // Cool + moderate humidity
                priority: 50, 
                terrainType: TerrainType.LandOnly,
                baseHeight: 55f, heightVariation: 35f, peaksInfluence: 0.7f, erosionSensitivity: 0.4f,
                surfaceBlock: BlockId.Grass, subsurfaceBlock: BlockId.Dirt, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Gravel, underwaterSubsurfaceBlock: BlockId.Stone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.Grass, Density = 0.2f, AllowedSurfaceBlocks = [BlockId.Grass] },
                    new() { Type = VegetationType.Flower, Density = 0.05f, AllowedSurfaceBlocks = [BlockId.Grass] },
                    new() { Type = VegetationType.TreeOak, Density = 0.008f, AllowedSurfaceBlocks = [BlockId.Grass] } // Very sparse trees
                }
            },
            
            // Plains: temperate, moderate humidity - THE MOST COMMON biome
            new (DEFAULT_FALLBACK_BIOME_ID, nameof(BiomeId.Plains), 
                new(0.40f, 0.70f), new(0.30f, 0.70f),  // Wide temperate range
                priority: 50, 
                terrainType: TerrainType.LandOnly,
                baseHeight: 12f, heightVariation: 8f, peaksInfluence: 0.2f, erosionSensitivity: 0.8f,
                surfaceBlock: BlockId.Grass, subsurfaceBlock: BlockId.Dirt, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Gravel, underwaterSubsurfaceBlock: BlockId.Stone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.Grass, Density = 0.3f, AllowedSurfaceBlocks = [BlockId.Grass] },
                    new() { Type = VegetationType.Flower, Density = 0.1f, AllowedSurfaceBlocks = [BlockId.Grass] },
                    new() { Type = VegetationType.TreeOak, Density = 0.01f, AllowedSurfaceBlocks = [BlockId.Grass] }
                }
            },
            
            // Beach: at water level, completely flat - coastal areas only
            new ((int)BiomeId.Beach, nameof(BiomeId.Beach), 
                new(0.0f, 1.0f), new(0.0f, 1.0f),  // Any climate near shore
                priority: 80,  // Higher than regular land biomes but below ocean
                terrainType: TerrainType.CoastOnly,
                baseHeight: 2f, heightVariation: 3f, peaksInfluence: 0.05f, erosionSensitivity: 0.95f,
                surfaceBlock: BlockId.Sand, subsurfaceBlock: BlockId.Sand, deepBlock: BlockId.Sandstone,
                underwaterSurfaceBlock: BlockId.Sand, underwaterSubsurfaceBlock: BlockId.Sandstone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.SugarCane, Density = 0.05f, AllowedSurfaceBlocks = [BlockId.Sand] }
                }
            },
            
            // Tundra: very cold, any humidity - frozen plains
            new ((int)BiomeId.Tundra, nameof(BiomeId.Tundra), 
                new(0.0f, 0.20f), new(0.0f, 0.6f),  // Very cold, any humidity
                priority: 50, 
                terrainType: TerrainType.LandOnly,
                baseHeight: 18f, heightVariation: 12f, peaksInfluence: 0.3f, erosionSensitivity: 0.6f,
                surfaceBlock: BlockId.GrassSnowy, subsurfaceBlock: BlockId.Dirt, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Gravel, underwaterSubsurfaceBlock: BlockId.Stone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.TreeSpruce, Density = 0.01f, AllowedSurfaceBlocks = [BlockId.GrassSnowy, BlockId.Dirt] }
                }
            },
            
            // Rainforest: hot, very wet - tropical jungle
            new ((int)BiomeId.Rainforest, nameof(BiomeId.Rainforest), 
                new(0.70f, 1.0f), new(0.70f, 1.0f),  // Hot + very wet
                priority: 50, 
                terrainType: TerrainType.LandOnly,
                baseHeight: 28f, heightVariation: 22f, peaksInfluence: 0.5f, erosionSensitivity: 0.5f,
                surfaceBlock: BlockId.Grass, subsurfaceBlock: BlockId.Dirt, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Clay, underwaterSubsurfaceBlock: BlockId.Stone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.TreeJungle, Density = 0.15f, AllowedSurfaceBlocks = [BlockId.Grass] },
                    new() { Type = VegetationType.Grass, Density = 0.5f, AllowedSurfaceBlocks = [BlockId.Grass] },
                    new() { Type = VegetationType.Flower, Density = 0.2f, AllowedSurfaceBlocks = [BlockId.Grass] }
                }
            },
            
            // Savanna: warm, dry - African-style grassland
            new ((int)BiomeId.Savanna, nameof(BiomeId.Savanna), 
                new(0.65f, 0.85f), new(0.20f, 0.45f),  // Warm + dry
                priority: 50, 
                terrainType: TerrainType.LandOnly,
                baseHeight: 20f, heightVariation: 15f, peaksInfluence: 0.4f, erosionSensitivity: 0.6f,
                surfaceBlock: BlockId.CoarseDirt, subsurfaceBlock: BlockId.Dirt, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Clay, underwaterSubsurfaceBlock: BlockId.Stone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.TreeOak, Density = 0.02f, AllowedSurfaceBlocks = [BlockId.CoarseDirt, BlockId.Grass] }, // Acacia placeholder
                    new() { Type = VegetationType.Grass, Density = 0.4f, AllowedSurfaceBlocks = [BlockId.CoarseDirt, BlockId.Grass] }
                }
            },
            
            // Desert: hot, very dry
            new ((int)BiomeId.Desert, nameof(BiomeId.Desert), 
                new(0.75f, 1.0f), new(0.0f, 0.20f),  // Hot + very dry
                priority: 50, 
                terrainType: TerrainType.LandOnly,
                baseHeight: 15f, heightVariation: 10f, peaksInfluence: 0.3f, erosionSensitivity: 0.7f,
                surfaceBlock: BlockId.Sand, subsurfaceBlock: BlockId.Sand, deepBlock: BlockId.Sandstone,
                underwaterSurfaceBlock: BlockId.Sand, underwaterSubsurfaceBlock: BlockId.Sandstone)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.Cactus, Density = 0.02f, AllowedSurfaceBlocks = [BlockId.Sand] },
                    new() { Type = VegetationType.DeadBush, Density = 0.05f, AllowedSurfaceBlocks = [BlockId.Sand] }
                }
            },

            // Swamp: warm-temperate, very wet
            new ((int)BiomeId.Swamp, nameof(BiomeId.Swamp), 
                new(0.50f, 0.70f), new(0.75f, 1.0f),  // Temperate + very wet
                priority: 50, 
                terrainType: TerrainType.LandOnly,
                baseHeight: 11f, heightVariation: 5f, peaksInfluence: 0.1f, erosionSensitivity: 0.9f,
                surfaceBlock: BlockId.Grass, subsurfaceBlock: BlockId.Dirt, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Clay, underwaterSubsurfaceBlock: BlockId.Dirt)
            {
                Vegetation = 
                {
                    new() { Type = VegetationType.TreeOak, Density = 0.08f, AllowedSurfaceBlocks = [BlockId.Grass, BlockId.Dirt] },
                    new() { Type = VegetationType.Grass, Density = 0.3f, AllowedSurfaceBlocks = [BlockId.Grass] },
                    new() { Type = VegetationType.BlueOrchid, Density = 0.1f, AllowedSurfaceBlocks = [BlockId.Grass] } // Blue orchids in swamp
                }
            },
            
            // Lake: inland water body with sand shores (Phase 2)
            // Uses Any terrain type but is selected via lake noise, not climate
            new ((int)BiomeId.Lake, nameof(BiomeId.Lake), 
                new(0.0f, 1.0f), new(0.0f, 1.0f),  // Any climate where lake noise is high
                priority: 85,  // Higher than land biomes, checked when lake noise exceeds threshold
                terrainType: TerrainType.Any,  // Can appear anywhere on land
                baseHeight: 0f, heightVariation: 2f, peaksInfluence: 0.05f, erosionSensitivity: 0.95f,
                surfaceBlock: BlockId.Sand, subsurfaceBlock: BlockId.Sand, deepBlock: BlockId.Stone,
                underwaterSurfaceBlock: BlockId.Sand, underwaterSubsurfaceBlock: BlockId.Gravel),
        ];
    }
}

/// <summary>
/// Defines parameters for biome region grouping using Voronoi/Worley noise.
/// Regions are large areas (multiple chunks) with a dominant biome and feathered boundaries.
/// This system creates natural biome clustering and prevents rapid biome transitions.
/// </summary>
public sealed class BiomeRegionParams
{
    /// <summary>
    /// Gets or sets the desired region cell size in chunks (both X and Z dimensions).
    /// Each region is centered on a Voronoi cell and extends across multiple chunks.
    /// - 4 (default): Regions are 4×4 chunks (64×64 blocks)
    /// - 8: Larger regions (128×128 blocks), less biome variety per area
    /// - 2: Smaller regions (32×32 blocks), more frequent biome changes
    /// Technical: Used in shader as: cellSize = CellSizeChunks * CHUNK_SIDE_SIZE (16).
    /// </summary>
    public float CellSizeChunks { get; set; } = 4f;
    
    /// <summary>
    /// Gets or sets the jitter strength [0,1] applied to Voronoi cell centers.
    /// Randomizes region center positions to prevent grid-like patterns.
    /// - 0.35 (default): Moderate jitter (natural looking distribution)
    /// - 0.0: No jitter (perfect grid, very artificial)
    /// - 0.7: Strong jitter (highly irregular regions, can create very small regions)
    /// Technical: Applied as: center += (random2D() - 0.5) * JitterStrength.
    /// </summary>
    public float JitterStrength { get; set; } = 0.35f;
    
    /// <summary>
    /// Gets or sets the feather width in world units (blocks) for blending between neighboring regions.
    /// Controls the softness of biome boundaries.
    /// - 3 (default): Clean transitions (recommended)
    /// - 1-2: Sharp transitions (may show visible seams)
    /// - 6+: Very soft transitions (can create "spray paint" mixing effect)
    /// Technical: Applied with smoothstep(d0, d0 + feather, distance) where d0 is nearest region distance.
    /// </summary>
    public float FeatherWidth { get; set; } = 3f;
    
    /// <summary>
    /// Gets or sets the maximum number of neighboring regions to blend together.
    /// Higher values create smoother transitions but increase GPU cost.
    /// - 3 (default): Blend 3 nearest regions
    /// - 4: Smoother transitions (slightly more expensive)
    /// - 2: Faster but sharper transitions
    /// Technical: Worley noise finds K nearest cell centers and weights them by distance.
    /// </summary>
    public int MaxRegionMix { get; set; } = 3;

    /// <summary>
    /// Creates a BiomeRegionParams instance with default values.
    /// </summary>
    public static BiomeRegionParams Default() => new();
}

/// <summary>
/// Defines parameters for procedural cave generation using dual-system approach.
/// Combines "cheese" caves (large chambers) with "spaghetti" caves (winding tunnels).
/// Both systems use 3D noise with depth/slope attenuation to prevent surface breaches.
/// </summary>
public sealed class CaveParams
{
    /// <summary>
    /// Gets or sets the frequency of cheese cave noise (inverse of feature size).
    /// Controls the size of large cavern systems.
    /// - 1/180 (default): Large chambers (~180 block spacing)
    /// - 1/200: Very large caverns
    /// - 1/100: Smaller, more frequent chambers
    /// Technical: Applied as fbm3D(position * CheeseFrequency).
    /// CHANGED: Reduced from 1/140 to 1/180 for larger individual caverns.
    /// </summary>
    public float CheeseFrequency { get; set; } = 1f / 180f;
    
    /// <summary>
    /// Gets or sets the amplitude multiplier for cheese cave density.
    /// Higher values emphasize large hollow pockets, lower values keep caverns tighter.
    /// CHANGED: Increased from 1.1 to 1.4 for more spacious caverns.
    /// </summary>
    public float CheeseAmplitude { get; set; } = 1.4f;

    /// <summary>
    /// Gets or sets the frequency of spaghetti cave noise (inverse of feature size).
    /// Controls the size and spacing of tunnel systems.
    /// - 1/140 (default): Long winding tunnels (~140 block wavelength)
    /// - 1/160: Very long tunnels
    /// - 1/80: Shorter, tighter tunnels
    /// Technical: Uses two perpendicular noise fields to create worm-like structures.
    /// CHANGED: Reduced from 1/110 to 1/140 for wider tunnels.
    /// </summary>
    public float SpaghettiFrequency { get; set; } = 1f / 140f;
    
    /// <summary>
    /// Gets or sets the amplitude multiplier for spaghetti cave density.
    /// Higher values widen tunnels, lower values keep them tight and winding.
    /// CHANGED: Increased from 1.2 to 1.5 for wider tunnels (at least 4-6 blocks tall).
    /// </summary>
    public float SpaghettiAmplitude { get; set; } = 1.5f;

    /// <summary>
    /// Gets or sets the density threshold for cave carving [0,1].
    /// Higher values create fewer, smaller caves; lower values create more, larger caves.
    /// - 0.88 (default): Moderate cave frequency with larger individual caves
    /// - 0.75: Higher cave frequency (more caves, easier underground navigation)
    /// - 0.95: Lower cave frequency (rare caves, more solid underground)
    /// Technical: If caveDensity * attenuation > CarveThreshold, carve air block.
    /// CHANGED: Reduced from 0.92 to 0.88 for larger cave volumes.
    /// </summary>
    public float CarveThreshold { get; set; } = 0.88f;

    /// <summary>
    /// Gets or sets the curl noise scale for cave path distortion.
    /// Currently unused. Reserved for future cave curvature control.
    /// </summary>
    public float CurlScale { get; set; } = 1f / 120f;
    
    /// <summary>
    /// Gets or sets the curl noise strength for cave path distortion.
    /// Currently unused. Reserved for future cave curvature intensity.
    /// </summary>
    public float CurlStrength { get; set; } = 12f;

    /// <summary>
    /// Gets or sets the ridge carving threshold for mountain caves.
    /// Currently unused. Reserved for future ridge-aligned cave systems.
    /// </summary>
    public float RidgeCarve { get; set; } = 0.35f;
    
    /// <summary>
    /// Gets or sets the Y-frequency multiplier for spaghetti caves to favor horizontal tunnels.
    /// Values GREATER than 1.0 stretch the noise vertically, making caves more horizontal.
    /// Values LESS than 1.0 compress vertically, making caves more vertical.
    /// - 2.0 (default): Moderate horizontal bias (~2x more likely to be horizontal)
    /// - 1.0: Equal horizontal/vertical tendency (no bias)
    /// - 4.0: Strong horizontal bias (very flat cave systems)
    /// CHANGED: Reduced from 2.5 to 2.0 for more natural cave shapes.
    /// </summary>
    public float SpaghettiYStretch { get; set; } = 2.0f;
    
    /// <summary>
    /// Gets or sets the slope value above which caves can breach the surface on hillsides.
    /// - 0.5 (default): ~26 degree slopes start allowing cave entrances
    /// - 0.3: Gentler slopes allow entrances (more entrances)
    /// - 0.7: Only steep slopes allow entrances (fewer entrances)
    /// CHANGED: Increased from 0.4 to 0.5 for more natural-looking entrances.
    /// </summary>
    public float SurfaceBreachSlopeMin { get; set; } = 0.5f;
    
    /// <summary>
    /// Gets or sets the maximum probability of surface breach on steep terrain.
    /// - 0.25 (default): Up to 25% chance on very steep slopes
    /// - 0.15: Conservative breach rate
    /// - 0.4: Aggressive breach rate (many cave entrances)
    /// CHANGED: Reduced from 0.35 to 0.25 for fewer but more natural entrances.
    /// </summary>
    public float SurfaceBreachMaxChance { get; set; } = 0.25f;
    
    /// <summary>
    /// Gets or sets the depth below surface (in blocks) where cave floor uses stone instead of grass.
    /// Prevents grass from appearing on cave floors in complete darkness.
    /// - 6 (default): Cave floors below 6 blocks from surface use stone
    /// - 4: More aggressive stone replacement
    /// - 10: Only very deep caves get stone floors
    /// </summary>
    public int CaveFloorDepthThreshold { get; set; } = 6;
    
    /// <summary>
    /// Gets or sets the frequency of entrance shape noise.
    /// This noise clusters cave breaches into coherent roundish openings instead of scattered holes.
    /// - 1/30 (default): Creates ~30 block entrance features
    /// - 1/20: Smaller, more frequent entrances
    /// - 1/50: Larger entrance zones but less frequent
    /// </summary>
    public float EntranceNoiseFrequency { get; set; } = 1f / 30f;
    
    /// <summary>
    /// Gets or sets the threshold for entrance noise [0, 1].
    /// Only areas where entrance noise exceeds this will allow surface breaches.
    /// - 0.6 (default): ~40% of steep slopes can have entrances (clustered)
    /// - 0.7: ~30% (fewer, more distinct entrances)
    /// - 0.5: ~50% (more frequent but still clustered)
    /// </summary>
    public float EntranceNoiseThreshold { get; set; } = 0.6f;
    
    /// <summary>
    /// Gets or sets the minimum cave volume required before allowing surface breach.
    /// Prevents "sieve" effect where tiny cave fragments poke through surface.
    /// - 3 (default): Requires at least 3 blocks of cave below breach point
    /// - 2: More permissive (more entrances but risk of sieves)
    /// - 5: Conservative (only large caves breach)
    /// </summary>
    public int MinBreachCaveDepth { get; set; } = 3;

    /// <summary>
    /// Creates a CaveParams instance with default values.
    /// </summary>
    public static CaveParams Default() => new();
}

/// <summary>
/// Configuration for ore generation following Minecraft-style depth distribution.
/// Ores are scattered in stone regions based on their preferred Y-level ranges.
/// </summary>
public sealed class OreParams
{
    /// <summary>
    /// Gets or sets the list of ore definitions specifying which ores spawn and where.
    /// Each ore has a depth range, rarity, and vein size configuration.
    /// </summary>
    public List<OreDefinition> OreTypes { get; set; } = DefaultOreTypes();

    /// <summary>
    /// Gets or sets the base ore generation seed offset.
    /// Added to terrain seed to create deterministic but varied ore placement.
    /// </summary>
    public uint SeedOffset { get; set; } = 7777u;

    /// <summary>
    /// Creates default ore generation parameters with Minecraft-style ore distribution.
    /// </summary>
    public static OreParams Default() => new();

    /// <summary>
    /// Creates the default set of ore definitions matching Minecraft 1.18+ distribution.
    /// </summary>
    public static List<OreDefinition> DefaultOreTypes() =>
    [
        // Coal: Common ore, spans wide depth range, most common in upper stone
        new OreDefinition
        {
            OreBlock = BlockId.CoalOre,
            MinY = 0,
            MaxY = 192,
            PeakY = 96,
            Rarity = 0.012f,         // ~1.2% chance at peak depth
            VeinSize = 17,
            DistributionType = OreDistribution.Triangle
        },
        // Copper: Mid-tier ore, concentrated in middle depths
        new OreDefinition
        {
            OreBlock = BlockId.CopperOre,
            MinY = 0,
            MaxY = 96,
            PeakY = 48,
            Rarity = 0.008f,
            VeinSize = 10,
            DistributionType = OreDistribution.Triangle
        },
        // Iron: Essential ore, two distribution peaks (surface and deep)
        new OreDefinition
        {
            OreBlock = BlockId.IronOre,
            MinY = -32,
            MaxY = 72,
            PeakY = 16,
            Rarity = 0.009f,
            VeinSize = 9,
            DistributionType = OreDistribution.Triangle
        },
        // Gold: Rare ore, concentrated deep underground and in badlands
        new OreDefinition
        {
            OreBlock = BlockId.GoldOre,
            MinY = -64,
            MaxY = 32,
            PeakY = -16,
            Rarity = 0.003f,
            VeinSize = 9,
            DistributionType = OreDistribution.Triangle
        },
        // Diamond: Very rare, deep underground only
        new OreDefinition
        {
            OreBlock = BlockId.DiamondOre,
            MinY = -64,
            MaxY = 16,
            PeakY = -58,
            Rarity = 0.0015f,
            VeinSize = 8,
            DistributionType = OreDistribution.Triangle
        }
    ];
}

/// <summary>
/// Defines a single ore type's spawning parameters.
/// </summary>
public sealed class OreDefinition
{
    /// <summary>
    /// The block type to place for this ore.
    /// </summary>
    public BlockId OreBlock { get; set; } = BlockId.CoalOre;

    /// <summary>
    /// Minimum Y-level where this ore can spawn (relative to sea level for negative values).
    /// </summary>
    public int MinY { get; set; } = 0;

    /// <summary>
    /// Maximum Y-level where this ore can spawn.
    /// </summary>
    public int MaxY { get; set; } = 128;

    /// <summary>
    /// Y-level with maximum spawn probability (for triangle distribution).
    /// </summary>
    public int PeakY { get; set; } = 64;

    /// <summary>
    /// Base spawn probability [0,1] at peak depth.
    /// Actual probability decreases away from PeakY based on DistributionType.
    /// </summary>
    public float Rarity { get; set; } = 0.01f;

    /// <summary>
    /// Maximum number of blocks in an ore vein.
    /// Actual vein size varies randomly from 1 to VeinSize.
    /// </summary>
    public int VeinSize { get; set; } = 8;

    /// <summary>
    /// How ore probability varies with depth within the Y range.
    /// </summary>
    public OreDistribution DistributionType { get; set; } = OreDistribution.Uniform;
}

/// <summary>
/// Ore spawn probability distribution types.
/// </summary>
public enum OreDistribution
{
    /// <summary>
    /// Equal probability throughout the Y range.
    /// </summary>
    Uniform,

    /// <summary>
    /// Probability peaks at PeakY and decreases linearly toward MinY and MaxY.
    /// Mimics Minecraft 1.18+ ore distribution.
    /// </summary>
    Triangle
}

/// <summary>
/// Defines a 1D spline with linear interpolation between control points.
/// Used primarily for mapping continentalness values to terrain elevation.
/// Domain is normalized [0,1], range can be any float values (typically elevation in blocks).
/// </summary>
public sealed class Spline1D
{
    /// <summary>
    /// Represents a single control point on the spline.
    /// </summary>
    public struct Point
    {
        /// <summary>
        /// Gets or sets the X coordinate (domain) of this control point. Range: [0,1].
        /// </summary>
        public float X { get; set; }
        
        /// <summary>
        /// Gets or sets the Y coordinate (range) of this control point.
        /// For height splines, this is elevation in blocks (can be negative for ocean floor).
        /// </summary>
        public float Y { get; set; }
        
        /// <summary>
        /// Initializes a new control point at origin (0,0).
        /// </summary>
        public Point()
        {
            X = 0f;
            Y = 0f;
        }
        
        /// <summary>
        /// Initializes a new control point at the specified coordinates.
        /// </summary>
        public Point(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Gets or sets the list of control points defining this spline.
    /// Points should be sorted by X coordinate for proper interpolation.
    /// </summary>
    public List<Point> Points { get; set; } = [];

    /// <summary>
    /// Removes all control points from this spline.
    /// </summary>
    public void Clear() => Points.Clear();

    /// <summary>
    /// Adds a new control point to this spline.
    /// Call <see cref="Sort"/> after adding all points to ensure proper interpolation.
    /// </summary>
    public void Add(float x, float y) => Points.Add(new Point(x, y));

    /// <summary>
    /// Sorts all control points by X coordinate in ascending order.
    /// Required for proper linear interpolation. Call after adding all points.
    /// </summary>
    public void Sort()
    {
        Points.Sort((a, b) => a.X.CompareTo(b.X));
    }

    /// <summary>
    /// Evaluates the spline at the given X coordinate using linear interpolation.
    /// </summary>
    /// <param name="x">X coordinate to evaluate. Clamped to [Points[0].X, Points[^1].X].</param>
    /// <returns>Interpolated Y value at the specified X coordinate.</returns>
    public float Evaluate(float x)
    {
        if (Points.Count == 0) return 0f;
        if (Points.Count == 1) return Points[0].Y;

        if (x <= Points[0].X) return Points[0].Y;
        if (x >= Points[^1].X) return Points[^1].Y;

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
    /// Bakes the spline to a uniform array of samples across [0,1] domain.
    /// Used for GPU texture upload where random access evaluation is expensive.
    /// </summary>
    /// <param name="samples">Number of samples to generate. Higher values = better interpolation quality.</param>
    /// <returns>Float array containing uniformly spaced Y values indexed by normalized X.</returns>
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

    /// <summary>
    /// Creates the default height spline for ocean-to-mountain elevation mapping.
    /// Maps continentalness [0,1] to elevation in blocks:
    /// - 0.00-0.30: Ocean basin
    /// - 0.35-0.45: Coastal transition
    /// - 0.45-0.65: Coastal lowlands and plains
    /// - 0.65-0.80: Highlands
    /// - 0.80-1.00: Mountain regions
    /// Note: Spline values are relative. GPU adds WATER_LEVEL (35) for absolute Y coordinates.
    /// </summary>
    /// <returns>Configured height spline with terrain transitions.</returns>
    public static Spline1D DefaultHeightSpline()
    {
        var s = new Spline1D();
        
        // Height values are relative to WaterLevel (35).
        // Valid absolute Y range: 0-383. So relative range: -35 to +348.
        // Ocean threshold is 0.40, so coast transition should be at ~0.40
        
        // Deep ocean - moderate trenches
        s.Add(0.00f, -30f);   // Deep ocean floor (Y=5)
        s.Add(0.10f, -27f);   // Ocean basin (Y=8)
        s.Add(0.22f, -18f);   // Mid ocean (Y=17)
        
        // Coastal transition - at ocean threshold 0.40
        s.Add(0.32f, -8f);    // Shallow ocean shelf (Y=27)
        s.Add(0.38f, -1f);    // Approaching shore (Y=34)
        s.Add(0.40f, 1f);     // Beach level (Y=36) - AT OCEAN THRESHOLD
        s.Add(0.44f, 10f);    // Coastal plain (Y=45)
        
        // Coastal lowlands to rolling terrain
        s.Add(0.50f, 20f);    // Low plains (Y=55)
        s.Add(0.58f, 35f);    // Plains/hills (Y=70)
        
        // Hill/highland transition
        s.Add(0.66f, 52f);    // Rolling hills (Y=87)
        s.Add(0.74f, 75f);    // High hills (Y=110)
        s.Add(0.80f, 100f);   // Highlands base (Y=135)
        
        // Mountain range
        s.Add(0.86f, 140f);   // Mountain foothills (Y=175)
        s.Add(0.92f, 190f);   // Lower mountains (Y=225)
        s.Add(0.96f, 250f);   // High mountains (Y=285)
        s.Add(1.00f, 310f);   // Peaks (Y=345)
        
        s.Sort();
        return s;
    }
}

/// <summary>
/// Configuration for a single layer of noise (FBM).
/// Encapsulates all parameters needed to sample noise for a specific climate factor.
/// </summary>
public sealed class NoiseLayer
{
    /// <summary>
    /// Base frequency of the noise (inverse of wavelength).
    /// </summary>
    public float BaseScale { get; set; }
    
    /// <summary>
    /// Number of octaves of noise to sum.
    /// </summary>
    public int Octaves { get; set; }
    
    /// <summary>
    /// Persistence (amplitude multiplier per octave).
    /// </summary>
    public float Persistence { get; set; }
    
    /// <summary>
    /// Lacunarity (frequency multiplier per octave).
    /// </summary>
    public float Lacunarity { get; set; }
    
    /// <summary>
    /// Scale of domain warp applied before sampling this noise.
    /// </summary>
    public float DomainWarpScale { get; set; }
    
    /// <summary>
    /// Strength of domain warp applied before sampling this noise.
    /// </summary>
    public float DomainWarpStrength { get; set; }
    
    /// <summary>
    /// Whether to use ridged noise (1 - |noise|) instead of standard noise.
    /// </summary>
    public bool UseRidged { get; set; }
    
    /// <summary>
    /// Sharpness of ridges if UseRidged is true.
    /// </summary>
    public float RidgeSharpness { get; set; } = 1.0f;
}

/// <summary>
/// Defines a rule for placing vegetation in a biome.
/// </summary>
public class VegetationRule
{
    /// <summary>The type of vegetation to place (Tree, Flower, etc.).</summary>
    public VegetationType Type { get; set; }
    
    /// <summary>Probability per column (0.0 - 1.0) that this vegetation will attempt to spawn.</summary>
    public float Density { get; set; }
    
    /// <summary>Frequency for the noise used to cluster vegetation (0.1 = large patches).</summary>
    public float NoiseFrequency { get; set; } = 0.1f;
    
    /// <summary>List of blocks this vegetation can grow on (e.g., Grass, Sand).</summary>
    public BlockId[] AllowedSurfaceBlocks { get; set; } = [];
}

/// <summary>
/// Types of vegetation generators available.
/// </summary>
public enum VegetationType
{
    Grass,
    Flower,
    TreeOak,    // Uses "Balloon" shape algorithm
    TreeBirch,  // Uses "Balloon" shape algorithm (different texture)
    TreeSpruce, // Uses "Cone" shape algorithm
    TreeJungle, // Uses "Tall/Mega" shape algorithm
    Cactus,
    DeadBush,
    SugarCane,
    BlueOrchid // Specific flower type for swamps
}

