// voxel-terrain.frag - Voxel Terrain Fragment Shader
// Purpose: Simple lighting and texturing for voxel terrain
// Version: 5.0 (BlockId-based textures - each block type has its own 150x50 atlas)
//
// Architecture Change: Textures are now indexed by BlockId, not BiomeId+Layer
// Each BlockId maps to a texture array layer containing a 150×50 atlas (Top|Bottom|Side)

#version 460

// ============================================================================
// Uniforms
// ============================================================================

// Camera position (from UBO)
layout(std140, binding = 0) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

// Directional light (matches standard LightUniform structure)
struct Light {    
    vec3 position;      // Direction for directional lights
    vec3 ambient;
    vec3 diffuse;
    vec3 specular;
    float falloff;
};
layout(std140, binding = 1) uniform light {
    Light dirLight;
};

// Material properties
uniform vec3 uMaterialDiffuse = vec3(0.6, 0.8, 0.4);
uniform vec3 uMaterialSpecular = vec3(0.2, 0.2, 0.2);
uniform float uMaterialShininess = 16.0;

// Block texture array: layer = BlockId, each layer is 150×50 atlas (Top|Bottom|Side)
uniform sampler2DArray uBlockTextures;

uniform int uIsUnderwater;
uniform float uTime;
uniform int uShowBiomes;

// ============================================================================
// Constants
// ============================================================================

const int WATER_LEVEL = 35;

// Atlas UV regions (150×50 split into 3 regions of 50×50)
const float ATLAS_TOP_U_MIN = 0.0;
const float ATLAS_TOP_U_MAX = 0.333333;
const float ATLAS_BOTTOM_U_MIN = 0.333333;
const float ATLAS_BOTTOM_U_MAX = 0.666666;
const float ATLAS_SIDE_U_MIN = 0.666666;
const float ATLAS_SIDE_U_MAX = 1.0;

// ============================================================================
// Fragment Input (from vertex shader)
// ============================================================================

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vTexCoord;
in float vAO;
in vec3 vViewDir;
flat in uint vBlockDescriptor;  // BlockId (lower 10 bits = unique ID)
flat in uint vBiomeId;          // CPU-computed biome ID (for debug display only)
flat in uint vIsEmissive;       // Emissive flag (1 = full brightness)
flat in uint vFaceId;           // Face ID (0-5 for cube faces, 6 for cross-billboard)
in float vSkyLight;
in float vBlockLight;

// ============================================================================
// Fragment Output
// ============================================================================

layout(location = 0) out vec4 FragColor;

// ============================================================================
// Helper Functions
// ============================================================================

// Sample block texture using the 150×50 atlas format
// Face determines which 50-pixel column to sample from
vec4 sampleBlockTexture(uint blockId, vec2 localUV, vec3 normal, uint face) {
    // Determine face type from normal
    // Top (+Y): use first 50 pixels
    // Bottom (-Y): use middle 50 pixels  
    // Sides (±X, ±Z): use last 50 pixels
    
    float uOffset;
    if (face == 6u) {
        uOffset = ATLAS_SIDE_U_MIN;
    } else if (normal.y > 0.5) {
        // Top face
        uOffset = ATLAS_TOP_U_MIN;
    } else if (normal.y < -0.5) {
        // Bottom face
        uOffset = ATLAS_BOTTOM_U_MIN;
    } else {
        // Side face
        uOffset = ATLAS_SIDE_U_MIN;
    }
    
    // Compute atlas UV: offset + local UV scaled to 1/3 width
    // Flip Y because OpenGL textures have origin at bottom-left but images load from top
    vec2 atlasUV = vec2(uOffset + localUV.x * 0.333333, 1.0 - localUV.y);
    
    // Sample from texture array (layer = blockId)
    return texture(uBlockTextures, vec3(atlasUV, float(blockId)));
}

