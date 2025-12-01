# Minecraft-Style Terrain Generation Architecture

## Overview

This document describes the **correct** Minecraft-style terrain generation pipeline. The key insight is that terrain generation happens in **distinct phases**, each building on the previous.

## The Pipeline (4 Phases)

```
Phase 1: BASE TERRAIN (Solid/Water/Air)
    Input:  World coordinates
    Output: 3D density field → Stone, Water, or Air
    
Phase 2: BIOME ASSIGNMENT  
    Input:  Climate noise (C/T/H/E/PV) at world coords
    Output: BiomeId per 4×4 cell (one per column, 16 cells/chunk)
    
Phase 3: SURFACE SHAPING
    Input:  Base terrain + Biome grid
    Output: Surface height adjusted by biome attributes
    
Phase 4: BLOCK ASSIGNMENT (Decoration)
    Input:  Shaped terrain + Biome
    Output: BlockId (byte) from palette - Grass, Dirt, Sand, Snow, etc.
```

---

## Phase 1: Base Terrain (Density Field)

Generate a 3D density field that determines: **Solid**, **Water**, or **Air**.

```csharp
for each (x, y, z) in chunk:
    float density = GetDensityAt(x, y, z);  // 3D noise + height bias
    
    if (density > 0)
        block = SOLID;           // Will become Stone initially
    else if (y <= WATER_LEVEL)
        block = WATER;
    else
        block = AIR;
```

**Density Function:**
- Combines 3D noise with Y-based gradient
- Higher Y = lower density (more likely to be air)
- Continentalness affects the "squash factor" (ocean areas pushed down)

**Output:** `byte[16×384×16]` with values: 0=Air, 1=Water, 2=Solid

---

## Phase 2: Biome Assignment

Assign biomes using climate parameters. Biomes are stored at **coarse resolution** (4×4 horizontal cells, 1 biome per full Y column).

```csharp
// Biome grid: 4×4 horizontally, 1 per Y column (no vertical variation for now)
// Total: 4 × 4 = 16 biome cells per chunk
// Future: could expand to 4×4×24 for cave biomes (384 cells)
BiomeId[4, 4] biomeGrid;  // Or BiomeId[16] flattened

for (int cellX = 0; cellX < 4; cellX++)
for (int cellZ = 0; cellZ < 4; cellZ++)
{
    // Sample at cell center (surface level)
    int worldX = chunkX * 16 + cellX * 4 + 2;
    int worldZ = chunkZ * 16 + cellZ * 4 + 2;
    int worldY = GetEstimatedSurfaceHeight(worldX, worldZ);  // Sample at surface
    
    // Get climate at this point
    float C = GetContinentalness(worldX, worldZ);
    float T = GetTemperature(worldX, worldY, worldZ);
    float H = GetHumidity(worldX, worldZ);
    float E = GetErosion(worldX, worldZ);
    float PV = GetPeaksValleys(worldX, worldZ);
    
    // Select biome from climate
    biomeGrid[cellX, cellZ] = SelectBiome(C, T, H, E, PV);
}
```

**Biome Selection Rules:**

Biome selection uses thresholds that are **configurable via `TerrainConfig`**:

| Parameter | Config Property | Default | Description |
|-----------|-----------------|---------|-------------|
| Deep ocean threshold | `DeepOceanThreshold` | 0.20 | cont01 < threshold → DeepOcean |
| Ocean threshold | `OceanThreshold` | 0.35 | cont01 < threshold → Ocean |
| Beach threshold | `OceanThreshold + CoastRange + 0.07` | ~0.47 | cont01 < threshold && E > 0.5 → Beach |
| Alpine temp | (hardcoded) | 0.12 | T < threshold → Alpine |
| Alpine elevation | `AlpineElevation` | 150 | estimatedAltitude > threshold → Alpine |
| Mountain PV | (hardcoded) | 0.7 | PV > threshold → Mountains |
| Mountain erosion | (hardcoded) | 0.4 | E < threshold (low erosion = jagged) |

**Critical Architectural Insight (Minecraft 1.18+):**

> **Ocean biomes are determined PURELY by continentalness noise, NOT by actual terrain height.**
> 
> In Minecraft, both biome selection AND terrain height are derived from the same noise parameters:
> - Low continentalness → Ocean biome selected
> - Low continentalness → Height spline returns low value → Terrain ends up underwater
> 
> This creates natural consistency without needing to check actual Y-level.
> The height spline must be calibrated so that `OceanThreshold` aligns with the
> continentalness value where terrain transitions from underwater to above water.

**Hardcoded (architectural):**
- Biome grid resolution: 4×4 cells (Minecraft standard, each cell = 4×4 blocks)
- Climate parameter count: 5 (C, T, H, E, PV)
- Selection priority order: DeepOcean → Ocean → Beach → Alpine → Mountains → Climate matrix

