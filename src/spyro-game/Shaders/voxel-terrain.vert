// Phase 5.2: Compressed Vertex Format (8 bytes)
// Greedy meshing UV fix: correct orientation for readable text from outside
#version 460
#extension GL_ARB_shader_draw_parameters : require

const vec3 FACE_NORMALS[6] = vec3[](
    vec3(1,0,0), vec3(-1,0,0), vec3(0,1,0), vec3(0,-1,0), vec3(0,0,1), vec3(0,0,-1)
);
const uint FACE_POS_X=0u, FACE_NEG_X=1u, FACE_POS_Y=2u, FACE_NEG_Y=3u, FACE_POS_Z=4u, FACE_NEG_Z=5u;

layout(std140,binding=0) uniform camera { mat4 view; mat4 projection; vec3 cameraPos; vec3 cameraDir; };
layout(std430,binding=13) readonly buffer ChunkInfo { int chunkInfo[]; };
uniform uint uWorldChunksXZ; uniform int uIsUnderwater; uniform mat4 uChunkTransform=mat4(1.0);

layout(location=0) in uvec2 aPackedData;

out vec3 vWorldPos; out vec3 vNormal; out vec2 vTexCoord; out float vAO; out vec3 vViewDir; flat out uint vBlockDescriptor;

// Base UV coordinates for corners (standard OpenGL bottom-left origin)
vec2 getBaseUV(uint c){ 
    if(c==0u) return vec2(0,0); 
    if(c==1u) return vec2(0,1); 
    if(c==2u) return vec2(1,1); 
    return vec2(1,0); 
}

float unpackAO(uint i){ 
    if(i==4u) return 1.0; 
    if(i==3u) return 0.8; 
    if(i==2u) return 0.6; 
    if(i==1u) return 0.4; 
    return 0.33; 
}

void main(){
    uint p1=aPackedData.x; uint p2=aPackedData.y;
    uint lx=p1&0x1Fu; uint ly=(p1>>5)&0x1FFu; uint lz=(p1>>14)&0x1Fu; 
    uint face=(p1>>19)&0x7u; uint aoIdx=(p1>>22)&0x7u; uint corner=(p1>>25)&0x3u;
    vBlockDescriptor=p2&0xFFu;
    
    // Extract extents (5 bits each at positions 8 and 13)
    uint extentX=(p2>>8)&0x1Fu;
    uint extentZ=(p2>>13)&0x1Fu;

    int chunkIdx=chunkInfo[gl_DrawIDARB]; 
    int cx=chunkIdx%int(uWorldChunksXZ); 
    int cz=chunkIdx/int(uWorldChunksXZ);
    
    vec3 localPos=vec3(float(lx),float(ly),float(lz));
    vec3 worldPos=vec3(float(cx*16),0.0,float(cz*16))+localPos;
    
    vec4 finalPos=uChunkTransform*vec4(worldPos,1.0);
    vWorldPos=finalPos.xyz;
    
    vec3 normal=FACE_NORMALS[face];
    vNormal=mat3(uChunkTransform)*normal;
    
    // UVs with extent-based tiling and correct orientation
    bool isWater=(vBlockDescriptor==1u);
    if(isWater){
        vTexCoord=getBaseUV(corner);
    } else {
        // Get base corner UV
        vec2 base=getBaseUV(corner);
        
        // Scale UVs by extents for tiling
        float scaleU, scaleV;
        if(face==FACE_POS_Y || face==FACE_NEG_Y){
            // Horizontal faces
            scaleU = float(extentX);
            scaleV = float(extentZ);
        } else if(face==FACE_POS_X || face==FACE_NEG_X){
            // X-facing sides
            scaleU = float(extentZ);
            scaleV = 1.0;
        } else {
            // Z-facing sides
            scaleU = float(extentX);
            scaleV = 1.0;
        }
        
        base.x *= scaleU;
        base.y *= scaleV;
        
        // Fix mirroring: flip U for faces where text is readable from INSIDE
        // These faces need flipping to be readable from OUTSIDE:
        // - NEG_X (-X): flip U
        // - POS_Z (+Z): flip U
        // - POS_Y (top): flip U
        // - NEG_Y (bottom): flip U
        if(face==FACE_NEG_X || face==FACE_POS_Z || face==FACE_POS_Y || face==FACE_NEG_Y){
            base.x = scaleU - base.x;
        }
        
        vTexCoord=base;
    }
    
    vAO=unpackAO(aoIdx);
    vViewDir=normalize(cameraPos-vWorldPos);
    gl_Position=projection*view*finalPos;
}
