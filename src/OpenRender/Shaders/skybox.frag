#version 460 core

out vec4 FragColor;
in vec3 texCoordCube;

uniform samplerCube texture_cubemap;

void main()
{    
    FragColor = texture(texture_cubemap, texCoordCube);
}