// terrain-caves.glsl
// Cave generation system (cheese caves, spaghetti tunnels)

#ifndef TERRAIN_CAVES_GLSL
#define TERRAIN_CAVES_GLSL

// Requires: terrain-common.glsl, terrain-noise.glsl

// ============================================================================
// Cave Density Functions
// ============================================================================

float getCheeseDensity(vec3 p) {
    // Cheese caves: 3D noise threshold
    return fbm3D(p * params.uCheeseFreq, params.uSeed + 300u, 2, 0.5, 2.0);
}

float getSpaghettiDensity(vec3 p) {
    // Spaghetti caves: Ridged 3D noise (worms)
    // We want long, thin tunnels. Ridged noise produces "veins".
    // We use two noise fields to create "worms" where both are close to 0.
    float n1 = fbm3D(p * params.uSpaghettiFreq, params.uSeed + 400u, 2, 0.5, 2.0);
    float n2 = fbm3D(p * params.uSpaghettiFreq, params.uSeed + 500u, 2, 0.5, 2.0);
    
    // Combine: density is high when both n1 and n2 are close to 0
    // This creates a tube-like structure
    float dist = sqrt(n1*n1 + n2*n2);
    // Invert so high density = cave
    // Map [0, 1] -> [1, 0] roughly
    return 1.0 - dist * 4.0; // Scale to control thickness
}

bool isCave(int wx, int wy, int wz, int depth, float slope) {
    vec3 p = vec3(wx, wy, wz);
    
    // Surface attenuation: reduce cave probability near surface
    // Use config value for fade distance instead of hardcoded 5.0
    float depthAtten = smoothstep(0.0, params.uCaveDepthFade, float(depth));
    
    // Allow entrances on slopes (cliffs/hills)
    // Use config values for slope thresholds instead of hardcoded 0.5, 2.0
    float slopeAtten = smoothstep(params.uCaveSlopeFadeMin, params.uCaveSlopeFadeMax, slope);
    
    // Use the best of both: if deep OR steep, allow cave
    // Favor steep slopes: weight slope more heavily
    float attenuation = max(depthAtten, slopeAtten * 1.2);
    attenuation = clamp(attenuation, 0.0, 1.0);
    
    // Cheese caves
    float cheese = getCheeseDensity(p);
    // Threshold: if cheese > threshold, it's a cave
    // Apply attenuation to the density (or increase threshold)
    if (cheese * attenuation > params.uCaveThreshold) return true;
    
    // Spaghetti caves
    float spaghetti = getSpaghettiDensity(p);
    if (spaghetti * attenuation > params.uCaveThreshold) return true;
    
    return false;
}

#endif // TERRAIN_CAVES_GLSL
