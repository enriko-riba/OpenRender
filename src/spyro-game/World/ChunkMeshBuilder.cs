using OpenRender;

namespace SpyroGame.World;

/// <summary>
/// CPU implementation of the visibility + meshing logic.
/// Produces packed vertex/index buffers compatible with the existing renderer.
/// Performance optimized with List pooling to reduce GC pressure.
/// </summary>
internal static class ChunkMeshBuilder
{
    private const byte PLACEHOLDER_POS_X = 1 << 0;
    private const byte PLACEHOLDER_NEG_X = 1 << 1;
    private const byte PLACEHOLDER_POS_Z = 1 << 2;
    private const byte PLACEHOLDER_NEG_Z = 1 << 3;
    private const bool MirrorMissingNeighbors = false;
    private const uint DisabledLightValue = 0xFFFFFFFFu; // Use a value outside valid range (0-255)
    
    // Set to false in production to avoid debug string allocations in hot paths
    private static readonly bool VerboseBuilderLogging = false;

    // Performance optimization: Thread-local pooled lists to avoid allocations per mesh
    [ThreadStatic] private static List<uint>? t_opaqueVertices;
    [ThreadStatic] private static List<uint>? t_opaqueIndices;
    [ThreadStatic] private static List<uint>? t_translucentVertices;
    [ThreadStatic] private static List<uint>? t_translucentIndices;
    [ThreadStatic] private static List<uint>? t_waterIndices;
    [ThreadStatic] private static List<uint>? t_cubeletIndices;
    
    // Performance optimization: Thread-local pooled dictionary for neighbor chunk cache
    [ThreadStatic] private static Dictionary<int, ChunkVoxelDataCache.ChunkVoxelDataView>? t_neighborCache;

    private static readonly (int dx, int dy, int dz)[] FaceDirections =
    [
        (1, 0, 0),  // +X
        (-1, 0, 0), // -X
        (0, 1, 0),  // +Y
        (0, -1, 0), // -Y
        (0, 0, 1),  // +Z
        (0, 0, -1)  // -Z
    ];

    private static readonly (int x, int y, int z)[][] FaceCornerOffsets =
    [
        [(1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)], // +X
        [(0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0)], // -X
        [(0, 1, 0), (0, 1, 1), (1, 1, 1), (1, 1, 0)], // +Y
        [(0, 0, 1), (0, 0, 0), (1, 0, 0), (1, 0, 1)], // -Y
        [(1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1)], // +Z
        [(0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)]  // -Z
    ];

    /// <summary>
    /// Get or create thread-local pooled lists for mesh building.
    /// </summary>
    private static (List<uint> opaqueVerts, List<uint> opaqueIdx, List<uint> transVerts, List<uint> transIdx, List<uint> waterIdx, List<uint> cubeletIdx) GetPooledLists()
    {
        t_opaqueVertices ??= new List<uint>(16384);
        t_opaqueIndices ??= new List<uint>(16384);
        t_translucentVertices ??= new List<uint>(2048);
        t_translucentIndices ??= new List<uint>(2048);
        t_waterIndices ??= new List<uint>(2048);
        t_cubeletIndices ??= new List<uint>(2048);

        // Clear for reuse
        t_opaqueVertices.Clear();
        t_opaqueIndices.Clear();
        t_translucentVertices.Clear();
        t_translucentIndices.Clear();
        t_waterIndices.Clear();
        t_cubeletIndices.Clear();

        return (t_opaqueVertices, t_opaqueIndices, t_translucentVertices, t_translucentIndices, t_waterIndices, t_cubeletIndices);
    }

