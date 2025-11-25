// voxel-terrain.frag - Phase 4: Voxel Terrain Fragment Shader
// Purpose: Simple lighting and texturing for voxel terrain
// Version: 3.0 (Simplified - GPU picking removed)
//
// Implements basic Blinn-Phong lighting with ambient occlusion

#version 460
#extension GL_ARB_bindless_texture : require

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

// Material properties (simple for now - Phase 5 will add per-block textures)
uniform vec3 uMaterialDiffuse = vec3(0.6, 0.8, 0.4);   // Grass-like color
uniform vec3 uMaterialSpecular = vec3(0.2, 0.2, 0.2);
uniform float uMaterialShininess = 16.0;

// Texture samplers
// M5: Bindless texture handles for biome blending
// [BiomeID * 8 + GeologyLayer]
uniform uvec2 uBiomeTextures[80];

uniform int uIsUnderwater; // 1 if camera is inside a water block, 0 otherwise
uniform float uTime;
uniform int uShowBiomes;

#define IS_FRAGMENT_SHADER
#include "terrain-common.glsl"

// Helper to map BlockType to GeologyLayer
int getGeologyLayer(uint blockType) {
    if (blockType == BLOCK_WATER_LEVEL) return 1; // Water
    if (blockType == BLOCK_GRASS_DIRT) return 2; // Surface
    if (blockType == BLOCK_DIRT) return 3; // Subsurface
    if (blockType == BLOCK_ROCK) return 4; // DeepSubsurface
    if (blockType == BLOCK_SAND) return 7; // ShoreLine
    if (blockType == BLOCK_BEDROCK) return 6; // UnderwaterSubsurface
    // Fallback
    return 2; // Surface
}

// Helper to sample biome texture
vec4 sampleBiomeTexture(uint biomeId, int layer, vec2 uv) {
    if (biomeId >= 10) biomeId = 0; // Safety clamp
    int index = int(biomeId) * 8 + layer;
    uvec2 handle = uBiomeTextures[index];
    
    if (handle.x == 0u && handle.y == 0u) {
        return vec4(1.0, 0.0, 1.0, 1.0); // Magenta error
    }
    
    return texture(sampler2D(handle), uv);
}


// ============================================================================
// Fragment Input (from vertex shader)
// ============================================================================

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vTexCoord;
in float vAO;
in vec3 vViewDir;
flat in uint vBlockType;

// ============================================================================
// Fragment Output
// ============================================================================

layout(location = 0) out vec4 FragColor;

// ============================================================================
// Constants
// ============================================================================
// Constants defined in terrain-common.glsl

// ============================================================================
// Main Shader
// ============================================================================

