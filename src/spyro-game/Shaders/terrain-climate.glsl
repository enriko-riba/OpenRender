// terrain-climate.glsl
// Consolidated noise, terrain field, and biome helpers for voxel terrain shading.
//
// Provides:
// - 2D FBM noise helpers with optional domain warping
// - Continentalness/erosion/height sampling
// - Temperature/humidity sampling and biome classification
//
// Requires: terrain-common.glsl

#ifndef TERRAIN_CLIMATE_GLSL
#define TERRAIN_CLIMATE_GLSL

// ============================================================================
// Noise Helpers (2D only — fragment shader never needs 3D FBM)
// ============================================================================

float noise2D_float(vec2 p, uint seed) {
    uint n = hash(uint(int(p.x)) + hash(uint(int(p.y)), seed), seed);
    return float(n) / 4294967295.0 * 2.0 - 1.0;
}

float smoothNoise(vec2 p, uint seed) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);

    float a = noise2D_float(i, seed);
    float b = noise2D_float(i + vec2(1.0, 0.0), seed);
    float c = noise2D_float(i + vec2(0.0, 1.0), seed);
    float d = noise2D_float(i + vec2(1.0, 1.0), seed);

    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p, uint seed, int octaves, float persistence, float lacunarity) {
    float total = 0.0;
    float frequency = 1.0;
    float amplitude = 1.0;
    float norm = 0.0;

    for (int i = 0; i < octaves; i++) {
        total += smoothNoise(p * frequency, seed + uint(i * 132)) * amplitude;
        norm += amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
    }

    return norm > 0.0 ? total / norm : 0.0;
}

vec2 domainWarp(vec2 p, uint seed) {
    vec2 q = vec2(
        fbm(p + vec2(0.0, 0.0), seed, 2, 0.5, 2.0),
        fbm(p + vec2(5.2, 1.3), seed, 2, 0.5, 2.0)
    );
    return q * params.uWarpStrength;
}

// ============================================================================
// Macro Fields and Height (subset still needed by fragment shader)
// ============================================================================

float getContinentalness(vec2 p) {
#ifdef IS_FRAGMENT_SHADER
    return smoothNoise(p * params.uContScale, params.uSeed);
#else
    vec2 wp = p * params.uWarpScale;
    vec2 warp = domainWarp(wp, params.uSeed);
    return fbm((p + warp) * params.uContScale, params.uSeed, 3, 0.5, 2.0);
#endif
}

float getErosion(vec2 p) {
    return fbm(p * params.uErodeScale, params.uSeed + 100u, 3, 0.5, 2.0);
}

float getPeaksValleys(vec2 p) {
    float n = fbm(p * params.uRidgeScale, params.uSeed + 200u, 3, 0.5, 2.0);
    return 1.0 - abs(n);
}

float getHeight(vec2 p) {
    float C = getContinentalness(p);
    float E = getErosion(p);
    float PV = getPeaksValleys(p);

    float tC = C * 0.5 + 0.5;
    float baseHeight = texture(uHeightSpline, tC).r;
    float height = baseHeight + float(WATER_LEVEL);

    if (tC >= params.uOceanThreshold) {
        float ruggedness = 1.0 - E * 0.5 - 0.5;
        height += PV * 20.0 * ruggedness;

        if (tC > params.uMountainThreshold) {
            float cliffNoise = fbm(p * params.uCliffFreq, params.uSeed + 1500u, 4, 0.6, 2.5);
            cliffNoise = abs(cliffNoise);
            float mountainness = smoothstep(params.uMountainThreshold, 0.95, tC);
            height += cliffNoise * params.uCliffAmp * mountainness;
        }
    }

    return height;
}

// ============================================================================
// Climate Sampling
// ============================================================================

float getTemperature(vec3 p) {
    float temp = params.uBaseTemp;
    float altitude = max(0.0, p.y - float(WATER_LEVEL));
    temp -= params.uLapseRate * altitude;

    float noise;
#ifdef IS_FRAGMENT_SHADER
    noise = smoothNoise(p.xz * params.uClimateScale, params.uSeed + 700u);
#else
    vec2 wp = p.xz * params.uClimateWarp;
    vec2 warp = domainWarp(wp, params.uSeed + 600u);
    noise = fbm((p.xz + warp) * params.uClimateScale, params.uSeed + 700u, 2, 0.5, 2.0);
#endif

    temp += noise * 0.2;
    return clamp(temp, 0.0, 1.0);
}

