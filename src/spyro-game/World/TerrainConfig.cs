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
    
    /// <summary>
    /// Gets or sets the continentalness noise frequency (inverse of wavelength in blocks).
    /// Controls the size of oceans and continents. Lower values create larger land masses.
    /// - 1/800 (default): Very large continents and ocean basins (800-block features)
    /// - 1/1200: Massive continental scale (Earth-like)
    /// - 1/400: Smaller, more fragmented continents
    /// Technical: This is the primary driver for ocean/land distribution via the height spline.
    /// </summary>
    public float ContinentalnessScale { get; set; } = 1f / 800f;
    
    /// <summary>
    /// Gets or sets the erosion noise frequency (inverse of wavelength in blocks).
    /// Controls the scale of terrain roughness and flat areas.
    /// - 1/300 (default): Large erosion patterns (300-block smooth/rough zones)
    /// - 1/500: Very gradual terrain character changes
    /// - 1/150: More chaotic, rapidly changing terrain roughness
    /// Technical: Modulates peaks/valleys amplitude. High erosion = flatter terrain, low = more dramatic relief.
    /// </summary>
    public float ErosionScale { get; set; } = 1f / 300f;
    
    /// <summary>
    /// Gets or sets the peaks/valleys ridge noise frequency (inverse of wavelength in blocks).
    /// Controls the spacing of mountain ridges and valley systems.
    /// - 1/120 (default): Wide ridges and valleys (120-block spacing)
    /// - 1/200: Broad, gentle mountain systems
    /// - 1/60: Tight, jagged ridge patterns
    /// Technical: Uses ridged noise (abs of FBM) to create sharp peaks and linear valleys.
    /// </summary>
    public float RidgeScale { get; set; } = 1f / 120f;

    // Domain warp
    
    /// <summary>
    /// Gets or sets the domain warp noise frequency (inverse of wavelength in blocks).
    /// Controls the scale of terrain flow distortion applied before continentalness sampling.
    /// - 1/150 (default): Large-scale terrain flow (150-block warp patterns)
    /// - 1/250: Very gentle, continental-scale flow
    /// - 1/80: Aggressive, swirling terrain patterns
    /// Technical: Applies 2D FBM offset to input coordinates, creating natural curved features.
    /// </summary>
    public float WarpScale { get; set; } = 1f / 150f;
    
    /// <summary>
    /// Gets or sets the domain warp displacement strength in blocks.
    /// Controls how far terrain features are displaced by the warp effect.
    /// - 60 (default): Moderate displacement (60-block maximum shift)
    /// - 100: Strong displacement, highly organic flowing terrain
    /// - 30: Subtle displacement, more regular terrain grid
    /// Technical: Multiplies the warp noise output to determine actual coordinate offset.
    /// </summary>
    public float WarpStrength { get; set; } = 60f;

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
    
    /// <summary>
    /// Gets or sets the climate noise frequency (inverse of wavelength in blocks).
    /// Controls the size of temperature and humidity variation zones.
    /// - 1/15000 (default): Very large climate zones (15km weather systems)
    /// - 1/25000: Continental-scale climate patterns
    /// - 1/8000: Smaller, more varied climate zones
    /// Technical: Applied to both temperature and humidity as FBM noise with ±0.2 range.
    /// </summary>
    public float ClimateScale { get; set; } = 1f / 15000f;
    
    /// <summary>
    /// Gets or sets the climate warp frequency (inverse of wavelength in blocks).
    /// Controls the scale of flow patterns in climate distribution.
    /// - 1/25000 (default): Very large climate flow patterns (25km distortions)
    /// - 1/40000: Continental-scale climate swirls
    /// - 1/12000: Smaller, more chaotic climate boundaries
    /// Technical: Domain warp applied before climate noise sampling for organic boundaries.
    /// </summary>
    public float ClimateWarp { get; set; } = 1f / 25000f;

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
    
    /// <summary>
    /// Vertical range below terrain surface where overhang effects are applied (in blocks).
    /// Example: 50 means overhangs can form up to 50 blocks below the surface.
    /// - Lower values (30): Shallower overhangs, limited to near-surface
    /// - Higher values (70): Deeper overhangs, more dramatic caves
    /// </summary>
    public float OverhangDepthRange { get; set; } = 50f;
    
    /// <summary>
    /// Vertical range above terrain surface where overhang effects extend (in blocks).
    /// Example: 30 means overhangs can extend up to 30 blocks above the base surface.
    /// - Lower values (20): Smaller overhangs
    /// - Higher values (50): Larger, more dramatic floating islands
    /// </summary>
    public float OverhangHeightRange { get; set; } = 30f;
    
    /// <summary>
    /// Height factor denominator for overhang strength falloff.
    /// Example: 40 means overhang effect fades over 40-block vertical range.
    /// - Lower values (25): Sharper falloff, more concentrated overhangs
    /// - Higher values (60): Gentler falloff, more gradual overhang transitions
    /// </summary>
    public float OverhangFalloffRange { get; set; } = 40f;

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
        public float ContinentalScale; public float ErosionScale; public float RidgeScale;
        public float WarpScale; public float WarpStrength;
        public float BaseTemperature; public float TemperatureLapseRate; public float BaseHumidity; public float CoastDrying;
        public float ClimateScale; public float ClimateWarp;
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

    public TerrainGenerationParams GetGenerationParams()
    {
        return new TerrainGenerationParams
        {
            Seed = (uint)this.Seed,
            WorldScale = this.WorldScale,
            MacroScale = 1.0f,
            ContinentalScale = this.ContinentalnessScale,
            ErosionScale = this.ErosionScale,
            RidgeScale = this.RidgeScale,
            WarpScale = this.WarpScale,
            WarpStrength = this.WarpStrength,
            BaseTemperature = this.BaseTemperature,
            TemperatureLapseRate = this.LapseRate,
            BaseHumidity = this.BaseHumidity,
            CoastDrying = this.CoastDrying,
            ClimateScale = this.ClimateScale,
            ClimateWarp = this.ClimateWarp,
            RegionCellSize = this.BiomeRegions.CellSizeChunks,
            RegionJitter = this.BiomeRegions.JitterStrength,
            RegionFeatherWidth = this.BiomeRegions.FeatherWidth,
            MaxRegionMix = (uint)this.BiomeRegions.MaxRegionMix,
            CheeseFrequency = this.Caves.CheeseFrequency,
            CheeseAmplitude = this.Caves.CheeseAmplitude,
            SpaghettiFrequency = this.Caves.SpaghettiFrequency,
            SpaghettiAmplitude = this.Caves.SpaghettiAmplitude,
            CaveCarveThreshold = this.Caves.CarveThreshold,
            CurlScale = this.Caves.CurlScale,
            CurlStrength = this.Caves.CurlStrength,
            CoastThreshold = this.CoastThreshold,
            MountainThreshold = this.MountainThreshold,
            CliffFrequency = this.CliffFrequency,
            CliffAmplitude = this.CliffAmplitude,
            OverhangFrequency = this.OverhangFrequency,
            OverhangAmplitude = this.OverhangAmplitude,
            ShorelineRange = this.ShorelineRange,
            SubsurfaceDepth = this.SubsurfaceDepth,
            CaveDepthFade = this.CaveDepthFade,
            CaveSlopeFadeMin = this.CaveSlopeFade.Min,
            CaveSlopeFadeMax = this.CaveSlopeFade.Max,
            CaveFloodExtension = this.CaveFloodingExtension,
            OceanThreshold = this.OceanThreshold,
            DeepOceanThreshold = this.DeepOceanThreshold,
            AlpineElevation = this.AlpineElevation,
            CoastRange = this.CoastRange,
            OverhangDepthRange = this.OverhangDepthRange,
            OverhangHeightRange = this.OverhangHeightRange,
            OverhangFalloffRange = this.OverhangFalloffRange,
        };
    }
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
    public const int OCEAN_BIOME_ID = 0;
    
    /// <summary>
    /// Hardcoded biome ID for Alpine. Must match shader constant ALPINE_BIOME_ID.
    /// </summary>
    public const int ALPINE_BIOME_ID = 9;
    
    /// <summary>
    /// Default fallback biome ID used when LUT sampling fails. Must match shader constant DEFAULT_FALLBACK_BIOME_ID.
    /// </summary>
    public const int DEFAULT_FALLBACK_BIOME_ID = 2;
    
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

    /// <summary>
    /// Gets or sets the texture paths indexed by GeologyLayer/BlockDescriptor enum values.
    /// Each index corresponds to a terrain layer:
    /// 0 = Air (unused)
    /// 1 = Water
    /// 2 = Surface (grass, sand, snow at terrain surface)
    /// 3 = Subsurface (dirt below surface)
    /// 4 = Deep Subsurface (rock/stone)
    /// 5 = Underwater Surface (ocean floor)
    /// 6 = Underwater Subsurface (deep ocean bedrock)
    /// 7 = Shoreline (beach sand)
    /// </summary>
    public List<string> TexturePaths { get; set; } = [];

    /// <summary>
    /// Initializes a new instance of <see cref="BiomeDefinition"/> with default values.
    /// </summary>
    public BiomeDefinition() { }

    /// <summary>
    /// Initializes a new instance of <see cref="BiomeDefinition"/> with specified parameters.
    /// </summary>
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
            new (OCEAN_BIOME_ID, "Ocean", 
                new(0.0f, 1.0f), new(0.0f, 1.0f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 100, 
                terrainType: TerrainType.OceanOnly),
            
            new (ALPINE_BIOME_ID, "Alpine", 
                new(0.0f, 1.0f), new(0.0f, 1.0f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/Alpine/snow.png", "Resources/voxel/Alpine/snow-dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 90, 
                terrainType: TerrainType.MountainOnly,
                minElevation: 200f),
            
            new (6, "Taiga", 
                new(0.25f, 0.5f), new(0.5f, 1.0f), 
                ["", "Resources/voxel/water.png", "Resources/voxel/Taiga/grass-dirt.png", "Resources/voxel/Taiga/dirt.png", "Resources/voxel/rock.png", "Resources/voxel/bedrock.png", "Resources/voxel/bedrock.png", "Resources/voxel/sand.png"],
                priority: 50, 
                terrainType: TerrainType.LandOnly),
            
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
    /// - 1/100 (default): Large chambers (~100 block spacing)
    /// - 1/150: Very large caverns
    /// - 1/50: Smaller, more frequent chambers
    /// Technical: Applied as fbm3D(position * CheeseFrequency).
    /// </summary>
    public float CheeseFrequency { get; set; } = 1f / 140f;
    
    /// <summary>
    /// Gets or sets the amplitude multiplier for cheese cave density.
    /// Higher values emphasize large hollow pockets, lower values keep caverns tighter.
    /// </summary>
    public float CheeseAmplitude { get; set; } = 1.1f;

    /// <summary>
    /// Gets or sets the frequency of spaghetti cave noise (inverse of feature size).
    /// Controls the size and spacing of tunnel systems.
    /// - 1/80 (default): Long winding tunnels (~80 block wavelength)
    /// - 1/120: Very long tunnels
    /// - 1/40: Shorter, tighter tunnels
    /// Technical: Uses two perpendicular noise fields to create worm-like structures.
    /// </summary>
    public float SpaghettiFrequency { get; set; } = 1f / 110f;
    
    /// <summary>
    /// Gets or sets the amplitude multiplier for spaghetti cave density.
    /// Higher values widen tunnels, lower values keep them tight and winding.
    /// </summary>
    public float SpaghettiAmplitude { get; set; } = 1.2f;

    /// <summary>
    /// Gets or sets the density threshold for cave carving [0,1].
    /// Higher values create fewer, smaller caves; lower values create more, larger caves.
    /// - 0.86 (default): Moderate cave frequency (balanced exploration)
    /// - 0.75: Higher cave frequency (more caves, easier underground navigation)
    /// - 0.92: Lower cave frequency (rare caves, more solid underground)
    /// Technical: If caveDensity * attenuation > CarveThreshold, carve air block.
    /// </summary>
    public float CarveThreshold { get; set; } = 0.92f;

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
    /// Creates a CaveParams instance with default values.
    /// </summary>
    public static CaveParams Default() => new();
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
    /// - 0.00-0.25: Ocean basin (-80 to -20 blocks)
    /// - 0.32-0.38: Steep coastal cliffs (-5 to +35 blocks)
    /// - 0.50-0.65: Coastal plains and inland hills (+50 to +80 blocks)
    /// - 0.75-1.00: Mountain regions (+120 to +320 blocks)
    /// Note: Spline values are relative. GPU adds WATER_LEVEL (35) for absolute Y coordinates.
    /// </summary>
    /// <returns>Configured height spline with 11 control points.</returns>
    public static Spline1D DefaultHeightSpline()
    {
        var s = new Spline1D();
        
        s.Add(0.00f, -80f);
        s.Add(0.15f, -50f);
        s.Add(0.25f, -20f);
        s.Add(0.32f, -5f);
        s.Add(0.38f, 35f);
        s.Add(0.50f, 50f);
        s.Add(0.65f, 80f);
        s.Add(0.75f, 120f);
        s.Add(0.85f, 180f);
        s.Add(0.95f, 250f);
        s.Add(1.00f, 320f);
        
        s.Sort();
        return s;
    }
}
