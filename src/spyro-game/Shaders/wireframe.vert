// wireframe.vert - Debug Wireframe Vertex Shader
#version 460

layout(std140, binding = 0) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

layout(location = 0) in vec3 aPosition;

uniform mat4 uModel;

void main() {
    gl_Position = projection * view * uModel * vec4(aPosition, 1.0);
}