// Sample block texture with animated UVs for water
// Applies multi-layer scrolling effect for realistic water surface
vec4 sampleWaterTexture(uint blockId, vec2 localUV, vec3 normal, float time) {
    // Water uses top face atlas section
    float uOffset = ATLAS_TOP_U_MIN;
    
    // Create multiple scrolling UV layers for organic water movement
    // Layer 1: Slow diagonal scroll
    vec2 scroll1 = vec2(time * 0.03, time * 0.02);
    // Layer 2: Faster opposite scroll
    vec2 scroll2 = vec2(-time * 0.05, time * 0.04);
    // Layer 3: Circular/wave motion
    float wavePhase = time * 1.5;
    vec2 scroll3 = vec2(sin(wavePhase) * 0.02, cos(wavePhase * 0.8) * 0.02);
    
    // Compute animated UVs for each layer
    vec2 uv1 = fract(localUV + scroll1);
    vec2 uv2 = fract(localUV * 1.3 + scroll2); // Slightly different scale for variation
    vec2 uv3 = fract(localUV * 0.7 + scroll3);
    
    // Convert to atlas coordinates
    vec2 atlasUV1 = vec2(uOffset + uv1.x * 0.333333, 1.0 - uv1.y);
    vec2 atlasUV2 = vec2(uOffset + uv2.x * 0.333333, 1.0 - uv2.y);
    vec2 atlasUV3 = vec2(uOffset + uv3.x * 0.333333, 1.0 - uv3.y);
    
    // Sample all layers
    vec4 color1 = texture(uBlockTextures, vec3(atlasUV1, float(blockId)));
    vec4 color2 = texture(uBlockTextures, vec3(atlasUV2, float(blockId)));
    vec4 color3 = texture(uBlockTextures, vec3(atlasUV3, float(blockId)));
    
    // Blend layers with different weights for organic look
    vec4 blended = color1 * 0.5 + color2 * 0.3 + color3 * 0.2;
    
    return blended;
}

// Biome debug colors (for visualization when ShowBiomes is enabled)
vec3 getBiomeDebugColor(uint biomeId) {
    // 13 distinct colors for all biomes (including DeepOcean, River, Swamp)
    vec3 colors[13] = vec3[13](
        vec3(0.0, 0.3, 0.8),   // 0: Ocean (blue)
        vec3(0.95, 0.85, 0.55),// 1: Beach (sand yellow)
        vec3(0.4, 0.75, 0.3),  // 2: Plains (bright green)
        vec3(0.8, 0.65, 0.35), // 3: Savanna (tan/orange)
        vec3(0.95, 0.85, 0.45),// 4: Desert (bright yellow)
        vec3(0.15, 0.55, 0.2), // 5: Rainforest (dark green)
        vec3(0.2, 0.45, 0.35), // 6: Taiga (dark blue-green)
        vec3(0.85, 0.90, 0.95),// 7: Tundra (white-gray, NOT blue)
        vec3(0.55, 0.45, 0.35),// 8: Highlands (brown)
        vec3(0.98, 0.98, 1.0), // 9: Alpine (pure white)
        vec3(0.0, 0.15, 0.5),  // 10: DeepOcean (very dark blue)
        vec3(0.2, 0.5, 0.8),   // 11: River (light blue)
        vec3(0.35, 0.5, 0.3)   // 12: Swamp (murky green)
    );
    
    if (biomeId < 13u) {
        return colors[biomeId];
    }
    return vec3(1.0, 0.0, 1.0); // Magenta for unknown
}

// ============================================================================
// Main Shader
// ============================================================================

