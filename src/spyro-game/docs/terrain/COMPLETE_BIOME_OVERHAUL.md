# Complete Biome System Overhaul

## Critical Issues Identified

### 1. Ocean at Y=90 Inland
**Root Cause**: Fragment shader uses simplified height approximation (`texture(uHeightSpline, tC).r + WATER_LEVEL`) which doesn't match actual terrain generation that adds erosion, peaks, and cliffs.

**Example**:
```
Actual terrain: height = spline[0.6] + erosion + peaks + cliffs = 90
Fragment approx: height = spline[0.6] = 55

Fragment thinks terrain at 90 is ABOVE the surface (90 > 55) → AIR
But we're rendering solid blocks, so this is wrong!
```

**The Real Problem**: We can't use terrain height to determine biomes in fragments because we don't know which Y we're rendering!

**Solution**: Biomes must be based ONLY on XZ position, not Y position!

### 2. Water Maze (Chunk-Dependent Walls)
**Root Cause**: Procedural fallback at chunk borders returns slightly different values than actual generation, causing asymmetric water face culling.

### 3. Straight Biome Borders
**Root Cause**: Only Alpine uses boundary noise. Other biomes use raw LUT lookup without any smoothing.

### 4. Alpine Height Restriction
**Current**: Both altitude and temperature triggers use same smoothstep (170-230)
**Needed**: 
- Altitude-based: >= 250
- Temperature-based: No height restriction

## The Fundamental Fix

**Key Insight**: Biomes should be determined by **XZ location** only, not Y position!

**Current Broken Logic**:
```glsl
// Fragment uses p.y to determine if ocean
terrainHeight = getHeightApprox(p.xz)
if (terrainHeight < WATER_LEVEL) return OCEAN

// Problem: Fragment at Y=90 doesn't know if it's above or below surface!
```

**Correct Logic**:
```glsl
// Biome is property of XZ column, not individual Y position
biomeId = getBiomeIdForColumn(p.xz)

// Ocean determination:
// - Compute shader: Check actual generated height at XZ
// - Fragment shader: CANNOT use Y position, must use continentalness approximation
```

## New Biome Architecture

### Biome Categories

1. **Geologic-Based** (XZ + actual terrain height):
   - Ocean: Terrain surface < WATER_LEVEL
   - Beach: 0-3 blocks above water in coastal areas

2. **Climate-Based** (XZ only - T/H from continentalness):
   - Plains, Forest, Desert, Savanna, Rainforest, Taiga, Tundra, Highlands
   - **With boundary noise for ALL** (like Alpine currently has)

3. **Special: Alpine** (XZ + dual triggers):
   - Altitude trigger: Elevation >= 250 (from actual terrain height)
   - Cold trigger: Temperature < 0.15 (no height restriction)

### Implementation Strategy

**Compute Shader** (has actual height):
```glsl
uint getBiomeId_Compute(vec3 p) {
    float terrainHeight = getHeight(p.xz);  // Accurate
    
    // Ocean: Based on terrain surface
    if (terrainHeight <= WATER_LEVEL) return OCEAN;
    
    // Beach: Proximity-based
    if (isCoastal && terrainHeight - WATER_LEVEL <= 3.0) return BEACH;
    
    // Alpine: Dual triggers
    bool alpineFromAltitude = (terrainHeight >= 250.0);
    bool alpineFromCold = (getTemperature(p) < 0.15);
    if (alpineFromAltitude || alpineFromCold) {
        // Apply boundary noise
        if (applyBoundaryNoise(p) > 0.55) return ALPINE;
    }
    
    // Climate biomes with boundary noise
    return getClimateBasedBiome(p);  // Includes noise
}
```

