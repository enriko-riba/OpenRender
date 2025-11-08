#version 460
#ifdef GL_ARB_shader_draw_parameters
#extension GL_ARB_shader_draw_parameters : enable
#endif

uniform mat4 model;
uniform int chunkSize;
uniform int useDrawData; // 0 = legacy uniforms, 1 = use ssbo_drawData + gl_DrawID

layout (std140, binding = 0) uniform camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

struct BlockState {
    uint index;
    uint packedBytes;
    uint packedAO;
};
layout(std430, binding = 2) buffer ssbo_blocks {
    BlockState blocks[];
};

struct DrawData {
    mat4 model;
    vec4 chunkPos;          // xyz: chunk world position, w unused
    int blocksBase;
    int chunkSize;
    int outlinedLocalIndex; // selected block local index for this draw (-1 if none)
    int enabled;            // 1 = draw, 0 = skip
};
layout(std430, binding = 5) buffer ssbo_drawData {
    DrawData draws[];
};

// Optional GPU frustum culling: a per-draw visibility mask written by compute.
// If provided and useDrawData != 0, we check it by draw index (gl_BaseInstance).
layout(std430, binding = 10) readonly buffer ssbo_drawVisible {
    int drawVisible[];
};

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 3) in vec2 aTexCoord;

out vec3 vertexNormal;
out vec3 fragPos;
out vec2 texCoord;

flat out uint materialIndex;
flat out uint textureIndex;
flat out uint blockId; // index into blocks SSBO (global for MDI, local for legacy)
flat out float aoBrightness;
flat out int outlinedLocalIndex; // per-draw outlined local index (or -1)


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
    // Select per-draw data using stable draw identifier
    int drawIndex = 0;
    if (useDrawData != 0) {
#ifdef GL_ARB_shader_draw_parameters
        // Prefer gl_DrawID when available (stable across drivers)
        drawIndex = int(gl_DrawID);
#else
        drawIndex = int(gl_BaseInstance);
#endif
    }
    DrawData dd = draws[drawIndex];

    int instanceIndex = (useDrawData != 0) ? (dd.blocksBase + gl_InstanceID) : gl_InstanceID;
    blockId = uint(instanceIndex);
    BlockState block = blocks[instanceIndex];
    uint blockDirection = (block.packedBytes & 0xffu);
    uint blockType = (block.packedBytes & 0xff00u) >> 8;
    uint packedAO = block.packedAO;

    materialIndex = blockType;
    textureIndex = blockType;
    
    // Calculate block position based on block index
    int cs = (useDrawData != 0) ? dd.chunkSize : chunkSize;
    uint x = block.index % uint(cs);
    uint z = (block.index / uint(cs)) % uint(cs);
    uint y = block.index / (uint(cs) * uint(cs));
        
    // Apply rotation to both position and normal
    mat3 rotationMatrix = getRotationMatrix(blockDirection);
    vec3 rotatedPosition = rotationMatrix * aPosition;
    vec3 rotatedNormal = rotationMatrix * aNormal;
   
    vec3 translatedPosition = rotatedPosition + vec3(x, y, z);
    if (useDrawData != 0) {
        translatedPosition += dd.chunkPos.xyz;
    }
    mat4 mdl = (useDrawData != 0) ? mat4(1.0) : model;
    vec4 worldPosition = mdl * vec4(translatedPosition, 1.0);

    // Disable compute-driven per-draw culling in VS to rule out buffer size/driver issues
    if (useDrawData != 0 && (dd.enabled == 0)) {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        vertexNormal = vec3(0.0);
        fragPos = vec3(0.0);
        texCoord = aTexCoord;
        outlinedLocalIndex = dd.outlinedLocalIndex;
        return;
    }
    vertexNormal = normalize((mdl * vec4(rotatedNormal, 0))).xyz;
    fragPos = worldPosition.xyz;  
    texCoord = aTexCoord;
    outlinedLocalIndex = dd.outlinedLocalIndex;

    int faceIndex = 0;
    if (abs(rotatedNormal.x) > 0.5)
        faceIndex = rotatedNormal.x > 0.0 ? 0 : 1;
    else if (abs(rotatedNormal.y) > 0.5)
        faceIndex = rotatedNormal.y > 0.0 ? 2 : 3;
    else
        faceIndex = rotatedNormal.z > 0.0 ? 4 : 5;

    float ao = float((packedAO >> (faceIndex * 4)) & 0xFu);
    aoBrightness = max(0.0, (15.0 - ao) / 15.0);

    gl_Position = projection * view * worldPosition;
}
