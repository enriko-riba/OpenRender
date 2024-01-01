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
};
layout(std430, binding = 2) readonly buffer ssbo_blocks {
    BlockState blocks[];
};

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 3) in vec2 aTexCoord;

out vec3 vertexNormal;
out vec3 fragPos;
out vec2 texCoord;

flat out uint materialIndex;
flat out uint textureIndex;

const float spacing = 1.0;

mat3 getRotationMatrix(uint blockDirection) {
    mat3 rotationMatrix;
    
    if (blockDirection == 1) {
        // 1 = East
        rotationMatrix = mat3(  
            0, 0, -1,
            0, 1, 0,
            1, 0, 0
        );
    } else if (blockDirection == 2) {
        //  2 = North
        rotationMatrix = mat3(
            -1, 0, 0,
            0, 1, 0,
            0, 0, -1
        );
    } else if (blockDirection == 3) {
        // 3 = West
        rotationMatrix = mat3(
            0, 0, 1,
            0, 1, 0,
            -1, 0, 0
        );
    } else if (blockDirection == 4) {
        // 4 = Top
        rotationMatrix = mat3(
            1, 0, 0,
            0, 0, 1,
            0, -1, 0
        );
    } else if (blockDirection == 5) {
        // 5 = Bottom
        rotationMatrix = mat3(
            1, 0, 0,
            0, -1, 0,
            0, 0, 1
        );
    } else {
        // No rotation (south)
        rotationMatrix = mat3(1.0);
    }
    return rotationMatrix;
}


void main(void)
{   
    BlockState block = blocks[gl_InstanceID];    

    materialIndex = block.blockType;
    textureIndex = block.blockType;

    // Apply rotation to both position and normal
    mat3 rotationMatrix = getRotationMatrix(block.blockDirection);
    vec3 rotatedPosition = rotationMatrix * aPosition;
    vec3 rotatedNormal = rotationMatrix * aNormal;

    // Calculate block position based on block index
    uint x = block.index % chunkSize;
    uint z = (block.index / chunkSize) % chunkSize;
    uint y = block.index / (chunkSize * chunkSize);
    vec3 translatedPosition = rotatedPosition + vec3(x * spacing, y * spacing, z * spacing);
    vec4 worldPosition = model * vec4(translatedPosition, 1.0);
    
    fragPos = worldPosition.xyz;    
    vertexNormal = (model * vec4(rotatedNormal, 0)).xyz;
    texCoord = aTexCoord;
    gl_Position = projection * view * worldPosition;
}