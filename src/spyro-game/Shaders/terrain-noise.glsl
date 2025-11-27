// terrain-noise.glsl
// Noise generation functions for terrain generation
// Includes: 2D/3D noise, FBM (Fractal Brownian Motion), domain warping

#ifndef TERRAIN_NOISE_GLSL
#define TERRAIN_NOISE_GLSL

// Requires: terrain-common.glsl (for hash function and params)

// ============================================================================
// 2D Noise Functions
// ============================================================================

float noise2D_float(vec2 p, uint seed) {
    // FIX: Cast to int first to handle negative coordinates correctly (2's complement)
    // uint(float) is undefined for negative values.
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
    float maxValue = 0.0;
    for(int i=0; i<octaves; i++) {
        total += smoothNoise(p * frequency, seed + uint(i*132)) * amplitude;
        maxValue += amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
    }
    return total / maxValue;
}

// ============================================================================
// 3D Noise Functions
// ============================================================================

float noise3D_float(vec3 p, uint seed) {
    uint n = hash(uint(int(p.x)) + hash(uint(int(p.y)) + hash(uint(int(p.z)), seed), seed), seed);
    return float(n) / 4294967295.0 * 2.0 - 1.0;
}

float smoothNoise3D(vec3 p, uint seed) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    
    float v000 = noise3D_float(i, seed);
    float v100 = noise3D_float(i + vec3(1.0, 0.0, 0.0), seed);
    float v010 = noise3D_float(i + vec3(0.0, 1.0, 0.0), seed);
    float v110 = noise3D_float(i + vec3(1.0, 1.0, 0.0), seed);
    float v001 = noise3D_float(i + vec3(0.0, 0.0, 1.0), seed);
    float v101 = noise3D_float(i + vec3(1.0, 0.0, 1.0), seed);
    float v011 = noise3D_float(i + vec3(0.0, 1.0, 1.0), seed);
    float v111 = noise3D_float(i + vec3(1.0, 1.0, 1.0), seed);
    
    return mix(
        mix(mix(v000, v100, f.x), mix(v010, v110, f.x), f.y),
        mix(mix(v001, v101, f.x), mix(v011, v111, f.x), f.y),
        f.z
    );
}

float fbm3D(vec3 p, uint seed, int octaves, float persistence, float lacunarity) {
    float total = 0.0;
    float frequency = 1.0;
    float amplitude = 1.0;
    float maxValue = 0.0;
    for(int i=0; i<octaves; i++) {
        total += smoothNoise3D(p * frequency, seed + uint(i*132)) * amplitude;
        maxValue += amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
    }
    return total / maxValue;
}

// ============================================================================
// Domain Warping
// ============================================================================

vec2 domainWarp(vec2 p, uint seed) {
    vec2 q = vec2(
        fbm(p + vec2(0.0, 0.0), seed, 2, 0.5, 2.0),
        fbm(p + vec2(5.2, 1.3), seed, 2, 0.5, 2.0)
    );
    return q * params.uWarpStrength;
}

#endif // TERRAIN_NOISE_GLSL
