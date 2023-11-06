#version 460

uniform mat4 projection;
uniform mat4 model;

layout (location = 0) in vec2 vertex; 
layout (location = 3) in vec2 aTexCoord;
out vec2 texUV;

void main()
{
    gl_Position = projection * model * vec4(vertex, 0, 1);
    texUV = aTexCoord;
}