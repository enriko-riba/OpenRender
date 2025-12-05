namespace SpyroGame.World;

/// <summary>
/// Configuration for terrain height shaping using Minecraft-style spline-based calculation.
/// All parameters are documented with units and valid ranges.
/// 
/// Phase 2 of Minecraft terrain pipeline: Replaces magic numbers and if-else chains
/// with configurable splines for continuous height calculation.
/// </summary>
public sealed class TerrainShapingConfig
{
    // === COASTAL ZONE PARAMETERS ===
    
    /// <summary>
    /// Width of the coastal transition zone as a fraction of inland distance [0, 1].
    /// At coastDist &lt; CoastalZoneWidth, beach/cliff blending is applied.
    /// - 0.2 (default): 20% of inland distance is coastal zone
    /// - 0.1: Narrow coastal zone, abrupt beach-to-land transition
    /// - 0.3: Wide coastal zone, gradual beach-to-land transition
    /// </summary>
    public float CoastalZoneWidth { get; set; } = 0.2f;
    
    /// <summary>
    /// Minimum cliffiness value where cliff behavior begins to appear [−1, 1].
    /// Below this, terrain is beach-like (flattening toward water).
    /// - 0.1 (default): Cliffs appear when cliffiness > 0.1
    /// - -0.2: Even slight negative cliffiness shows some cliff features
    /// - 0.3: Only strong cliffiness values produce cliffs
    /// </summary>
    public float CoastalCliffStart { get; set; } = 0.1f;
    
    /// <summary>
    /// Cliffiness value where cliff behavior is fully dominant [−1, 1].
    /// Above this, terrain is full cliff height.
    /// - 0.5 (default): Full cliff at cliffiness = 0.5
    /// - 0.3: Reaches full cliff earlier (more cliffs)
    /// - 0.8: Requires high cliffiness for full cliff effect
    /// </summary>
    public float CoastalCliffEnd { get; set; } = 0.5f;
    
    /// <summary>
    /// Height offset above water level for beach surfaces in blocks.
    /// - 2 (default): Beach is 2 blocks above water
    /// - 1: Beach barely above water (more flooding)
    /// - 4: Higher beaches, more sand area
    /// </summary>
    public float BeachHeightOffset { get; set; } = 2f;
    
    /// <summary>
    /// Additional blend factor for beach height interpolation [0, 1].
    /// Higher values make beach height closer to base height even at shore.
    /// - 0.2 (default): Moderate flattening at shore
    /// - 0.0: Strong flattening, very flat beaches
    /// - 0.4: Less flattening, beaches follow terrain more
    /// </summary>
    public float BeachBlendBias { get; set; } = 0.2f;
    
    /// <summary>
    /// Maximum height boost for coastal cliffs in blocks.
    /// Multiplied by cliffiness and inverse coast factor.
    /// - 10 (default): Up to 10 blocks of cliff height at coast
    /// - 5: Subtle coastal cliffs
    /// - 20: Dramatic coastal cliff faces
    /// </summary>
    public float CoastalCliffAmplitude { get; set; } = 10f;
    
    // === TERRAIN TYPE THRESHOLDS ===
    
    /// <summary>
    /// TerrainType threshold below which terrain is flat plains [0, 1].
    /// - 0.35 (default): Bottom 35% is flat plains
    /// - 0.25: Less flat terrain, more hills
    /// - 0.45: More flat terrain, fewer dramatic features
    /// </summary>
    public float FlatPlainsThreshold { get; set; } = 0.35f;
    
    /// <summary>
    /// TerrainType threshold below which terrain is rolling hills [0, 1].
    /// Must be greater than FlatPlainsThreshold.
    /// - 0.65 (default): 35-65% is rolling hills
    /// - 0.55: Narrower hills band, more dramatic terrain
    /// - 0.75: Wider hills band, less dramatic terrain
    /// </summary>
    public float RollingHillsThreshold { get; set; } = 0.65f;
    
    // === PEAKS/VALLEYS AMPLITUDES ===
    
