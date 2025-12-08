namespace SpyroGame.World.Generation;

/// <summary>
/// Handles the placement of vegetation (trees, flowers, grass, cacti) in generated chunks.
/// Runs as a post-processing pass after the base terrain is generated.
/// </summary>
internal sealed class VegetationGenerator(TerrainConfig config)
{
    private readonly int seed = config.Seed;

    /// <summary>
    /// Decorates a chunk with vegetation based on biome rules.
    /// </summary>
    public void DecorateChunk(ChunkData chunk, ChunkBiomeData biomes, int chunkX, int chunkZ)
    {
        // Cache surface heights to avoid placing vegetation on top of other vegetation (e.g. flowers on tree leaves)
        // We want to place vegetation on the original terrain surface
        var initialSurfaceHeights = new int[chunk.SurfaceHeights.Length];
        Array.Copy(chunk.SurfaceHeights, initialSurfaceHeights, chunk.SurfaceHeights.Length);

        // Iterate over all columns in the chunk
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                var worldX = chunkX * VoxelHelper.ChunkSideSize + x;
                var worldZ = chunkZ * VoxelHelper.ChunkSideSize + z;
                
                // Get surface height (highest opaque block) from cache
                var surfaceY = initialSurfaceHeights[z * VoxelHelper.ChunkSideSize + x];

                // Skip if invalid height or too close to top
                if (surfaceY is <= 0 or >= (VoxelHelper.ChunkYSize - 10)) continue;

                // Get biome at this column
                var biomeId = biomes.GetBiomeAt(x, z);
                var biome = config.Biomes.Find(b => b.Id == (int)biomeId);
                
                // Skip if biome has no vegetation rules
                if (biome == null || biome.Vegetation.Count == 0) continue;

                // Get the block at the surface
                var surfaceBlock = chunk.GetBlock(x, surfaceY, z);

                // Deterministic random for this column
                var random = new Random(Hash(seed, worldX, worldZ));

                foreach (var rule in biome.Vegetation)
                {
                    // Check allowed surface blocks
                    if (rule.AllowedSurfaceBlocks.Length > 0 && !Array.Exists(rule.AllowedSurfaceBlocks, b => b == surfaceBlock))
                        continue;

                    // Check density (probability)
                    // TODO: Add noise modulation for patchiness if NoiseFrequency > 0
                    if (random.NextSingle() < rule.Density)
                    {
                        // Prevent placing trees on chunk borders to avoid cut-off leaves
                        // Trees have a radius of up to 2 blocks
                        if (IsTree(rule.Type))
                        {
                            if (x < 2 || x >= VoxelHelper.ChunkSideSize - 2 ||
                                z < 2 || z >= VoxelHelper.ChunkSideSize - 2)
                            {
                                continue;
                            }
                        }

                        // Place vegetation one block ABOVE the surface
                        PlaceVegetation(chunk, x, surfaceY + 1, z, rule.Type, random);
                        
                        // Only one vegetation item per column
                        break; 
                    }
                }
            }
        }
    }

    private static void PlaceVegetation(ChunkData chunk, int x, int y, int z, VegetationType type, Random random)
    {
        switch (type)
        {
            case VegetationType.Grass:
                PlacePlant(chunk, x, y, z, BlockId.TallGrass);
                break;
            case VegetationType.Flower:
                // Pick random flower
                var flowers = new[] { 
                    BlockId.Poppy, BlockId.Dandelion, BlockId.BlueOrchid, BlockId.Allium, 
                    BlockId.AzureBluet, BlockId.RedTulip, BlockId.OrangeTulip, BlockId.WhiteTulip, 
                    BlockId.PinkTulip, BlockId.OxeyeDaisy, BlockId.Cornflower, BlockId.LilyOfTheValley 
                };
                PlacePlant(chunk, x, y, z, flowers[random.Next(flowers.Length)]);
                break;
            case VegetationType.BlueOrchid:
                PlacePlant(chunk, x, y, z, BlockId.BlueOrchid);
                break;
            case VegetationType.TreeOak:
                PlaceTree(chunk, x, y, z, BlockId.OakLog, BlockId.OakLeaves, 4 + random.Next(3));
                break;
            case VegetationType.TreeBirch:
                PlaceTree(chunk, x, y, z, BlockId.BirchLog, BlockId.BirchLeaves, 4 + random.Next(3));
                break;
            case VegetationType.TreeSpruce:
                PlaceSpruceTree(chunk, x, y, z, 6 + random.Next(4));
                break;
            case VegetationType.TreeJungle:
                PlaceTree(chunk, x, y, z, BlockId.JungleLog, BlockId.JungleLeaves, 10 + random.Next(10));
                break;
            case VegetationType.Cactus:
                PlaceCactus(chunk, x, y, z, 1 + random.Next(3));
                break;
            case VegetationType.DeadBush:
                PlacePlant(chunk, x, y, z, BlockId.DeadBush);
                break;
            case VegetationType.SugarCane:
                PlaceColumn(chunk, x, y, z, BlockId.SugarCane, 2 + random.Next(2));
                break;
        }
    }

    private static void PlacePlant(ChunkData chunk, int x, int y, int z, BlockId plant)
    {
        if (ChunkData.IsWithinBounds(x, y, z) && chunk.GetBlock(x, y, z) == BlockId.Air)
        {
            chunk.SetBlock(x, y, z, plant);
        }
    }

    private static void PlaceColumn(ChunkData chunk, int x, int y, int z, BlockId block, int height)
    {
        for (var i = 0; i < height; i++)
        {
            if (ChunkData.IsWithinBounds(x, y + i, z) && chunk.GetBlock(x, y + i, z) == BlockId.Air)
            {
                chunk.SetBlock(x, y + i, z, block);
            }
        }
    }

    private static void PlaceCactus(ChunkData chunk, int x, int y, int z, int height)
    {
        // Cactus needs air around it (except bottom)
        // Simplified check: just place it for now, collision/update logic handles the rest in a real game
        // But for generation, we should try to respect it to avoid ugly overlaps
        if (!CheckNeighborsAir(chunk, x, y, z)) return;

        PlaceColumn(chunk, x, y, z, BlockId.Cactus, height);
    }

    private static bool CheckNeighborsAir(ChunkData chunk, int x, int y, int z)
    {
        // Check 4 neighbors at base level
        var neighbors = new[] { (1,0), (-1,0), (0,1), (0,-1) };
        foreach (var (dx, dz) in neighbors)
        {
            if (ChunkData.IsWithinBounds(x + dx, y, z + dz))
            {
                var block = chunk.GetBlock(x + dx, y, z + dz);
                if (block.IsSolid()) return false;
            }
        }
        return true;
    }

    private static void PlaceTree(ChunkData chunk, int x, int y, int z, BlockId log, BlockId leaves, int height)
    {
        // Simple balloon tree
        // Trunk
        for (var i = 0; i < height; i++)
        {
            SafeSetBlock(chunk, x, y + i, z, log);
        }

        // Leaves
        var leafStart = height - 3;
        var leafEnd = height;
        var radius = 2;

        for (var ly = leafStart; ly <= leafEnd; ly++)
        {
            //var yOffset = ly - leafEnd; // 0 at top, -3 at bottom
            var r = radius;
            if (ly == leafEnd) r = 1; // Top is smaller
            else if (ly == leafStart) r = 1; // Bottom is smaller (optional)

            for (var lx = -r; lx <= r; lx++)
            {
                for (var lz = -r; lz <= r; lz++)
                {
                    // Rounded shape
                    if (Math.Abs(lx) + Math.Abs(lz) <= r + 1) 
                    {
                        // Don't overwrite trunk
                        if (lx == 0 && lz == 0 && ly < height) continue;

                        SafeSetBlock(chunk, x + lx, y + ly, z + lz, leaves);
                    }
                }
            }
        }
    }

    private static void PlaceSpruceTree(ChunkData chunk, int x, int y, int z, int height)
    {
        // Trunk
        for (var i = 0; i < height; i++)
        {
            SafeSetBlock(chunk, x, y + i, z, BlockId.SpruceLog);
        }

        // Top leaf
        SafeSetBlock(chunk, x, y + height, z, BlockId.SpruceLeaves);

        // Leaves: Pyramidal shape
        // Start 3 blocks from bottom
        var leafStart = 3;
        
        for (var ly = height - 1; ly >= leafStart; ly--)
        {
            var distFromTop = height - ly;
            
            // Radius increases as we go down: 1, 1, 2, 2, 3, 3...
            var radius = (distFromTop + 1) / 2;
            
            // Cap radius to keep it looking like a tree
            if (radius > 3) radius = 3;
            if (height < 8 && radius > 2) radius = 2;

            for (var lx = -radius; lx <= radius; lx++)
            {
                for (var lz = -radius; lz <= radius; lz++)
                {
                    var dist = Math.Abs(lx) + Math.Abs(lz);
                    var place = false;

                    if (radius == 1)
                    {
                        // Cross shape
                        if (dist <= 1) place = true;
                    }
                    else if (radius == 2)
                    {
                        // Diamond/Square-ish
                        if (dist <= 3) place = true;
                    }
                    else // radius >= 3
                    {
                        // Larger Diamond
                        if (dist <= 5) place = true;
                    }

                    if (place)
                    {
                        if (lx == 0 && lz == 0) continue;
                        SafeSetBlock(chunk, x + lx, y + ly, z + lz, BlockId.SpruceLeaves);
                    }
                }
            }
        }
    }

    private static void SafeSetBlock(ChunkData chunk, int x, int y, int z, BlockId block)
    {
        if (ChunkData.IsWithinBounds(x, y, z))
        {
            var existing = chunk.GetBlock(x, y, z);
            // Only replace air or replaceable blocks (like grass)
            if (existing == BlockId.Air || existing.IsReplaceable())
            {
                chunk.SetBlock(x, y, z, block);
            }
        }
    }

    private static int Hash(int seed, int x, int z)
    {
        var h = seed + x * 374761393 + z * 668265263;
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }

    private static bool IsTree(VegetationType type) => type is VegetationType.TreeOak or VegetationType.TreeBirch or VegetationType.TreeSpruce or VegetationType.TreeJungle;
}
