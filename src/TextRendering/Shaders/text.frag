#version 330 core
uniform sampler2D fontAtlasSampler;
uniform vec3 textColor;

in vec2 texUV;
out vec4 outputColor;
void main()
{
    vec4 texel = texture(fontAtlasSampler, texUV);
    outputColor  = vec4(texel.rgb * textColor, texel.a);
}