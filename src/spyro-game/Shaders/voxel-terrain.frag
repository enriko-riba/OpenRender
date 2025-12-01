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
vec4 sampleBlockTexture(uint blockId, vec2 localUV, vec3 normal) {
    // Determine face type from normal
    // Top (+Y): use first 50 pixels
    // Bottom (-Y): use middle 50 pixels  
    // Sides (±X, ±Z): use last 50 pixels
    
    float uOffset;
    if (normal.y > 0.5) {
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

// Biome debug colors (for visualization when ShowBiomes is enabled)
vec3 getBiomeDebugColor(uint biomeId) {
    // 10 distinct colors for 10 biomes
    vec3 colors[10] = vec3[10](
        vec3(0.0, 0.3, 0.8),   // 0: Ocean (deep blue)
        vec3(0.9, 0.8, 0.5),   // 1: Beach (sand)
        vec3(0.4, 0.7, 0.3),   // 2: Plains (green)
        vec3(0.7, 0.6, 0.3),   // 3: Savanna (tan)
        vec3(0.9, 0.7, 0.4),   // 4: Desert (yellow)
        vec3(0.2, 0.5, 0.2),   // 5: Rainforest (dark green)
        vec3(0.3, 0.5, 0.4),   // 6: Taiga (blue-green)
        vec3(0.6, 0.7, 0.8),   // 7: Tundra (light blue-gray)
        vec3(0.5, 0.4, 0.3),   // 8: Highlands (brown)
        vec3(0.95, 0.95, 0.98) // 9: Alpine (white)
    );
    
    if (biomeId < 10u) {
        return colors[biomeId];
    }
    return vec3(1.0, 0.0, 1.0); // Magenta for unknown
}

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
    vec3 L = normalize(-dirLight.position);
    vec3 V = normalize(vViewDir);

    // Extract BlockId (lower 10 bits)
    uint blockId = vBlockDescriptor & 0x3FFu;
    
    // Check if this is water
    bool isWater = blockId == 1u;
    bool isTopFace = N.y > 0.5;

    // Sample block texture
    vec4 baseColor = sampleBlockTexture(blockId, vTexCoord, N);

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
        
        vec3 deepWaterColor = vec3(0.1, 0.15, 0.25);
        vec3 waterBaseColor = deepWaterColor * dirLight.ambient * 3.0;
        
        vec3 waterFogColor = isCameraUnderwater 
            ? dirLight.ambient * vec3(0.2, 0.5, 0.8)
            : dirLight.ambient * vec3(0.10, 0.18, 0.30);
        
        float brightness = dot(dirLight.ambient, vec3(0.333));
        float nightOpacityBoost = 1.0 - smoothstep(0.05, 0.2, brightness);
        
        if (isTopFace) {
            float alphaFade = clamp((dist - 2.0) / 20.0, 0.0, 1.0);
            if (isCameraUnderwater) {
                alphaFade = clamp((dist - 5.0) / 30.0, 0.0, 1.0);
                baseColor.a = mix(0.85, 1.0, alphaFade);
            } else {
                baseColor.a = mix(0.88, 1.0, alphaFade);
            }
            
            baseColor.rgb = mix(baseColor.rgb, waterBaseColor, 0.5);
            baseColor.a = clamp(baseColor.a + nightOpacityBoost * 0.3, 0.0, 1.0);
            
            if (!isCameraUnderwater) {
                vec3 viewDir = normalize(vViewDir);
                float fresnel = pow(1.0 - clamp(abs(dot(waterNormal, viewDir)), 0.0, 1.0), 3.0);
                vec3 surfaceTint = mix(vec3(0.05, 0.15, 0.25), vec3(0.35, 0.6, 0.9), fresnel);
                baseColor.rgb = mix(baseColor.rgb, surfaceTint, 0.65);
                float minAlpha = mix(0.70, 0.92, fresnel);
                baseColor.a = max(baseColor.a, minAlpha);
            }
            
            if (!isCameraUnderwater) {
                vec3 H = normalize(L + V);
                float specAngle = max(dot(H, waterNormal), 0.0);
                float specular = pow(specAngle, 256.0) * 1.5;
                vec3 sunSpecular = dirLight.specular * specular * vec3(1.0, 0.9, 0.7);
                baseColor.rgb += sunSpecular;
            }
            
            if (!isCameraUnderwater) {
                float absorption = exp(-dist * 0.10);
                baseColor.rgb = mix(waterFogColor, baseColor.rgb, absorption);
                float opacityBoost = mix(0.60, 0.98, 1.0 - absorption);
                baseColor.a = max(baseColor.a, opacityBoost);
            }
        } else {
            // Side faces (underwater walls)
            baseColor.rgb = mix(waterBaseColor, baseColor.rgb, 0.3);
            float sideAlpha = clamp((dist - 2.0) / 15.0, 0.5, 0.95);
            baseColor.a = sideAlpha;
            
            if (isCameraUnderwater) {
                float causticsBase = sin(vWorldPos.x * 4.0 + vWorldPos.z * 4.0 + uTime * 2.0) * 0.5 + 0.5;
                float caustics2 = sin(vWorldPos.x * 3.2 - vWorldPos.z * 2.8 + uTime * 1.5) * 0.5 + 0.5;
                float caustics = causticsBase * caustics2;
                float depthFade = clamp((waterLevel - vWorldPos.y) / 30.0, 0.0, 1.0);
                baseColor.rgb += caustics * 0.15 * (1.0 - depthFade);
            }
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
    
    // Ambient occlusion
    float ao = vAO;
    
    // Blinn-Phong specular (reduced for terrain)
    vec3 H = normalize(L + V);
    float NdotH = max(dot(finalNormal, H), 0.0);
    float specular = pow(NdotH, uMaterialShininess) * 0.3;
    
    if (isWater) {
        specular = 0.0; // Water has its own specular
    }

    // Combine lighting
    vec3 ambient = dirLight.ambient * ao;
    vec3 diffuseColor = dirLight.diffuse * diffuse * ao;
    vec3 specularColor = dirLight.specular * specular * uMaterialSpecular;
    
    vec3 finalColor = baseColor.rgb * (ambient + diffuseColor) + specularColor;

    // Underwater fog
    if (isCameraUnderwater && !isWater) {
        float dist = length(vWorldPos - cameraPos);
        float fogDensity = 0.03;
        float fogFactor = 1.0 - exp(-dist * fogDensity);
        vec3 underwaterFogColor = dirLight.ambient * vec3(0.15, 0.35, 0.5);
        finalColor = mix(finalColor, underwaterFogColor, fogFactor);
    }

    FragColor = vec4(finalColor, baseColor.a);
}
