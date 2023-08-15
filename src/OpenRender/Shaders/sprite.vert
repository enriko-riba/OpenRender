#version 330 core
layout (location = 0) in vec2 vertex; 
layout (location = 3) in vec2 aTexCoord;

uniform mat4 projection;
uniform mat4 model;

out vec2 texUV;

void main()
{
    gl_Position = projection * model * vec4(vertex, 0, 1);
    texUV = aTexCoord;
}