**Selection Logic (Minecraft-style, noise-only):**
```csharp
public BiomeId SelectBiome(float C, float T, float H, float E, float PV, float estimatedAltitude)
{
    var cont01 = C * 0.5f + 0.5f;  // Convert [-1,1] to [0,1]
    
    // Priority 1: Ocean biomes (ONLY use continentalness, never actual height!)
    if (cont01 < config.DeepOceanThreshold)  
        return BiomeId.DeepOcean;
    if (cont01 < config.OceanThreshold)
        return BiomeId.Ocean;
    
    // Priority 2: Beach (coastal zone with high erosion = flat coasts)
    var beachThreshold = config.OceanThreshold + config.CoastRange + 0.07f;
    if (cont01 < beachThreshold && erosion01 > 0.5f)  
        return BiomeId.Beach;
    
    // Priority 3: Alpine (extreme cold OR high estimated altitude)
    if (T < 0.12f || estimatedAltitude > config.AlpineElevation + WaterLevel)  
        return BiomeId.Alpine;
    
    // Priority 4: Mountains (high PV with low erosion)
    if (pv01 > 0.7f && erosion01 < 0.4f)  
        return T < 0.35f ? BiomeId.Alpine : BiomeId.Highlands;
        return BiomeId.Highlands;
    
    // Priority 5: Climate matrix (uses BiomeDefinition.Temperature/Humidity ranges)
    return SelectFromClimateMatrix(T, H);  // Uses BiomeDefinition ranges
}
```

**Climate Matrix (per-biome config via `BiomeDefinition`):**
Each biome has `Temperature` and `Humidity` ranges defined in its `BiomeDefinition`.
The climate matrix selection finds the best-matching biome where T and H fall within ranges.

**Output:** `BiomeId[4, 4]` per chunk (16 bytes)

> **Future Enhancement:** Expand to `BiomeId[4, 24, 4]` (384 bytes) for cave biomes with vertical variation.

---

## Phase 3: Surface Shaping

Adjust terrain height based on biome's `base_height` and `height_variation` attributes.

```csharp
// Each BiomeDefinition has:
public class BiomeDefinition
{
    public BiomeId Id;
    public float BaseHeight;        // Offset from water level
    public float HeightVariation;   // How much terrain varies
    public float PeaksInfluence;    // How much PV noise affects height
    public float ErosionSensitivity; // How much erosion flattens
}

// During height calculation:
for each column (x, z):
    BiomeId biome = GetBiomeAt(x, y_surface, z);
    var def = BiomeDefinitions[biome];
    
    float targetHeight = WATER_LEVEL + def.BaseHeight;
    targetHeight += (PV - 0.5) * def.HeightVariation * def.PeaksInfluence;
    targetHeight = Lerp(targetHeight, flattenedHeight, E * def.ErosionSensitivity);
    
    // Blend with neighbors for smooth transitions
    // ...
```

**Biome Height Blending:**
At biome boundaries, blend `BaseHeight` and `HeightVariation` from adjacent biomes to avoid discontinuities.

---

## Phase 4: Block Assignment (Surface Decoration)

Replace generic "Solid" blocks with actual block types based on biome and depth.

```csharp
// Surface decoration:
for each column (x, z):
    int surfaceY = GetSurfaceHeight(x, z);
    BiomeId biome = GetBiomeAt(x, surfaceY, z);
    var def = BiomeDefinitions[biome];
    
    for (int y = 0; y < CHUNK_HEIGHT; y++)
    {
        if (IsAir(x, y, z)) continue;
        if (IsWater(x, y, z)) continue;
        
        int depthFromSurface = surfaceY - y;
        BlockId block;
        
        if (depthFromSurface == 0)
            block = def.SurfaceBlock;
        else if (depthFromSurface <= 3)
            block = def.SubsurfaceBlock;
        else
            block = def.DeepBlock;
            
        // Store via palette system
        byte paletteIndex = chunk.GetOrAddPaletteEntry(block);
        SetVoxel(x, y, z, paletteIndex);
    }
```

**Output:** 
- `BlockId[] Palette` - max 128 unique block types per chunk
- `byte[] VoxelData` - palette index per voxel (16×384×16 = 98,304 bytes)

---

## BlockId → Texture Mapping (Face-Based Atlas)

**Texture Format:** Each BlockId maps to a **150×50 pixel atlas** containing three 50×50 sub-images:

```
┌──────────┬──────────┬──────────┐
│   TOP    │  BOTTOM  │   SIDE   │
│  (0-49)  │ (50-99)  │(100-149) │
│  50×50   │  50×50   │  50×50   │
└──────────┴──────────┴──────────┘
     0          1          2
```

**Face → UV Region Mapping:**
| Face Direction | Atlas Column | U Range |
|----------------|--------------|---------|
| +Y (Top)       | 0            | 0.0 - 0.333 |
| -Y (Bottom)    | 1            | 0.333 - 0.666 |
| ±X, ±Z (Sides) | 2            | 0.666 - 1.0 |

