#version 460 core

uniform mat4 projection;
uniform mat4 view;

layout (location = 0) in vec3 aPosition;
out vec3 texCoordCube;

void main(void)
{
    texCoordCube = aPosition;
    vec4 pos = projection * mat4(mat3(view)) * vec4(aPosition, 1.0);
    gl_Position = pos.xyww;
}