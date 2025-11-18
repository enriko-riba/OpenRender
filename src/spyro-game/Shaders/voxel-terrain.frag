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

// Texture atlas (grass-dirt.png: 2x3 grid of voxel faces)
// Row 1: Top, Bottom, Front
// Row 2: Left, Right, Back
uniform sampler2D uBlockTexture;

// ============================================================================
// Fragment Input (from vertex shader)
// ============================================================================

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vTexCoord;
in float vAO;
in vec3 vViewDir;

// ============================================================================
// Fragment Output
// ============================================================================

layout(location = 0) out vec4 FragColor;

// ============================================================================
// Main Shader
// ============================================================================

void main() {
    // Normalize interpolated vectors
    vec3 N = normalize(vNormal);
    vec3 L = normalize(-dirLight.position);  // Direction TO light (negate direction)
    vec3 V = normalize(vViewDir);
    
    // Sample texture atlas
    vec3 texColor = texture(uBlockTexture, vTexCoord).rgb;
    
    // Ambient component (modulated by AO)
    vec3 ambient = dirLight.ambient * texColor * vAO;
    
    // Diffuse component (Lambertian)
    float NdotL = max(dot(N, L), 0.0);
    vec3 diffuse = dirLight.diffuse * texColor * NdotL;
    
    // Specular component (Blinn-Phong)
    vec3 specular = vec3(0.0);
    if (NdotL > 0.0) {
        vec3 H = normalize(L + V);
        float NdotH = max(dot(N, H), 0.0);
        float specPower = pow(NdotH, uMaterialShininess);
        specular = dirLight.specular * uMaterialSpecular * specPower;
    }
    
    // Combine components
    vec3 finalColor = ambient + diffuse + specular;
    
    // Apply gamma correction (approximate sRGB)
    finalColor = pow(finalColor, vec3(1.0 / 2.2));
    
    // Output with full opacity
    FragColor = vec4(finalColor, 1.0);
}
