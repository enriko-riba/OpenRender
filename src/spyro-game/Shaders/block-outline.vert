#version 460 core

layout(location = 0) in vec3 aPosition;

// Camera UBO (already bound by scene renderer)
layout(std140, binding = 0) uniform camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

uniform mat4 uBlockTransform;  // Transform for the picked block

void main()
{
    gl_Position = projection * view * uBlockTransform * vec4(aPosition, 1.0);
}
