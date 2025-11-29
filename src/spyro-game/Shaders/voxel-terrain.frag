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
// [BiomeID * 24 + GeologyLayer * 3 + FaceType]
// FaceType: 0=Top, 1=Bottom, 2=Sides
uniform uvec2 uBiomeTextures[240];

uniform int uIsUnderwater; // 1 if camera is inside a water block, 0 otherwise
uniform float uTime;
uniform int uShowBiomes;

#define IS_FRAGMENT_SHADER
#include "terrain-common.glsl"
#include "terrain-climate.glsl"

// Helper to get BlockDescriptor (geology layer index) from block descriptor value
// BlockDescriptor values map directly to geology layers (0-7)
// This is now a simple passthrough since we refactored to use BlockDescriptor enum
uint getBlockDescriptor(uint blockDescriptor) {
    return blockDescriptor;  // Direct mapping: BD_* values ARE the geology layer indices
}

// Helper to determine face type from normal for texture selection
uint getFaceType(vec3 normal) {
    if (normal.y > 0.5) return 0u;        // Top face
    if (normal.y < -0.5) return 1u;       // Bottom face
    return 2u;                             // Side faces
}

// Helper to sample biome texture - now uses separate textures per face type!
vec4 sampleBiomeTexture(uint biomeId, int layer, vec2 uv, vec3 normal) {
    if (biomeId >= 10) biomeId = 0; // Safety clamp
    
    // Determine which texture to use based on face orientation
    uint faceType = getFaceType(normal);
    
    // Calculate index: BiomeID * 24 + Layer * 3 + FaceType
    int index = int(biomeId) * 24 + layer * 3 + int(faceType);
    uvec2 handle = uBiomeTextures[index];
    
    if (handle.x == 0u && handle.y == 0u) {
        return vec4(1.0, 0.0, 1.0, 1.0); // Magenta error
    }
    
    // Simple texture sampling - no atlas math needed! GL_REPEAT handles tiling
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
flat in uint vBlockDescriptor;  // Renamed from vBlockType to match BlockDescriptor enum
in float vSkyLight;
in float vBlockLight;

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

    // Sample texture based on block descriptor
    vec4 baseColor = vec4(0.0);
    
    bool isWater = vBlockDescriptor == BD_WATER;
    bool isTopFace = N.y > 0.5;

    // Procedural Water Normal
    vec3 waterNormal = N;
    
    if (isWater && isTopFace) {
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
    
    // M5: Biome Texture Selection
    uint layer = getBlockDescriptor(vBlockDescriptor);

    // Fix for greedy meshing artifacts and water biome detection
    // CRITICAL FIX: Water blocks have their top face at the UPPER edge (Y+1)
    // We need to sample the biome INSIDE the water block, not in the air above it
    vec3 voxelCenter;
    if (isWater) {
        // Water block biome sampling:
        // The top face vertices are at Y+1 (top of water block)
        // We need to sample at the water block's Y position, not Y+1
        vec3 samplePos = vWorldPos;
        
        // If this is a top face (normal pointing up), shift down into water
        if (isTopFace) {
            samplePos.y -= 0.5;  // Move from top edge into water block
        }
        
        voxelCenter = floor(samplePos) + 0.5;
    } else {
        // Solid blocks: Offset slightly inward from face to ensure we're inside the block
        voxelCenter = floor(vWorldPos - N * 0.01) + 0.5;
    }
    
    // Use direct biome ID lookup (Voronoi is too expensive in fragment shader)
    // The getBiomeId() function now uses actual terrain height for ocean detection
    // and provides natural boundaries via noise
    uint primaryBiomeId = getBiomeId(voxelCenter);
    baseColor = sampleBiomeTexture(primaryBiomeId, int(layer), vTexCoord, N);
    
        // Water specific processing
        if (isWater) {
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

        if (!isCameraUnderwater && isTopFace) {
            vec3 viewDir = normalize(vViewDir);
            float fresnel = pow(1.0 - clamp(abs(dot(waterNormal, viewDir)), 0.0, 1.0), 3.0);
            vec3 surfaceTint = mix(vec3(0.05, 0.15, 0.25), vec3(0.35, 0.6, 0.9), fresnel);
            baseColor.rgb = mix(baseColor.rgb, surfaceTint, 0.65);
            float minAlpha = mix(0.55, 0.85, fresnel);
            baseColor.a = max(baseColor.a, minAlpha);
        }

        // Sun Specular Reflection (Blinn-Phong)
        // Only visible from above
        if (!isCameraUnderwater && isTopFace) {
            vec3 H = normalize(L + V);
            float specAngle = max(dot(H, waterNormal), 0.0);
            // High shininess for water
            float specular = pow(specAngle, 256.0) * 1.5; 
            vec3 sunSpecular = dirLight.specular * specular * vec3(1.0, 0.9, 0.7);
            
            // Add specular to base color
            baseColor.rgb += sunSpecular;
        }

        // Outside view absorption to limit visibility into water volume
        if (!isCameraUnderwater) {
            float absorption = exp(-dist * 0.12);
            vec3 absorptionColor = dirLight.ambient * vec3(0.10, 0.18, 0.30);
            baseColor.rgb = mix(absorptionColor, baseColor.rgb, absorption);
            float opacityBoost = mix(0.45, 0.95, 1.0 - absorption);
            baseColor.a = max(baseColor.a, opacityBoost);
        }

        // Submerged side walls should look uniform and opaque to hide meshing seams
        if (!isTopFace) {
            vec3 wallColor = dirLight.ambient * vec3(0.07, 0.12, 0.18);
            baseColor.rgb = mix(baseColor.rgb, wallColor, 0.8);
            baseColor.a = max(baseColor.a, 0.95);
        }
    } else {
        // Solid blocks are opaque
        baseColor.a = 1.0;
    }
    
    vec3 texColor = baseColor.rgb;

    // Strengthen AO curve
    float aoStrength = pow(vAO, 2.0); // Make dark areas darker

    // --- DUAL CHANNEL LIGHTING ---
    
    // 1. Sky Light (Sun)
    // Masks the directional light. If SkyLight is 0 (Cave), Sun is blocked.
    float skyFactor = vSkyLight;
    
    // 2. Block Light (Torches/Lava)
    // Additive light source. Warm color.
    vec3 torchColor = vec3(1.0, 0.8, 0.6);
    vec3 localLight = torchColor * vBlockLight;

    // Ambient component (modulated by AO and Sky Light)
    // Keep a tiny minimum ambient (0.05) so caves aren't 100% pitch black if unlit
    // Bumped to 0.2 based on user feedback "nothing visible in dark places"
    vec3 ambient = dirLight.ambient * texColor * aoStrength * max(skyFactor, 0.2);
    
    // Diffuse component
    float NdotL = max(dot(N, L), 0.0);
    
    // Underwater Lighting: Wrapped diffuse (scattering)
    if (isCameraUnderwater) {
        NdotL = NdotL * 0.5 + 0.5; 
    }

    // Use waterNormal for water blocks, original N for others
    vec3 lightingNormal = (vBlockDescriptor == BD_WATER) ? waterNormal : N;
    float NdotL_Water = max(dot(lightingNormal, L), 0.0);
    if (isCameraUnderwater) NdotL_Water = NdotL_Water * 0.5 + 0.5;

    // Apply Sky Factor to Diffuse (Sun)
    vec3 diffuse = dirLight.diffuse * texColor * NdotL_Water * aoStrength * skyFactor;
    
    // Specular component (Blinn-Phong)
    vec3 specular = vec3(0.0);
    if (NdotL_Water > 0.0) {
        vec3 H = normalize(L + V);
        float NdotH = max(dot(lightingNormal, H), 0.0);
        float specPower = pow(NdotH, uMaterialShininess);
        // Apply Sky Factor to Specular (Sun)
        specular = dirLight.specular * uMaterialSpecular * specPower * aoStrength * skyFactor;
    }

    // Attenuate light underwater
    if (isCameraUnderwater) {
        diffuse *= vec3(0.4, 0.7, 0.9); // Blue-green filter
        specular *= 0.0; // No specular underwater
    }
    
    // Combine components (Ambient + Sun + Local + Specular)
    // Local light is added on top
    vec3 finalColor = ambient + diffuse + specular + (localLight * texColor * aoStrength);
    

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
        if (vWorldPos.y > waterLevel + 0.05 && vBlockDescriptor != BD_WATER) {
             // Mix in more fog color to hide the "crisp" outside world
             finalColor = mix(finalColor, waterFogColor, 0.9);
        }
    }

    // M4: Biome Visualization (Debug)
    if (uShowBiomes == 1) {
        // Visualize primary biome (reuse calculation from above)
        // Use the same Voronoi-based selection as texture rendering
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
