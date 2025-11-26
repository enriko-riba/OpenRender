// wireframe.frag - Debug Wireframe Fragment Shader
#version 460

uniform vec3 uColor;

out vec4 FragColor;

void main() {
    FragColor = vec4(uColor, 1.0);
}
