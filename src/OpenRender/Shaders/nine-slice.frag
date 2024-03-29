#version 460
#extension GL_ARB_bindless_texture : require

uniform vec3 tint;
uniform int leftWidth;
uniform int topHeight;
uniform int rightWidth;
uniform int bottomHeight;
uniform int width;  // Total width of the texture (in pixels)
uniform int height; // Total height of the texture (in pixels)

uniform int targetWidth;    // Total sprite width (in pixels)
uniform int targetHeight;   // Total sprite height (in pixels)

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
    // Calculate texture coordinates based on the nine-slice parameters
    vec2 texCoord = texUV;

    // Calculate the UV positions of the inner corner points
    float leftX = float(leftWidth) / float(targetWidth);
    float bottomY = float(topHeight) / float(targetHeight);
    float rightX = 1.0 - float(rightWidth) / float(targetWidth);
    float topY = 1.0 - float(bottomHeight) / float(targetHeight);

    // Calculate the scaling factors for x and y directions
    float scaleX = float(targetWidth) / float(width);
    float scaleY = float(targetHeight) / float(height);

    if(texCoord.x <= leftX)
    {
        texCoord.x *= scaleX;
    }
    else if(texCoord.x >= rightX)
    {
        texCoord.x = 1.0 - (1.0 - texCoord.x) * scaleX;
    }
    else
    {
        // Normalize texCoord.x within the source range
        float normalizedX = (texCoord.x - leftX) / (rightX - leftX);
        float targetMin = float(leftWidth) / float(width);
        float targetMax = 1.0 - float(rightWidth) / float(width);
        texCoord.x = mix(targetMin, targetMax, normalizedX);
    }

    if(texCoord.y <= bottomY)
    {
        texCoord.y *= scaleY;
    }
    else if(texCoord.y >= topY)
    {
        texCoord.y = 1.0 - (1.0 - texCoord.y) * scaleY;
    }
    else
    {
        // Normalize texCoord.y within the source range
        float normalizedY = (texCoord.y - bottomY) / (topY-bottomY);
        float targetMin = float(bottomHeight) / float(height);
        float targetMax = 1.0 - float(topHeight) / float(height);
        texCoord.y = mix(targetMin, targetMax, normalizedY);
    }

    if(sourceFrame.length() > 0.0)
    {
        texCoord = sourceFrame.xy + mix(vec2(0), sourceFrame.zw, texCoord);
    }

    outputColor = texture(tex.diffuse, texCoord);
    outputColor.rgb *= tint;
}