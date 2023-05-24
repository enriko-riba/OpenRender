#version 330 core

layout (std140) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

 struct Material {   
    vec3 diffuse;
    vec3 emissive;
    vec3 specular;
    float shininess;
};
layout(std140) uniform material {
    Material mat;
};

struct Light {    
    vec3 position;    
    vec3 ambient;
    vec3 diffuse;
    vec3 specular;
    float falloff;
};
layout(std140) uniform light {
    Light dirLight;
};

layout (location = 0) in vec3 aPosition;
out vec3 texCoordCube;

void main(void)
{
    texCoordCube = aPosition;
    vec4 pos = projection * mat4(mat3(view)) * vec4(aPosition, 1.0);
    gl_Position = pos.xyww;
}