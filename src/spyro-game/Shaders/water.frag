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
uniform float uDayFactor;

// Inputs from vertex shader
in vec3 vertexNormal;
in vec2 texCoord;
in vec3 fragPos;

// Output
out vec4 outputColor;

// Fog UBO (shared across shaders)
// fogColor4.rgb = fog color, fogParams = (near, far, enabled, unused)
layout (std140, binding = 4) uniform fogBlock {
    vec4 fogColor4;
    vec4 fogParams;
};

// --- Fog params to match terrain fog ---
const float LEGACY_FAR_CUSHION = 0.5;
const float LEGACY_FAR_PLANE = 430.0;
float getFogFactor(float d)
{
    float enabled = fogParams.z;
    float nearD = enabled > 0.5 ? fogParams.x : (LEGACY_FAR_PLANE * 0.75);
    float farD  = enabled > 0.5 ? fogParams.y : (LEGACY_FAR_PLANE - LEGACY_FAR_CUSHION);
    float denom = max(farD - nearD, 0.0001);
    return clamp((d - nearD) / denom, 0.0, 1.0);
}

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
    vec3 foamTextureColor = texture(texFoam, foamTexCoord).rgb;
    vec3 foamColor = foamTextureColor * dirLight.ambient; // Modulate foam by ambient light

    // --- BASE WATER COLOR ---
    vec3 deepWaterColor = vec3(0.1, 0.15, 0.25);
    vec3 waterBaseColor = deepWaterColor * dirLight.ambient; // Make water color day/night aware

    // --- COMBINE ---
    float foamAmount = 0.2; // A constant to control how much foam is visible
    vec3 finalColor = mix(waterBaseColor, foamColor, foamAmount);
    finalColor += sunSpecular;

    // --- ALPHA ---
    // Make transparency dependent on day/night cycle
    float dayFactor = uDayFactor;
    float minAlpha = mix(0.95, 0.6, dayFactor); // Night is more opaque, day is more transparent

    float fresnel = 0.02 + 0.98 * pow(1.0 - dot(V, N), 5.0);
    float alpha = mix(minAlpha, 1.0, fresnel);

    // Increase opacity with distance
    float dist = distance(fragPos, cameraPos);
    float distAlpha = clamp((dist - 20.0) / 100.0, 0.0, 1.0); // Fade to opaque between 20 and 120 units
    alpha = max(alpha, distAlpha);

    // --- FOG (match terrain fog) ---
    if (cameraPos.y < 35.0) {
        // Underwater fog
        float dist = distance(fragPos, cameraPos);
        float fogEnd = 30.0;
        float fog = clamp(dist / fogEnd, 0.0, 1.0);
        vec3 waterFogColor = vec3(0.0, 0.2, 0.4);
        vec3 fogged = mix(finalColor, waterFogColor, fog);
        fogged *= vec3(0.5, 0.7, 1.0); // Tint
        outputColor = vec4(fogged, alpha);
    } else {
        // Normal atmospheric fog
        float dCam = distance(fragPos, cameraPos);
        float fog = getFogFactor(dCam);
        vec3 fogColor = (fogParams.z > 0.5) ? fogColor4.rgb : dirLight.ambient;
        vec3 fogged = mix(finalColor, fogColor, fog);
        outputColor = vec4(fogged, alpha);
    }
}
