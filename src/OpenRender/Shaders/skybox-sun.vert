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


out vec3 texCoordCube;  //  skybox cube map texture coordinates
out vec2 sunCoord;      //  sun coordinates

void main(void)
{
    mat4 invPV = inverse(projection * mat4(mat3(view)));    

    //----------------------------------------------
    // project sun position
    //----------------------------------------------

    // calculate sun direction in world space
    vec3 sunDirectionWorld = normalize(dirLight.position); //  'position' is actually the light direction
    
    // transform sun direction to clip space    
    vec4 sunClipPos = projection * view * vec4(sunDirectionWorld, 0.0);

    // calculate normalized device coordinates for the sun
    sunCoord = sunClipPos.xy / sunClipPos.w;

    // remap NDC coordinates to [0, 1]
    sunCoord = 0.5 * sunCoord + 0.5;
    //----------------------------------------------

    vec2 pos  = vec2( (gl_VertexID & 2)>>1, 1 - (gl_VertexID & 1)) * 2.0 - 1.0;
    vec4 front= invPV * vec4(pos, -1.0, 1.0);
    vec4 back = invPV * vec4(pos,  1.0, 1.0);

    texCoordCube = back.xyz / back.w - front.xyz / front.w;
    gl_Position = vec4(pos, 1.0, 1.0);    
}