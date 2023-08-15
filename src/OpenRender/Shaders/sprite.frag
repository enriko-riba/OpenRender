#version 330 core
uniform sampler2D texture_diffuse;
uniform vec3 tint;

in vec2 texUV;
out vec4 outputColor;

void main()
{
    outputColor = texture(texture_diffuse, texUV);
    outputColor.rgb *= tint;
}