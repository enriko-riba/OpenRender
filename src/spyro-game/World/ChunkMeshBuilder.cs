using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using OpenRender;

namespace SpyroGame.World;

/// <summary>
/// CPU implementation of the Phase 3 visibility + meshing logic.
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
    private const uint DisabledLightValue = 0x0Fu;
    private static bool VerboseBuilderLogging = false;
    private static bool DebugWaterFaces = true;

    // Performance optimization: Thread-local pooled lists to avoid allocations per mesh
    [ThreadStatic] private static List<uint>? t_opaqueVertices;
    [ThreadStatic] private static List<uint>? t_opaqueIndices;
    [ThreadStatic] private static List<uint>? t_translucentVertices;
    [ThreadStatic] private static List<uint>? t_translucentIndices;

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
    private static (List<uint> opaqueVerts, List<uint> opaqueIdx, List<uint> transVerts, List<uint> transIdx) GetPooledLists()
    {
        t_opaqueVertices ??= new List<uint>(16384);
        t_opaqueIndices ??= new List<uint>(16384);
        t_translucentVertices ??= new List<uint>(2048);
        t_translucentIndices ??= new List<uint>(2048);

        // Clear for reuse
        t_opaqueVertices.Clear();
        t_opaqueIndices.Clear();
        t_translucentVertices.Clear();
        t_translucentIndices.Clear();

        return (t_opaqueVertices, t_opaqueIndices, t_translucentVertices, t_translucentIndices);
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
            Log.Warn($"ChunkMeshBuilder: version mismatch chunk={workItem.ChunkIndex} expected={workItem.CacheVersion} actual={chunkView.Version} seq={workItem.EnqueueId} build={workItem.BuildId}");
        }

        // Performance optimization: Use thread-local pooled lists instead of allocating new ones
        var (opaqueVertices, opaqueIndices, translucentVertices, translucentIndices) = GetPooledLists();
        var sampler = new ChunkVoxelSampler(cache, chunkView, workItem);

       
        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    var block = sampler.SampleBlock(x, y, z);
                    if (!HasRenderableGeometry(block))
                    {
                        continue;
                    }

                    var isTranslucent = IsTranslucent(block);
                    var isLiquid = block.IsLiquid();
                    var targetVertexList = isTranslucent ? translucentVertices : opaqueVertices;
                    var targetIndexList = isTranslucent ? translucentIndices : opaqueIndices;

                    for (uint face = 0; face < FaceDirections.Length; face++)
                    {
                        // WATER OPTIMIZATION: Only render top face (+Y, face index 2) for liquids
                        // This creates a flat water surface without "water curtain" artifacts
                        // Skip side faces (0,1,4,5) and bottom face (3) for water
                        if (isLiquid && face != 2) // 2 = +Y (top face)
                        {
                            continue;
                        }

                        var (dx, dy, dz) = FaceDirections[(int)face];
                        var neighborBlock = sampler.SampleBlock(x + dx, y + dy, z + dz);
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

        var mergedIndices = new uint[opaqueIndices.Count + translucentIndices.Count];
        opaqueIndices.CopyTo(mergedIndices, 0);
        if (translucentIndices.Count > 0)
        {
            var vertexOffset = opaqueVertices.Count / 2; // two uints per vertex
            for (var i = 0; i < translucentIndices.Count; i++)
            {
                mergedIndices[opaqueIndices.Count + i] = translucentIndices[i] + (uint)vertexOffset;
            }
        }

        if (VerboseBuilderLogging)
        {
            Log.Debug($"ChunkMeshBuilder: chunk={workItem.ChunkIndex} faces={faceCount} translucentFaces={sampler.TranslucentFaceCount} mask=0x{workItem.PlaceholderMask:X2} cacheVer={chunkView.Version} seq={workItem.EnqueueId} build={workItem.BuildId}");
        }

        // Debug: Log neighbor misses and water face count
        if (DebugWaterFaces && (sampler.NeighborMisses > 0 || sampler.TranslucentFaceCount > 0))
        {
            Log.Info($"ChunkMeshBuilder DEBUG: chunk={workItem.ChunkIndex} waterFaces={sampler.TranslucentFaceCount} neighborMisses={sampler.NeighborMisses}");
        }

        mesh = new CpuChunkMesh(
            workItem.ChunkIndex,
            workItem.PlaceholderMask,
            mergedVertices,
            mergedIndices,
            faceCount,
            sampler.TranslucentFaceCount,
            chunkView.Version,
            workItem.EnqueueId,
            workItem.BuildId);

        return true;
    }

    private static bool HasRenderableGeometry(BlockId block) => !block.IsAir();

    private static bool IsOpaque(BlockId block) => block.IsOpaque();

    private static bool IsTranslucent(BlockId block) => block.IsTranslucent();

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
        Span<uint> ao = stackalloc uint[4];

        // Get biome at this block position (same for all vertices of the face)
        var biome = sampler.SampleBiome(x, z);

        for (uint corner = 0; corner < 4; corner++)
        {
            var (ox, oy, oz) = offsets[corner];
            var vx = x + ox;
            var vy = y + oy;
            var vz = z + oz;
            ao[(int)corner] = sampler.ComputeAmbientOcclusion(face, corner, x, y, z);
            var light = sampler.SamplePackedLight(vx, vy, vz);
            vertexScratch.Add(PackVertexPosition(vx, vy, vz, face, ao[(int)corner], corner));
            vertexScratch.Add(PackVertexAttributes(block, light, biome));
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

    private static uint PackVertexPosition(int x, int y, int z, uint face, uint ao, uint corner)
    {
        var ux = (uint)x & 0x1Fu;
        var uy = (uint)y & 0x1FFu;
        var uz = (uint)z & 0x1Fu;
        return ux | (uy << 5) | (uz << 14) | ((face & 0x7u) << 19) | ((ao & 0x7u) << 22) | ((corner & 0x3u) << 25);
    }

    /// <summary>
    /// Pack vertex attributes into a single uint.
    /// Layout: bits 0-7: blockId, bits 8-15: light, bits 16-23: biomeId
    /// </summary>
    private static uint PackVertexAttributes(BlockId block, uint light, BiomeId biome)
        => (uint)block.GetId() | ((light & 0xFFu) << 8) | (((uint)biome & 0xFFu) << 16);

    private sealed class ChunkVoxelSampler
    {
        private readonly ChunkVoxelDataCache cache;
        private readonly ChunkVoxelDataCache.ChunkVoxelDataView centerView;
        private readonly ChunkMeshingJobSystem.ChunkMeshWorkItem workItem;
        private readonly Dictionary<int, ChunkVoxelDataCache.ChunkVoxelDataView> neighborCache = [];
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
        }

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
        {
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
                return (BlockId)(ushort)centerView.ReadVoxel(x, y, z);
            }

            if (MirrorMissingNeighbors && TryClonePlaceholder(x, z, out var cloneX, out var cloneZ))
            {
                var clampedY = Math.Clamp(y, 0, VoxelHelper.ChunkYSize - 1);
                return (BlockId)(ushort)centerView.ReadVoxel(cloneX, clampedY, cloneZ);
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
                    return (BlockId)(ushort)voxel;
                }
            }

            // Neighbor lookup failed - track this for debugging
            NeighborMisses++;
            return BlockId.Air;
        }

        public uint SamplePackedLight(int x, int y, int z) => DisabledLightValue;

        /// <summary>
        /// Get the biome ID at the given local chunk coordinates.
        /// Uses the 4x4 biome grid (each cell covers 4x4 blocks).
        /// </summary>
        public BiomeId SampleBiome(int localX, int localZ)
        {
            if (biomeData is null)
            {
                return BiomeId.Plains; // Fallback if no biome data
            }
            // Clamp to valid range
            localX = Math.Clamp(localX, 0, VoxelHelper.ChunkSideSize - 1);
            localZ = Math.Clamp(localZ, 0, VoxelHelper.ChunkSideSize - 1);
            return biomeData.GetBiomeAt(localX, localZ);
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
        private static bool IsOccluder(BlockId block)
        {
            // Air and Water don't occlude - use BlockId opaque flag
            return block.IsOpaque();
        }
        
        /// <summary>
        /// Get the two tangent vectors for a face (perpendicular to normal).
        /// t1 and t2 define the two axes along which the face extends.
        /// Returns (t1x, t1y, t1z, t2x, t2y, t2z).
        /// </summary>
        private static (int, int, int, int, int, int) GetFaceTangents(uint face)
        {
            // For each face, define the two axes the face spans.
            // These are unit vectors along positive axis directions.
            return face switch
            {
                0 => (0, 1, 0, 0, 0, 1),   // +X: spans Y and Z
                1 => (0, 1, 0, 0, 0, 1),   // -X: spans Y and Z
                2 => (1, 0, 0, 0, 0, 1),   // +Y: spans X and Z
                3 => (1, 0, 0, 0, 0, 1),   // -Y: spans X and Z
                4 => (1, 0, 0, 0, 1, 0),   // +Z: spans X and Y
                5 => (1, 0, 0, 0, 1, 0),   // -Z: spans X and Y
                _ => (1, 0, 0, 0, 1, 0)
            };
        }
        
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