void main() {
    // Underwater State
    float waterLevel = float(WATER_LEVEL) + 0.85;
    bool isCameraUnderwater = uIsUnderwater == 1;
    bool isFragmentUnderwater = vWorldPos.y < waterLevel;

    // Normalize interpolated vectors
    vec3 N = normalize(vNormal);
    vec3 L = normalize(-dirLight.position);
    vec3 V = normalize(vViewDir);

    // Extract BlockId (lower 10 bits)
    uint blockId = vBlockDescriptor & 0x3FFu;
    
    // Check if this is water
    bool isWater = blockId == 1u;
    bool isTopFace = N.y > 0.5;

    // Sample block texture (use animated UVs for water top faces)
    vec4 baseColor;
    if (isWater && isTopFace) {
        baseColor = sampleWaterTexture(blockId, vTexCoord, N, uTime);
    } else {
        baseColor = sampleBlockTexture(blockId, vTexCoord, N, vFaceId);
    }

    // Alpha test: Discard fully transparent pixels (fixes artifacts for cutout blocks in Opaque queue)
    if (baseColor.a < 0.05) discard;

    // Procedural Water Normal
    vec3 waterNormal = N;
    
    if (isWater && isTopFace) {
        // Wave animation
        float speed = 2.5;
        float w1 = sin(vWorldPos.x * 3.5 + vWorldPos.z * 3.0 + uTime * speed);
        float w2 = cos(vWorldPos.x * 3.2 - vWorldPos.z * 3.8 + uTime * speed * 1.1);
        float w3 = sin(vWorldPos.z * 5.5 + uTime * speed * 1.5);
        float w4 = cos((vWorldPos.x + vWorldPos.z) * 6.0 + uTime * speed * 2.0);
        
        vec3 waveOffset = vec3((w1 + w3) * 0.015, 0.0, (w2 + w4) * 0.015); 
        waterNormal = normalize(N + waveOffset);
    }

    // Water specific processing
    if (isWater) {
        float dist = length(vWorldPos - cameraPos);
        
        vec3 deepWaterColor = vec3(0.08, 0.12, 0.2);
        // Scale water ambient by sky light to darken in caves
        vec3 waterBaseColor = deepWaterColor * dirLight.ambient * 2.5 * max(vSkyLight, 0.05);

        vec3 waterFogColor = isCameraUnderwater
            ? dirLight.ambient * vec3(0.08, 0.2, 0.35)
            : dirLight.ambient * vec3(0.10, 0.18, 0.30);
        
        float brightness = dot(dirLight.ambient, vec3(0.333));
        float nightOpacityBoost = 1.0 - smoothstep(0.05, 0.2, brightness);
        
        if (isTopFace) {
            if (isCameraUnderwater) {
                // Looking UP at water surface from below
                // The water surface should blend into the murky underwater environment
                float underwaterFogDensity = 0.15; // Strong fog for limited visibility
                float fogFactor = 1.0 - exp(-dist * underwaterFogDensity);
                
                // Muted blue-green color for water surface seen from below
                // Should NOT be bright - water surface from below is dark/murky
                vec3 underwaterSurfaceColor = dirLight.ambient * vec3(0.1, 0.25, 0.4);
                baseColor.rgb = underwaterSurfaceColor;
                
                // Subtle caustic shimmer (very subtle, not bright)
                float causticsBase = sin(vWorldPos.x * 3.0 + vWorldPos.z * 2.5 + uTime * 1.5) * 0.5 + 0.5;
                float caustics2 = cos(vWorldPos.x * 2.2 - vWorldPos.z * 3.1 + uTime * 2.0) * 0.5 + 0.5;
                float caustics = causticsBase * caustics2 * 0.1; // Reduced caustic intensity
                baseColor.rgb += caustics * dirLight.ambient * 0.3;
                
                // Apply fog - water surface fades into fog at distance
                baseColor.rgb = mix(baseColor.rgb, waterFogColor, fogFactor);
                
                // Semi-transparent so you can see it's the surface
                baseColor.a = mix(0.6, 0.9, clamp(dist / 15.0, 0.0, 1.0));
            } else {
                // Looking DOWN at water surface from above (original logic)
                float alphaFade = clamp((dist - 2.0) / 20.0, 0.0, 1.0);
                baseColor.a = mix(0.88, 1.0, alphaFade);
                
                baseColor.rgb = mix(baseColor.rgb, waterBaseColor, 0.5);
                baseColor.a = clamp(baseColor.a + nightOpacityBoost * 0.3, 0.0, 1.0);
                
                vec3 viewDir = normalize(vViewDir);
                float fresnel = pow(1.0 - clamp(abs(dot(waterNormal, viewDir)), 0.0, 1.0), 3.0);
                vec3 surfaceTint = mix(vec3(0.05, 0.15, 0.25), vec3(0.35, 0.6, 0.9), fresnel);
                baseColor.rgb = mix(baseColor.rgb, surfaceTint, 0.65);
                float minAlpha = mix(0.70, 0.92, fresnel);
                baseColor.a = max(baseColor.a, minAlpha);
                
                vec3 H = normalize(L + V);
                float specAngle = max(dot(H, waterNormal), 0.0);
                float specular = pow(specAngle, 256.0) * 1.5;
                vec3 sunSpecular = dirLight.specular * specular * vec3(1.0, 0.9, 0.7) * vSkyLight;
                baseColor.rgb += sunSpecular;

                float absorption = exp(-dist * 0.10);
                baseColor.rgb = mix(waterFogColor, baseColor.rgb, absorption);
                float opacityBoost = mix(0.60, 0.98, 1.0 - absorption);
                baseColor.a = max(baseColor.a, opacityBoost);
            }
        } else {
            // Side faces (underwater walls) - should not be generated but handle just in case
            baseColor.rgb = mix(waterBaseColor, baseColor.rgb, 0.3);
            float sideAlpha = clamp((dist - 2.0) / 15.0, 0.5, 0.95);
            baseColor.a = sideAlpha;
        }
    }

    // Biome debug mode
    if (uShowBiomes == 1 && !isWater) {
        vec3 biomeColor = getBiomeDebugColor(vBiomeId);
        baseColor.rgb = mix(baseColor.rgb, biomeColor, 0.7);
    }

    // Lighting
    vec3 finalNormal = isWater ? waterNormal : N;
    float NdotL = max(dot(finalNormal, L), 0.0);
    float diffuse = NdotL;
    
    // Ambient occlusion - only affects ambient/indirect light
    float ao = vAO;
    
    // Blinn-Phong specular (reduced for terrain)
    vec3 H = normalize(L + V);
    float NdotH = max(dot(finalNormal, H), 0.0);
    float specular = pow(NdotH, uMaterialShininess) * 0.3;
    
    if (isWater) {
        specular = 0.0; // Water has its own specular
    }

    // UNDERWATER: Force uniform lighting to eliminate face brightness differences
    // Bottom faces only get ambient (NdotL=0), sides get ambient+diffuse
    // This causes visible brightness difference underwater - fix by using uniform lighting
    if (isCameraUnderwater && !isWater) {
        // Use only ambient lighting underwater - no directional light variance
        diffuse = 0.0;
        specular = 0.0;
        // Do NOT boost AO underwater - it makes faces look too bright/glowing
        // ao = mix(ao, 1.0, 0.5); 
    }

    // Combine lighting - Minecraft-style light calculation
    // vSkyLight/vBlockLight are per-vertex light levels (0-1, from 0-15 range)
    // dirLight.ambient reflects day/night cycle brightness
    float skyFactor = vSkyLight;
    float blockFactor = vBlockLight;
    
    // Calculate ambient brightness from day/night cycle (0 = night, ~1 = day)
    float ambientBrightness = dot(dirLight.ambient, vec3(0.299, 0.587, 0.114)); // Luminance
    
    // Combined light uses MAX of sky and block for overall brightness
    float combinedLight = max(skyFactor, blockFactor);
    
    // Ambient depends on combined light (max of sky and block)
    // AO only affects ambient - direct light ignores occlusion
    vec3 ambient = dirLight.ambient * ao * max(combinedLight, 0.05);

    // Diffuse (Sun) depends on sky light only - NO AO on direct light
    vec3 diffuseColor = dirLight.diffuse * diffuse * skyFactor;

    // Specular (Sun) depends on sky light only - no AO on specular
    vec3 specularColor = dirLight.specular * specular * uMaterialSpecular * skyFactor;

    // Block Light tint - apply warm color when block light is dominant
    // At night: use block light when it exceeds effective sky brightness
    // Effective sky = skyFactor * ambientBrightness (accounts for day/night)
    float effectiveSkyBrightness = skyFactor * ambientBrightness;
    vec3 blockLightColor = vec3(1.0, 0.8, 0.6); // Warm torch color
    float blockLightDominance = max(0.0, blockFactor - effectiveSkyBrightness);
    vec3 localLightTint = blockLightColor * blockLightDominance * ao;

    // Emissive blocks (Lava, Glowstone, etc) ignore shading/AO
    if (vIsEmissive == 1u) {
        ambient = vec3(1.0);
        diffuseColor = vec3(0.0);
        specularColor = vec3(0.0);
        localLightTint = vec3(0.0);
    }

    vec3 finalColor = baseColor.rgb * (ambient + diffuseColor + localLightTint) + specularColor;

    // Fog for terrain seen through water from above
    if (!isCameraUnderwater && !isWater && isFragmentUnderwater) {
        float depth = waterLevel - vWorldPos.y;
        // Calculate path length through water (vViewDir points to camera)
        float viewCos = max(vViewDir.y, 0.05); 
        float waterPath = depth / viewCos;
        
        // Max visibility ~18 blocks
        float fogFactor = clamp(waterPath / 18.0, 0.0, 1.0);
        
        // Use dark water color for fog (matches water surface base)
        vec3 deepWaterColor = vec3(0.08, 0.12, 0.2);
        vec3 waterFogColor = deepWaterColor * dirLight.ambient * 2.5;
        
        finalColor = mix(finalColor, waterFogColor, fogFactor);
    }

    // Underwater rendering effects - apply when camera is submerged
    if (isCameraUnderwater && !isWater) {
        const float fogEnd = 30.0;            // Matches water.frag distance fog
        const float maxDepthEffect = 60.0;    // Matches water.frag depth rolloff
        const float depthFogEnd = 20.0;       // Depth distance where depth-based fog reaches 100%

        float dist = length(vWorldPos - cameraPos);
        float cameraDepth = max(waterLevel - cameraPos.y, 0.0);
        float fragmentDepthBelowWater = waterLevel - vWorldPos.y;
        float fragmentDepth = max(fragmentDepthBelowWater, 0.0);
        bool isFragmentAboveWater = fragmentDepthBelowWater < 0.0;

        float distanceFog = clamp(dist / fogEnd, 0.0, 1.0);
        float depthFactor = clamp(fragmentDepth / maxDepthEffect, 0.0, 1.0);
        float depthFog = clamp(fragmentDepth / depthFogEnd, 0.0, 1.0);
        float combinedFog = clamp(distanceFog + depthFog * 0.85, 0.0, 1.0);

        vec3 shallowFogColor = vec3(0.1, 0.35, 0.55);
        vec3 deepFogColor = vec3(0.02, 0.1, 0.18);
        vec3 waterFogColor = mix(shallowFogColor, deepFogColor, depthFactor) * dirLight.ambient * 2.5;

        // Light absorption with depth (same exponential curve as water.frag)
        float lightAbsorption = exp(-fragmentDepth * 0.015);
        vec3 foggedColor = finalColor * lightAbsorption;

        // Treat fragments above the water surface as fully fogged (total internal reflection)
        if (isFragmentAboveWater) {
            combinedFog = 1.0;
        }

        // Blend towards underwater fog color to limit visibility
        foggedColor = mix(foggedColor, waterFogColor, combinedFog);

        // Distance is still the absolute factor; enforce a strong fade between 20-30 units
        float farFade = smoothstep(20.0, fogEnd, dist);
        foggedColor = mix(foggedColor, waterFogColor, max(farFade, combinedFog));

        // Apply the same subtle blue tint used in water.frag for underwater view
        foggedColor *= vec3(0.5, 0.7, 1.0);

        // Reduce visibility based on how deep the camera is
        float cameraDepthFactor = clamp(cameraDepth / maxDepthEffect, 0.0, 1.0);
        foggedColor *= (1.0 - cameraDepthFactor * 0.25);

        FragColor = vec4(foggedColor, baseColor.a);
        return;
    }

    FragColor = vec4(finalColor, baseColor.a);
}