    /// <summary>
    /// Peaks contribution in flat plains biomes in blocks.
    /// - 5 (default): Subtle 5-block variations
    /// - 2: Very flat plains
    /// - 10: More hilly "plains"
    /// </summary>
    public float PeakAmplitudePlains { get; set; } = 5f;
    
    /// <summary>
    /// Peaks contribution in rolling hills biomes in blocks.
    /// - 25 (default): Moderate 25-block hills
    /// - 15: Gentler hills
    /// - 40: More dramatic hills
    /// </summary>
    public float PeakAmplitudeHills { get; set; } = 25f;
    
    /// <summary>
    /// Peaks contribution in dramatic terrain biomes in blocks.
    /// - 50 (default): Strong 50-block peaks
    /// - 30: Less dramatic peaks
    /// - 80: Very dramatic mountainous peaks
    /// </summary>
    public float PeakAmplitudeDramatic { get; set; } = 50f;
    
    // === CLIFF PARAMETERS ===
    
    /// <summary>
    /// Base cliff contribution multiplier in dramatic terrain.
    /// - 5 (default): 5× cliff noise amplitude
    /// - 3: Subtle cliff contributions
    /// - 10: Strong cliff variations
    /// </summary>
    public float CliffBaseMultiplier { get; set; } = 5f;
    
    /// <summary>
    /// Cliffiness threshold where sharp cliffs begin [−1, 1].
    /// - 0.75 (default): Sharp cliffs at cliffiness > 0.75
    /// - 0.6: More sharp cliffs
    /// - 0.85: Rare sharp cliffs
    /// </summary>
    public float SharpCliffThreshold { get; set; } = 0.75f;
    
    /// <summary>
    /// Cliffiness threshold where plateaus begin (negative values) [−1, 0].
    /// - -0.2 (default): Plateaus at cliffiness < -0.2
    /// - -0.1: More plateaus
    /// - -0.4: Rare plateaus
    /// </summary>
    public float PlateauThreshold { get; set; } = -0.2f;
    
    /// <summary>
    /// Height boost for plateau formations in blocks.
    /// - 15 (default): 15-block plateau elevation
    /// - 8: Lower plateaus
    /// - 25: Higher plateaus
    /// </summary>
    public float PlateauHeightBoost { get; set; } = 15f;
    
    /// <summary>
    /// Plateau height variation amplitude.
    /// - 60 (default): Up to 60-block variation
    /// - 30: Flatter plateau tops
    /// - 100: More varied plateau surfaces
    /// </summary>
    public float PlateauVariation { get; set; } = 60f;
    
    // === MOUNTAIN PARAMETERS ===
    
    /// <summary>
    /// Upper continentalness for mountain smoothstep transition [0, 1].
    /// Mountains transition from MountainThreshold to this value.
    /// - 0.92 (default): Mountains fully developed at C=0.92
    /// - 0.85: Faster mountain ramp-up
    /// - 0.98: Gradual mountain development
    /// </summary>
    public float MountainFullThreshold { get; set; } = 0.92f;
    
    /// <summary>
    /// Base height boost for mountain zones in blocks.
    /// - 80 (default): Mountains rise 80 blocks above baseline
    /// - 50: Lower mountains
    /// - 120: Very high mountains
    /// </summary>
    public float MountainHeightBoost { get; set; } = 80f;
    
    /// <summary>
    /// Additional peak amplitude in alpine mountain zones.
    /// - 60 (default): Extra 60-block peaks in mountains
    /// - 30: Gentler mountain tops
    /// - 100: Very jagged mountain peaks
    /// </summary>
    public float AlpinePeakAmplitude { get; set; } = 60f;
    
    /// <summary>
    /// TerrainType threshold where flatness scaling begins in mountains [0, 1].
    /// Below this, mountains have reduced cliff detail.
    /// - 0.4 (default): Flat areas < 0.4 have reduced cliffs
    /// - 0.3: More mountains get full cliff detail
    /// - 0.5: More mountains have reduced cliffs
    /// </summary>
    public float MountainFlatnessStart { get; set; } = 0.2f;
    
