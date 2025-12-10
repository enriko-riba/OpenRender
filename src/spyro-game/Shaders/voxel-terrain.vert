// Phase 5.2: Compressed Vertex Format (8 bytes)
// Simple per-face rendering with proper UV tiling
#version 460
#extension GL_ARB_shader_draw_parameters : require

const vec3 FACE_NORMALS[7] = vec3[](
    vec3(1,0,0), vec3(-1,0,0), vec3(0,1,0), vec3(0,-1,0), vec3(0,0,1), vec3(0,0,-1), vec3(0,1,0)
);
const uint FACE_POS_X=0u, FACE_NEG_X=1u, FACE_POS_Y=2u, FACE_NEG_Y=3u, FACE_POS_Z=4u, FACE_NEG_Z=5u;

layout(std140,binding=0) uniform camera { mat4 view; mat4 projection; vec3 cameraPos; vec3 cameraDir; };
layout(std430,binding=13) readonly buffer ChunkInfo { int chunkInfo[]; };
uniform uint uWorldChunksXZ; uniform int uIsUnderwater; uniform mat4 uChunkTransform=mat4(1.0);
uniform int uIsCubeletPass; // 1 if rendering cubelets, 0 otherwise

layout(location=0) in uvec2 aPackedData;

out vec3 vWorldPos; out vec3 vNormal; out vec2 vTexCoord; out float vAO; out vec3 vViewDir; 
flat out uint vBlockDescriptor; flat out uint vBiomeId; flat out uint vIsEmissive;
flat out uint vFaceId;
out float vSkyLight; out float vBlockLight;

vec2 getBaseUV(uint c){
    if(c==0u) return vec2(0,0); 
    if(c==1u) return vec2(0,1); 
    if(c==2u) return vec2(1,1); 
    return vec2(1,0); 
}

// Minecraft-style AO: More pronounced shadows for better depth perception
// Values tuned for visible corner/edge darkening
float unpackAO(uint i){ 
    if(i==4u) return 1.0;   // No occlusion - full brightness
    if(i==3u) return 0.75;  // 1 occluder - slight shadow
    if(i==2u) return 0.55;  // 2 occluders - medium shadow
    if(i==1u) return 0.35;  // 3 occluders or both sides - deep shadow
    return 0.25;            // Fallback - very dark
}

