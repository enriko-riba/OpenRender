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


uniform samplerCube texture_cubemap;
uniform float sunRadius;

in vec3 texCoordCube;
out vec4 FragColor;

const vec3 _SkyTint = vec3(0.2, 0.2, 0.91);
const vec3 SUN_COLOR = vec3(1, 1, 0.25);   
const float SUN_CONE = 0.35;

void main()
{ 
    vec3 L = normalize(dirLight.position); // the position is actually the light direction

    // Transform the sun's position to view space
    //vec4 sunViewSpace = projection * mat4(mat3(view)) * vec4(L, 0.0);

    // Calculate the screen space position of the sun circle
    //vec2 sunScreenSpace = ((sunViewSpace.xy / sunViewSpace.w) * 0.5) + 0.5;
    

    vec4 sunViewSpace = projection * view * vec4(L, 0.0);
    vec2 sunScreenSpace = ((sunViewSpace.xy / sunViewSpace.w) * 0.5) + 0.5;

    // Calculate the distance between the current fragment and the center position of the sun circle
    float distanceToSun = distance(texCoordCube.xy, sunScreenSpace.xy);

    // Conditionally apply sun color or sky color based on the distance
    vec3 tex = texture(texture_cubemap, texCoordCube).rgb;
    vec3 finalColor = (distanceToSun < SUN_CONE) ? SUN_COLOR : tex;
    

    // Calculate the dot product between the current fragment direction and the sun direction
//    float dotProduct = dot(texCoordCube, L);
//
//    // Calculate the angle between the current fragment direction and the sun direction
//    float angle = acos(dotProduct) / 3.14159265;
//
//    // Conditionally apply sun color or sky color based on the angle
//    vec3 tex = texture(texture_cubemap, texCoordCube).rgb;
//    vec3 finalColor = (angle < SUN_CONE) ? _SunColor : tex;

    // Render the final color
    FragColor = vec4(finalColor, 1.0);
}