    /// <summary>
    /// TerrainType threshold where mountains have full cliff detail [0, 1].
    /// - 0.4 (default): Full detail at terrainType >= 0.4
    /// - 0.3: Earlier full detail
    /// - 0.5: Later full detail
    /// </summary>
    public float MountainFlatnessEnd { get; set; } = 0.4f;
    
    /// <summary>
    /// TerrainType threshold above which alpine peaks are added [0, 1].
    /// - 0.5 (default): Extra peaks when terrainType > 0.5
    /// - 0.4: More alpine peaks
    /// - 0.7: Fewer alpine peaks
    /// </summary>
    public float AlpinePeakTerrainThreshold { get; set; } = 0.5f;
    
    // === EROSION SMOOTHING ===
    
    /// <summary>
    /// Maximum smoothing factor from erosion (squared erosion × this value).
    /// - 0.3 (default): Up to 30% blend toward smooth baseline
    /// - 0.15: Less erosion smoothing, rougher terrain
    /// - 0.5: More erosion smoothing, smoother terrain
    /// </summary>
    public float ErosionSmoothingMax { get; set; } = 0.3f;
    
    /// <summary>
    /// Height offset for erosion smoothing target in blocks.
    /// Smoothing blends toward HeightSpline(C) + WaterLevel + this value.
    /// - 20 (default): Smooth toward baseline + 20 blocks
    /// - 10: Smooth toward lower elevation
    /// - 30: Smooth toward higher elevation
    /// </summary>
    public float ErosionSmoothingHeightOffset { get; set; } = 20f;
    
    // === TERRAIN DETAIL NOISE SCALES (for additional noise not in climate cache) ===
    
    /// <summary>
    /// Scale for terrain type noise (controls flat vs mountainous).
    /// - 1/400 (default): ~400 block features
    /// - 1/600: Larger terrain type regions
    /// - 1/200: Smaller, more varied regions
    /// </summary>
    public float TerrainTypeNoiseScale { get; set; } = 1f / 400f;
    
    /// <summary>
    /// Scale for cliffiness noise (controls cliff steepness).
    /// - 1/150 (default): ~150 block cliff features
    /// - 1/250: Larger cliff patterns
    /// - 1/80: Smaller, more jagged cliffs
    /// </summary>
    public float CliffNoiseScale { get; set; } = 1f / 150f;
    
    // === 3D TERRAIN FACTOR (Phase 4: Weirdness → 3D Features) ===
    
    /// <summary>
    /// Weirdness threshold below which terrain is purely 2D heightmap.
    /// Weirdness values closer to 0 result in "normal" terrain.
    /// - 0.3 (default): |weirdness| &lt; 0.3 = pure 2D terrain
    /// - 0.2: Less terrain uses 3D features
    /// - 0.4: More terrain uses 3D features (more overhangs)
    /// </summary>
    public float Weirdness3DThresholdLow { get; set; } = 0.3f;
    
    /// <summary>
    /// Weirdness threshold above which terrain has maximum 3D features.
    /// - 0.7 (default): |weirdness| > 0.7 = full 3D features
    /// - 0.6: Reach full 3D earlier (more dramatic terrain)
    /// - 0.9: Only very weird terrain gets full 3D
    /// </summary>
    public float Weirdness3DThresholdHigh { get; set; } = 0.7f;
    
    /// <summary>
    /// Erosion threshold below which 3D features are allowed.
    /// Low erosion + high weirdness = dramatic overhangs.
    /// - 0.6 (default): 3D features where erosion &lt; 0.6
    /// - 0.4: 3D only in very rough terrain
    /// - 0.8: 3D features in most terrain types
    /// </summary>
    public float Erosion3DThreshold { get; set; } = 0.6f;
    
    /// <summary>
    /// Minimum 3D factor multiplier [0, 1].
    /// Even "normal" terrain gets at least this much 3D effect.
    /// - 0.1 (default): Always at least 10% overhang strength
    /// - 0.0: Completely flat in normal areas
    /// - 0.3: Always some 3D variety
    /// </summary>
    public float Min3DFactor { get; set; } = 0.1f;
    
