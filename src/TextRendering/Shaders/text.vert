#version 330 core
layout (location = 0) in vec2 vertex; 
layout (location = 3) in vec2 aTexCoord;

uniform mat4 projection;

out vec2 texUV;
void main()
{
    gl_Position = projection * vec4(vertex, 0.0, 1.0);
    texUV = aTexCoord;
}