// voxel-terrain.frag - Phase 4: Voxel Terrain Fragment Shader
// Purpose: Simple lighting and texturing for voxel terrain
// Version: 3.0 (Simplified - GPU picking removed)
//
// Implements basic Blinn-Phong lighting with ambient occlusion

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

// Material properties (simple for now - Phase 5 will add per-block textures)
uniform vec3 uMaterialDiffuse = vec3(0.6, 0.8, 0.4);   // Grass-like color
uniform vec3 uMaterialSpecular = vec3(0.2, 0.2, 0.2);
uniform float uMaterialShininess = 16.0;

// Texture samplers
uniform sampler2D uTexGrass;   // Slot 0
uniform sampler2D uTexWater;   // Slot 1
uniform sampler2D uTexDirt;    // Slot 2
uniform sampler2D uTexRock;    // Slot 3
uniform sampler2D uTexSand;    // Slot 4
uniform sampler2D uTexBedRock; // Slot 5

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
const uint BLOCK_WATER_LEVEL = 1u;
const uint BLOCK_ROCK = 2u;
const uint BLOCK_SAND = 3u;
const uint BLOCK_DIRT = 4u;
const uint BLOCK_GRASS_DIRT = 5u;
const uint BLOCK_BEDROCK = 8u;

// ============================================================================
// Main Shader
// ============================================================================

void main() {
    // Normalize interpolated vectors
    vec3 N = normalize(vNormal);
    vec3 L = normalize(-dirLight.position);  // Direction TO light (negate direction)
    vec3 V = normalize(vViewDir);

    // Sample texture based on block type
    vec4 baseColor;
    
    if (vBlockType == BLOCK_WATER_LEVEL) {
        baseColor = texture(uTexWater, vTexCoord);
        baseColor.a *= 0.8; // Translucency
    } else if (vBlockType == BLOCK_GRASS_DIRT) {
        baseColor = texture(uTexGrass, vTexCoord);
        baseColor.a = 1.0;
    } else if (vBlockType == BLOCK_DIRT) {
        baseColor = texture(uTexDirt, vTexCoord);
        baseColor.a = 1.0;
    } else if (vBlockType == BLOCK_ROCK) {
        baseColor = texture(uTexRock, vTexCoord);
        baseColor.a = 1.0;
    } else if (vBlockType == BLOCK_SAND) {
        baseColor = texture(uTexSand, vTexCoord);
        baseColor.a = 1.0;
    } else if (vBlockType == BLOCK_BEDROCK) {
        baseColor = texture(uTexBedRock, vTexCoord);
        baseColor.a = 1.0;
    } else {
        // Fallback
        baseColor = texture(uTexGrass, vTexCoord);
        baseColor.a = 1.0;
        baseColor.rgb = vec3(1.0, 0.0, 1.0); // Magenta for error
    }
    
    vec3 texColor = baseColor.rgb;

    // Strengthen AO curve
    float aoStrength = pow(vAO, 2.0); // Make dark areas darker

    // Ambient component (modulated by AO)
    vec3 ambient = dirLight.ambient * texColor * aoStrength;
    
    // Diffuse component (Lambertian) - Apply AO here too for stronger effect!
    float NdotL = max(dot(N, L), 0.0);
    vec3 diffuse = dirLight.diffuse * texColor * NdotL * aoStrength;
    
    // Specular component (Blinn-Phong) - Apply AO to specular too (occluded areas shouldn't shine)
    vec3 specular = vec3(0.0);
    if (NdotL > 0.0) {
        vec3 H = normalize(L + V);
        float NdotH = max(dot(N, H), 0.0);
        float specPower = pow(NdotH, uMaterialShininess);
        specular = dirLight.specular * uMaterialSpecular * specPower * aoStrength;
    }
    
    // Combine components
    vec3 finalColor = ambient + diffuse + specular;
    
    // Apply gamma correction (approximate sRGB)
    finalColor = pow(finalColor, vec3(1.0 / 2.2));
    
    // Underwater Fog
    if (cameraPos.y < 35.0) { // Hardcoded WATER_LEVEL_Y
        float dist = length(vWorldPos - cameraPos);
        float fogStart = 0.0;
        float fogEnd = 60.0; // Visibility limit
        float fogFactor = clamp((dist - fogStart) / (fogEnd - fogStart), 0.0, 1.0);
        
        vec3 waterColor = vec3(0.0, 0.3, 0.5); // Deep blue
        finalColor = mix(finalColor, waterColor, fogFactor);
        
        // Blue tint
        finalColor *= vec3(0.6, 0.8, 1.0);
    }

    // Output with opacity
    FragColor = vec4(finalColor, baseColor.a);
}