**Implementation:**
```csharp
// During meshing: compute UV based on face direction
public static (float uMin, float uMax) GetFaceUVRange(int faceIndex)
{
    return faceIndex switch
    {
        2 => (0.0f, 0.333f),      // +Y (Top)
        3 => (0.333f, 0.666f),    // -Y (Bottom)  
        _ => (0.666f, 1.0f),      // ±X, ±Z (Sides)
    };
}
```

**Texture Array Setup:**
```csharp
// Layer N = texture for BlockId with lower 10 bits = N
public void BuildBlockTextureArray()
{
    // Each layer is 150×50 (the full atlas)
    GL.TexStorage3D(TextureTarget3d.Texture2DArray, 
        mipmapLevels: 4, 
        SizedInternalFormat.Rgba8, 
        width: 150, height: 50, 
        depth: 1024);  // Up to 1024 block types
    
    // Load each block's atlas into its corresponding layer
    LoadBlockTexture(layer: 0, "textures/air.png");      // Air (transparent)
    LoadBlockTexture(layer: 1, "textures/water.png");    // Water
    LoadBlockTexture(layer: 2, "textures/stone.png");    // Stone
    LoadBlockTexture(layer: 3, "textures/bedrock.png");  // Bedrock
    // ...
    LoadBlockTexture(layer: 11, "textures/grass.png");   // Grass (green top, dirt bottom, grass-dirt side)
    LoadBlockTexture(layer: 20, "textures/sand.png");    // Sand (same on all faces)
}
```

**Shader UV Computation:**
```glsl
// In fragment shader: compute final UV from face and local UV
vec2 computeBlockUV(vec2 localUV, uint faceIndex)
{
    float uOffset = (faceIndex == 2u) ? 0.0 : 
                    (faceIndex == 3u) ? 0.333 : 0.666;
    return vec2(uOffset + localUV.x * 0.333, localUV.y);
}
```

---

## Meshing Pipeline (Who Does What)

### Step 1: Terrain Generation → VoxelData + Palette

**Owner:** `CpuTerrainGenerator` / `ChunkGenerationJobSystem`

```csharp
// During terrain generation (Phase 4: Block Assignment)
// Note: Palette implementation details (List vs array) left to implementation
public class CpuTerrainGenerator
{
    public void GenerateChunk(ChunkData chunk, int chunkX, int chunkZ)
    {
        // Initialize chunk data
        chunk.InitializePalette();  // Air is always index 0
        chunk.VoxelData = new byte[16 * 384 * 16];  // All zeros = Air
        
        // Phase 1-3: Generate terrain shape...
        
        // Phase 4: Assign actual blocks
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        {
            int surfaceY = GetSurfaceHeight(x, z);
            var biome = GetBiomeAt(x, surfaceY, z);
            var def = BiomeDefinitions[biome];
            
            for (int y = 0; y < 384; y++)
            {
                BlockId block = DetermineBlock(x, y, z, surfaceY, def);
                
                // Add to palette if new, get index
                byte paletteIndex = chunk.GetOrAddPaletteEntry(block);
                
                // Store palette index in voxel data
                int idx = y * 256 + z * 16 + x;
                chunk.VoxelData[idx] = paletteIndex;
            }
        }
    }
}
```

### Step 2: Meshing → Vertex Buffer with Palette Index

**Owner:** `ChunkMeshBuilder`

The mesh builder reads `VoxelData` (palette indices) and resolves to `BlockId` via the palette to check flags. The **palette index** (not BlockId) is passed to the vertex buffer.

```csharp
public class ChunkMeshBuilder
{
    public void BuildMesh(ChunkData chunk, out Vertex[] vertices)
    {
        var verts = new List<Vertex>();
        
        for (int x = 0; x < 16; x++)
        for (int y = 0; y < 384; y++)
        for (int z = 0; z < 16; z++)
        {
            int idx = y * 256 + z * 16 + x;
            byte paletteIndex = chunk.VoxelData[idx];
            BlockId block = chunk.GetBlock(x, y, z);  // Resolves via palette
            
            // Skip non-solid blocks (Air, Water handled separately)
            if (!block.IsSolid()) continue;
            
            // Check each face
            foreach (Face face in AllFaces)
            {
                BlockId neighbor = GetNeighborBlock(chunk, x, y, z, face);
                
                // Cull face if neighbor is opaque
                if (neighbor.IsOpaque()) continue;
                
                // Emit face vertices
                // IMPORTANT: Pass paletteIndex (byte), not BlockId
                EmitFace(opaqueVerts, x, y, z, face, paletteIndex);
            }
        }
        
        // === WATER MESHING (top faces only) ===
        // Water surfaces are rendered separately in translucent pass.
        // Only top faces (+Y) are meshed where water meets air.
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        for (int y = 383; y >= 0; y--)  // Top-down to find surface
        {
            int idx = y * 256 + z * 16 + x;
            byte paletteIndex = chunk.VoxelData[idx];
            BlockId block = chunk.Palette[paletteIndex];
            
            if (!block.IsLiquid()) continue;
            
            // Check if block above is air (water surface)
            if (y < 383)
            {
                int aboveIdx = (y + 1) * 256 + z * 16 + x;
                BlockId above = chunk.Palette[chunk.VoxelData[aboveIdx]];
                if (above.IsLiquid()) continue;  // Not surface
            }
            
            // Emit top face only for water surface
            EmitFace(translucentVerts, x, y, z, Face.PosY, paletteIndex);
        }
        
        opaqueVertices = opaqueVerts.ToArray();
        translucentVertices = translucentVerts.ToArray();
    }
    
    private void EmitFace(List<Vertex> verts, int x, int y, int z, 
                          Face face, byte paletteIndex)
    {
        // Each vertex includes packed position + data
        var quad = GetFaceQuad(x, y, z, face);
        foreach (var v in quad)
        {
            verts.Add(new Vertex
            {
                PackedPosition = PackPosition(x, y, z, (int)face, v.Corner),
                PackedData = PackData(paletteIndex)
            });
        }
    }
}
```