    public static bool TryBuild(ChunkMeshingJobSystem.ChunkMeshWorkItem workItem, ChunkVoxelDataCache cache, out CpuChunkMesh mesh)
    {
        mesh = null!;

        if (!cache.TryGetReadOnly(workItem.ChunkIndex, out var chunkView) || !chunkView.IsValid)
        {
            Log.Warn($"ChunkMeshBuilder: missing voxel cache for chunk {workItem.ChunkIndex} (mask=0x{workItem.PlaceholderMask:X2}) expectedVersion={workItem.CacheVersion} build={workItem.BuildId}");
            return false;
        }

        if (chunkView.ChunkIndex != workItem.ChunkIndex)
        {
            Log.Error($"ChunkMeshBuilder: cache chunk mismatch (expected {workItem.ChunkIndex}, got {chunkView.ChunkIndex}) seq={workItem.EnqueueId} build={workItem.BuildId}");
            return false;
        }

        if (workItem.CacheVersion > 0 && chunkView.Version != workItem.CacheVersion)
        {
            if (VerboseBuilderLogging)
            {
                Log.Debug($"ChunkMeshBuilder: version mismatch chunk={workItem.ChunkIndex} expected={workItem.CacheVersion} actual={chunkView.Version} seq={workItem.EnqueueId} build={workItem.BuildId}");
            }
        }

        // Performance optimization: Use thread-local pooled lists instead of allocating new ones
        var (opaqueVertices, opaqueIndices, translucentVertices, translucentIndices, waterIndices, cubeletIndices) = GetPooledLists();
        var sampler = new ChunkVoxelSampler(cache, chunkView, workItem);
        
        // Track maximum surface height across all columns for tighter frustum culling
        var maxSurfaceHeight = 0;

        // Optimization: Iterate columns (X, Z) first, then Y up to surface height
        // This allows us to skip the vast majority of air blocks above the terrain.
        // While Y-inner loop has a larger stride (256 bytes), the massive reduction in iterations
        // outweighs the cache locality cost.
        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                // Determine the maximum Y to check for this column
                // We need to go up to the highest opaque block (SurfaceHeight)
                // OR the water level (for ocean surfaces)
                // Plus 1 to ensure we check the air block *above* the surface for face culling
                // Plus extra safety margin to handle potential heightmap desync
                var surfaceHeight = chunkView.GetSurfaceHeight(x, z);
                var maxY = Math.Max(surfaceHeight + 2, VoxelHelper.WaterLevel + 1);
                
                // Track the maximum surface height for frustum culling optimization
                if (surfaceHeight > maxSurfaceHeight)
                {
                    maxSurfaceHeight = surfaceHeight;
                }
                
                // Clamp to chunk bounds
                maxY = Math.Min(maxY, VoxelHelper.ChunkYSize);

                for (var y = 0; y < maxY; y++)
                {
                    var block = sampler.SampleBlock(x, y, z);
                    if (!HasRenderableGeometry(block))
                    {
                        continue;
                    }

                    var isTranslucent = IsTranslucent(block);
                    var isLiquid = block.IsLiquid();
                    var isWater = block.IsWater();
                    
                    var targetVertexList = isTranslucent ? translucentVertices : opaqueVertices;
                    // Split translucent indices into Water and Other (Translucent)
                    var targetIndexList = isTranslucent ? (isWater ? waterIndices : translucentIndices) : opaqueIndices;

                    // Check if this block uses a special render shape
                    var renderShape = BlockRegistry.GetRenderShape(block);
                    if (renderShape == BlockRenderShape.CrossBillboard)
                    {
                        // Cross-billboard blocks always render (no face culling against neighbors)
                        // Generates 8 vertices (4 per quad × 2 quads) and 24 indices (double-sided)
                        // This equals 4 "faces" in the vertex/index counting system (24 indices ÷ 6 per face)
                        AppendCrossBillboard(targetVertexList, targetIndexList, sampler, block, x, y, z);
                        sampler.IncrementFaceCount(isTranslucent);
                        sampler.IncrementFaceCount(isTranslucent);
                        sampler.IncrementFaceCount(isTranslucent);
                        sampler.IncrementFaceCount(isTranslucent); // 4 faces total for cross-billboard
                        continue;
                    }
                    else if (renderShape == BlockRenderShape.Cubelet)
                    {
                        // Cubelet blocks (small 1/10th size cubes)
                        // Always render all 6 faces (no culling against neighbors)
                        // Use opaque vertices but separate index list
                        AppendCubelet(opaqueVertices, cubeletIndices, sampler, block, x, y, z);
                        // Increment face count (6 faces)
                        for (var i = 0; i < 6; i++) sampler.IncrementFaceCount(false);
                        continue;
                    }

                    for (uint face = 0; face < FaceDirections.Length; face++)
                    {
                        var (dx, dy, dz) = FaceDirections[(int)face];
                        var neighborBlock = sampler.SampleBlock(x + dx, y + dy, z + dz, out var neighborMissing);
                        if (isLiquid && neighborMissing)
                        {
                            // If the neighbor chunk isn't loaded/available yet, treating it as air causes
                            // liquid side faces to be emitted at the streaming boundary. Because liquids are
                            // blended (and not per-face sorted), these boundary "curtains" can accumulate and
                            // show up as dark rectangular artifacts.
                            //
                            // Instead, treat missing neighbor data as "same liquid" so we cull those faces.
                            neighborBlock = block;
                        }
                        
                        // LIQUID FACE CULLING:
                        // - Render top face (+Y) for water/lava surface ONLY when exposed to air/transparent
                        // - Render side/bottom faces when neighbor is air OR transparent (glass, ice, etc.)
                        // - Never render faces between two liquid blocks of the same type
                        // - Never render faces against opaque solid blocks (hidden anyway)
                        if (isLiquid)
                        {
                            //var isTopFace = face == 2; // +Y
                            
                            // Skip internal liquid-liquid faces (same liquid type)
                            if (neighborBlock.IsLiquid() && neighborBlock == block)
                            {
                                continue;
                            }
                            
                            // For ALL faces (including top), skip if neighbor is opaque solid
                            // This prevents lava at Y=0 from rendering faces against bedrock at Y=1
                            var neighborIsAir = neighborBlock.IsAir();
                            var neighborIsTransparent = neighborBlock.IsTransparent();
                            
                            if (!neighborIsAir && !neighborIsTransparent)
                            {
                                continue;
                            }
                        }
                        
                        if (!ShouldEmitFace(block, neighborBlock))
                        {
                            continue;
                        }

                        AppendFace(targetVertexList, targetIndexList, sampler, block, x, y, z, face);
                        sampler.IncrementFaceCount(isTranslucent);
                    }
                }
            }
        }
        var faceCount = sampler.FaceCount;
        var faceExplosionThreshold = VoxelHelper.ChunkVoxelCount * 5;
        if (faceCount > faceExplosionThreshold)
        {
            Log.Warn($"ChunkMeshBuilder: chunk {workItem.ChunkIndex} produced {faceCount} faces (>5x voxel count). Placeholder mask=0x{workItem.PlaceholderMask:X2}");
        }
        else if (faceCount == 0 && workItem.PlaceholderMask != 0)
        {
            Log.Debug($"ChunkMeshBuilder: chunk {workItem.ChunkIndex} built empty mesh with placeholder mask 0x{workItem.PlaceholderMask:X2} seq={workItem.EnqueueId} build={workItem.BuildId}");
        }

        var mergedVertices = new uint[opaqueVertices.Count + translucentVertices.Count];
        opaqueVertices.CopyTo(mergedVertices, 0);
        translucentVertices.CopyTo(mergedVertices, opaqueVertices.Count);

        var mergedIndices = new uint[opaqueIndices.Count + waterIndices.Count + translucentIndices.Count];
        opaqueIndices.CopyTo(mergedIndices, 0);
        
        var vertexOffset = opaqueVertices.Count / 2; // two uints per vertex
        
        // Append Water indices (offset by opaque vertex count)
        if (waterIndices.Count > 0)
        {
            for (var i = 0; i < waterIndices.Count; i++)
            {
                mergedIndices[opaqueIndices.Count + i] = waterIndices[i] + (uint)vertexOffset;
            }
        }
        
        // Append Translucent indices (offset by opaque vertex count)
        if (translucentIndices.Count > 0)
        {
            var waterOffset = opaqueIndices.Count + waterIndices.Count;
            for (var i = 0; i < translucentIndices.Count; i++)
            {
                mergedIndices[waterOffset + i] = translucentIndices[i] + (uint)vertexOffset;
            }
        }

        // Cubelet indices are no longer appended to the mesh
        // This prevents them from being included in the total face count and rendered incorrectly


        if (VerboseBuilderLogging)
        {
            Log.Debug($"ChunkMeshBuilder: chunk={workItem.ChunkIndex} faces={faceCount} translucentFaces={sampler.TranslucentFaceCount} mask=0x{workItem.PlaceholderMask:X2} cacheVer={chunkView.Version} seq={workItem.EnqueueId} build={workItem.BuildId}");
        }

        // Calculate face counts for CpuChunkMesh
        // Note: Indices are 6 per face (triangles)
        var waterFaceCount = waterIndices.Count / 6;
        var translucentFaceCount = translucentIndices.Count / 6;
        var cubeletFaceCount = cubeletIndices.Count / 6;
        // Total translucent faces tracked by sampler includes both water and other translucent
        // But we need to pass them separately to CpuChunkMesh
        
        mesh = new CpuChunkMesh(
            workItem.ChunkIndex,
            workItem.PlaceholderMask,
            mergedVertices,
            mergedIndices,
            faceCount,
            translucentFaceCount, // This is now just the non-water translucent faces
            waterFaceCount,       // New parameter
            Math.Max(maxSurfaceHeight + 1, VoxelHelper.WaterLevel + 1), // +1 for safety margin
            chunkView.Version,
            workItem.EnqueueId,
            workItem.BuildId);

        return true;
    }

    private static bool HasRenderableGeometry(BlockId block) => !block.IsAir();

    private static bool IsOpaque(BlockId block) => block.IsOpaque();

    /// <summary>
    /// Checks if a block should be rendered in the translucent pass.
    /// This includes blocks with the Translucent flag AND blocks using Blend rendering.
    /// AlphaTest blocks (torches, leaves, flowers) go to OPAQUE queue and use discard in shader.
    /// </summary>
    private static bool IsTranslucent(BlockId block)
    {
        // Check the BlockId translucent flag first (water, ice, glass)
        if (block.IsTranslucent())
            return true;
        
        // Only Blend needs translucent pass (Water, Stained Glass).
        // AlphaTest (Torches, Leaves) should be Opaque to write depth.
        var renderMethod = BlockRegistry.GetProperties(block).Render;
        return renderMethod is RenderMethod.Blend;
    }

    private static bool ShouldEmitFace(BlockId block, BlockId neighborBlock)
    {
        if (!HasRenderableGeometry(block))
        {
            return false;
        }

        var blockIsWater = block.IsWater();
        var neighborIsWater = neighborBlock.IsWater();

        if (!HasRenderableGeometry(neighborBlock))
        {
            // Neighbor is air/replaceable – always emit face
            return true;
        }

        if (blockIsWater)
        {
            // Water only renders against transparent, non-water neighbors (air, foliage, etc.)
            // Prevents extra water surfaces when capped by solids.
            if (!neighborBlock.IsTransparent())
            {
                return false;
            }

            return !neighborIsWater;
        }

        if (neighborIsWater)
        {
            // Solid blocks render against water (needed for shoreline seams)
            return true;
        }

        if (block == neighborBlock)
        {
            // If it's leaves (or any AlphaTest block), we want to see internal faces
            // because they have holes (cutout).
            // Glass/Water (Blend) should still cull.
            var props = BlockRegistry.GetProperties(block);
            if (props.Render == RenderMethod.AlphaTest)
            {
                return true;
            }

            return false;
        }

        if (IsOpaque(block) && IsOpaque(neighborBlock))
        {
            return false;
        }

        return true;
    }

    private static void AppendFace(List<uint> vertexScratch, List<uint> indexScratch, ChunkVoxelSampler sampler, BlockId block, int x, int y, int z, uint face)
    {
        var baseVertex = (uint)(vertexScratch.Count / 2);
        var offsets = FaceCornerOffsets[(int)face];

        // Get biome at this block position (same for all vertices of the face)
        var biome = sampler.SampleBiome(x, z);

        // Performance optimization: Compute all 4 corner light/AO values with a single pass
        // over the 9-block neighborhood instead of 4 separate passes with 4 samples each.
        Span<uint> ao = stackalloc uint[4];
        Span<uint> light = stackalloc uint[4];
        sampler.ComputeFaceLightingAndAO(face, x, y, z, ao, light);

        // Debug: Log light values being packed for faces near torch
        if (VerboseBuilderLogging && y == 42 && z == 15 && x <= 2)
        {
            var avgLight = (light[0] + light[1] + light[2] + light[3]) / 4;
            var avgBlock = ((light[0] >> 4) + (light[1] >> 4) + (light[2] >> 4) + (light[3] >> 4)) / 4;
            Log.Debug($"AppendFace: chunk={sampler.GetChunkIndex()} block=({x},{y},{z}) face={face} light=[0x{light[0]:X2},0x{light[1]:X2},0x{light[2]:X2},0x{light[3]:X2}] avgBlock={avgBlock}");
        }

        // WATER TOPMOST FLAG:
        // For water blocks, we need to tell the shader if this is the topmost water block
        // in a column. The shader uses this to lower the top vertices of side faces by 0.15
        // to match the lowered water surface (which prevents z-fighting).
        // 
        // Why only topmost? If we lowered ALL water side faces, stacked water blocks would
        // have gaps/overlaps at each boundary. By only lowering the topmost, the water
        // column renders correctly:
        // - Topmost water: side faces have lowered top vertices matching the surface
        // - Lower water: side faces are full height (but not visible - occluded by water above)
        //
        // Edge case: Glass wall next to water column - only the topmost water's side face
        // is visible through glass, and it correctly matches the lowered surface.
        uint offsetSeed = 0;
        if (block.IsWater())
        {
            var blockAbove = sampler.SampleBlock(x, y + 1, z);
            var isTopmostWater = !blockAbove.IsWater();
            if (isTopmostWater)
            {
                offsetSeed = 1u; // Bit 0 = "is topmost water" flag
            }
        }

        for (uint corner = 0; corner < 4; corner++)
        {
            var (ox, oy, oz) = offsets[corner];
            var vx = x + ox;
            var vy = y + oy;
            var vz = z + oz;
            
            vertexScratch.Add(PackVertexPosition(vx, vy, vz, face, ao[(int)corner], corner, offsetSeed));
            vertexScratch.Add(PackVertexAttributes(block, light[(int)corner], biome));
        }

        // Minecraft-style quad flip to avoid "bowtie" AO interpolation artifacts.
        // Compare sums of opposite corner AO values to decide triangulation.
        // Default diagonal: 0-2. Flipped diagonal: 1-3.
        var flipQuad = ao[0] + ao[2] < ao[1] + ao[3];
        if (flipQuad)
        {
            // Triangles: 1-2-3, 1-3-0 (diagonal from 1 to 3)
            indexScratch.Add(baseVertex + 1);
            indexScratch.Add(baseVertex + 2);
            indexScratch.Add(baseVertex + 3);
            indexScratch.Add(baseVertex + 1);
            indexScratch.Add(baseVertex + 3);
            indexScratch.Add(baseVertex);
        }
        else
        {
            // Triangles: 0-1-2, 0-2-3 (diagonal from 0 to 2)
            indexScratch.Add(baseVertex);
            indexScratch.Add(baseVertex + 1);
            indexScratch.Add(baseVertex + 2);
            indexScratch.Add(baseVertex);
            indexScratch.Add(baseVertex + 2);
            indexScratch.Add(baseVertex + 3);
        }
    }

    /// <summary>
    /// Append a cross-billboard (two crossed quads forming an X shape) for blocks like torches, flowers, saplings.
    /// The quads are positioned diagonally across the voxel from corner to corner.
    /// Generates 4 faces (16 vertices, 24 indices) for proper mesh buffer accounting.
    /// </summary>
    private static void AppendCrossBillboard(List<uint> vertexScratch, List<uint> indexScratch, ChunkVoxelSampler sampler, BlockId block, int x, int y, int z)
    {
        var biome = sampler.SampleBiome(x, z);
        
        // Sample light at the block's position (use the voxel's own light since it's transparent)
        var light = sampler.SamplePackedLight(x, y, z);
        
        // Debug: Log light value for torch at specific position
        if (block.IsEmissive() && VerboseBuilderLogging)
        {
            Log.Debug($"AppendCrossBillboard: Torch at ({x},{y},{z}) chunk={sampler.GetChunkIndex()} rawLight=0x{light:X8} sky={(light & 0xF)} block={(light >> 4) & 0xF}");
        }
        
        if (light == 0xFFFFFFFFu) // DisabledLightValue
        {
            // Fallback: sample above the block
            light = sampler.SamplePackedLight(x, y + 1, z);
        }
        // Use the packed light directly (preserve both sky and block light channels)
        var packedLight = (light != 0xFFFFFFFFu) ? light : 0xFFu;

        // Cross-billboard uses face index 6 (special marker) to indicate it's a billboard
        const uint billboardFace = 6u;
        const uint defaultAO = 0u; // No AO for billboards - they're transparent

        // Calculate a deterministic seed for the offset based on block position
        // This ensures all vertices of the billboard move together
        // NOTE: We exclude Y from the seed so that stacked vegetation (Sugar Cane, Tall Grass)
        // shares the same offset and stays connected.
        // 
        // Seed layout (5 bits total):
        // - Bits 0-3: Random variation seed (0-15)
        // - Bit 4: "Is top of stack" flag (1 = top, 0 = not top)
        //
        // Height shrinking should ONLY be applied to the topmost block of a stack
        // to avoid gaps between stacked vegetation blocks.
        var baseSeed = (uint)((x * 3129871) ^ (z * 116129791)) & 0xFu; // 4 bits for random
        
        // Check if this is the top of a vegetation stack
        // A block is the top if the block above is NOT the same billboard type
        var blockAbove = sampler.SampleBlock(x, y + 1, z);
        var isTopOfStack = blockAbove != block;
        
        var seed = baseSeed | (isTopOfStack ? 0x10u : 0u); // Set bit 4 if top of stack

        // Helper to add a quad (4 vertices, 6 indices) - one "face" in the mesh system
        void AddQuad(int x0, int y0, int z0, int x1, int y1, int z1, int x2, int y2, int z2, int x3, int y3, int z3)
        {
            var baseVertex = (uint)(vertexScratch.Count / 2);
            
            vertexScratch.Add(PackVertexPosition(x0, y0, z0, billboardFace, defaultAO, 0, seed));
            vertexScratch.Add(PackVertexAttributes(block, packedLight, biome));
            vertexScratch.Add(PackVertexPosition(x1, y1, z1, billboardFace, defaultAO, 1, seed));
            vertexScratch.Add(PackVertexAttributes(block, packedLight, biome));
            vertexScratch.Add(PackVertexPosition(x2, y2, z2, billboardFace, defaultAO, 2, seed));
            vertexScratch.Add(PackVertexAttributes(block, packedLight, biome));
            vertexScratch.Add(PackVertexPosition(x3, y3, z3, billboardFace, defaultAO, 3, seed));
            vertexScratch.Add(PackVertexAttributes(block, packedLight, biome));

            // Two triangles: 0-1-2, 0-2-3
            indexScratch.Add(baseVertex);
            indexScratch.Add(baseVertex + 1);
            indexScratch.Add(baseVertex + 2);
            indexScratch.Add(baseVertex);
            indexScratch.Add(baseVertex + 2);
            indexScratch.Add(baseVertex + 3);
        }

        // Quad 1 diagonal (0,0,0) to (1,1,1) - front face
        AddQuad(x, y, z, x, y + 1, z, x + 1, y + 1, z + 1, x + 1, y, z + 1);
        // Quad 1 diagonal - back face (reversed vertices)
        AddQuad(x + 1, y, z + 1, x + 1, y + 1, z + 1, x, y + 1, z, x, y, z);

        // Quad 2 diagonal (1,0,0) to (0,1,1) - front face
        AddQuad(x + 1, y, z, x + 1, y + 1, z, x, y + 1, z + 1, x, y, z + 1);
        // Quad 2 diagonal - back face (reversed vertices)
        AddQuad(x, y, z + 1, x, y + 1, z + 1, x + 1, y + 1, z, x + 1, y, z);
    }

    private static void AppendCubelet(List<uint> vertexScratch, List<uint> indexScratch, ChunkVoxelSampler sampler, BlockId block, int x, int y, int z)
    {
        var biome = sampler.SampleBiome(x, z);
        
        // Sample light at the block's position
        var light = sampler.SamplePackedLight(x, y, z);
        if (light == 0xFFFFFFFFu) light = sampler.SamplePackedLight(x, y + 1, z);
        var packedLight = (light != 0xFFFFFFFFu) ? light : 0xFFu;

        // Use standard face indices (0-5)
        // No AO for cubelets (too small)
        const uint defaultAO = 0u;

        // Helper to add a face
        void AddFace(uint face)
        {
            var baseVertex = (uint)(vertexScratch.Count / 2);
            var offsets = FaceCornerOffsets[(int)face];

            for (uint corner = 0; corner < 4; corner++)
            {
                var (ox, oy, oz) = offsets[corner];
                var vx = x + ox;
                var vy = y + oy;
                var vz = z + oz;
                
                // Pack vertex with standard face ID
                // The shader will use the separate draw call to identify it as a cubelet
                vertexScratch.Add(PackVertexPosition(vx, vy, vz, face, defaultAO, corner));
                vertexScratch.Add(PackVertexAttributes(block, packedLight, biome));
            }

            // Standard quad indices
            indexScratch.Add(baseVertex);
            indexScratch.Add(baseVertex + 1);
            indexScratch.Add(baseVertex + 2);
            indexScratch.Add(baseVertex);
            indexScratch.Add(baseVertex + 2);
            indexScratch.Add(baseVertex + 3);
        }

        // Add all 6 faces
        for (uint f = 0; f < 6; f++) AddFace(f);
    }

    /// <summary>
    /// Pack vertex position and metadata into a single uint.
    /// 
    /// Bit layout (32 bits total):
    /// - Bits 0-4:   X position within chunk (5 bits, 0-31)
    /// - Bits 5-13:  Y position (9 bits, 0-511)
    /// - Bits 14-18: Z position within chunk (5 bits, 0-31)
    /// - Bits 19-21: Face index (3 bits, 0-6 where 6=billboard)
    /// - Bits 22-24: Ambient occlusion (3 bits, 0-7)
    /// - Bits 25-26: Corner index (2 bits, 0-3)
    /// - Bits 27-31: Offset seed (5 bits, usage depends on block type):
    ///   
    ///   For BILLBOARDS (face == 6):
    ///     - Bits 0-3: Random variation seed for X/Z offset (0-15)
    ///     - Bit 4: "Is top of stack" flag for height shrinking
    ///   
    ///   For WATER blocks (blockId == 1):
    ///     - Bit 0: "Is topmost water" flag (1 = this water block has no water above)
    ///              Used by shader to determine if side face top vertices should be lowered
    ///              to match the lowered water surface. Only topmost water blocks need this
    ///              adjustment to avoid visual artifacts when viewed through glass or at edges.
    ///     - Bits 1-4: Reserved for future use
    ///   
    ///   For OTHER blocks: Currently unused (always 0)
    /// </summary>
    private static uint PackVertexPosition(int x, int y, int z, uint face, uint ao, uint corner, uint offsetSeed = 0)
    {
        var ux = (uint)x & 0x1Fu;
        var uy = (uint)y & 0x1FFu;
        var uz = (uint)z & 0x1Fu;
        return ux | (uy << 5) | (uz << 14) | ((face & 0x7u) << 19) | ((ao & 0x7u) << 22) | ((corner & 0x3u) << 25) | ((offsetSeed & 0x1Fu) << 27);
    }

    /// <summary>
    /// Pack vertex attributes into a single uint.
    /// Layout: bits 0-9: blockId (10 bits), bits 10-17: light (8 bits), bits 18-25: biomeId (8 bits), bit 26: emissive
    /// </summary>
    private static uint PackVertexAttributes(BlockId block, uint light, BiomeId biome)
        => (uint)block.GetId() | ((light & 0xFFu) << 10) | (((uint)biome & 0xFFu) << 18) | (block.IsEmissive() ? (1u << 26) : 0u);

    private sealed class ChunkVoxelSampler
    {
        private readonly ChunkVoxelDataCache cache;
        private readonly ChunkVoxelDataCache.ChunkVoxelDataView centerView;
        private readonly ChunkMeshingJobSystem.ChunkMeshWorkItem workItem;
        private readonly Dictionary<int, ChunkVoxelDataCache.ChunkVoxelDataView> neighborCache;
        private readonly int chunkX;
        private readonly int chunkZ;
        private readonly ChunkBiomeData? biomeData;

        internal ChunkVoxelSampler(ChunkVoxelDataCache cache, ChunkVoxelDataCache.ChunkVoxelDataView centerView, ChunkMeshingJobSystem.ChunkMeshWorkItem workItem)
        {
            this.cache = cache;
            this.centerView = centerView;
            this.workItem = workItem;
            chunkX = workItem.ChunkIndex % VoxelHelper.WorldChunksXZ;
            chunkZ = workItem.ChunkIndex / VoxelHelper.WorldChunksXZ;
            cache.TryGetBiomeData(workItem.ChunkIndex, out biomeData);
            
            // Use thread-static pooled dictionary to avoid allocation per mesh build
            t_neighborCache ??= new Dictionary<int, ChunkVoxelDataCache.ChunkVoxelDataView>(8);
            t_neighborCache.Clear();
            neighborCache = t_neighborCache;
        }

        public int GetChunkIndex() => workItem.ChunkIndex;
        public int FaceCount { get; private set; }
        public int TranslucentFaceCount { get; private set; }
        public int NeighborMisses { get; private set; }

        public void IncrementFaceCount(bool isTranslucent)
        {
            FaceCount++;
            if (isTranslucent)
            {
                TranslucentFaceCount++;
            }
        }

        public BlockId SampleBlock(int x, int y, int z)
            => SampleBlock(x, y, z, out _);

        public BlockId SampleBlock(int x, int y, int z, out bool missingNeighborData)
        {
            missingNeighborData = false;
            if (y < 0)
            {
                return BlockId.Stone;
            }

            if (y >= VoxelHelper.ChunkYSize)
            {
                return BlockId.Air;
            }

            if (centerView.IsWithinBounds(x, y, z))
            {
                return centerView.ReadVoxel(x, y, z);
            }

            if (MirrorMissingNeighbors && TryClonePlaceholder(x, z, out var cloneX, out var cloneZ))
            {
                var clampedY = Math.Clamp(y, 0, VoxelHelper.ChunkYSize - 1);
                return centerView.ReadVoxel(cloneX, clampedY, cloneZ);
            }

            if (ShouldTreatAsPlaceholderEdge(x, z))
            {
                // When neighbor is missing (placeholder edge), treat as AIR so faces ARE emitted.
                // When the neighbor loads, edge seam refresh will check if faces need to be
                // removed (if neighbor turned out to be solid) and trigger a remesh if needed.
                return BlockId.Air;
            }

            if (TryGetNeighborView(x, z, out var neighborView, out var localX, out var localZ))
            {
                if (neighborView.TryReadVoxel(localX, y, localZ, out var voxel))
                {
                    return voxel;
                }
            }

            // Neighbor lookup failed - track this for debugging
            NeighborMisses++;
            missingNeighborData = true;
            return BlockId.Air;
        }

        public uint SamplePackedLight(int x, int y, int z)
        {
            if (centerView.IsWithinBounds(x, y, z))
            {
                var light = centerView.ReadLight(x, y, z);
                if (VerboseBuilderLogging && y == 43 && z == 15 && x >= 0 && x <= 2)
                {
                    Log.Debug($"  SamplePackedLight: chunk={workItem.ChunkIndex} pos=({x},{y},{z}) FROM_CENTER light=0x{light:X2}");
                }
                return light;
            }

            if (TryGetNeighborView(x, z, out var neighborView, out var localX, out var localZ))
            {
                if (neighborView.IsWithinBounds(localX, y, localZ))
                {
                    var light = neighborView.ReadLight(localX, y, localZ);
                    if (VerboseBuilderLogging && y == 43 && z == 15)
                    {
                        Log.Debug($"  SamplePackedLight: chunk={workItem.ChunkIndex} pos=({x},{y},{z}) FROM_NEIGHBOR={neighborView.ChunkIndex} localPos=({localX},{y},{localZ}) light=0x{light:X2}");
                    }
                    return light;
                }
            }

            return DisabledLightValue;
        }

        public uint ComputeSmoothLight(uint face, uint corner, int x, int y, int z)
        {
            var (nx, ny, nz) = FaceDirections[(int)face];
            var (t1x, t1y, t1z, t2x, t2y, t2z) = GetFaceTangents(face);
            var (c1, c2) = GetCornerSigns(face, corner);

            // Coordinates of the 4 neighbors
            // Center (Base) - the air block directly in front of the face
            var bx = x + nx;
            var by = y + ny;
            var bz = z + nz;

            // Side 1
            var s1x = bx + t1x * c1;
            var s1y = by + t1y * c1;
            var s1z = bz + t1z * c1;

            // Side 2
            var s2x = bx + t2x * c2;
            var s2y = by + t2y * c2;
            var s2z = bz + t2z * c2;

            // Corner
            var cx = bx + t1x * c1 + t2x * c2;
            var cy = by + t1y * c1 + t2y * c2;
            var cz = bz + t1z * c1 + t2z * c2;

            var lBase = SamplePackedLight(bx, by, bz);
            var lSide1 = SamplePackedLight(s1x, s1y, s1z);
            var lSide2 = SamplePackedLight(s2x, s2y, s2z);
            var lCorner = SamplePackedLight(cx, cy, cz);

            // Check if side blocks are opaque - if both are, light cannot reach through the corner
            // This matches Minecraft's AO logic: diagonal corner is blocked when both adjacent sides are solid
            var side1Opaque = SampleBlock(s1x, s1y, s1z).IsOpaque();
            var side2Opaque = SampleBlock(s2x, s2y, s2z).IsOpaque();
            var cornerBlocked = side1Opaque && side2Opaque;

            // Filter out missing neighbors (DisabledLightValue)
            // If a neighbor is missing, we exclude it from the average to avoid "glowing" artifacts (if we used 15)
            // or "black" artifacts (if we used 0).
            // We average only the valid samples.
            
            var skySum = 0;
            var blockSum = 0;
            var count = 0;

            void AddSample(uint l)
            {
                if (l != DisabledLightValue)
                {
                    skySum += (int)(l & 0xF);
                    blockSum += (int)((l >> 4) & 0xF);
                    count++;
                }
            }

            AddSample(lBase);
            AddSample(lSide1);
            AddSample(lSide2);
            // Only include corner light if light can physically reach through (both sides not opaque)
            if (!cornerBlocked)
            {
                AddSample(lCorner);
            }

            if (count == 0)
            {
                // All neighbors missing - this typically happens at chunk boundaries when 
                // neighbor chunk data isn't in cache yet. The mesh will be incomplete and
                // should be remeshed once neighbors are available.
                
                if (y >= VoxelHelper.ChunkYSize - 1)
                {
                    return 15 | (0 << 4); // Full sky light at world top
                }
                
                // Sample block above (x, y+1, z) as fallback
                var fallbackLight = SamplePackedLight(x, y + 1, z);
                if (fallbackLight != DisabledLightValue)
                {
                    return fallbackLight;
                }
                
                // Default to 0 light - face will be dark, indicating missing neighbor data
                // This is correct behavior - the chunk will be remeshed when neighbors load
                return 0;
            }

            // Simple average
            var skyAvg = skySum / count;
            var blockAvg = blockSum / count;

            return (uint)(skyAvg | (blockAvg << 4));
        }

        /// <summary>
        /// Get the biome ID at the given local chunk coordinates.
        /// Uses per-column biome storage for voxel-resolution borders.
        /// </summary>
        public BiomeId SampleBiome(int localX, int localZ)
        {
            // Clamp to valid range
            localX = Math.Clamp(localX, 0, VoxelHelper.ChunkSideSize - 1);
            localZ = Math.Clamp(localZ, 0, VoxelHelper.ChunkSideSize - 1);

            // Prefer high-res per-column biomes when available.
            if (biomeData is not null)
            {
                return biomeData.GetBiomeAt(localX, localZ);
            }

            // Fallback: indicate missing biome data.
            return BiomeId.Unknown;
        }

        /// <summary>
        /// Performance-optimized method to compute light and AO for all 4 corners of a face.
        /// Instead of 4 corners × 4 samples = 16 lookups, we sample the 9-block neighborhood once
        /// and compute all corner values from those cached samples.
        /// </summary>
        public void ComputeFaceLightingAndAO(uint face, int x, int y, int z, Span<uint> ao, Span<uint> light)
        {
            var (nx, ny, nz) = FaceDirections[(int)face];
            var (t1x, t1y, t1z, t2x, t2y, t2z) = GetFaceTangents(face);
            
            // Base position (air block directly in front of the face)
            var bx = x + nx;
            var by = y + ny;
            var bz = z + nz;
            
            // DEBUG: Log when sampling from a position that might have torch light
            var debugThisFace = VerboseBuilderLogging && by == 43 && bz == 15 && bx >= 0 && bx <= 2;
            if (debugThisFace)
            {
                var rawLight = SamplePackedLight(bx, by, bz);
                Log.Debug($"ComputeFaceLightingAndAO: chunk={workItem.ChunkIndex} block=({x},{y},{z}) face={face} basePos=({bx},{by},{bz}) rawLight=0x{rawLight:X8}");
            }
            
            // Sample all 9 positions in the 3x3 grid around the face (in tangent space)
            // Layout (looking at face from outside):
            //   [6] [7] [8]   = (-1,+1) (0,+1) (+1,+1) in (t1, t2) space
            //   [3] [4] [5]   = (-1, 0) (0, 0) (+1, 0)
            //   [0] [1] [2]   = (-1,-1) (0,-1) (+1,-1)
            // Index 4 is the base position (center)
            
            Span<uint> lightSamples = stackalloc uint[9];
            Span<bool> opaqueSamples = stackalloc bool[9];
            
            // Sample the 3x3 grid
            for (var t2 = -1; t2 <= 1; t2++)
            {
                for (var t1 = -1; t1 <= 1; t1++)
                {
                    var idx = (t2 + 1) * 3 + (t1 + 1);
                    var sx = bx + t1x * t1 + t2x * t2;
                    var sy = by + t1y * t1 + t2y * t2;
                    var sz = bz + t1z * t1 + t2z * t2;
                    
                    lightSamples[idx] = SamplePackedLight(sx, sy, sz);
                    opaqueSamples[idx] = SampleBlock(sx, sy, sz).IsOpaque();
                }
            }
            
            // For each corner, compute AO and smooth light from the cached samples
            // Corner indices map to specific 2x2 quadrants of the 3x3 grid:
            // Each corner uses: center (4) + side1 + side2 + corner
            
            for (uint corner = 0; corner < 4; corner++)
            {
                // Get corner signs to determine which quadrant to use
                var (c1, c2) = GetCornerSigns(face, corner);
                
                // Map corner signs to grid indices
                // c1, c2 are each -1 or +1
                // Grid index for t1 offset: c1 = -1 -> 0, c1 = +1 -> 2
                // Grid index for t2 offset: c2 = -1 -> 0, c2 = +1 -> 2
                var t1Idx = c1 < 0 ? 0 : 2;
                var t2Idx = c2 < 0 ? 0 : 2;
                
                // Sample indices for this corner:
                // base = center = index 4
                // side1 = center + t1 offset = 4 + (t1Idx - 1) = 3 (if t1=-1) or 5 (if t1=+1)
                // side2 = center + t2 offset = 4 + (t2Idx - 1) * 3 = 1 (if t2=-1) or 7 (if t2=+1)
                // corner = t1 + t2 offset combined
                var baseIdx = 4;
                var side1Idx = 4 + (t1Idx - 1);  // 3 or 5
                var side2Idx = 4 + (t2Idx - 1) * 3;  // 1 or 7
                var cornerIdx = t2Idx * 3 + t1Idx;  // 0, 2, 6, or 8
                
                var side1Opaque = opaqueSamples[side1Idx];
                var side2Opaque = opaqueSamples[side2Idx];
                var cornerBlocked = side1Opaque && side2Opaque;
                
                // === AO Calculation ===
                var cornerOccluder = opaqueSamples[cornerIdx];
                uint aoVal;
                if (side1Opaque && side2Opaque)
                {
                    aoVal = 0; // Maximum occlusion
                }
                else
                {
                    var occluders = (side1Opaque ? 1 : 0) + (side2Opaque ? 1 : 0) + (cornerOccluder ? 1 : 0);
                    aoVal = (uint)(3 - occluders);
                }
                ao[(int)corner] = aoVal + 1; // Shader expects 1-4 range
                
                // === Smooth Light Calculation ===
                var lBase = lightSamples[baseIdx];
                var lSide1 = lightSamples[side1Idx];
                var lSide2 = lightSamples[side2Idx];
                var lCorner = lightSamples[cornerIdx];

                var skySum = 0;
                var blockSum = 0;
                var count = 0;

                void AddLightSample(uint l)
                {
                    if (l != DisabledLightValue)
                    {
                        skySum += (int)(l & 0xF);
                        blockSum += (int)((l >> 4) & 0xF);
                        count++;
                    }
                }
                
                AddLightSample(lBase);
                AddLightSample(lSide1);
                AddLightSample(lSide2);
                if (!cornerBlocked)
                {
                    AddLightSample(lCorner);
                }
                
                if (count == 0)
                {
                    // Fallback for missing neighbor data
                    if (y >= VoxelHelper.ChunkYSize - 1)
                    {
                        light[(int)corner] = 15 | (0 << 4);
                    }
                    else
                    {
                        var fallbackLight = SamplePackedLight(x, y + 1, z);
                        light[(int)corner] = fallbackLight != DisabledLightValue ? fallbackLight : 0u;
                    }
                }
                else
                {
                    light[(int)corner] = (uint)((skySum / count) | ((blockSum / count) << 4));
                }
            }
        }

        /// <summary>
        /// Minecraft-style ambient occlusion.
        /// For each vertex corner, check the 3 adjacent neighbor blocks (side1, side2, corner).
        /// AO level = 3 - (side1 + side2 + corner) if no sides occlude, or 0 if both sides occlude.
        /// Returns 0-3 mapped to 0-7 (0=fully occluded, 7=no occlusion).
        /// </summary>
        public uint ComputeAmbientOcclusion(uint face, uint corner, int x, int y, int z)
        {
            // Get the face normal direction
            var (nx, ny, nz) = FaceDirections[(int)face];
            
            // Position of the block whose face we're rendering
            // The vertex is at (x,y,z) relative to block origin
            // We need to check neighbors of the BLOCK, not the vertex
            
            // Get the two tangent directions for this face
            var (t1x, t1y, t1z, t2x, t2y, t2z) = GetFaceTangents(face);
            
            // Get corner-specific offsets (-1 or +1 in tangent directions)
            var (c1, c2) = GetCornerSigns(face, corner);
            
            // Sample the 3 neighbors that affect this corner's AO
            // side1: offset in first tangent direction
            // side2: offset in second tangent direction  
            // cornerBlock: offset in both tangent directions (diagonal)
            var side1 = IsOccluder(SampleBlock(x + nx + t1x * c1, y + ny + t1y * c1, z + nz + t1z * c1));
            var side2 = IsOccluder(SampleBlock(x + nx + t2x * c2, y + ny + t2y * c2, z + nz + t2z * c2));
            var cornerOccluder = IsOccluder(SampleBlock(x + nx + t1x * c1 + t2x * c2, y + ny + t1y * c1 + t2y * c2, z + nz + t1z * c1 + t2z * c2));
            
            // Minecraft formula: if both sides are solid, corner doesn't matter (full occlusion)
            // Otherwise, AO = 3 - (side1 + side2 + corner)
            int ao;
            if (side1 && side2)
            {
                ao = 0; // Maximum occlusion
            }
            else
            {
                var occluders = (side1 ? 1 : 0) + (side2 ? 1 : 0) + (cornerOccluder ? 1 : 0);
                ao = 3 - occluders;
            }
            
            // Shader expects: 0=darkest, 4=brightest
            // Our ao is 0-3, shift to 1-4 for shader compatibility
            return (uint)(ao + 1);
        }

        /// <summary>
        /// Check if a block is an occluder for AO purposes.
        /// Solid opaque blocks occlude; air and water don't.
        /// </summary>
        private static bool IsOccluder(BlockId block) =>
            // Air and Water don't occlude - use BlockId opaque flag
            block.IsOpaque();

        /// <summary>
        /// Get the two tangent vectors for a face (perpendicular to normal).
        /// t1 and t2 define the two axes along which the face extends.
        /// Returns (t1x, t1y, t1z, t2x, t2y, t2z).
        /// </summary>
        private static (int, int, int, int, int, int) GetFaceTangents(uint face) =>
            // For each face, define the two axes the face spans.
            // These are unit vectors along positive axis directions.
            face switch
            {
                0 => (0, 1, 0, 0, 0, 1),   // +X: spans Y and Z
                1 => (0, 1, 0, 0, 0, 1),   // -X: spans Y and Z
                2 => (1, 0, 0, 0, 0, 1),   // +Y: spans X and Z
                3 => (1, 0, 0, 0, 0, 1),   // -Y: spans X and Z
                4 => (1, 0, 0, 0, 1, 0),   // +Z: spans X and Y
                5 => (1, 0, 0, 0, 1, 0),   // -Z: spans X and Y
                _ => (1, 0, 0, 0, 1, 0)
            };

        /// <summary>
        /// Get the corner-specific sign multipliers for tangent directions.
        /// For a corner at position (ox, oy, oz), if the corner is at the low end of an axis (0),
        /// we sample in the negative direction (-1). If at high end (1), sample in positive (+1).
        /// This ensures we check the neighbors that could occlude light reaching this corner.
        /// </summary>
        private static (int, int) GetCornerSigns(uint face, uint corner)
        {
            // Get the corner offset from FaceCornerOffsets
            var (ox, oy, oz) = FaceCornerOffsets[(int)face][(int)corner];
            
            // Determine which axes vary for this face and compute signs
            return face switch
            {
                // +X and -X faces: vary in Y (t1) and Z (t2)
                0 or 1 => (oy == 0 ? -1 : 1, oz == 0 ? -1 : 1),
                
                // +Y and -Y faces: vary in X (t1) and Z (t2)
                2 or 3 => (ox == 0 ? -1 : 1, oz == 0 ? -1 : 1),
                
                // +Z and -Z faces: vary in X (t1) and Y (t2)
                4 or 5 => (ox == 0 ? -1 : 1, oy == 0 ? -1 : 1),
                
                _ => (0, 0)
            };
        }

        private bool TryClonePlaceholder(int x, int z, out int cloneX, out int cloneZ)
        {
            var mask = workItem.PlaceholderMask;
            if (x >= VoxelHelper.ChunkSideSize && (mask & PLACEHOLDER_POS_X) != 0)
            {
                cloneX = VoxelHelper.ChunkSideSize - 1;
                cloneZ = Math.Clamp(z, 0, VoxelHelper.ChunkSideSize - 1);
                return true;
            }

            if (x < 0 && (mask & PLACEHOLDER_NEG_X) != 0)
            {
                cloneX = 0;
                cloneZ = Math.Clamp(z, 0, VoxelHelper.ChunkSideSize - 1);
                return true;
            }

            if (z >= VoxelHelper.ChunkSideSize && (mask & PLACEHOLDER_POS_Z) != 0)
            {
                cloneZ = VoxelHelper.ChunkSideSize - 1;
                cloneX = Math.Clamp(x, 0, VoxelHelper.ChunkSideSize - 1);
                return true;
            }

            if (z < 0 && (mask & PLACEHOLDER_NEG_Z) != 0)
            {
                cloneZ = 0;
                cloneX = Math.Clamp(x, 0, VoxelHelper.ChunkSideSize - 1);
                return true;
            }

            cloneX = x;
            cloneZ = z;
            return false;
        }

        private bool ShouldTreatAsPlaceholderEdge(int x, int z)
        {
            var mask = workItem.PlaceholderMask;
            if (x < 0 && (mask & PLACEHOLDER_NEG_X) != 0)
            {
                return true;
            }

            if (x >= VoxelHelper.ChunkSideSize && (mask & PLACEHOLDER_POS_X) != 0)
            {
                return true;
            }

            if (z < 0 && (mask & PLACEHOLDER_NEG_Z) != 0)
            {
                return true;
            }

            return z >= VoxelHelper.ChunkSideSize && (mask & PLACEHOLDER_POS_Z) != 0;
        }

        private bool TryGetNeighborView(int x, int z, out ChunkVoxelDataCache.ChunkVoxelDataView neighborView, out int localX, out int localZ)
        {
            neighborView = default;
            localX = x;
            localZ = z;

            var nChunkX = chunkX;
            var nChunkZ = chunkZ;

            if (x < 0)
            {
                nChunkX--;
                localX += VoxelHelper.ChunkSideSize;
            }
            else if (x >= VoxelHelper.ChunkSideSize)
            {
                nChunkX++;
                localX -= VoxelHelper.ChunkSideSize;
            }

            if (z < 0)
            {
                nChunkZ--;
                localZ += VoxelHelper.ChunkSideSize;
            }
            else if (z >= VoxelHelper.ChunkSideSize)
            {
                nChunkZ++;
                localZ -= VoxelHelper.ChunkSideSize;
            }

            if (nChunkX == chunkX && nChunkZ == chunkZ)
            {
                return false;
            }

            if (nChunkX < 0 || nChunkX >= VoxelHelper.WorldChunksXZ || nChunkZ < 0 || nChunkZ >= VoxelHelper.WorldChunksXZ)
            {
                return false;
            }

            var neighborIndex = nChunkZ * VoxelHelper.WorldChunksXZ + nChunkX;
            if (!neighborCache.TryGetValue(neighborIndex, out neighborView))
            {
                if (!cache.TryGetReadOnly(neighborIndex, out neighborView))
                {
                    return false;
                }

                neighborCache[neighborIndex] = neighborView;
            }

            return true;
        }
    }
}
