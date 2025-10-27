#version 460
#extension GL_ARB_bindless_texture : require

#define MAX_LIGHTS 4

// light types (kept for reference)
#define DIR_LIGHT   0
#define POINT_LIGHT 1
#define SPOT_LIGHT  2

uniform int uTotalLights;
uniform int outlinedBlockId;
uniform int useFog = 1;

layout (std140, binding = 0) uniform camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

struct Light {
    vec3 position;   // CONVENTION: this is the *light RAY direction* (from sun toward world)
    vec3 ambient;
    vec3 diffuse;
    vec3 specular;
    float falloff;
};
layout (std140, binding = 1) uniform light {
    Light dirLight;
};

layout (std430, binding = 0) readonly buffer ssbo_textures {
    sampler2D bindlessTextures[];
};

struct Material {
    vec3  diffuse;
    vec3  emissive;
    vec3  specular;
    float shininess;
};
layout (std430, binding = 1) readonly buffer ssbo_materials {
    Material materials[];
};

struct BlockState {
    uint index;
    uint packedBytes;  // low: direction (0..5), next: blockType, etc.
};
layout (std430, binding = 2) readonly buffer ssbo_blocks {
    BlockState blocks[];
};

in vec3 vertexNormal;       // interpolated normal (already transformed in VS)
in vec2 texCoord;           // uv
in vec3 fragPos;            // world position
flat in uint materialIndex; // per-instance material index
flat in uint textureIndex;  // per-instance texture index
flat in uint blockId;       // per-instance block ID (instance ID)

out vec4 outputColor;

const float FAR_CUSHION = 0.5;
const float FAR_PLANE = 430.0;

// Simple linear fog between FogMin and FogMax
float getFogFactor(float d)
{
    const float FogMax = FAR_PLANE - FAR_CUSHION;
    const float FogMin = FAR_PLANE * 0.75;
    return clamp(1.0 - (FogMax - d) / (FogMax - FogMin), 0, 1);
}

void main()
{
    float dCam = distance(fragPos, cameraPos);
    if (dCam > (FAR_PLANE - FAR_CUSHION)) {
        discard;
    }

    // Fetch material/texture
    Material m = materials[materialIndex];
    sampler2D tex = bindlessTextures[textureIndex];

    // Normalized inputs
    vec3 N = normalize(vertexNormal);

    // Our convention: dirLight.position is the LIGHT RAY direction (sun -> world).
    // For lighting, we need the vector from the fragment *toward the light* (toward sun).
    vec3 L = normalize(-dirLight.position);

    // Base texture & diffuse color
    vec4 texDiffuse = texture(tex, texCoord);
    vec4 texColor   = texDiffuse * vec4(m.diffuse, 1.0);

    // Lambert
    float lambert = max(dot(N, L), 0.0);
    vec3 Ac = dirLight.ambient;
    vec3 Dc = dirLight.diffuse * lambert;
    vec3 Sc = vec3(0.0);

    // Blinn-Phong specular
    if (lambert > 0.0)
    {
        vec3 V = normalize(cameraPos - fragPos);
        vec3 H = normalize(L + V);
        float spec = max(dot(H, N), 0.0);

        // A slightly snappier exponent mapping (feel free to tune)
        float exponent = pow(2.0, m.shininess * 2.0) + 2.0;
        Sc = pow(spec, exponent) * m.shininess * dirLight.specular * m.specular;
    }

    // Compose lighting
    vec3 lit = m.emissive + (Ac + Dc + Sc);
    vec4 base = vec4(clamp(lit, 0.0, 1.0), 1.0) * texColor;

    // Optional block outline overlay (bindlessTextures[0] assumed to be outline atlas)
    BlockState blk = blocks[blockId];
    if (uint(outlinedBlockId) == blk.index)
    {
        sampler2D outlineSampler = bindlessTextures[0];
        vec4 texOutline = texture(outlineSampler, texCoord);
        base = vec4(mix(base.rgb, texOutline.rgb, texOutline.a), base.a);
    }

    // Fog
    if(useFog > 0)
    {
        float d = distance(fragPos, cameraPos);
        float f = getFogFactor(d);
        outputColor = mix(base, vec4(dirLight.ambient, 1.0), f);
    }
    else{
        outputColor = base;
    }
}
