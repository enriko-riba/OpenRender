// terrain-generation.glsl
// Core terrain generation (height maps, density, block types)

#ifndef TERRAIN_GENERATION_GLSL
#define TERRAIN_GENERATION_GLSL

// Requires: terrain-common.glsl, terrain-noise.glsl, terrain-caves.glsl

// ============================================================================
// Macro Fields (Continentalness, Erosion, Peaks/Valleys)
// ============================================================================

float getContinentalness(vec2 p) {
#ifdef IS_FRAGMENT_SHADER
    // Fast approximation for visuals
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
    // Ridged noise
    float n = fbm(p * params.uRidgeScale, params.uSeed + 200u, 3, 0.5, 2.0);
    return 1.0 - abs(n);
}

// ============================================================================
// Height Calculation
// ============================================================================

float getHeight(vec2 p) {
    float C = getContinentalness(p); // [-1, 1]
    float E = getErosion(p);         // [-1, 1]
    float PV = getPeaksValleys(p);   // [0, 1]
    
    // Remap C to [0, 1] for spline sampling
    float tC = C * 0.5 + 0.5;
    
    // Sample Spline
    float baseHeight = texture(uHeightSpline, tC).r;
    
    // Apply Erosion and Peaks
    // Simple shaping for M2
    // FIX: Add WATER_LEVEL because spline is relative to sea level (0 = coast)
    float height = baseHeight + float(WATER_LEVEL);
    
    // NEW: Only apply erosion and peaks on LAND areas (C >= OceanThreshold)
    // Ocean floors should be smooth, not eroded
    if (tC >= params.uOceanThreshold) {
        // Add some variation based on PV and E
        // If erosion is low (rugged), add peaks
        // If erosion is high (flat), reduce peaks
        float ruggedness = (1.0 - E * 0.5 - 0.5); // [0, 1] roughly
        height += PV * 20.0 * ruggedness;
        
        // Add dramatic cliff noise in mountainous regions
        if (tC > params.uMountainThreshold) {
            // High-frequency ridge noise for cliff faces
            float cliffNoise = fbm(p * params.uCliffFreq, params.uSeed + 1500u, 4, 0.6, 2.5);
            // Make it ridged (abs creates sharp peaks)
            cliffNoise = abs(cliffNoise);
            // Scale by mountain intensity
            float mountainness = smoothstep(params.uMountainThreshold, 0.95, tC);
            height += cliffNoise * params.uCliffAmp * mountainness;
        }
    }
    
    return height;
}

// ============================================================================
// 3D Density (for overhangs)
// ============================================================================

float getTerrainDensity(vec3 p) {
    vec2 xz = p.xz;
    float y = p.y;
    
    float C = getContinentalness(xz);
    float tC = C * 0.5 + 0.5;
    
    // Base height at this XZ
    float baseHeight = getHeight(xz);
    
    // Simple density: positive below surface, negative above
    float density = baseHeight - y;
    
    // Add 3D overhang noise in mountains
    // Use configurable ranges instead of hardcoded values
    if (tC > params.uMountainThreshold && 
        y > baseHeight - params.uOverhangDepthRange && 
        y < baseHeight + params.uOverhangHeightRange) {
        float mountainness = smoothstep(params.uMountainThreshold, 1.0, tC);
        // 3D noise for overhangs
        float overhangNoise = fbm3D(p * params.uOverhangFreq, params.uSeed + 2000u, 3, 0.5, 2.0);
        // Add overhang effect near the surface
        float heightFactor = 1.0 - abs((y - baseHeight) / params.uOverhangFalloffRange);
        heightFactor = clamp(heightFactor, 0.0, 1.0);
        density += overhangNoise * params.uOverhangAmp * mountainness * heightFactor;
    }
    
    return density;
}

// ============================================================================
// Helper Functions
// ============================================================================

int generateHeight(int wx, int wz) {
    // M2: Use new height generation
    float h = getHeight(vec2(wx, wz));
    int height = int(h);
    return clamp(height, 0, CHUNK_Y_SIZE - 1);
}