**Water Rendering Notes:**
- Water is rendered in the **translucent pass** (after opaque geometry)
- Only **top faces** are meshed (flat water surface)
- No back-to-front sorting needed for flat surfaces
- Current shader-based water effects (waves, reflections) continue to work
- Side/bottom water faces are NOT meshed (avoids artifacts and "water curtains")
- Future improvement: proper volumetric water with depth fog

### Step 3: Upload Palette to GPU

**Owner:** `VoxelTerrainRenderer` / `Phase3BufferManager`

Each chunk's palette must be accessible to the shader. We use **per-chunk palette SSBOs** (simpler than a global indexed buffer).

**Per-Chunk Palette SSBO (Recommended)**

Each chunk has its own small SSBO containing only its palette entries:

```csharp
public class ChunkRenderData
{
    public int PaletteSSBO;       // GL buffer with this chunk's BlockId[] palette
    public int VertexBuffer;
    public int DrawCommandSlot;
}

public class Phase3BufferManager
{
    private const int MaxPaletteEntriesPerChunk = 128;
    
    public void UploadChunkPalette(ChunkRenderData renderData, BlockId[] palette)
    {
        // Each chunk gets its own small SSBO
        if (renderData.PaletteSSBO == 0)
            renderData.PaletteSSBO = GL.GenBuffer();
        
        // Convert palette to ushort array (only actual entries, not padded)
        ushort[] paletteData = new ushort[palette.Length];
        for (int i = 0; i < palette.Length; i++)
            paletteData[i] = (ushort)palette[i];
        
        // Upload this chunk's palette
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, renderData.PaletteSSBO);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, 
            paletteData.Length * sizeof(ushort), paletteData, 
            BufferUsageHint.StaticDraw);
    }
    
    public void ReleaseChunk(ChunkRenderData renderData)
    {
        if (renderData.PaletteSSBO != 0)
        {
            GL.DeleteBuffer(renderData.PaletteSSBO);
            renderData.PaletteSSBO = 0;
        }
    }
}
```

**Why per-chunk SSBO (not global indexed)?**
- Simpler: no offset calculations, no chunk slot management
- Each chunk's palette is only as large as needed (5-50 entries typically)
- Bind once per chunk during multi-draw (already have per-chunk data)
- No wasted space for unused palette slots
- Cleanup is straightforward when chunk unloads

---

## Shader Texture Handling (Exact Details)

### Vertex Format (Optimized)

Since all blocks are in a uniform axis-aligned grid:
- **Position** can be computed from voxel index + vertex corner (0-7)
- **Normal** is implicit from face direction (6 faces × 1 normal each)
- Only need: voxel index, corner/face info, palette index, local UV

**Note:** Face index determines which sub-image (top/bottom/side) to use from the 150×50 atlas.

```csharp
// C# Vertex struct - OPTIMIZED (8 bytes per vertex!)
[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex
{
    // Pack voxel position (x, y, z) + corner index + face into 32 bits:
    // Bits 0-3:   X position within chunk (0-15)
    // Bits 4-7:   Z position within chunk (0-15)  
    // Bits 8-16:  Y position (0-383, needs 9 bits)
    // Bits 17-19: Face index (0-5: +X, -X, +Y, -Y, +Z, -Z)
    // Bits 20-21: Corner index within face (0-3 for quad)
    // Bits 22-23: Reserved
    public uint PackedPosition;   // 4 bytes
    
    // Bits 0-7:   Palette index (0-127)
    // Bits 8-15:  Reserved (AO, light level, etc.)
    public ushort PackedData;     // 2 bytes
    
    public ushort Padding;        // 2 bytes (alignment)
    // Total: 8 bytes per vertex (was 36 bytes - 78% reduction!)
}

// Packing helpers
public static uint PackPosition(int x, int y, int z, int face, int corner)
{
    return (uint)(x & 0xF) 
         | (uint)((z & 0xF) << 4)
         | (uint)((y & 0x1FF) << 8)
         | (uint)((face & 0x7) << 17)
         | (uint)((corner & 0x3) << 20);
}

public static ushort PackData(byte paletteIndex)
{
    return (ushort)paletteIndex;
}
```

