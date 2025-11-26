// terrain-greedy-meshing.glsl
// Greedy meshing support (quad packing/unpacking)

#ifndef TERRAIN_GREEDY_MESHING_GLSL
#define TERRAIN_GREEDY_MESHING_GLSL

// Requires: terrain-common.glsl

// ============================================================================
// Merged Quad Data Structures
// ============================================================================

// Pack merged quad data (8 bytes total)
// Uint 0: Start position (X:5, Y:9, Z:5) | Face:3 | ExtentX:5 | ExtentZ:5
// Uint 1: BlockType:8 | AO corners (4×3 bits = 12) | padding:12
uint packQuad1(ivec3 startPos, uint face, uint extentX, uint extentZ) {
    uint x = uint(startPos.x) & 0x1Fu;      // 5 bits
    uint y = uint(startPos.y) & 0x1FFu;     // 9 bits
    uint z = uint(startPos.z) & 0x1Fu;      // 5 bits
    uint f = face & 0x7u;                    // 3 bits
    uint ex = extentX & 0x1Fu;               // 5 bits
    uint ez = extentZ & 0x1Fu;               // 5 bits
    
    return x | (y << 5) | (z << 14) | (f << 19) | (ex << 22) | (ez << 27);
}

uint packQuad2(uint blockType, uint ao0, uint ao1, uint ao2, uint ao3) {
    uint bt = blockType & 0xFFu;         // 8 bits
    uint a0 = ao0 & 0x7u;                // 3 bits
    uint a1 = ao1 & 0x7u;                // 3 bits
    uint a2 = ao2 & 0x7u;                // 3 bits
    uint a3 = ao3 & 0x7u;                // 3 bits
    
    return bt | (a0 << 8) | (a1 << 11) | (a2 << 14) | (a3 << 17);
}

// Note: Unpack functions removed due to Nvidia Cg compiler issues with 'out' parameters
// They will be re-added in Phase GM-2 when needed by compute-compact.comp
// For now, unpacking can be done inline where needed

#endif // TERRAIN_GREEDY_MESHING_GLSL