float getSlope(int wx, int wz) {
    float h0 = float(generateHeight(wx, wz));
    float h1 = float(generateHeight(wx + 1, wz));
    float h2 = float(generateHeight(wx, wz + 1));
    float h3 = float(generateHeight(wx - 1, wz));
    float h4 = float(generateHeight(wx, wz - 1));
    
    // Average slope magnitude
    float dx = max(abs(h1 - h0), abs(h3 - h0));
    float dz = max(abs(h2 - h0), abs(h4 - h0));
    return sqrt(dx*dx + dz*dz);
}

bool isNearWater(int wx, int wz) {
    // Check neighbors in radius 3
    // Optimization: check sparse points first
    for (int dz = -3; dz <= 3; dz+=3) {
        for (int dx = -3; dx <= 3; dx+=3) {
            if (dx == 0 && dz == 0) continue;
            int h = generateHeight(wx + dx, wz + dz);
            if (h <= WATER_LEVEL) return true;
        }
    }
    // Check closer points if needed (optional for performance)
    return false;
}

// ============================================================================
// Block Type Generation
// ============================================================================

uint generateBlockType(int height, int y, int wx, int wz) {
    if (y > height) {
        if (y <= WATER_LEVEL) {
            return BD_WATER;
        }
        return BD_AIR;
    } 
    
    // Solid blocks (y <= height)
    
    vec2 xz = vec2(wx, wz);
    float C = getContinentalness(xz);
    float tC = C * 0.5 + 0.5;
    
    // NEW: Only carve caves in LAND areas (not in ocean floor)
    // Ocean floors should be solid bedrock/sand, not have caves
    bool isLand = (tC >= params.uOceanThreshold);
    
    // Check for 3D density overhangs in mountains (only on land)
    if (isLand && tC > params.uMountainThreshold && y > height - 50 && y > WATER_LEVEL + 20) {
        vec3 p = vec3(wx, y, wz);
        float density = getTerrainDensity(p);
        if (density < 0.0) {
            // Overhang carved out
            return BD_AIR;
        }
    }
    
    // Cave Carving (only on land, not in ocean)
    // Don't carve bedrock (y=0) or ocean floor
    if (isLand && y > 0) {
        int depth = height - y;
        // Calculate slope for breach logic
        // Extended range to depth < 15 (from 12) for better breach detection
        float slope = 0.0;
        if (depth < 15) {
            slope = getSlope(wx, wz);
        }
        
        if (isCave(wx, y, wz, depth, slope)) {
            // If it's a cave, it's Air (unless it's below water level, then it might be flooded)
            if (y <= WATER_LEVEL) {
                // Flooded cave logic:
                // If the terrain column is underwater (ocean), flood the cave.
                // This prevents "water portals" on the ocean floor.
                // Caves under land (height > WATER_LEVEL) remain dry (Air), preserving "air pockets".
                // Use config value for flooding extension instead of hardcoded 8
                if (y <= WATER_LEVEL + int(params.uCaveFloodExt)) {
                    return BD_WATER;
                }
                
                // Dry cave under land
                return BD_AIR;
            }
            return BD_AIR;
        }
    }
    
    // Surface block
    if (y == height) {
        if (y < WATER_LEVEL) {
            // Underwater surface (ocean floor or shallow water)
            if (y >= WATER_LEVEL - 1) return BD_SHORELINE; // 1 block below water
            return BD_UNDERWATER_SUBSURFACE; // Deep underwater (bedrock)
        } else {
            // Above water surface (land)
            if (y <= WATER_LEVEL + int(params.uShorelineRange)) {
                // Shoreline check
                if (isNearWater(wx, wz)) return BD_SHORELINE;
            }
            return BD_SURFACE;
        }
    }
    
    // Sub-surface
    int depth = height - y;
    if (depth <= int(params.uSubsurfaceDepth)) return BD_SUBSURFACE;
    return BD_DEEP_SUBSURFACE;
}

#endif // TERRAIN_GENERATION_GLSL
