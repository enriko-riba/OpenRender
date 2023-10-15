#version 460

//uniform mat4 model;

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
};
layout(std140, binding = 2) uniform material {
    Material mat;
};

layout(std140, binding = 0) readonly buffer ssbo_transform
{
    mat4 Data[];

} models;

uniform int uHasDiffuseTexture;         //  should the diffuse color be sampled from texture_diffuse1
uniform float uDetailTextureFactor;     //  scale of detail texture that is blended with diffuse, if 0 detail sampling is not used

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec3 aColor;
layout (location = 3) in vec2 aTexCoord;

out vec3 vertexColor;
out vec3 vertexNormal;
out vec3 fragPos;
out vec2 texCoord;
out vec3 texCoordCube;

void main(void)
{
    mat4 model = models.Data[gl_DrawID];
    vertexColor = aColor;
    vertexNormal = normalize(model * vec4(aNormal, 0)).xyz;
    fragPos = (model * vec4(aPosition, 1.0)).xyz;
    texCoord = aTexCoord;
    texCoordCube = aPosition;
    gl_Position = projection * view * model * vec4(aPosition, 1.0);
}