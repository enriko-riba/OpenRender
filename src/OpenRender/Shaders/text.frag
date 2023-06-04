#version 330 core

in vec2 fragTexCoord;
out vec4 fragColor;

uniform sampler2D textSampler;
uniform vec3 textColor;

void main()
{
    vec4 texel = texture(textSampler, fragTexCoord);
//    vec4 sampled = vec4(1.0, 1.0, 1.0, texel.r);
//    fragColor = vec4(textColor * texel.rgb, texel.a) * sampled;
    fragColor = vec4(texel.rgb * textColor, texel.a);
}