**UV Computation in Shader:**
- Corner index (0-3) determines local UV within sub-image: `(0,0), (1,0), (1,1), (0,1)`
- Face index determines atlas column offset: Top=0, Bottom=0.333, Side=0.666
- Final UV = `(columnOffset + localU * 0.333, localV)`

**Alternative: Even More Compact (6 bytes)**
```csharp
// If we use indices and reconstruct more in shader:
[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertexCompact
{
    public uint PackedPosition;   // 4 bytes (x, y, z, face, corner)
    public byte PaletteIndex;     // 1 byte
    public byte Flags;            // 1 byte (AO, UV flip, etc.)
    // Total: 6 bytes per vertex
}
```

### Shader Bindings (Optimized Vertex Format)

```glsl
// === Vertex Shader ===
#version 460 core

// Packed vertex data (8 bytes total)
layout(location = 0) in uint aPackedPosition;  // x, y, z, face, corner
layout(location = 1) in uint aPackedData;      // paletteIndex, u, v (ushort)

// Per-chunk palette: maps paletteIndex → BlockId (ushort)
layout(std430, binding = 2) readonly buffer ChunkPalette
{
    uint palette[];  // ushort[] stored as uint for alignment
};

// Chunk offset in world coordinates (set per draw call or via SSBO)
uniform vec3 uChunkWorldOffset;

// Pre-computed normal lookup (6 faces)
const vec3 FACE_NORMALS[6] = vec3[6](
    vec3( 1,  0,  0),   // +X
    vec3(-1,  0,  0),   // -X
    vec3( 0,  1,  0),   // +Y
    vec3( 0, -1,  0),   // -Y
    vec3( 0,  0,  1),   // +Z
    vec3( 0,  0, -1)    // -Z
);

// Corner local UV within face quad (CCW winding)
const vec2 CORNER_LOCAL_UV[4] = vec2[4](
    vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 1)
);

// Atlas column offset: Top(+Y)=0, Bottom(-Y)=0.333, Sides=0.666
const float FACE_ATLAS_OFFSET[6] = float[6](
    0.666,  // +X (side)
    0.666,  // -X (side)
    0.0,    // +Y (top)
    0.333,  // -Y (bottom)
    0.666,  // +Z (side)
    0.666   // -Z (side)
);

// Corner position offsets per face (indexed by face * 4 + corner)
// ... (omitted for brevity, but computable from face normal)

out VS_OUT {
    vec3 fragPos;
    vec3 normal;
    vec2 texCoord;      // Already computed atlas UV
    flat uint blockId;
    flat uint face;
} vs_out;

void main()
{
    // Unpack position bits
    uint x = aPackedPosition & 0xFu;
    uint z = (aPackedPosition >> 4) & 0xFu;
    uint y = (aPackedPosition >> 8) & 0x1FFu;
    uint face = (aPackedPosition >> 17) & 0x7u;
    uint corner = (aPackedPosition >> 20) & 0x3u;
    
    // Unpack data bits
    uint paletteIndex = aPackedData & 0xFFu;
    
    // Compute world position from voxel + corner offset
    vec3 voxelPos = vec3(float(x), float(y), float(z));
    vec3 cornerOffset = GetCornerOffset(face, corner);  // Returns 0 or 1 per axis
    vec3 localPos = voxelPos + cornerOffset;
    vec3 worldPos = uChunkWorldOffset + localPos;
    
    // Lookup normal from face index (no interpolation needed)
    vs_out.normal = FACE_NORMALS[face];
    
    // Compute atlas UV: column offset + local UV scaled to 1/3 width
    vec2 localUV = CORNER_LOCAL_UV[corner];
    float atlasOffset = FACE_ATLAS_OFFSET[face];
    vs_out.texCoord = vec2(atlasOffset + localUV.x * 0.333, localUV.y);
    
    // Resolve palette → BlockId → texture layer
    uint blockIdFull = palette[paletteIndex];
    vs_out.blockId = blockIdFull & 0x3FFu;  // Lower 10 bits
    vs_out.face = face;
    
    vs_out.fragPos = worldPos;
    gl_Position = uProjection * uView * vec4(worldPos, 1.0);
}
```

```glsl
// === Fragment Shader ===
#version 460 core

in VS_OUT {
    vec3 fragPos;
    vec3 normal;
    vec2 texCoord;      // Atlas UV (0-0.333 for top, 0.333-0.666 for bottom, 0.666-1.0 for side)
    flat uint blockId;  // Lower 10 bits = texture array layer
    flat uint face;
} fs_in;

// Block texture array: layer N = 150×50 atlas for block ID N
layout(binding = 0) uniform sampler2DArray uBlockTextures;

out vec4 FragColor;

void main()
{
    // Sample from atlas: texCoord already has column offset applied
    vec4 texColor = texture(uBlockTextures, 
        vec3(fs_in.texCoord, float(fs_in.blockId)));
    
    // Basic lighting using pre-computed face normal
    vec3 lightDir = normalize(vec3(0.5, 1.0, 0.3));
    float diff = max(dot(fs_in.normal, lightDir), 0.3);
    
    FragColor = vec4(texColor.rgb * diff, texColor.a);
}
```