void main() {
    // Underwater State
    float waterLevel = float(WATER_LEVEL) + 1.0;
    bool isCameraUnderwater = uIsUnderwater == 1;
    bool isFragmentUnderwater = vWorldPos.y < waterLevel;

    // Normalize interpolated vectors
    vec3 N = normalize(vNormal);
    vec3 L = normalize(-dirLight.position);  // Direction TO light (negate direction)
    vec3 V = normalize(vViewDir);

    // Sample texture based on block type
    vec4 baseColor = vec4(0.0);
    
    // Procedural Water Normal
    vec3 waterNormal = N;
    
    if (vBlockType == BLOCK_WATER_LEVEL) {
        // Improved wave animation - Higher frequency and more random
        float speed = 2.5;
        
        // Wave 1: High frequency diagonal
        float w1 = sin(vWorldPos.x * 3.5 + vWorldPos.z * 3.0 + uTime * speed);
        
        // Wave 2: Crossing
        float w2 = cos(vWorldPos.x * 3.2 - vWorldPos.z * 3.8 + uTime * speed * 1.1);
        
        // Wave 3: Detail
        float w3 = sin(vWorldPos.z * 5.5 + uTime * speed * 1.5);

        // Wave 4: Interference
        float w4 = cos((vWorldPos.x + vWorldPos.z) * 6.0 + uTime * speed * 2.0);
        
        // Perturb normal - Very low amplitude to avoid artifacts
        vec3 waveOffset = vec3((w1 + w3) * 0.015, 0.0, (w2 + w4) * 0.015); 
        waterNormal = normalize(N + waveOffset);
    }
    
    // M5: Biome Blending
    int layer = getGeologyLayer(vBlockType);

    // Fix for greedy meshing artifacts: Calculate biome at voxel center
    // This ensures the whole voxel gets a single biome assignment, preventing
    // diagonal artifacts on faces and gradients across the block.
    vec3 voxelCenter = floor(vWorldPos - N * 0.01) + 0.5;
    
    // CRITICAL FIX: Use ONLY the primary biome (no blending)
    // Biome blending at the pixel level causes gradient artifacts between biomes.
    // Each block should have a single, solid texture from its primary biome.
    uint primaryBiomeId = getBiomeId(voxelCenter);
    baseColor = sampleBiomeTexture(primaryBiomeId, layer, vTexCoord);
    
    // OLD CODE (REMOVED - was causing gradient blending artifacts):
    // RegionMix regionMix = getBiomeWeights(voxelCenter);
    // float totalWeight = 0.0;
    // for (int i = 0; i < 4; i++) {
    //     if (regionMix.weights[i] > 0.001) {
    //         baseColor += sampleBiomeTexture(regionMix.biomeIds[i], layer, vTexCoord) * regionMix.weights[i];
    //         totalWeight += regionMix.weights[i];
    //     }
    // }
    // if (totalWeight > 0.0) baseColor /= totalWeight;
    // else baseColor = vec4(1.0, 0.0, 1.0, 1.0); // Error
    
    // Water specific processing
    if (vBlockType == BLOCK_WATER_LEVEL) {
        // Water opacity increases with distance to hide underwater culling artifacts
        float dist = length(vWorldPos - cameraPos);
        
        // Adjusted opacity for outside view:
        // Fade to opaque very fast (between 2m and 15m) to strictly limit visibility
        float alphaFade = clamp((dist - 2.0) / 13.0, 0.0, 1.0); 
        
        // If underwater, keep the denser fog/opacity (0.8 to 1.0)
        if (isCameraUnderwater) {
             alphaFade = clamp((dist - 5.0) / 30.0, 0.0, 1.0);
             baseColor.a = mix(0.8, 1.0, alphaFade);
        } else {
             // Outside view: Start at 0.85 opacity
             baseColor.a = mix(0.85, 1.0, alphaFade);
        }
        
        // Day/Night Tinting
        vec3 deepWaterColor = vec3(0.1, 0.15, 0.25);
        // Modulate by ambient light (unified system)
        // Scale up to maintain visibility
        vec3 waterBaseColor = deepWaterColor * dirLight.ambient * 3.0; 
        
        // Mix texture with water color
        baseColor.rgb = mix(baseColor.rgb, waterBaseColor, 0.5);
        
        // Increase opacity at night (harder to see through)
        // Use ambient brightness to determine night status
        float brightness = dot(dirLight.ambient, vec3(0.333));
        float nightOpacityBoost = 1.0 - smoothstep(0.05, 0.2, brightness);
        baseColor.a = clamp(baseColor.a + nightOpacityBoost * 0.3, 0.0, 1.0);

        // Sun Specular Reflection (Blinn-Phong)
        // Only visible from above
        if (!isCameraUnderwater) {
            vec3 H = normalize(L + V);
            float specAngle = max(dot(H, waterNormal), 0.0);
            // High shininess for water
            float specular = pow(specAngle, 256.0) * 1.5; 
            vec3 sunSpecular = dirLight.specular * specular * vec3(1.0, 0.9, 0.7);
            
            // Add specular to base color
            baseColor.rgb += sunSpecular;
        }
    } else {
        // Solid blocks are opaque
        baseColor.a = 1.0;
    }
    
    vec3 texColor = baseColor.rgb;

    // Strengthen AO curve
    float aoStrength = pow(vAO, 2.0); // Make dark areas darker

    // Ambient component (modulated by AO)
    vec3 ambient = dirLight.ambient * texColor * aoStrength;
    
    // Diffuse component
    float NdotL = max(dot(N, L), 0.0);
    
    // Underwater Lighting: Wrapped diffuse (scattering)
    if (isCameraUnderwater) {
        NdotL = NdotL * 0.5 + 0.5; 
    }

    // Use waterNormal for water blocks, original N for others
    vec3 lightingNormal = (vBlockType == BLOCK_WATER_LEVEL) ? waterNormal : N;
    float NdotL_Water = max(dot(lightingNormal, L), 0.0);
    if (isCameraUnderwater) NdotL_Water = NdotL_Water * 0.5 + 0.5;

    vec3 diffuse = dirLight.diffuse * texColor * NdotL_Water * aoStrength;
    
    // Specular component (Blinn-Phong)
    vec3 specular = vec3(0.0);
    if (NdotL_Water > 0.0) {
        vec3 H = normalize(L + V);
        float NdotH = max(dot(lightingNormal, H), 0.0);
        float specPower = pow(NdotH, uMaterialShininess);
        specular = dirLight.specular * uMaterialSpecular * specPower * aoStrength;
    }

    // Caustics (Underwater on solid blocks)
    // REMOVED to fix artifacts
    /*
    if (isCameraUnderwater && vBlockType != BLOCK_WATER_LEVEL) {
        float scale = 15.0; // Smaller pattern
        float speed = .001;
        float c1 = sin(vWorldPos.x * scale + uTime * speed);
        float c2 = sin(vWorldPos.z * scale + uTime * speed);
        float c3 = sin((vWorldPos.x + vWorldPos.z) * scale * 0.5 + uTime * speed);
        float caustic = pow(0.5 + 0.5 * (c1 + c2 + c3) / 3.0, 4.0); // Softer power (was 8.0)
        
        // Fade caustics with depth
        float depth = waterLevel - vWorldPos.y;
        float depthFade = clamp(1.0 - depth / 10.0, 0.0, 1.0); // Fade out faster
        
        diffuse += vec3(0.5, 0.7, 0.8) * caustic * depthFade * 0.3; // Reduced intensity
    }
    */

    // Attenuate light underwater
    if (isCameraUnderwater) {
        diffuse *= vec3(0.4, 0.7, 0.9); // Blue-green filter
        specular *= 0.0; // No specular underwater
    }
    
    // Combine components
    vec3 finalColor = ambient + diffuse + specular;
    

    // --- ATMOSPHERIC FOG (Above water) ---
    if (!isCameraUnderwater) {
        float dist = length(vWorldPos - cameraPos);
        // Fog parameters - tuned for 16 chunk view distance (~256 blocks)
        // End fog slightly before the chunk load distance to fully hide popping
        float fogStart = 150.0; 
        float fogEnd = 250.0;   
        float fogFactor = clamp((dist - fogStart) / (fogEnd - fogStart), 0.0, 1.0);
        // Smooth the transition
        fogFactor = smoothstep(0.0, 1.0, fogFactor);
        
        // Disable fog in biome debug mode
        if (uShowBiomes == 1) {
            fogFactor = 0.0;
        }
        
        // Fog color based on ambient light (unified system)
        vec3 fogColor = dirLight.ambient;
        
        // Adjust fog brightness based on time of day
        // dirLight.position is -sunDir, so sunElevation is -dirLight.position.y
        float sunElevation = -dirLight.position.y;
        float dayFactor = smoothstep(-0.1, 0.1, sunElevation);
        
        // Day: Boost fog brightness to match sky (1.5x)
        // Night: Darken fog to match dark skybox and avoid "ghostly" look (0.2x)
        float brightnessBoost = mix(0.2, 1.5, dayFactor);
        fogColor *= brightnessBoost;
        
        finalColor = mix(finalColor, fogColor, fogFactor);
    }
    
    // Underwater Effects

    // 1. View from above: Tint underwater fragments
    // REMOVED: Rely on the actual water block transparency to tint objects behind it.
    // This fixes the issue where dry tunnels below sea level were tinted blue.

    // 2. View from underwater: Strong fog and tint
    if (isCameraUnderwater) {
        float dist = length(vWorldPos - cameraPos);
        
        // Exponential fog for denser, more natural underwater feel
        float fogDensity = 0.15; // High density for short visibility (~20m)
        float fogFactor = 1.0 - exp(-dist * fogDensity);
        
        // Disable fog in biome debug mode
        if (uShowBiomes == 1) {
            fogFactor = 0.0;
        }
        
        // Darker fog at night
        // Use ambient light for fog color (unified system)
        // Match skybox gradient to hide distant terrain contours
        vec3 deepColor = dirLight.ambient * vec3(0.2, 0.5, 0.8);
        vec3 surfaceColor = dirLight.ambient * vec3(0.4, 0.7, 1.0);
        // vViewDir is vector TO camera. We need vector FROM camera (view ray).
        // So use -vViewDir.y
        float t = smoothstep(0.0, 1.0, -vViewDir.y);
        vec3 waterFogColor = mix(deepColor, surfaceColor, t);
        
        finalColor = mix(finalColor, waterFogColor, fogFactor);
        
        // Obscure outside terrain (fragments above water)
        // Exclude water blocks to prevent flickering on the water surface itself (at y=36.0)
        if (vWorldPos.y > waterLevel + 0.05 && vBlockType != BLOCK_WATER_LEVEL) {
             // Mix in more fog color to hide the "crisp" outside world
             finalColor = mix(finalColor, waterFogColor, 0.9);
        }
    }

    // M4: Biome Visualization (Debug)
    if (uShowBiomes == 1) {
        // Visualize primary biome (reuse calculation from above)
        uint biomeId = primaryBiomeId;
        vec3 biomeColor = vec3(0.5);
        
        switch(biomeId) {
            case 0u: biomeColor = vec3(0.0, 0.0, 1.0); break; // Ocean
            case 1u: biomeColor = vec3(1.0, 1.0, 0.0); break; // Beach
            case 2u: biomeColor = vec3(0.0, 1.0, 0.0); break; // Plains
            case 3u: biomeColor = vec3(1.0, 0.5, 0.0); break; // Savanna
            case 4u: biomeColor = vec3(1.0, 0.0, 0.0); break; // Desert
            case 5u: biomeColor = vec3(0.0, 0.5, 0.0); break; // Rainforest
            case 6u: biomeColor = vec3(0.0, 1.0, 1.0); break; // Taiga
            case 7u: biomeColor = vec3(1.0, 1.0, 1.0); break; // Tundra
            case 8u: biomeColor = vec3(0.5, 0.5, 0.5); break; // Highlands
            case 9u: biomeColor = vec3(0.5, 0.0, 0.5); break; // Alpine
        }
        
        finalColor = mix(finalColor, biomeColor, 0.5);
    }

    // Output with opacity
    FragColor = vec4(finalColor, baseColor.a);
}