void main(){
    uint p1=aPackedData.x; uint p2=aPackedData.y;
    uint lx=p1&0x1Fu; uint ly=(p1>>5)&0x1FFu; uint lz=(p1>>14)&0x1Fu; 
    uint face=(p1>>19)&0x7u; uint aoIdx=(p1>>22)&0x7u; uint corner=(p1>>25)&0x3u;
    uint offsetSeed=(p1>>27)&0x1Fu;
    
    // Unpack p2: bits 0-9 = blockId, bits 10-17 = light, bits 18-25 = biomeId, bit 26 = emissive
    uint blockId = p2 & 0x3FFu;
    vBlockDescriptor = blockId;
    uint lightByte = (p2 >> 10) & 0xFFu;
    vBiomeId = (p2 >> 18) & 0xFFu;
    vIsEmissive = (p2 >> 26) & 1u;
    vFaceId = face;
    
    vSkyLight = float(lightByte & 0xF) / 15.0;
    vBlockLight = float((lightByte >> 4) & 0xF) / 15.0;

    int chunkIdx=chunkInfo[gl_DrawIDARB];
    int cx=chunkIdx%int(uWorldChunksXZ); 
    int cz=chunkIdx/int(uWorldChunksXZ);
    
    vec3 localPos=vec3(float(lx),float(ly),float(lz));

    // Apply random offset for billboards (face 6)
    if (face == 6u) {
        // offsetSeed layout (5 bits):
        // - Bits 0-3: Random variation seed (0-15)
        // - Bit 4: "Is top of stack" flag (1 = top, 0 = not top)
        uint randomBits = offsetSeed & 0xFu;
        bool isTopOfStack = (offsetSeed & 0x10u) != 0u;
        
        // Map 4-bit seed to [-0.2, 0.2] range for X/Z offset
        float hash = float(randomBits) / 15.0;
        float ox = (fract(hash * 12.9898) - 0.5) * 0.4;
        float oz = (fract(hash * 78.233) - 0.5) * 0.4;
        localPos.x += ox;
        localPos.z += oz;

        // Apply random height variation (0% to 33% reduction) by shrinking top vertices
        // ONLY apply to the topmost block of a stack to avoid gaps in stacked vegetation
        // Corners 1 and 2 are at the top (UV.y = 1), corners 0 and 3 are at the bottom
        if (isTopOfStack && (corner == 1u || corner == 2u)) {
            float heightReduction = fract(hash * 43.719) * 0.33;
            // Top vertices: move down by heightReduction (shrinking the billboard)
            localPos.y -= heightReduction;
        }
    }

    // Shrink vertices for Cubelet pass (1/10th size)
    if (uIsCubeletPass == 1) {
        // Reconstruct block origin from vertex position
        // We know lx, ly, lz are integers.
        // We need to know if this vertex is at min or max of the block.
        // We can deduce this from the face and corner, but simpler:
        // The vertex is either at x or x+1.
        // If we assume the block is at floor(localPos), then localPos - floor(localPos) is 0 or 1.
        // Center of block is floor(localPos) + 0.5.
        // Vector from center to vertex is localPos - (floor(localPos) + 0.5).
        // This vector is +/- 0.5.
        // We want to scale this vector by 0.1 (to get size 0.1) or 0.2 (to get size 0.2).
        // User asked for 1/10th size. So scale factor is 0.1.
        
        vec3 blockOrigin = floor(localPos);
        // Handle edge case where vertex is exactly on integer boundary (it always is)
        // But if localPos is x+1, floor gives x+1. We want x.
        // Wait, if localPos is 16, floor is 16. But block is 15.
        // We need to know which side of the vertex we are on.
        // Actually, we can use the normal!
        // If normal.x > 0, we are on +X face.
        // But we are shrinking the CUBE, not just moving faces.
        
        // Let's use the fact that we are shrinking towards the center of the voxel.
        // We need the center of the voxel this vertex belongs to.
        // A vertex is shared by 8 voxels (potentially).
        // But here, we are rendering a specific voxel's face.
        // The vertex position passed in 'lx, ly, lz' was calculated as 'x + ox'.
        // So it is the corner of the voxel we are rendering.
        // We need to recover 'x, y, z' of the voxel.
        // We can't easily recover 'x' from 'x+ox' without knowing 'ox'.
        // But we know 'face' and 'corner'.
        // We can reconstruct 'ox, oy, oz' from 'face' and 'corner' using the same table as CPU.
        // But we don't have the table in GLSL.
        
        // Alternative: We can infer the offset direction from the vertex position relative to the block center?
        // No, we don't know the block center.
        
        // Let's hardcode the table or logic.
        // Or simpler:
        // We can pass the offset in the vertex data? No space.
        
        // Let's implement the table logic.
        // Face 0 (+X): (1,0,0), (1,1,0), (1,1,1), (1,0,1) -> All have x=1 (relative to block)
        // Face 1 (-X): (0,0,1), (0,1,1), (0,1,0), (0,0,0) -> All have x=0
        // Face 2 (+Y): (0,1,0), (0,1,1), (1,1,1), (1,1,0) -> All have y=1
        // Face 3 (-Y): (0,0,1), (0,0,0), (1,0,0), (1,0,1) -> All have y=0
        // Face 4 (+Z): (1,0,1), (1,1,1), (0,1,1), (0,0,1) -> All have z=1
        // Face 5 (-Z): (0,0,0), (0,1,0), (1,1,0), (1,0,0) -> All have z=0
        
        // So for the primary axis of the face, we know the offset (0 or 1).
        // For the other axes?
        // Face 0 (+X): y,z vary.
        // Corner 0: (1,0,0) -> y=0, z=0
        // Corner 1: (1,1,0) -> y=1, z=0
        // Corner 2: (1,1,1) -> y=1, z=1
        // Corner 3: (1,0,1) -> y=0, z=1
        // This matches UVs: (0,0), (0,1), (1,1), (1,0).
        // So UV.x corresponds to one axis, UV.y to another?
        // getBaseUV(0)=(0,0), (1)=(0,1), (2)=(1,1), (3)=(1,0).
        
        // Let's use a helper function to get relative pos from face/corner.
        vec3 relPos = vec3(0.0);
        if (face == 0u) { relPos = vec3(1, (corner==1u||corner==2u)?1:0, (corner==2u||corner==3u)?1:0); }
        else if (face == 1u) { relPos = vec3(0, (corner==1u||corner==2u)?1:0, (corner==0u||corner==1u)?1:0); } // Note: corner mapping might differ
        else if (face == 2u) { relPos = vec3((corner==2u||corner==3u)?1:0, 1, (corner==1u||corner==2u)?1:0); } // +Y
        else if (face == 3u) { relPos = vec3((corner==2u||corner==3u)?1:0, 0, (corner==0u||corner==3u)?1:0); } // -Y
        else if (face == 4u) { relPos = vec3((corner==0u||corner==1u)?1:0, (corner==1u||corner==2u)?1:0, 1); } // +Z
        else if (face == 5u) { relPos = vec3((corner==2u||corner==3u)?1:0, (corner==1u||corner==2u)?1:0, 0); } // -Z
       
        
        // Correct logic:
        if (face == 0u) relPos = vec3(1, (corner==1u||corner==2u)?1:0, (corner==2u||corner==3u)?1:0);
        else if (face == 1u) relPos = vec3(0, (corner==1u||corner==2u)?1:0, (corner==0u||corner==1u)?1:0);
        else if (face == 2u) relPos = vec3((corner==2u||corner==3u)?1:0, 1, (corner==1u||corner==2u)?1:0);
        else if (face == 3u) relPos = vec3((corner==2u||corner==3u)?1:0, 0, (corner==0u||corner==3u)?1:0);
        else if (face == 4u) relPos = vec3((corner==0u||corner==1u)?1:0, (corner==1u||corner==2u)?1:0, 1);
        else if (face == 5u) relPos = vec3((corner==2u||corner==3u)?1:0, (corner==1u||corner==2u)?1:0, 0);
        
        // Now we have the relative position (0 or 1) for this vertex within the block.
        // The block origin is localPos - relPos.
        vec3 origin = localPos - relPos;
        
        // We want to shrink the cube to 1/5th size (0.2).
        // Center is origin + 0.5.
        // New pos = Center + (relPos - 0.5) * 0.2.
        //         = origin + 0.5 + (relPos - 0.5) * 0.2
        //         = origin + 0.5 + relPos * 0.2 - 0.05
        //         = origin + 0.45 + relPos * 0.2
        
        localPos = origin + vec3(0.45) + relPos * 0.2;
    }

    // ==================== WATER SURFACE DISPLACEMENT ====================
    // Water blocks need special handling to avoid z-fighting and visual artifacts.
    //
    // PROBLEM: Water top faces at Y=N would z-fight with blocks placed at Y=N.
    // SOLUTION: Lower the water surface by 0.15 blocks.
    //
    // COMPLICATION: If we lower the top face, the side faces need adjustment too,
    // otherwise they would stick above the lowered surface.
    //
    // ADDITIONAL COMPLICATION: For stacked water (multiple water blocks vertically),
    // we can't just lower ALL side face top vertices - that would create gaps/overlaps
    // at each water block boundary when viewed through glass.
    //
    // SOLUTION: The mesh builder sets a "topmost water" flag (bit 0 of offsetSeed)
    // for water blocks that have no water above them. Only these blocks get the
    // side face vertex adjustment.
    //
    // offsetSeed bit layout for WATER blocks (blockId == 1):
    //   - Bit 0: "Is topmost water" flag (1 = no water above this block)
    //   - Bits 1-4: Reserved
    //
    // See ChunkMeshBuilder.PackVertexPosition() for full bit layout documentation.
    // =====================================================================
    if (blockId == 1u) {
        bool isTopmostWater = (offsetSeed & 1u) != 0u;
        
        if (face == FACE_POS_Y) {
            // Top face (+Y): Always lower all vertices by 0.15
            // This prevents z-fighting with blocks placed at the same Y level
            localPos.y -= 0.15;
        } else if (face != FACE_NEG_Y && isTopmostWater) {
            // Side faces (+X, -X, +Z, -Z) of TOPMOST water only:
            // Lower the top vertices (corners 1 and 2) to match the lowered surface.
            // 
            // Why only topmost? Stacked water blocks would have overlapping/gapped
            // side faces at each boundary if we lowered all of them. The mesh builder
            // only emits side faces for water touching air anyway, so only the topmost
            // water's side faces are visible at the water column edge.
            //
            // Corner mapping for side faces (from FaceCornerOffsets):
            //   Face 0 (+X): corners 1,2 have y=1 (top)
            //   Face 1 (-X): corners 1,2 have y=1 (top)
            //   Face 4 (+Z): corners 1,2 have y=1 (top)
            //   Face 5 (-Z): corners 1,2 have y=1 (top)
            if (corner == 1u || corner == 2u) {
                localPos.y -= 0.15;
            }
        }
        // Bottom face (-Y): No adjustment needed
    }

    vec3 worldPos=vec3(float(cx*16),0.0,float(cz*16))+localPos;
    
    vec4 finalPos=uChunkTransform*vec4(worldPos,1.0);
    vWorldPos=finalPos.xyz;
    
    vec3 normal=FACE_NORMALS[face];
    vNormal=mat3(uChunkTransform)*normal;
    
    // Simple UVs for 1×1 faces
    vTexCoord=getBaseUV(corner);
    
    vAO=unpackAO(aoIdx);
    vViewDir=normalize(cameraPos-vWorldPos);
    gl_Position=projection*view*finalPos;
}
