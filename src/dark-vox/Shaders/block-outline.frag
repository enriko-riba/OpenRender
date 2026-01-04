#version 460 core

out vec4 FragColor;

uniform vec3 uOutlineColor;  // Color of the outline (default: yellow)

void main()
{
    FragColor = vec4(uOutlineColor, 1.0);
}