**Vertex Memory Savings:**
| Format | Per Vertex | Per Chunk (est. 100K verts) |
|--------|------------|----------------------------|
| Original (pos+normal+uv+idx) | 36 bytes | 3.6 MB |
| Optimized (packed) | 8 bytes | 0.8 MB |
| **Savings** | **78%** | **2.8 MB per chunk** |
```

### GPU Resource Setup (C# Side)

```csharp
public class VoxelTerrainRenderer
{
    private int _blockTextureArray;  // sampler2DArray with all block textures
    private int _paletteSSBO;        // Per-chunk palette buffer
    
    public void Initialize()
    {
        // 1. Create texture array with all block textures
        _blockTextureArray = CreateBlockTextureArray();
        
        // 2. Create palette SSBO (will be updated per-chunk)
        _paletteSSBO = GL.GenBuffer();
    }
    
    private int CreateBlockTextureArray()
    {
        // Create 2D array texture: 150×50 atlas per layer (top|bottom|side), 1024 layers max
        int texArray = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2DArray, texArray);
        GL.TexStorage3D(TextureTarget3d.Texture2DArray, 
            mipmapLevels: 4, 
            SizedInternalFormat.Rgba8, 
            width: 150, height: 50, 
            depth: 1024);  // Up to 1024 block types
        
        // Load each block's 150×50 atlas into its corresponding layer
        // Layer index == BlockId's lower 10 bits
        LoadBlockTexture(layer: 0, "textures/air.png");      // Air (transparent)
        LoadBlockTexture(layer: 1, "textures/water.png");    // Water
        LoadBlockTexture(layer: 2, "textures/stone.png");    // Stone
        LoadBlockTexture(layer: 3, "textures/bedrock.png");  // Bedrock
        // ...
        LoadBlockTexture(layer: 11, "textures/grass.png");   // Grass
        LoadBlockTexture(layer: 20, "textures/sand.png");    // Sand
        // etc.
        
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2DArray);
        return texArray;
    }
    
    public void RenderChunk(ChunkRenderData chunk)
    {
        // Bind texture array to unit 0
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2DArray, _blockTextureArray);
        
        // Bind this chunk's palette SSBO to binding 2
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 
            2, chunk.PaletteSSBO);
        
        // Draw
        GL.BindVertexArray(chunk.VAO);
        GL.DrawElements(PrimitiveType.Triangles, chunk.IndexCount, 
            DrawElementsType.UnsignedInt, 0);
    }
}
```

### Summary: Data Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                        TERRAIN GENERATION                            │
│  CpuTerrainGenerator builds:                                        │
│    - BlockId[] Palette (5-50 unique blocks per chunk)               │
│    - byte[] VoxelData (palette indices, 98KB per chunk)             │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                           MESHING                                    │
│  ChunkMeshBuilder:                                                  │
│    - Reads VoxelData[i] → paletteIndex                              │
│    - Resolves Palette[paletteIndex] → BlockId (for flag checks)     │
│    - Outputs vertices with PaletteIndex attribute                   │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         GPU UPLOAD                                   │
│  VoxelTerrainRenderer / Phase3BufferManager:                        │
│    - Uploads vertex buffer (with PaletteIndex per vertex)           │
│    - Uploads Palette[] to per-chunk SSBO                            │
│    - Texture array already loaded at startup                        │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                      VERTEX SHADER                                   │
│    - Reads aPaletteIndex from vertex                                │
│    - Looks up palette[aPaletteIndex] → BlockId (ushort)             │
│    - Extracts blockId & 0x3FF → texture layer index                 │
│    - Passes blockId to fragment shader                              │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                     FRAGMENT SHADER                                  │
│    - Uses blockId directly as texture array layer                   │
│    - texture(uBlockTextures, vec3(uv, blockId))                     │
│    - NO LOOKUP TABLE - blockId IS the layer index!                  │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Files to Remove/Simplify (After Migration)

These cleanups happen **after** the new pipeline is working:

- **DELETE:** `terrain-climate.glsl` - No longer needed (biomes on CPU)
- **DELETE:** `getBiomeId()` function and all climate noise in fragment shader
- **DELETE:** `World/BlockType.cs` - Obsolete enum replaced by BlockId
- **DELETE:** `World/BlockDescriptor.cs` - Replaced by BlockId flags
- **SIMPLIFY:** `terrain-common.glsl` - Keep only constants, remove noise functions
- **SIMPLIFY:** `voxel-terrain.frag` - Remove biome detection, just use BlockId

---

## Data Structures

### BlockId Enum (Flags-Based)

`BlockId` is a `[Flags]` enum where **bits encode both the unique ID and properties**.
This eliminates the need for separate `BlockDescriptor` or `BlockFlags` lookup tables.

```csharp
/// <summary>
/// Block identifier with embedded property flags.
/// Lower bits = unique block ID, upper bits = property flags.
/// </summary>
[Flags]
public enum BlockId : ushort  // 16 bits
{
    // === Property Flags (upper 6 bits) ===
    None        = 0,
    Solid       = 1 << 10,    // Blocks movement, needs faces rendered
    Opaque      = 1 << 11,    // Blocks light, culls neighbor faces
    Liquid      = 1 << 12,    // Water/lava flow behavior
    Translucent = 1 << 13,    // Partial transparency (water, ice, leaves)
    Replaceable = 1 << 14,    // Can be overwritten by other blocks
    // Bit 15 reserved for future use
    
