#version 460 core

layout (std140, binding = 0) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};
struct Light {    
    vec3 position;    
    vec3 ambient;
    vec3 diffuse;
    vec3 specular;
    float falloff;
};
layout(std140, binding = 1) uniform light {
    Light dirLight;
};

in vec3 texCoordCube;
in vec2 sunCoord; // Added input for sun coordinates

uniform samplerCube texture_cubemap;
uniform float sunRadius;

out vec4 FragColor;

float GetSunMask(float sunViewDot, float sunRadius)
{
    float stepRadius = 1 - sunRadius * sunRadius;
    return step(stepRadius, sunViewDot);
}

void main()
{
    // Calculate distance to the sun position
    float distanceToSun = distance(texCoordCube.xy, sunCoord);
        
    if (distanceToSun < sunRadius) {        
        FragColor = vec4(1.0, 1.0, 0, 1.0);
    } else {
        FragColor = texture(texture_cubemap, texCoordCube);
    } 
}