#version 460
#extension GL_ARB_bindless_texture : require

#define MAX_LIGHTS 4


//	light types
#define DIR_LIGHT			0
#define POINT_LIGHT			1
#define SPOT_LIGHT			2
#define NO_COLOR			vec4(0);			// for no light we must return 0 in alpha channel otherwise the transparent pixels would loose its transparency
uniform int uTotalLights;

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

 struct Material {   
    vec3 diffuse;
    vec3 emissive;
    vec3 specular;
    float shininess;
    float detailTextureScaleFactor;
    float detailTextureBlendFactor;
};
layout(std430, binding = 1) readonly buffer ssbo_material {
    Material mat[];
};

struct Textures {
    sampler2D diffuse;
    sampler2D detail;
    sampler2D normal;
    sampler2D specular;
    sampler2D bump;
    sampler2D t6;
    sampler2D t7;
    sampler2D t8;
};
layout(std430, binding = 2) readonly buffer ssbo_textures {
    Textures textures[];
};

in vec3 vertexColor;                    //  interpolated vertex color
in vec3 vertexNormal;                   //  interpolated normal
in vec2 texCoord;                       //  uv texture coordinates
in vec3 fragPos;
flat in int drawID;
out vec4 outputColor;

vec3 BumpMap(vec3 normal)
{
     vec3 posDX = dFdxFine(fragPos);  // choose dFdx (#version 420) or dFdxFine (#version 450) here
     vec3 posDY = dFdyFine (fragPos);
     vec3 r1 = cross (posDY, normal);
     vec3 r2 = cross (normal, posDX);
     float det = dot (posDX, r1);
     float Hll = texture(textures[drawID].bump, texCoord).x;    //-- height from bump map texture, tc=texture coordinates
     float Hlr = texture(textures[drawID].bump, texCoord + dFdx(texCoord.xy)).x;
     float Hul = texture(textures[drawID].bump, texCoord + dFdy(texCoord.xy)).x;
     vec3 surf_grad = sign(det) * ( (Hlr - Hll) * r1 + (Hul - Hll) * r2 );    
     const float bump_amt = 0.8;       // bump_amt = adjustable bump amount
     return normal * (1.0-bump_amt) + bump_amt * normalize(abs(det) * normal - surf_grad );  // bump normal    
}

void main()
{    
    outputColor = vec4(0);

    vec3 norm = normalize(vertexNormal);
    vec3 lightDir = normalize(dirLight.position);
    vec3 viewDir = normalize(cameraPos - fragPos);

    vec3 texDiffuse = vec3(1);
    vec3 texDetail = vec3(1);
    
    Material mat = mat[drawID];
    sampler2D texture_diffuse = textures[drawID].diffuse;
    sampler2D texture_detail = textures[drawID].detail;
    sampler2D texture_normal = textures[drawID].normal;

    texDiffuse = vec3(texture(texture_diffuse, texCoord));
    texDetail = vec3(texture(texture_detail, texCoord * mat.detailTextureScaleFactor));
    texDiffuse = mix(texDiffuse, texDetail, mat.detailTextureBlendFactor);

    vec3 texColor = texDiffuse * min(vertexColor + mat.diffuse, 1.0);   
    //norm = BumpMap(norm);
   
    vec3 Dc = dirLight.diffuse * clamp(dot(-lightDir, norm), 0.0, 1.0);
    vec3 Ac = dirLight.ambient;
    vec3 Sc = vec3(0);
    float NdotL = dot(norm, -lightDir);
    if(NdotL > 0)
    {
        vec3 R = normalize(2 * NdotL * norm + lightDir);     
        Sc = pow(clamp(dot(R, viewDir), 0.0, 1.0), mat.shininess) * mat.specular;
    }
    outputColor += vec4(mat.emissive.rgb + (Ac + Dc + Sc) * texColor, 1);
}