    // === Block IDs (lower 10 bits = 1024 unique blocks) ===
    // Air: replaceable, not solid, not opaque
    Air         = 0 | Replaceable,
    
    // Water: liquid, translucent, replaceable, not solid
    Water       = 1 | Liquid | Translucent | Replaceable,
    
    // Stone: solid, opaque
    Stone       = 2 | Solid | Opaque,
    
    // Bedrock: solid, opaque (indestructible handled elsewhere)
    Bedrock     = 3 | Solid | Opaque,
    
    // Dirt: solid, opaque
    Dirt        = 10 | Solid | Opaque,
    
    // Grass: solid, opaque
    Grass       = 11 | Solid | Opaque,
    
    // GrassSnowy: solid, opaque
    GrassSnowy  = 12 | Solid | Opaque,
    
    // Sand: solid, opaque
    Sand        = 20 | Solid | Opaque,
    
    // Ice: solid, translucent (can see through slightly)
    Ice         = 42 | Solid | Translucent,
    
    // Leaves: solid (for collision), translucent (for rendering)
    OakLeaves   = 140 | Solid | Translucent,
    
    // ... more blocks with appropriate flags
}

// Mask constants for extracting parts
public static class BlockIdMasks
{
    public const ushort IdMask    = 0x03FF;  // Lower 10 bits = block ID
    public const ushort FlagsMask = 0xFC00;  // Upper 6 bits = flags
}

// Extension methods for flag checking (no lookup table needed!)
public static class BlockIdExtensions
{
    public static bool IsSolid(this BlockId b)       => (b & BlockId.Solid) != 0;
    public static bool IsOpaque(this BlockId b)      => (b & BlockId.Opaque) != 0;
    public static bool IsLiquid(this BlockId b)      => (b & BlockId.Liquid) != 0;
    public static bool IsTranslucent(this BlockId b) => (b & BlockId.Translucent) != 0;
    public static bool IsReplaceable(this BlockId b) => (b & BlockId.Replaceable) != 0;
    public static ushort GetId(this BlockId b)       => (ushort)((ushort)b & BlockIdMasks.IdMask);
}
```

**Key Benefits:**
- No lookup tables - extract flags directly via bitmask
- Meshing can check `IsSolid()`, `IsOpaque()` inline
- Shader receives only the 10-bit ID (via palette), flags used only on CPU
- Can support 1024 unique blocks with 6 property flags

### ChunkData (Palette System)

**Critical Architecture:**
- `BlockId[] Palette` = array of BlockId values (max 128 entries per chunk)
- `byte[] VoxelData` = palette indices (1 byte per voxel)
- Voxel lookup: `BlockId block = chunk.Palette[voxelData[index]];`

```csharp
public class ChunkData
{
    /// <summary>
    /// Block palette: maps byte index → BlockId.
    /// Most chunks use 5-15 unique blocks; max 128.
    /// </summary>
    public BlockId[] Palette;  // Typically small (5-50 entries)
    
    /// <summary>
    /// Voxel data: 7-bit palette index per block.
    /// To get actual block: Palette[VoxelData[i]]
    /// </summary>
    public byte[] VoxelData;   // 16×384×16 = 98,304 bytes
    
    /// <summary>
    /// Biome storage: coarse grid (4×4 horizontal, 1 per column).
    /// Future: expand to 4×4×24 for cave biomes.
    /// </summary>
    public BiomeId[] Biomes;   // 4×4 = 16 bytes (or 384 for cave biomes)
    
    /// <summary>
    /// Optional: cached surface heights per column.
    /// </summary>
    public int[] SurfaceHeights;  // 16×16 = 256 ints = 1,024 bytes
    
    /// <summary>
    /// Get BlockId at position (resolves palette indirection).
    /// </summary>
    public BlockId GetBlock(int x, int y, int z)
    {
        int idx = y * 256 + z * 16 + x;
        byte paletteIndex = VoxelData[idx];
        return Palette[paletteIndex];
    }
    
    /// <summary>
    /// Get or add a palette entry, returns the palette index.
    /// Implementation detail: can use List<BlockId> internally or array with resize.
    /// </summary>
    public byte GetOrAddPaletteEntry(BlockId blockId)
    {
        // Implementation-specific: search existing, add if not found
        // Must return index < 128
    }
    
