#version 330 core

uniform mat4 model;
layout (std140) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};
layout (location = 0) in vec3 aPosition;

void main(void)
{
    gl_Position = projection * view * model * vec4(aPosition, 1.0);
}