    /// <summary>
    /// Maximum 3D factor multiplier [0, 1].
    /// Caps 3D effect even in extreme weirdness.
    /// - 1.0 (default): Full overhang amplitude possible
    /// - 0.7: Limit extreme 3D features
    /// - 1.2: Allow extra-dramatic overhangs
    /// </summary>
    public float Max3DFactor { get; set; } = 1.0f;

    // === PHASE 2: MAGIC NUMBER REPLACEMENTS ===

    /// <summary>
    /// Amplitude multiplier for weirdness-based terrain variation.
    /// Replaces hardcoded 70f.
    /// </summary>
    public float WeirdnessAmplitude { get; set; } = 70f;

    /// <summary>
    /// Base influence of weirdness on terrain height.
    /// Replaces hardcoded 0.3f in (0.3f + roughness * 0.7f).
    /// </summary>
    public float WeirdnessInfluenceBase { get; set; } = 0.3f;

    /// <summary>
    /// Roughness influence of weirdness on terrain height.
    /// Replaces hardcoded 0.7f in (0.3f + roughness * 0.7f).
    /// </summary>
    public float WeirdnessInfluenceRoughness { get; set; } = 0.7f;

    /// <summary>
    /// Threshold for extreme weirdness boost.
    /// Replaces hardcoded 0.4f.
    /// </summary>
    public float ExtremeWeirdnessThreshold { get; set; } = 0.4f;

    /// <summary>
    /// Height boost for extreme weirdness.
    /// Replaces hardcoded 60f.
    /// </summary>
    public float ExtremeWeirdnessBoost { get; set; } = 60f;

    /// <summary>
    /// Continentalness threshold where mountains begin.
    /// Replaces hardcoded 0.50f.
    /// </summary>
    public float MountainStartThreshold { get; set; } = 0.50f;

    /// <summary>
    /// Multiplier for mountain height boost.
    /// Replaces hardcoded 1.3f.
    /// </summary>
    public float MountainHeightBoostMultiplier { get; set; } = 1.3f;

    /// <summary>
    /// Multiplier for cliff amplitude.
    /// Replaces hardcoded 1.5f.
    /// </summary>
    public float CliffAmplitudeMultiplier { get; set; } = 1.5f;

    /// <summary>
    /// Multiplier for valley depth carving.
    /// Replaces hardcoded 60f.
    /// </summary>
    public float ValleyDepthMultiplier { get; set; } = 60f;

    /// <summary>
    /// Multiplier for ridge peak boost.
    /// Replaces hardcoded 80f.
    /// </summary>
    public float RidgeBoostMultiplier { get; set; } = 80f;

    /// <summary>
    /// Multiplier for plains amplitude.
    /// Replaces hardcoded 2f.
    /// </summary>
    public float PlainsAmplitudeMultiplier { get; set; } = 2f;

    /// <summary>
    /// Erosion threshold for smoothing.
    /// Replaces hardcoded 0.7f.
    /// </summary>
    public float ErosionSmoothingThreshold { get; set; } = 0.7f;

    /// <summary>
    /// Factor for erosion smoothing strength.
    /// Replaces hardcoded 0.15f.
    /// </summary>
    public float ErosionSmoothingFactor { get; set; } = 0.15f;

    /// <summary>
    /// Amplitude for ocean floor variation.
    /// Replaces hardcoded 25f.
    /// </summary>
    public float OceanVariationAmplitude { get; set; } = 25f;

    /// <summary>
    /// Multiplier for overhang amplitude.
    /// Replaces hardcoded 2.0f.
    /// </summary>
    public float OverhangMultiplier { get; set; } = 2.0f;

    /// <summary>
    /// Continentalness threshold where overhangs begin.
    /// Replaces hardcoded 0.40f.
    /// </summary>
    public float OverhangStartThreshold { get; set; } = 0.40f;

    /// <summary>
    /// Continentalness threshold where overhangs are fully effective.
    /// Replaces hardcoded 0.75f.
    /// </summary>
    public float OverhangFullThreshold { get; set; } = 0.75f;
    
    /// <summary>
    /// Creates default terrain shaping configuration.
    /// </summary>
    public static TerrainShapingConfig Default() => new();
}
