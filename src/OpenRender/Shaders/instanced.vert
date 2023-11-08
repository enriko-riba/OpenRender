#version 430 core

layout (std140, binding = 0) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec3 aColor;
layout (location = 3) in vec2 aTexCoord;
layout (location = 6) in mat4 instanceWorldMatrix;

out vec3 vertexColor;
out vec3 vertexNormal;
out vec3 fragPos;
out vec2 texCoord;
out vec3 texCoordCube;

void main(void)
{
    vertexColor = aColor;
    vertexNormal = normalize(instanceWorldMatrix * vec4(aNormal, 0)).xyz;
    fragPos = (instanceWorldMatrix * vec4(aPosition, 1.0)).xyz;
    texCoord = aTexCoord;
    texCoordCube = aPosition;
    gl_Position = projection * view * instanceWorldMatrix * vec4(aPosition, 1.0);
}