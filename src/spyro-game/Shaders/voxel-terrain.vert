// voxel-terrain.vert - Phase 4: Voxel Terrain Vertex Shader
// Purpose: Transform and pass through voxel terrain vertices from Phase 3 compaction
// Version: 2.0 (Phase 5.2: Optimized - derives normals from face index)
//
// Phase 5.2: Normals no longer stored per-vertex (saved 12 bytes = 33% reduction!)
// Instead, we derive the normal from the face index (0-5) using a lookup table.
// This works perfectly for axis-aligned voxel faces which have trivial normals.
//
// Input: Compacted vertices from compute-compact.comp
// Output: Transformed vertices with lighting data for fragment shader

#version 460

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

// ============================================================================
// Uniforms
// ============================================================================

// Camera matrices
layout(std140, binding = 0) uniform camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

// Per-chunk transform (for now, identity - chunks are in world space)
uniform mat4 uChunkTransform = mat4(1.0);

// ============================================================================
// Vertex Input (from compacted buffer)
// ============================================================================

// Phase 5.2 optimized layout: position(12) + texCoord(8) + ao(4) + faceIndex(4) = 28 bytes
layout(location = 0) in vec3 aPosition;   // Voxel world position
layout(location = 1) in vec2 aTexCoord;   // Texture UV
layout(location = 2) in float aAO;        // Ambient occlusion [0,1]
// Note: aFaceIndex is read as uint via VertexAttribIFormat, so it gets the raw bits from the buffer
layout(location = 3) in uint aFaceIndex;  // Face direction index [0-5] + blockType [8-15]

// ============================================================================
// Vertex Output (to fragment shader)
// ============================================================================

out vec3 vWorldPos;          // World-space position
out vec3 vNormal;            // World-space normal (derived from face index)
out vec2 vTexCoord;          // Texture coordinates
out float vAO;               // Ambient occlusion
out vec3 vViewDir;           // Direction to camera
flat out uint vBlockDescriptor;  // Block descriptor for texture/geology layer selection (renamed from vBlockType)

// ============================================================================
// Main Shader
// ============================================================================

void main() {
    // Transform position to world space (chunk space = world space for now)
    vec4 worldPos = uChunkTransform * vec4(aPosition, 1.0);
    vWorldPos = worldPos.xyz;

    // Extract face index and block descriptor
    uint faceIndex = aFaceIndex & 0x7u; // 3 bits for face (0-5)
    vBlockDescriptor = (aFaceIndex >> 8) & 0xFFu; // 8 bits for block descriptor (geology layer)

    // Derive normal from face index (Phase 5.2 optimization!)
    // This replaces 12 bytes of stored normal data with a simple array lookup
    vec3 normal = FACE_NORMALS[faceIndex];
    
    // Transform normal to world space
    // Note: For uniform scaling, we can use mat3(uChunkTransform)
    // For non-uniform scaling, use inverse transpose
    vNormal = mat3(uChunkTransform) * normal;
    
    // Pass through texture coordinates and AO
    vTexCoord = aTexCoord;
    vAO = aAO;
    
    // Calculate view direction for specular lighting
    vViewDir = normalize(cameraPos - vWorldPos);
    
    // Transform to clip space
    gl_Position = projection * view * worldPos;
}
