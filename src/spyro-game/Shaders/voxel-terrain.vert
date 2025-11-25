// Phase 5.2: Compressed Vertex Format (8 bytes)
// Input: Compacted vertices from compute-compact.comp
// Output: Transformed vertices with lighting data for fragment shader

#version 460
#extension GL_ARB_shader_draw_parameters : require

// ============================================================================
// Constants
// ============================================================================

// Face normal lookup table (6 axis-aligned directions)
const vec3 FACE_NORMALS[6] = vec3[](
    vec3(1, 0, 0),   // 0: +X (right)
    vec3(-1, 0, 0),  // 1: -X (left)
    vec3(0, 1, 0),   // 2: +Y (top)
    vec3(0, -1, 0),  // 3: -Y (bottom)
    vec3(0, 0, 1),   // 4: +Z (front)
    vec3(0, 0, -1)   // 5: -Z (back)
);

// Face indices
const uint FACE_POS_X = 0u;
const uint FACE_NEG_X = 1u;
const uint FACE_POS_Y = 2u;
const uint FACE_NEG_Y = 3u;
const uint FACE_POS_Z = 4u;
const uint FACE_NEG_Z = 5u;

// Texture atlas layout: 3 columns x 1 row (Simplified)
const float ATLAS_COLS = 3.0;
const float ATLAS_ROWS = 1.0;
const vec2 TILE_SIZE = vec2(1.0 / ATLAS_COLS, 1.0 / ATLAS_ROWS);

// ============================================================================
// Uniforms & Buffers
// ============================================================================

// Camera matrices
layout(std140, binding = 0) uniform camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

// Chunk Info Buffer (maps gl_DrawID -> ChunkIndex)
layout(std430, binding = 13) readonly buffer ChunkInfo {
    int chunkInfo[];
};

uniform uint uWorldChunksXZ;
uniform int uIsUnderwater; // To flip UVs for water

// Per-chunk transform (for now, identity - chunks are in world space)
uniform mat4 uChunkTransform = mat4(1.0);

// ============================================================================
// Vertex Input (Compressed)
// ============================================================================

// Phase 5.2 optimized layout: 2 uints (8 bytes)
// Uint 0: Pos(X:5, Y:9, Z:5) | Face(3) | AO(3) | Corner(2)
// Uint 1: BlockType(8) | Padding(24)
layout(location = 0) in uvec2 aPackedData;

// ============================================================================
// Vertex Output (to fragment shader)
// ============================================================================

out vec3 vWorldPos;          // World-space position
out vec3 vNormal;            // World-space normal (derived from face index)
out vec2 vTexCoord;          // Texture coordinates
out float vAO;               // Ambient occlusion
out vec3 vViewDir;           // Direction to camera
flat out uint vBlockDescriptor;  // Block descriptor for texture/geology layer selection

// ============================================================================
// Helper Functions
// ============================================================================

vec2 getBaseUV(uint corner) {
    if (corner == 0u) return vec2(0, 1);
    if (corner == 1u) return vec2(0, 0);
    if (corner == 2u) return vec2(1, 0);
    if (corner == 3u) return vec2(1, 1);
    return vec2(0, 0);
}

vec2 flipV(vec2 uv) { return vec2(uv.x, 1.0 - uv.y); }

ivec2 faceToTile(uint face) {
    if (face == FACE_POS_Y) return ivec2(0, 0); // Top
    if (face == FACE_NEG_Y) return ivec2(1, 0); // Bottom
    return ivec2(2, 0); // Side (Front, Back, Left, Right)
}

vec2 getAtlasUV(uint face, uint corner) {
    vec2 uv = getBaseUV(corner);
    ivec2 tile = faceToTile(face);
    vec2 offset = vec2(float(tile.x) / ATLAS_COLS, float(tile.y) / ATLAS_ROWS);
    vec2 finalUv = offset + uv * TILE_SIZE;
    return flipV(finalUv);
}

float unpackAO(uint aoIdx) {
    if (aoIdx == 4u) return 1.0;
    if (aoIdx == 3u) return 0.8;
    if (aoIdx == 2u) return 0.6;
    if (aoIdx == 1u) return 0.4;
    return 0.33;
}

// ============================================================================
// Main Shader
// ============================================================================

void main() {
    // Unpack data
    uint packed1 = aPackedData.x;
    uint packed2 = aPackedData.y;
    
    uint lx = packed1 & 0x1Fu;
    uint ly = (packed1 >> 5) & 0x1FFu;
    uint lz = (packed1 >> 14) & 0x1Fu;
    uint face = (packed1 >> 19) & 0x7u;
    uint aoIdx = (packed1 >> 22) & 0x7u;
    uint corner = (packed1 >> 25) & 0x3u;
    
    vBlockDescriptor = packed2 & 0xFFu;
    
    // Get Chunk Position
    int chunkIdx = chunkInfo[gl_DrawIDARB]; // Use ARB extension for compatibility
    
    // Calculate Chunk World Position
    // chunkIdx = z * width + x
    int chunkX = chunkIdx % int(uWorldChunksXZ);
    int chunkZ = chunkIdx / int(uWorldChunksXZ);
    
    // Assuming CHUNK_SIDE_SIZE is 16. We can pass it as uniform or hardcode.
    // It's hardcoded in compute shaders as 16.
    float chunkWorldX = float(chunkX * 16);
    float chunkWorldZ = float(chunkZ * 16);
    
    vec3 localPos = vec3(float(lx), float(ly), float(lz));
    vec3 worldPos = vec3(chunkWorldX, 0.0, chunkWorldZ) + localPos;
    
    // Transform to world space (uChunkTransform is usually identity)
    vec4 finalPos = uChunkTransform * vec4(worldPos, 1.0);
    vWorldPos = finalPos.xyz;
    
    // Normal
    vec3 normal = FACE_NORMALS[face];
    vNormal = mat3(uChunkTransform) * normal;
    
    // UVs
    bool isWater = (vBlockDescriptor == 1u); // BD_WATER = 1
    if (isWater) {
        vTexCoord = flipV(getBaseUV(corner));
    } else {
        vTexCoord = getAtlasUV(face, corner);
    }
    
    // AO
    vAO = unpackAO(aoIdx);
    
    // View Dir
    vViewDir = normalize(cameraPos - vWorldPos);
    
    // Position
    gl_Position = projection * view * finalPos;
}
