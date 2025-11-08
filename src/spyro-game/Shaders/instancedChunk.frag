#version 460
#extension GL_ARB_bindless_texture : require

#define MAX_LIGHTS 4

// light types (kept for reference)
#define DIR_LIGHT   0
#define POINT_LIGHT 1
#define SPOT_LIGHT  2

uniform int uTotalLights;
uniform int outlinedBlockId;
uniform int chunkSize; // for mapping 3D voxel index -> 2D column index
uniform int useFog = 1;

// Fog UBO (shared across shaders)
// fogColor4.rgb = fog color, fogParams = (near, far, enabled, unused)
layout (std140, binding = 4) uniform fogBlock {
    vec4 fogColor4;
    vec4 fogParams;
};

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
    uint packedAO;
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
flat in float aoBrightness;
flat in int outlinedLocalIndex; // per-draw outlined local index (MDI/DrawData path)

out vec4 outputColor;

// Linear fog driven by UBO (falls back to legacy constants if UBO disabled)
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

void main()
{
    float dCam = distance(fragPos, cameraPos);
    float farD = (fogParams.z > 0.5) ? fogParams.y : (LEGACY_FAR_PLANE - LEGACY_FAR_CUSHION);
    if (dCam > farD) {
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
    base.rgb *= aoBrightness;

    // Optional block outline overlay (bindlessTextures[0] assumed to be outline atlas)
    // Prefer per-draw outlined index from VS; fall back to legacy uniform if negative
    int selectedLocalIndex = (outlinedLocalIndex >= 0) ? outlinedLocalIndex : outlinedBlockId;
    BlockState blk = blocks[blockId];
    // Compare by exact local 3D voxel index (vi) and skip air
    if (selectedLocalIndex >= 0 && selectedLocalIndex == int(blk.index) && materialIndex != 0u)
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
        vec3 fogColor = (fogParams.z > 0.5) ? fogColor4.rgb : dirLight.ambient;
        outputColor = mix(base, vec4(fogColor, 1.0), f);
    }
    else{
        outputColor = base;
    }
}
