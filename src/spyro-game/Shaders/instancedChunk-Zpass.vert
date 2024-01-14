#version 460

uniform mat4 model;
uniform int chunkSize;

layout (std140, binding = 0) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

struct BlockState {   
    uint index;
    uint blockType;
    uint blockDirection;
    uint reserved;
};
layout(std430, binding = 2) readonly buffer ssbo_blocks {
    BlockState blocks[];
};

layout (location = 0) in vec3 aPosition;
void main(void)
{  
    BlockState block = blocks[gl_InstanceID];
    
    // Calculate block position based on block index
    uint x = block.index % chunkSize;
    uint z = (block.index / chunkSize) % chunkSize;
    uint y = block.index / (chunkSize * chunkSize);

    vec3 translatedPosition = aPosition + vec3(x, y, z);
    vec4 worldPosition = model * vec4(translatedPosition, 1.0);

    gl_Position = projection * view * worldPosition;
}