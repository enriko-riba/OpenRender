// noise.glsl - GPU noise library for terrain generation
// Ported from TerrainBuilder.cs and NoiseData.cs
// Version: Phase 2 Initial Implementation

#ifndef NOISE_GLSL
#define NOISE_GLSL

// ============================================================================
// Hash Functions (for pseudo-random gradients)
// ============================================================================

// PCG hash - high quality pseudo-random number generator
uint pcgHash(uint seed) {
    uint state = seed * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// Hash vec2 to uint
uint hash2D(ivec2 p, uint seed) {
    return pcgHash(uint(p.x) + pcgHash(uint(p.y) + seed));
}

// Hash to float in [-1, 1]
float hashFloat(uint h) {
    return float(h) / float(0xFFFFFFFFu) * 2.0 - 1.0;
}

// ============================================================================
// Gradient Noise (Perlin-style) Implementation
// ============================================================================

// Get pseudo-random gradient vector for grid point
vec2 getGradient2D(ivec2 p, uint seed) {
    uint h = hash2D(p, seed);
    float angle = hashFloat(h) * 3.14159265359; // Random angle
    return vec2(cos(angle), sin(angle));
}

// Quintic interpolation for smooth gradients
float quinticSmooth(float t) {
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

// Sample gradient noise at arbitrary point
// freq: frequency multiplier for coordinates
// amp: amplitude multiplier for output
// seed: random seed for repeatability
float gradientNoise2D(vec2 pos, float freq, float amp, uint seed) {
    // Scale by frequency
    vec2 p = pos * freq;
    
    // Grid cell corners
    ivec2 p0 = ivec2(floor(p));
    ivec2 p1 = p0 + ivec2(1, 0);
    ivec2 p2 = p0 + ivec2(0, 1);
    ivec2 p3 = p0 + ivec2(1, 1);
    
    // Fractional position within cell
    vec2 f = fract(p);
    
    // Get gradients at corners
    vec2 g0 = getGradient2D(p0, seed);
    vec2 g1 = getGradient2D(p1, seed);
    vec2 g2 = getGradient2D(p2, seed);
    vec2 g3 = getGradient2D(p3, seed);
    
    // Compute dot products
    float d0 = dot(g0, f - vec2(0.0, 0.0));
    float d1 = dot(g1, f - vec2(1.0, 0.0));
    float d2 = dot(g2, f - vec2(0.0, 1.0));
    float d3 = dot(g3, f - vec2(1.0, 1.0));
    
    // Interpolate
    vec2 u = vec2(quinticSmooth(f.x), quinticSmooth(f.y));
    
    float x0 = mix(d0, d1, u.x);
    float x1 = mix(d2, d3, u.x);
    float result = mix(x0, x1, u.y);
    
    return result * amp;
}

// ============================================================================
// Domain Warp Noise
// ============================================================================

// Domain warped noise - applies distortion to sampling coordinates
// Based on NoiseData.SampleDomainWarped()
float domainWarpedNoise2D(vec2 pos, float baseFreq, float warpFreq, float warpAmp, uint seed) {
    // Generate two independent warp offsets using seed variations
    uint warpSeedX = seed ^ 0x9E3779B9u;
    uint warpSeedY = seed ^ 0x7F4A7C15u;
    
    // Sample warp fields
    float warpX = gradientNoise2D(pos, warpFreq, warpAmp, warpSeedX);
    float warpY = gradientNoise2D(pos, warpFreq, warpAmp, warpSeedY);
    
    // Apply warp to position
    vec2 warpedPos = pos + vec2(warpX, warpY);
    
    // Sample base noise at warped position
    return gradientNoise2D(warpedPos, baseFreq, 1.0, seed);
}

// ============================================================================
// Utility Functions
// ============================================================================

// Smooth step interpolation (cubic hermite)
float smoothstep01(float x) {
    x = clamp(x, 0.0, 1.0);
    return x * x * (3.0 - 2.0 * x);
}

// Linear interpolation
float lerp(float a, float b, float t) {
    return a + (b - a) * t;
}

// Saturate to [0, 1]
float saturate(float x) {
    return clamp(x, 0.0, 1.0);
}

#endif // NOISE_GLSL
