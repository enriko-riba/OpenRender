#version 460
#extension GL_ARB_bindless_texture : require

uniform vec3 tint;
uniform vec4 sourceFrame;   //  in NDC x,y = start, z,w = width,height

struct Textures {
    sampler2D diffuse;
    sampler2D detail;
    sampler2D normal;
    sampler2D specular;
    sampler2D bump;
    sampler2D t6;
    sampler2D t7;
    sampler2D t8;
};
layout(std140, binding = 3) uniform textures {
    Textures tex;
};

in vec2 texUV;
out vec4 outputColor;

void main()
{
    vec2 textureUV = texUV;
    if(sourceFrame.length() > 0.0)
    {
        textureUV = sourceFrame.xy + mix(vec2(0), sourceFrame.zw, texUV);
    }
    
    outputColor = texture(tex.diffuse, textureUV);   
    outputColor.rgb *= tint;
}