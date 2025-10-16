#version 460

uniform mat4 model;

layout (std140, binding = 0) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

layout (location = 0) in vec3 aPosition;
layout (location = 3) in vec2 aTexCoord;

uniform float uTime;

out vec3 vertexNormal;
out vec3 fragPos;
out vec2 texCoord;

const vec2 repetitionFactor = vec2(20000.0, 20000.0);

// Wave parameters
const float waveAmplitude = 0.3;  // Adjust the amplitude of the waves
const float waveFrequencyX = 1.0; // Adjust the frequency of the waves 
const float waveSpeedX = .005;    // Adjust the speed of the waves

void main(void)
{      
    texCoord = aTexCoord * repetitionFactor;
    vertexNormal = normalize((model * vec4(0, 1, 0, 0))).xyz;
    
    vec3 translatedPosition = aPosition;
    translatedPosition.y += waveAmplitude * (sin(uTime * waveFrequencyX /*+ aPosition.x * waveSpeedX*/) + 1.5);

    vec4 worldPosition = model * vec4(translatedPosition, 1.0);
    fragPos = worldPosition.xyz;  
    gl_Position = projection * view * worldPosition;
}