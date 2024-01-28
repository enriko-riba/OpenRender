#version 460
#extension GL_ARB_bindless_texture : require

#define MAX_LIGHTS 4

//	light types
#define DIR_LIGHT			0
#define POINT_LIGHT			1
#define SPOT_LIGHT			2
#define NO_COLOR			vec4(0);			// for no light we must return 0 in alpha channel otherwise the transparent pixels would loose its transparency

uniform int uTotalLights;
uniform int outlinedBlockId;

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

 struct Material {   
    vec3 diffuse;
    vec3 emissive;
    vec3 specular;
    float shininess;
};
layout(std430, binding = 1) readonly buffer ssbo_materials {
    Material mat[];
};
struct BlockState {   
    uint index;
//    uint blockDirection;
//    uint blockType;
    uint packedBytes;
};
layout(std430, binding = 2) readonly buffer ssbo_blocks {
    BlockState blocks[];
};

in vec3 vertexNormal;                   //  interpolated normal
in vec2 texCoord;                       //  uv texture coordinates
in vec3 fragPos;
flat in uint materialIndex;
flat in uint textureIndex;
flat in uint blockId;

out vec4 outputColor;

float getFogFactor(float d)
{
    //  FarPlane = 450f;
    const float FogMax = 447.0;
    const float FogMin = 400.0;

    if (d>=FogMax) return 1;
    if (d<=FogMin) return 0;

    return 1 - (FogMax - d) / (FogMax - FogMin);
}

void main()
{ 
    Material mat = mat[materialIndex];
    sampler2D tex = bindlessTextures[textureIndex];
    //vec3 N = normalize(cross(dFdx(fragPos), dFdy(fragPos)));
    vec3 N = vertexNormal;
    vec3 L = -normalize(dirLight.position);

    vec4 texDiffuse = vec4(texture(tex, texCoord));    
    vec4 texColor = texDiffuse * vec4(mat.diffuse, 1);

    float lambert = clamp(dot(N, L), 0, 1);
    vec3 Ac = dirLight.ambient;
    vec3 Dc = dirLight.diffuse * lambert;
    vec3 Sc = vec3(0);

    if(lambert > 0)
    {
        // blinn-phong
        vec3 V = normalize(cameraPos - fragPos);
        vec3 H = normalize(L + V);
        float specular = clamp(dot(H, N), 0, 1);
        float exponent = pow(2, mat.shininess * 2.0) + 2;       
        Sc = clamp(pow(specular, exponent) * mat.shininess * dirLight.specular * mat.specular, 0, 1);
    }

    outputColor = clamp(vec4(mat.emissive.rgb + (Sc + Ac + Dc), 1) * texColor, 0, 1);
    
    BlockState block = blocks[blockId];
    if(uint(outlinedBlockId) == block.index)
	{		
        sampler2D outlineSampler = bindlessTextures[0];
        vec4 texOutline = texture(outlineSampler, texCoord);
        outputColor = vec4(mix(outputColor, texOutline, texOutline.a).rgb, outputColor.a);
    }
    
    // b1 is first byte, b2 is second byte, b3 is third byte, b4 is fourth byte
//    uint b1 = (block.reserved & 0xff);
//    uint b2 = (block.reserved & 0xff00) >> 8;
//    uint b3 = (block.reserved & 0xff0000) >> 16;
//    uint b4 = (block.reserved & (0xff << 24)) >> 24;
//    if(b2 != 0)
//    {
//        outputColor = vec4(1, 0, 0, 1);
//    }


    // fog
    vec4 fog = vec4(0.40f, 0.40f, 0.42f, 1.0f);
    float d = distance(fragPos, cameraPos);
    float factor = getFogFactor(d);
    outputColor = mix(outputColor, fog, factor);
}