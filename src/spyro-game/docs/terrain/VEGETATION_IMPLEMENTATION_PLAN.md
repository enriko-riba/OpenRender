# Vegetation Implementation Plan

## Overview
This document outlines the plan to implement Minecraft-style vegetation (grass, flowers, trees, cacti, etc.) into the terrain generation pipeline.

## 1. Data Structures

### 1.1. New Block IDs
Update `BlockId.cs` to include vegetation blocks.

```csharp
// Vegetation (160-199)
TallGrass = 160,
Poppy = 161,
Dandelion = 162,
BlueOrchid = 163,
Allium = 164,
AzureBluet = 165,
RedTulip = 166,
OrangeTulip = 167,
WhiteTulip = 168,
PinkTulip = 169,
OxeyeDaisy = 170,
Cornflower = 171,
LilyOfTheValley = 172,
WitherRose = 173,
Sunflower = 174,
Lilac = 175,
RoseBush = 176,
Peony = 177,
DeadBush = 178,
Cactus = 179,
SugarCane = 180,
Bamboo = 181,
```

### 1.2. Block Registry Updates
Update `BlockRegistry.cs` to define properties for these blocks:
- **Model**: `Cross` (for flowers/grass), `Cube` (for cactus), `Column` (for logs).
- **Transparency**: `Translucent` or `Cutout`.
- **Collision**: `None` (for grass/flowers), `Solid` (for trees/cactus).

### 1.3. Biome Configuration
Add vegetation rules to `BiomeDefinition` in `TerrainConfig.cs`.

```csharp
public class VegetationRule
{
    public VegetationType Type { get; set; }
    public float Density { get; set; } // Probability per column (0.0 - 1.0)
    public float NoiseFrequency { get; set; } = 0.1f; // For patch distribution
    public BlockId[] AllowedSurfaceBlocks { get; set; }
}

public enum VegetationType
{
    Grass,
    Flower,
    TreeOak,    // Uses "Balloon" shape algorithm
    TreeBirch,  // Uses "Balloon" shape algorithm (different texture)
    TreeSpruce, // Uses "Cone" shape algorithm
    TreeJungle, // Uses "Tall/Mega" shape algorithm
    Cactus,
    DeadBush,
    SugarCane
}

> **Design Note**: Specific tree types (Oak, Spruce, etc.) are used instead of a generic `Tree` type because they map to distinct generation algorithms (shapes) and simplify biome configuration. For example, a Spruce tree uses a conical generation pattern, while an Oak tree uses a spherical/balloon pattern. This allows biomes to simply specify "Spruce" without needing to define the shape parameters every time.

// In BiomeDefinition:
public List<VegetationRule> Vegetation { get; set; } = new();
```

## 2. Vegetation Generator

Create `src/spyro-game/World/Generation/VegetationGenerator.cs`.

### Responsibilities
- **DecorateChunk(ChunkData chunk, ChunkBiomeData biomes)**: Main entry point.
- **PlaceTree(ChunkData chunk, int x, int y, int z, TreeType type)**: Generates a tree.
- **PlacePlant(ChunkData chunk, int x, int y, int z, BlockId plant)**: Places a single block plant.

### Logic
1.  Iterate over surface blocks (using `chunk.SurfaceHeights`).
2.  For each column, check biome.
3.  Iterate over `VegetationRules` for that biome.
4.  Use noise/randomness to decide if vegetation should be placed.
    - Use a deterministic seed based on world coordinates `(worldX, worldZ)`.
5.  If placed, call `PlaceTree` or `PlacePlant`.

### Tree Generation
- **Oak**: 4-6 blocks high log, spherical-ish leaves.
- **Birch**: Similar to Oak but different texture/leaves.
- **Spruce**: Cone shape.
- **Jungle**: Tall, vines (maybe later).

*Note: Initially, trees will be generated per-chunk. Trees on boundaries might be clipped or require neighbor access. For Phase 1, we accept clipping or restrict placement to inner area.*

## 3. Integration

### 3.1. CpuTerrainGenerator
Modify `GenerateChunk` in `CpuTerrainGenerator.cs`.

```csharp
public ChunkGenerationResult GenerateChunk(...)
{
    // ... existing code ...
    FillChunk(...);
    
    // NEW: Vegetation Pass
    var vegetationGen = new VegetationGenerator(config);
    vegetationGen.DecorateChunk(chunkData, currentChunkBiome, currentChunkX, currentChunkZ);
    
    // ... existing code ...
}
```

### 3.2. Collision Updates
Ensure that `ChunkCollisionData` is updated if vegetation adds solid blocks (trees, cactus).
Since `FillChunk` calculates collision spans, we might need to:
1.  Run vegetation pass *inside* `FillChunk` (hard for multi-block trees).
2.  Or re-calculate/update collision spans after vegetation.
    - Since trees are sparse, updating spans might be fast.
    - Or just run a second pass over the chunk to build collision data *after* all generation (terrain + vegetation).

**Recommendation**: Move collision span generation to a separate pass after `FillChunk` and `DecorateChunk`.

## 4. Meshing

### 4.1. Cross Model
Implement `BlockModelType.Cross` in `ChunkMeshBuilder.cs`.
- Two quads intersecting at 90 degrees.
- Texture coordinates from `BlockRegistry`.
- No backface culling for these faces.

### 4.2. Leaves
- Treat as `Translucent` or `Cutout`.
- If `Cutout` (AlphaTest), they can go into the opaque pass with discard in shader.
- If `Translucent`, they need sorting (expensive). Minecraft uses "Fancy" (translucent) vs "Fast" (cutout).
- Start with **Cutout** for simplicity and performance.

## 5. Execution Steps

1.  **Define Blocks**: Add IDs to `BlockId.cs`.
2.  **Registry**: Update `BlockRegistry.cs` with models and properties.
3.  **Config**: Update `TerrainConfig.cs` with `VegetationRule`.
4.  **Generator**: Implement `VegetationGenerator.cs`.
5.  **Pipeline**: Hook into `CpuTerrainGenerator.cs`.
6.  **Meshing**: Implement Cross model in `ChunkMeshBuilder.cs`.
7.  **Testing**: Verify visual results.
