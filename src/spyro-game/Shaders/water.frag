#version 460
#extension GL_ARB_bindless_texture : require

// Uniforms
layout (std140, binding = 0) uniform camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

struct Light {
    vec3 position;
    vec3 ambient;
    vec3 diffuse;
    vec3 specular;
    float falloff;
};

layout(std140, binding = 1) uniform light {
    Light dirLight;
};

layout(std430, binding = 0) readonly buffer ssbo_textures {
    sampler2D bindlessTextures[];
};

uniform float uTime;

// Inputs from vertex shader
in vec3 vertexNormal;
in vec2 texCoord;
in vec3 fragPos;

// Output
out vec4 outputColor;

// =================================================================================
//  PROCEDURAL WATER LOGIC
// =================================================================================

struct Wave {
    float height;
    vec3 normal_displacement;
};

Wave gerstnerWave(vec2 pos, float steepness, float amplitude, float speed, float freq, vec2 dir) {
    float wave_arg = freq * dot(dir, pos) + speed * uTime;
    float s = sin(wave_arg);
    float c = cos(wave_arg);
    float wa = steepness * amplitude;
    Wave w;
    w.normal_displacement.x = wa * dir.x * c;
    w.height = amplitude * s;
    w.normal_displacement.z = wa * dir.y * c;
    return w;
}

vec3 getWaterNormalAndHeight(vec2 pos, out float totalHeight) {
    vec3 normal = vec3(0, 1, 0);
    totalHeight = 0.0;

    // Combine multiple waves for a more complex surface
    Wave wave1 = gerstnerWave(pos, 0.5, 0.05, 1.5, 15.0, vec2(1.0, 0.2));
    Wave wave2 = gerstnerWave(pos, 0.5, 0.025, 2.5, 30.0, vec2(0.8, -0.2));
    Wave wave3 = gerstnerWave(pos, 0.5, 0.0125, 3.5, 60.0, vec2(-0.2, 0.8));

    totalHeight = wave1.height + wave2.height + wave3.height;

    normal.y -= (wave1.normal_displacement.x + wave2.normal_displacement.x + wave3.normal_displacement.x);
    normal.x -= totalHeight * 0.5; // Scaling down normal displacement
    normal.z -= (wave1.normal_displacement.z + wave2.normal_displacement.z + wave3.normal_displacement.z);

    return normalize(normal);
}

void main() {
    vec2 pos = fragPos.xz;
    float totalHeight;
    vec3 N = getWaterNormalAndHeight(pos, totalHeight);
    vec3 V = normalize(cameraPos - fragPos);
    vec3 L = normalize(-dirLight.position);

    // --- SUN SPECULAR ---
    vec3 H = normalize(L + V);
    float specAngle = max(dot(H, N), 0.0);
    float specular = pow(specAngle, 256.0) * 1.5;
    vec3 sunSpecular = dirLight.specular * specular * vec3(1.0, 0.9, 0.7);

    // --- FOAM ---
    const int foamTextureIndex = 9;
    sampler2D texFoam = bindlessTextures[foamTextureIndex];
    vec2 foamTexCoord = texCoord * 0.25 + vec2(uTime * 0.05, uTime * 0.03);
    vec3 foamColor = texture(texFoam, foamTexCoord).rgb;

    // --- BASE WATER COLOR ---
    vec3 waterBaseColor = vec3(0.1, 0.2, 0.3);

    // --- COMBINE ---
    float foamAmount = 0.2; // A constant to control how much foam is visible
    vec3 finalColor = mix(waterBaseColor, foamColor, foamAmount);
    finalColor += sunSpecular;

    // --- ALPHA ---
    float fresnel = 0.02 + 0.98 * pow(1.0 - dot(V, N), 5.0);
    float alpha = mix(0.6, 1.0, fresnel);

    outputColor = vec4(finalColor, alpha);
}
