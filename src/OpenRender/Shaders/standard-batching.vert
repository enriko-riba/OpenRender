#version 460
#extension GL_ARB_bindless_texture : require

layout (std140, binding = 0) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

layout(std140, binding = 0) readonly buffer ssbo_transform
{
    mat4 modelMatrices[];
};

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec3 aColor;
layout (location = 3) in vec2 aTexCoord;

out vec3 vertexColor;
out vec3 vertexNormal;
out vec3 fragPos;
out vec2 texCoord;
out vec3 texCoordCube;
flat out int drawID;

void main(void)
{
    mat4 model = modelMatrices[gl_DrawID];
    vertexColor = aColor;
    vertexNormal = mat3(transpose(inverse(model))) * aNormal;  
    fragPos = (model * vec4(aPosition, 1.0)).xyz;
    texCoord = aTexCoord;
    texCoordCube = aPosition;
    drawID = gl_DrawID;
    gl_Position = projection * view * model * vec4(aPosition, 1.0);
}