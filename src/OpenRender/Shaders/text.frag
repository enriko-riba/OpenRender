#version 330 core
uniform sampler2D fontAtlasSampler;
uniform vec3 textColor;

in vec2 texUV;
out vec4 outputColor;
void main()
{
    vec4 texel = texture(fontAtlasSampler, texUV);
    vec4 sampled = vec4(1.0, 1.0, 1.0, texel.b);
    outputColor  = vec4(texel.rgb * textColor, texel.a);
    //outputColor = vec4(texel.b * textColor, texel.a);
}