    /// <summary>
    /// Initialize palette with Air at index 0.
    /// </summary>
    public void InitializePalette()
    {
        // Implementation-specific
    }
}
```

**Memory Comparison:**
| Approach | Per Voxel | Per Chunk (16×384×16) |
|----------|-----------|------------------------|
| Direct BlockId (ushort) | 2 bytes | 196,608 bytes |
| Palette + Index | 1 byte + ~100 bytes palette | ~98,500 bytes |
| **Savings** | **50%** | **~98 KB per chunk** |

### BiomeDefinition (Block Mappings)
```csharp
public class BiomeDefinition
{
    public BiomeId Id;
    public string Name;
    
    // Climate requirements (for selection)
    public Range Temperature;
    public Range Humidity;
    public TerrainType AllowedTerrain;
    
    // Height shaping
    public float BaseHeight;
    public float HeightVariation;
    public float PeaksInfluence;
    public float ErosionSensitivity;
    
    // Block assignment (full BlockId with flags)
    public BlockId SurfaceBlock;           // e.g., Grass | Solid | Opaque
    public BlockId SubsurfaceBlock;        // e.g., Dirt | Solid | Opaque  
    public BlockId DeepBlock;              // e.g., Stone | Solid | Opaque
    public BlockId UnderwaterSurfaceBlock; // e.g., Gravel | Solid | Opaque
}
```

---

## Implementation Phases

### Phase A: Block System Foundation ⬜ TODO
1. **Redesign `BlockId`** as `[Flags] enum : ushort`
   - Lower 10 bits = unique ID (1024 blocks max)
   - Upper 6 bits = flags (Solid, Opaque, Liquid, Translucent, Replaceable)
   - Add extension methods: `IsSolid()`, `IsOpaque()`, `IsLiquid()`, `GetId()`
2. **Create `ChunkData` class** with:
   - `BlockId[] Palette` (variable size, max 128 entries)
   - `byte[] VoxelData` (98,304 palette indices)
   - `BiomeId[] Biomes` (16 entries for 4×4 grid; future: 384 for cave biomes)
   - Helper methods: `GetBlock()`, `SetBlock()`, `GetOrAddPaletteEntry()`
3. **Update `BiomeDefinition`** to use new BlockId values (already has block fields)

### Phase B: Terrain Generation Refactor ⬜ TODO  
1. Create `CpuTerrainGenerator` or integrate into `ChunkGenerationJobSystem`:
   - Phase 1: Density field → Solid/Water/Air
   - Phase 2: Biome assignment per 4×4×16 cell
   - Phase 3: Surface shaping with biome height attributes
   - Phase 4: Block assignment from biome's SurfaceBlock/SubsurfaceBlock/DeepBlock

### Phase C: Meshing Pipeline ⬜ TODO
1. Update `ChunkMeshBuilder` to:
   - Read from `ChunkData.VoxelData` (palette indices)
   - Resolve BlockId via palette for flag checks
   - Output packed vertex format (8 bytes)
   - Emit water top-faces only to translucent buffer
2. Update `Phase3BufferManager`:
   - Add global palette SSBO indexed by chunk slot
   - Upload chunk palettes on mesh ready

### Phase D: Shader Simplification ⬜ TODO
1. Add palette SSBO binding to vertex shader
2. Lookup palette → BlockId → texture layer
3. Compute atlas UV from face index (top/bottom/side)
4. Remove all biome/climate noise from shaders
5. Delete `terrain-climate.glsl` after migration complete

### Phase E: Cleanup ⬜ TODO
1. Delete `World/BlockDescriptor.cs`
2. Delete `World/BlockType.cs`
3. Remove unused climate uniforms from shaders
4. Update documentation

---

## Key Differences from Current System

| Aspect | Current | Target (Minecraft-style) |
|--------|---------|--------------------------|
| Biome computation | Shader (GPU noise) | CPU (stored per chunk) |
| Block types | `BlockDescriptor` (geological) | `BlockId` (flags enum with ID + properties) |
| Block properties | Separate lookup table | Embedded in `BlockId` via flags |
| Texture selection | BiomeId × Layer × Face | PaletteIndex → BlockId → Texture array |
| Texture format | ? | 150×50 atlas (top/bottom/side) |
| Height shaping | Mixed noise + biome | Biome attributes + blending |
| Voxel storage | Direct block type | 1 byte palette index |
| Water rendering | All faces (broken) | Top faces only (flat surface) |

---

## Success Criteria

1. ⬜ Biomes computed entirely on CPU (BiomeGenerator exists, needs integration)
2. ⬜ BlockId is flags enum with embedded properties
3. ⬜ Per-chunk palette reduces voxel storage by 50%
4. ⬜ Shader uses palette → BlockId → texture (no runtime biome calculation)
5. ⬜ Block textures use 150×50 atlas (top/bottom/side faces)
6. ⬜ Water renders as flat top-surface only (no artifacts)
7. ⬜ Each biome has distinct surface/subsurface blocks