**Fragment Shader** (no accurate height):
```glsl
uint getBiomeId_Fragment(vec3 p) {
    float C = getContinentalness(p.xz);
    float tC = C * 0.5 + 0.5;
    
    // Ocean: Use continentalness approximation
    // If continentalness is very low, it's probably ocean
    float heightApprox = texture(uHeightSpline, tC).r + WATER_LEVEL;
    if (heightApprox <= WATER_LEVEL + 10.0) {  // Add tolerance
        return OCEAN;
    }
    
    // Beach: Cannot determine precisely, skip or use coastal heuristic
    if (tC < 0.4 && heightApprox < WATER_LEVEL + 5.0) return BEACH;
    
    // Alpine: Use approximation
    bool alpineFromCold = (getTemperature(p) < 0.15);
    bool alpineFromAltitude = (heightApprox >= 250.0);  // Approximate
    if (alpineFromCold || alpineFromAltitude) {
        if (applyBoundaryNoise(p) > 0.55) return ALPINE;
    }
    
    // Climate biomes (same as compute)
    return getClimateBasedBiome(p);
}
```

## Detailed Fixes

### Fix 1: Unified Boundary Noise for ALL Climate Biomes

Currently only Alpine has this. Apply to all!

```glsl
uint getClimateBasedBiome(vec3 p) {
    float t = getTemperature(p);
    float h = getHumidity(p);
    
    // Add noise BEFORE LUT lookup to shift T/H slightly
    // This creates organic boundaries between climate zones
    float noiseT = fbm(p.xz * 0.02, params.uSeed + 4000u, 2, 0.5, 2.0) * 0.1;
    float noiseH = fbm(p.xz * 0.02, params.uSeed + 5000u, 2, 0.5, 2.0) * 0.1;
    
    t = clamp(t + noiseT, 0.0, 1.0);
    h = clamp(h + noiseH, 0.0, 1.0);
    
    uint biomeId = texture(uBiomeLUT, vec2(t, h)).r;
    
    // Skip special biomes from LUT
    if (biomeId == OCEAN_BIOME_ID || biomeId == 1u) {
        return DEFAULT_FALLBACK_BIOME_ID;
    }
    
    return biomeId;
}
```

### Fix 2: Split Alpine Triggers

```glsl
// Alpine detection with separate thresholds
bool alpineFromAltitude = false;
bool alpineFromCold = false;

// Altitude trigger: >= 250 with smoothstep
if (terrainHeight >= 220.0) {
    float altInfluence = smoothstep(220.0, 280.0, terrainHeight);
    float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0) * 0.5 + 0.5;
    float alpineFavor = altInfluence * 0.7 + boundaryNoise * 0.3;
    alpineFromAltitude = (alpineFavor > 0.55);
}

// Cold trigger: temp < 0.15, NO height restriction
float t = getTemperature(p);
if (t < 0.20) {
    float coldInfluence = smoothstep(0.20, 0.10, t);
    float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0) * 0.5 + 0.5;
    float alpineFavor = coldInfluence * 0.7 + boundaryNoise * 0.3;
    alpineFromCold = (alpineFavor > 0.55);
}

if (alpineFromAltitude || alpineFromCold) {
    return ALPINE_BIOME_ID;
}
```

### Fix 3: Water Maze - Deterministic Procedural

The water maze suggests procedural generation at chunk borders returns inconsistent results.

**Solution**: Make `proceduralVoxel()` match `generateBlockType()` EXACTLY.

Check if there's any difference between:
1. `generateBlockType()` logic
2. `proceduralVoxel()` logic

They should be IDENTICAL for the same world position!

## Implementation Order

1. **First**: Fix ocean detection in fragment shader (use continentalness approximation with tolerance)
2. **Second**: Add boundary noise to ALL climate biomes
3. **Third**: Split Alpine into altitude (>=250) and cold (no restriction) triggers
4. **Fourth**: Debug water maze (check procedural determinism)

## Testing Checklist

- [ ] Ocean biome: Only below water, never at Y=90 inland
- [ ] Beach biome: Only 0-3 blocks above water at coast
- [ ] Alpine biome: On mountains >=250 AND in cold lowlands (any height)
- [ ] Climate biomes: Curved organic boundaries (not straight lines)
- [ ] Water rendering: No maze walls, seamless at chunk borders

## Expected Biome Distribution

```
Ocean (< water) → Beach (0-3 above) → Climate Biomes (T/H based, curved) → Alpine (high OR cold)
                                      ↓
                                  All have organic boundaries
                                  via noise-shifted T/H
```

