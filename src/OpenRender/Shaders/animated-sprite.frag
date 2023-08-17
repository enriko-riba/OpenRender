#version 330 core
uniform sampler2D texture_diffuse;
uniform vec3 tint;
uniform vec4 uvInfo;

in vec2 texUV;
out vec4 outputColor;

void main()
{
    vec2 uv = uvInfo.xy + (texUV * uvInfo.zw);
    outputColor = texture(texture_diffuse, uv);
    outputColor.rgb *= tint;
}