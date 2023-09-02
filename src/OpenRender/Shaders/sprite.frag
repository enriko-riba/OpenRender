#version 330 core
uniform sampler2D texture_diffuse;
uniform vec3 tint;
uniform vec4 sourceFrame;   //  in NDC x,y = start, z,w = width,height

in vec2 texUV;
out vec4 outputColor;

void main()
{
    vec2 textureUV = texUV;
    if(sourceFrame.length() > 0.0)
    {
        textureUV = sourceFrame.xy + mix(vec2(0), sourceFrame.zw, texUV);
    }
    outputColor = texture(texture_diffuse, textureUV);
    outputColor.rgb *= tint;
}