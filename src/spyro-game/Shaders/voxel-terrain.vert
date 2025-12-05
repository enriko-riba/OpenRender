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

    // Fix z-fighting for water: displace top surface downwards
    if (blockId == 1u && face == FACE_POS_Y) {
        localPos.y -= 0.15;
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
