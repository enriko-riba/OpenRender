using System;
using System.Collections.Generic;
using OpenRender;

namespace SpyroGame.World;

/// <summary>
/// CPU implementation of the Phase 3 visibility + meshing logic.
/// Produces packed vertex/index buffers compatible with the existing renderer.
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

        var vertexScratch = new List<uint>(8192);
        var indexScratch = new List<uint>(8192);
        var sampler = new ChunkVoxelSampler(cache, chunkView, workItem);

        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    var descriptor = sampler.SampleDescriptor(x, y, z);
                    if (!IsSolid(descriptor))
                    {
                        continue;
                    }

                    for (uint face = 0; face < FaceDirections.Length; face++)
                    {
                        var (dx, dy, dz) = FaceDirections[(int)face];
                        var neighborDescriptor = sampler.SampleDescriptor(x + dx, y + dy, z + dz);
                        if (IsSolid(neighborDescriptor))
                        {
                            continue;
                        }

                        AppendFace(vertexScratch, indexScratch, sampler, descriptor, x, y, z, face);
                        sampler.IncrementFaceCount();
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

        if (VerboseBuilderLogging)
        {
            Log.Debug($"ChunkMeshBuilder: chunk={workItem.ChunkIndex} faces={faceCount} mask=0x{workItem.PlaceholderMask:X2} cacheVer={chunkView.Version} seq={workItem.EnqueueId} build={workItem.BuildId}");
        }

        mesh = new CpuChunkMesh(
            workItem.ChunkIndex,
            workItem.PlaceholderMask,
            [.. vertexScratch],
            [.. indexScratch],
            faceCount,
            chunkView.Version,
            workItem.EnqueueId,
            workItem.BuildId);

        return true;
    }

    private static bool IsSolid(byte descriptor) => descriptor > (byte)BlockDescriptor.Water;

    private static void AppendFace(List<uint> vertexScratch, List<uint> indexScratch, ChunkVoxelSampler sampler, byte descriptor, int x, int y, int z, uint face)
    {
        var baseVertex = (uint)(vertexScratch.Count / 2);
        var offsets = FaceCornerOffsets[(int)face];

        for (uint corner = 0; corner < 4; corner++)
        {
            var (ox, oy, oz) = offsets[corner];
            var vx = x + ox;
            var vy = y + oy;
            var vz = z + oz;
            var ao = sampler.ComputeAmbientOcclusion(face, corner, x, y, z);
            var light = sampler.SamplePackedLight(vx, vy, vz);
            vertexScratch.Add(PackVertexPosition(vx, vy, vz, face, ao, corner));
            vertexScratch.Add(PackVertexAttributes(descriptor, light));
        }

        indexScratch.Add(baseVertex);
        indexScratch.Add(baseVertex + 1);
        indexScratch.Add(baseVertex + 2);
        indexScratch.Add(baseVertex);
        indexScratch.Add(baseVertex + 2);
        indexScratch.Add(baseVertex + 3);
    }

    private static uint PackVertexPosition(int x, int y, int z, uint face, uint ao, uint corner)
    {
        var ux = (uint)x & 0x1Fu;
        var uy = (uint)y & 0x1FFu;
        var uz = (uint)z & 0x1Fu;
        return ux | (uy << 5) | (uz << 14) | ((face & 0x7u) << 19) | ((ao & 0x7u) << 22) | ((corner & 0x3u) << 25);
    }

    private static uint PackVertexAttributes(byte descriptor, uint light)
        => (uint)descriptor | ((light & 0xFFu) << 8);

    private sealed class ChunkVoxelSampler
    {
        private readonly ChunkVoxelDataCache cache;
        private readonly ChunkVoxelDataCache.ChunkVoxelDataView centerView;
        private readonly ChunkMeshingJobSystem.ChunkMeshWorkItem workItem;
        private readonly Dictionary<int, ChunkVoxelDataCache.ChunkVoxelDataView> neighborCache = [];
        private readonly int chunkX;
        private readonly int chunkZ;

        internal ChunkVoxelSampler(ChunkVoxelDataCache cache, ChunkVoxelDataCache.ChunkVoxelDataView centerView, ChunkMeshingJobSystem.ChunkMeshWorkItem workItem)
        {
            this.cache = cache;
            this.centerView = centerView;
            this.workItem = workItem;
            chunkX = workItem.ChunkIndex % VoxelHelper.WorldChunksXZ;
            chunkZ = workItem.ChunkIndex / VoxelHelper.WorldChunksXZ;
        }

        public int FaceCount { get; private set; }

        public void IncrementFaceCount() => FaceCount++;

        public byte SampleDescriptor(int x, int y, int z)
        {
            if (y < 0)
            {
                return (byte)BlockDescriptor.Subsurface;
            }

            if (y >= VoxelHelper.ChunkYSize)
            {
                return (byte)BlockDescriptor.Air;
            }

            if (centerView.IsWithinBounds(x, y, z))
            {
                return (byte)(centerView.ReadVoxel(x, y, z) & 0xFF);
            }

            if (MirrorMissingNeighbors && TryClonePlaceholder(x, z, out var cloneX, out var cloneZ))
            {
                var clampedY = Math.Clamp(y, 0, VoxelHelper.ChunkYSize - 1);
                return (byte)(centerView.ReadVoxel(cloneX, clampedY, cloneZ) & 0xFF);
            }

            if (ShouldTreatAsPlaceholderEdge(x, z))
            {
                return (byte)BlockDescriptor.Air;
            }

            if (TryGetNeighborView(x, z, out var neighborView, out var localX, out var localZ))
            {
                if (neighborView.TryReadVoxel(localX, y, localZ, out var voxel))
                {
                    return (byte)(voxel & 0xFF);
                }
            }

            return (byte)BlockDescriptor.Air;
        }

        public uint SamplePackedLight(int x, int y, int z) => DisabledLightValue;

        public uint ComputeAmbientOcclusion(uint face, uint corner, int x, int y, int z) => 7u;

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