float getHumidity(vec3 p) {
    float hum = params.uBaseHum;
    float C = getContinentalness(p.xz);
    float distFromCoast = max(0.0, C - params.uCoastThreshold);
    hum -= distFromCoast * params.uCoastDry;

    float noise;
#ifdef IS_FRAGMENT_SHADER
    noise = smoothNoise(p.xz * params.uClimateScale, params.uSeed + 900u);
#else
    vec2 wp = p.xz * params.uClimateWarp;
    vec2 warp = domainWarp(wp, params.uSeed + 800u);
    noise = fbm((p.xz + warp) * params.uClimateScale, params.uSeed + 900u, 2, 0.5, 2.0);
#endif

    hum += noise * 0.2;
    return clamp(hum, 0.0, 1.0);
}

// ============================================================================
// Biome Selection
// ============================================================================

const uint OCEAN_BIOME_ID = 0u;
const uint ALPINE_BIOME_ID = 9u;
const uint DEFAULT_FALLBACK_BIOME_ID = 2u;

uint getBiomeId(vec3 p) {
    float C = getContinentalness(p.xz);
    float tC = C * 0.5 + 0.5;

    float terrainHeight = 0.0;
    float heightApprox = 0.0;
    bool isUnderwater = false;

#ifdef IS_FRAGMENT_SHADER
    heightApprox = texture(uHeightSpline, tC).r + float(WATER_LEVEL);
    isUnderwater = (tC < params.uOceanThreshold) &&
                   (heightApprox <= float(WATER_LEVEL) + 10.0) &&
                   (p.y <= float(WATER_LEVEL));
#else
    terrainHeight = getHeight(p.xz);
    isUnderwater = (terrainHeight <= float(WATER_LEVEL)) &&
                   (p.y <= float(WATER_LEVEL));
#endif

    if (isUnderwater) {
        return OCEAN_BIOME_ID;
    }

#ifdef IS_FRAGMENT_SHADER
    bool isCoastal = (tC < params.uCoastRange);
    bool likelyBeach = isCoastal && (tC > params.uOceanThreshold) && (tC < params.uOceanThreshold + 0.1);
    if (likelyBeach) {
        return 1u;
    }
#else
    float heightAboveWater = terrainHeight - float(WATER_LEVEL);
    bool isCoastal = (tC < params.uCoastRange);
    if (isCoastal && heightAboveWater >= 0.0 && heightAboveWater <= 3.0) {
        return 1u;
    }
#endif

    float t = getTemperature(p);
    float h = getHumidity(p);
    float noiseT = fbm(p.xz * 0.02, params.uSeed + 4000u, 2, 0.5, 2.0) * 0.08;
    float noiseH = fbm(p.xz * 0.02, params.uSeed + 5000u, 2, 0.5, 2.0) * 0.08;
    float tShifted = clamp(t + noiseT, 0.0, 1.0);
    float hShifted = clamp(h + noiseH, 0.0, 1.0);

    uint baseBiomeId = texture(uBiomeLUT, vec2(tShifted, hShifted)).r;
    if (baseBiomeId == OCEAN_BIOME_ID || baseBiomeId == 1u) {
        baseBiomeId = DEFAULT_FALLBACK_BIOME_ID;
    }

    bool alpineFromAltitude = false;
    bool alpineFromCold = false;

#ifdef IS_FRAGMENT_SHADER
    if (heightApprox >= 220.0) {
        float altInfluence = smoothstep(220.0, 280.0, heightApprox);
        float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0) * 0.5 + 0.5;
        float alpineFavor = altInfluence * 0.7 + boundaryNoise * 0.3;
        alpineFromAltitude = (alpineFavor > 0.55);
    }
#else
    if (terrainHeight >= 220.0) {
        float altInfluence = smoothstep(220.0, 280.0, terrainHeight);
        float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0) * 0.5 + 0.5;
        float alpineFavor = altInfluence * 0.7 + boundaryNoise * 0.3;
        alpineFromAltitude = (alpineFavor > 0.55);
    }
#endif

    if (t < 0.20) {
        float coldInfluence = smoothstep(0.20, 0.10, t);
        float boundaryNoise = fbm(p.xz * 0.015, params.uSeed + 3000u, 2, 0.5, 2.0) * 0.5 + 0.5;
        float alpineFavor = coldInfluence * 0.7 + boundaryNoise * 0.3;
        alpineFromCold = (alpineFavor > 0.55);
    }

    if (alpineFromAltitude || alpineFromCold) {
        return ALPINE_BIOME_ID;
    }

    return baseBiomeId;
}

#endif // TERRAIN_CLIMATE_GLSL
