using OpenRender;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.Client.Rendering;
using SpyroGame.World;
using SpyroGame.World.Registry;
using System.Collections.Concurrent;

namespace SpyroGame.Client.Terrain;

/// <summary>
/// Client-side terrain view:
/// - Receives voxel chunk payloads from the server
/// - Builds meshes on background threads
/// - Uploads meshes to GPU buffers and drives <see cref="VoxelTerrainRenderer"/>
///
/// This is intentionally NOT a streaming system; the server decides what is loaded/unloaded.
/// </summary>
public sealed class ClientTerrainSystem : IDisposable
{
    private readonly ChunkVoxelDataCache voxelCache = new();
    private readonly ChunkMeshingJobSystem meshingJobs;
    private readonly Dictionary<int, ChunkDescriptor> activeChunks = [];
    private readonly ConcurrentDictionary<int, ChunkMesh> pendingMeshes = new();

    // Collision rebuilding is CPU-expensive (256 columns per chunk). Do it incrementally to avoid frame hitches.
    private readonly Dictionary<int, int> pendingCollisionRebuildNextColumnByChunk = [];

    private TerrainMeshBufferManager? meshBuffers;
    private VoxelTerrainRenderer? terrainRenderer;

    public CollisionManager CollisionManager { get; } = new();

    public int ActiveChunkCount => activeChunks.Count;
    public int PendingMeshCount => pendingMeshes.Count;

    public int[] GetActiveChunkIndicesSnapshot()
    {
        if (activeChunks.Count == 0)
        {
            return [];
        }

        var result = new int[activeChunks.Count];
        var i = 0;
        foreach (var idx in activeChunks.Keys)
        {
            result[i++] = idx;
        }

        return result;
    }

    public int[] GetReadyChunkIndicesSnapshot()
    {
        if (activeChunks.Count == 0)
        {
            return [];
        }

        var result = new List<int>(capacity: Math.Max(16, activeChunks.Count));
        foreach (var kvp in activeChunks)
        {
            if (kvp.Value.State == TerrainChunkState.Ready)
            {
                result.Add(kvp.Key);
            }
        }

        return result.ToArray();
    }

    public int ReadyChunkCount
    {
        get
        {
            var count = 0;
            foreach (var kvp in activeChunks)
            {
                if (kvp.Value.State == TerrainChunkState.Ready)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public ClientTerrainSystem()
    {
        meshingJobs = new ChunkMeshingJobSystem(voxelCache);
    }

    public VoxelTerrainRenderer TerrainRenderer
        => terrainRenderer ?? throw new InvalidOperationException("ClientTerrainSystem not initialized");

    public BiomeId GetBiomeAtWorldPos(int worldX, int worldZ)
        => voxelCache.GetBiomeAtWorldPos(worldX, worldZ);

    public void InitializeGraphics()
    {
        meshBuffers?.Dispose();

        voxelCache.Clear();
        meshingJobs.DrainPendingWorkItems();
        activeChunks.Clear();
        pendingMeshes.Clear();

        var absoluteMaxRadius = Math.Clamp(VoxelHelper.MaxDistanceInChunks + 4, 1, VoxelHelper.WorldChunksXZ);
        var maxActiveChunks = CalculateMaxViewChunksForRadius(absoluteMaxRadius);

        meshBuffers = new TerrainMeshBufferManager();
        meshBuffers.AllocateBuffers(maxActiveChunks);

        terrainRenderer = new VoxelTerrainRenderer();
        terrainRenderer.InitializeBlockTextures();
    }

    public void ApplyChunkPayloadBytes(int chunkIndex, byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        var data = ChunkData.Deserialize(reader);

        if (data.ChunkIndex != chunkIndex)
        {
            Log.Warn($"ClientTerrainSystem: Payload chunk index mismatch (msg={chunkIndex}, data={data.ChunkIndex})");
            data.ChunkIndex = chunkIndex;
        }

        voxelCache.Store(data);

        // Optional biome payload footer (network-only).
        // Server may append per-column biome ids after the ChunkData payload.
        if (ms.Position < ms.Length)
        {
            try
            {
                const uint biomeMagic = 0x4D4F4942; // 'BIOM' in little-endian
                var magic = reader.ReadUInt32();
                if (magic == biomeMagic)
                {
                    var biomeVersion = reader.ReadByte();
                    if (biomeVersion == 1)
                    {
                        var len = reader.ReadInt32();
                        if (len == ChunkBiomeData.ColumnCount)
                        {
                            var biomeData = new ChunkBiomeData();
                            for (var i = 0; i < ChunkBiomeData.ColumnCount; i++)
                            {
                                biomeData.ColumnBiomes[i] = (BiomeId)reader.ReadByte();
                            }
                            voxelCache.StoreBiomeData(chunkIndex, biomeData);
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed/partial footer; chunk voxel data is still valid.
            }
        }
        EnqueueCollisionRebuild(chunkIndex);

        if (!voxelCache.TryGetVersion(chunkIndex, out var version))
        {
            version = 0;
        }

        if (!activeChunks.TryGetValue(chunkIndex, out var descriptor))
        {
            descriptor = new ChunkDescriptor
            {
                ChunkIndex = chunkIndex,
                AtlasOffset = 0,
                IndexOffset = 0,
                CommandSlot = -1,
                VisibleVoxelCount = 0,
                State = TerrainChunkState.HasTerrain,
                GenerationStartFrame = 0,
                Fence = 0,
                MaxSurfaceHeight = 0,
            };
            activeChunks[chunkIndex] = descriptor;
        }

        // Enqueue CPU meshing.
        // IMPORTANT: propagate boundary light so chunk-border faces don't go black when neighbors are present.
        meshingJobs.Enqueue(chunkIndex, placeholderMask: 0, cacheVersion: version, propagateLight: true);

        // When a chunk arrives, it can change lighting across borders.
        // Re-mesh any already-present cardinal neighbors so seams resolve as streaming completes.
        foreach (var neighborIdx in GetCardinalNeighborChunkIndices(chunkIndex))
        {
            if (!activeChunks.ContainsKey(neighborIdx))
            {
                continue;
            }

            if (!voxelCache.TryGetVersion(neighborIdx, out var neighborVersion))
            {
                neighborVersion = 0;
            }

            meshingJobs.Enqueue(neighborIdx, placeholderMask: 0, cacheVersion: neighborVersion, propagateLight: true);
        }
    }

    public bool TryApplyPredictedBlockEdit(Vector3i globalPosition, BlockId blockId)
    {
        if (!VoxelHelper.IsGlobalPositionInWorld(globalPosition))
        {
            return false;
        }

        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(globalPosition);
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIndex);

        var localX = globalPosition.X - chunkPos.X;
        var localZ = globalPosition.Z - chunkPos.Z;
        var localY = globalPosition.Y;

        if (localX is < 0 or >= VoxelHelper.ChunkSideSize ||
            localZ is < 0 or >= VoxelHelper.ChunkSideSize ||
            localY is < 0 or >= VoxelHelper.ChunkYSize)
        {
            return false;
        }

        if (!voxelCache.TryAcquireChunkData(chunkIndex, out var lease))
        {
            return false;
        }

        using (lease)
        {
            var data = lease.Data;
            var old = data.GetBlock(localX, localY, localZ);
            if (old == blockId)
            {
                return true;
            }

            data.SetBlock(localX, localY, localZ, blockId);

            // Update collision immediately for better interaction/picking responsiveness.
            CollisionManager.RebuildColumnFromVoxelData(chunkIndex, localX, localZ, data);
        }

        if (!voxelCache.TryGetVersion(chunkIndex, out var version))
        {
            version = 0;
        }

        if (!activeChunks.ContainsKey(chunkIndex))
        {
            activeChunks[chunkIndex] = new ChunkDescriptor
            {
                ChunkIndex = chunkIndex,
                AtlasOffset = 0,
                IndexOffset = 0,
                CommandSlot = -1,
                VisibleVoxelCount = 0,
                State = TerrainChunkState.HasTerrain,
                GenerationStartFrame = 0,
                Fence = 0,
                MaxSurfaceHeight = 0,
            };
        }

        meshingJobs.Enqueue(chunkIndex, placeholderMask: 0, cacheVersion: version, propagateLight: true);

        foreach (var neighborIdx in GetCardinalNeighborChunkIndices(chunkIndex))
        {
            if (!activeChunks.ContainsKey(neighborIdx))
            {
                continue;
            }

            if (!voxelCache.TryGetVersion(neighborIdx, out var neighborVersion))
            {
                neighborVersion = 0;
            }

            meshingJobs.Enqueue(neighborIdx, placeholderMask: 0, cacheVersion: neighborVersion, propagateLight: true);
        }

        return true;
    }

    private void EnqueueCollisionRebuild(int chunkIndex) =>
        // Restart rebuild from column 0 whenever new data arrives for this chunk.
        pendingCollisionRebuildNextColumnByChunk[chunkIndex] = 0;

    private static int[] GetCardinalNeighborChunkIndices(int chunkIndex)
    {
        var chunkX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var chunkZ = chunkIndex / VoxelHelper.WorldChunksXZ;

        var results = new List<int>(capacity: 4);

        if (chunkX > 0) results.Add(chunkIndex - 1);
        if (chunkX + 1 < VoxelHelper.WorldChunksXZ) results.Add(chunkIndex + 1);
        if (chunkZ > 0) results.Add(chunkIndex - VoxelHelper.WorldChunksXZ);
        if (chunkZ + 1 < VoxelHelper.WorldChunksXZ) results.Add(chunkIndex + VoxelHelper.WorldChunksXZ);

        return results.ToArray();
    }

    public void UnloadChunk(int chunkIndex)
    {
        // Release voxel and collision data.
        voxelCache.TryRelease(chunkIndex);
        CollisionManager.RemoveChunkData(chunkIndex);
        pendingMeshes.TryRemove(chunkIndex, out _);
        pendingCollisionRebuildNextColumnByChunk.Remove(chunkIndex);

        if (!activeChunks.TryGetValue(chunkIndex, out var desc))
        {
            return;
        }

        activeChunks.Remove(chunkIndex);

        if (meshBuffers == null)
        {
            return;
        }

        var oldFaceCount = Math.Max(0, desc.VisibleVoxelCount);
        var oldVertexCount = oldFaceCount * 4;
        var oldIndexCount = oldFaceCount * 6;

        if (desc.CommandSlot >= 0)
        {
            meshBuffers.FreeCommandSlot(desc.CommandSlot);
        }

        if (oldVertexCount > 0 && desc.AtlasOffset >= 0)
        {
            meshBuffers.FreeVertexRegion((uint)desc.AtlasOffset, (uint)oldVertexCount);
        }

        if (oldIndexCount > 0 && desc.IndexOffset >= 0)
        {
            meshBuffers.FreeIndexRegion((uint)desc.IndexOffset, (uint)oldIndexCount);
        }

        RefreshRendererBuffers("unload");
    }

    public void UpdateUploads(int maxUploadsPerFrame = 8)
    {
        if (meshBuffers == null)
        {
            return;
        }

        meshBuffers.ResetFrameStats();

        // Drain completed meshes into pending dict (latest wins).
        while (meshingJobs.TryDequeueResult(out var mesh) && mesh != null)
        {
            pendingMeshes[mesh.ChunkIndex] = mesh;
        }

        var uploaded = 0;
        foreach (var kvp in pendingMeshes)
        {
            if (uploaded >= maxUploadsPerFrame)
            {
                break;
            }

            if (TryUploadCpuMesh(kvp.Value))
            {
                pendingMeshes.TryRemove(kvp.Key, out _);
                uploaded++;
            }
        }

        // Rebuild a limited number of collision columns per frame to avoid hitching.
        // Collision is used for picking/interaction; being a few frames behind is OK.
        ProcessCollisionRebuildBudget(maxColumnsPerFrame: 64);
    }

    private void ProcessCollisionRebuildBudget(int maxColumnsPerFrame)
    {
        if (maxColumnsPerFrame <= 0 || pendingCollisionRebuildNextColumnByChunk.Count == 0)
        {
            return;
        }

        var processed = 0;
        var completedChunks = new List<int>(capacity: 4);

        foreach (var kvp in pendingCollisionRebuildNextColumnByChunk)
        {
            if (processed >= maxColumnsPerFrame)
            {
                break;
            }

            var chunkIndex = kvp.Key;
            var next = kvp.Value;

            if (!voxelCache.TryAcquireChunkData(chunkIndex, out var lease))
            {
                completedChunks.Add(chunkIndex);
                continue;
            }

            using (lease)
            {
                var data = lease.Data;
                while (next < VoxelHelper.ChunkSideSizeSquare && processed < maxColumnsPerFrame)
                {
                    var x = next % VoxelHelper.ChunkSideSize;
                    var z = next / VoxelHelper.ChunkSideSize;
                    CollisionManager.RebuildColumnFromVoxelData(chunkIndex, x, z, data);
                    next++;
                    processed++;
                }
            }

            if (next >= VoxelHelper.ChunkSideSizeSquare)
            {
                completedChunks.Add(chunkIndex);
            }
            else
            {
                pendingCollisionRebuildNextColumnByChunk[chunkIndex] = next;
            }
        }

        foreach (var chunkIndex in completedChunks)
        {
            pendingCollisionRebuildNextColumnByChunk.Remove(chunkIndex);
        }
    }

    public void Dispose()
    {
        meshingJobs.Dispose();
        voxelCache.Dispose();
        meshBuffers?.Dispose();
        terrainRenderer?.Dispose();
    }

    private static int CalculateMaxViewChunksForRadius(int radius)
    {
        var diameter = radius * 2 + 1;
        return diameter * diameter;
    }

    private bool TryUploadCpuMesh(ChunkMesh mesh)
    {
        if (meshBuffers == null || terrainRenderer == null)
        {
            return false;
        }

        if (!activeChunks.TryGetValue(mesh.ChunkIndex, out var descriptor))
        {
            voxelCache.TryRelease(mesh.ChunkIndex);
            return true;
        }

        var faceCount = Math.Max(0, mesh.VisibleFaceCount);
        var waterFaceCount = Math.Clamp(mesh.WaterFaceCount, 0, faceCount);
        var translucentFaceCount = Math.Clamp(mesh.TranslucentFaceCount, 0, faceCount - waterFaceCount);
        var alphaTestFaceCount = Math.Clamp(mesh.AlphaTestFaceCount, 0, faceCount - waterFaceCount - translucentFaceCount);
        var opaqueFaceCount = Math.Max(0, faceCount - waterFaceCount - translucentFaceCount - alphaTestFaceCount);
        var vertexCount = faceCount * 4;
        var indexCount = faceCount * 6;

        if (mesh.IndexData.Length != indexCount)
        {
            indexCount = mesh.IndexData.Length;
            faceCount = indexCount / 6;
            vertexCount = faceCount * 4;
            waterFaceCount = Math.Clamp(mesh.WaterFaceCount, 0, faceCount);
            translucentFaceCount = Math.Clamp(mesh.TranslucentFaceCount, 0, Math.Max(0, faceCount - waterFaceCount));
            alphaTestFaceCount = Math.Clamp(mesh.AlphaTestFaceCount, 0, Math.Max(0, faceCount - waterFaceCount - translucentFaceCount));
            opaqueFaceCount = Math.Max(0, faceCount - waterFaceCount - translucentFaceCount - alphaTestFaceCount);
        }

        var expectedVertexEntries = vertexCount * 2;

        var vertexSpan = ReadOnlySpan<uint>.Empty;
        if (vertexCount > 0 && mesh.VertexData.Length > 0)
        {
            var safeLength = Math.Min(expectedVertexEntries, mesh.VertexData.Length);
            vertexSpan = mesh.VertexData.AsSpan(0, safeLength);
        }

        var indexSpan = ReadOnlySpan<uint>.Empty;
        if (indexCount > 0 && mesh.IndexData.Length > 0)
        {
            var safeLength = Math.Min(indexCount, mesh.IndexData.Length);
            indexSpan = mesh.IndexData.AsSpan(0, safeLength);
        }

        // Allocate new regions before upload; don't free old until after commands updated.
        var oldVertexOffset = descriptor.AtlasOffset;
        var oldIndexOffset = descriptor.IndexOffset;
        var oldFaceCount = Math.Max(0, descriptor.VisibleVoxelCount);
        var oldVertexCount = oldFaceCount * 4;
        var oldIndexCount = oldFaceCount * 6;

        var vertexOffset = vertexCount > 0 ? (int)meshBuffers.AllocateVertexRegion((uint)vertexCount) : -1;
        var indexOffset = indexCount > 0 ? (int)meshBuffers.AllocateIndexRegion((uint)indexCount) : -1;

        if (!meshBuffers.UploadMeshData(vertexSpan, vertexOffset, indexSpan, indexOffset))
        {
            if (vertexOffset >= 0) meshBuffers.FreeVertexRegion((uint)vertexOffset, (uint)vertexCount);
            if (indexOffset >= 0) meshBuffers.FreeIndexRegion((uint)indexOffset, (uint)indexCount);
            return false;
        }

        var slot = descriptor.CommandSlot >= 0 ? descriptor.CommandSlot : meshBuffers.AllocateCommandSlot();
        WriteIndirectCommands(slot, mesh.ChunkIndex, vertexOffset, indexOffset, (uint)opaqueFaceCount, (uint)alphaTestFaceCount, (uint)waterFaceCount, (uint)translucentFaceCount);

        activeChunks[mesh.ChunkIndex] = new ChunkDescriptor
        {
            ChunkIndex = mesh.ChunkIndex,
            AtlasOffset = vertexOffset >= 0 ? vertexOffset : 0,
            IndexOffset = indexOffset >= 0 ? indexOffset : 0,
            CommandSlot = slot,
            VisibleVoxelCount = faceCount,
            State = TerrainChunkState.Ready,
            GenerationStartFrame = 0,
            Fence = 0,
            MaxSurfaceHeight = mesh.MaxSurfaceHeight,
        };

        if (oldFaceCount > 0)
        {
            if (oldVertexCount > 0 && oldVertexOffset >= 0)
            {
                meshBuffers.FreeVertexRegion((uint)oldVertexOffset, (uint)oldVertexCount);
            }

            if (oldIndexCount > 0 && oldIndexOffset >= 0)
            {
                meshBuffers.FreeIndexRegion((uint)oldIndexOffset, (uint)oldIndexCount);
            }
        }

        RefreshRendererBuffers("mesh upload");
        return true;
    }

    private void WriteIndirectCommands(int slot, int chunkIndex, int vertexOffset, int indexOffset, uint opaqueFaces, uint alphaTestFaces, uint waterFaces, uint translucentFaces)
    {
        if (meshBuffers == null || slot < 0)
        {
            return;
        }

        // Avoid transient mismatches between command data and chunkInfo mapping.
        // If render and upload happen in different parts of the frame (or on different threads),
        // clearing first ensures the slot is either "draw nothing" or fully consistent.
        meshBuffers.ClearCommandSlot(slot);

        var baseVertex = vertexOffset >= 0 ? (uint)vertexOffset : 0u;
        var firstIndex = indexOffset >= 0 ? (uint)indexOffset : 0u;

        Span<uint> command =
        [
            opaqueFaces * 6u,
            1u,
            firstIndex,
            baseVertex,
            0u,
            alphaTestFaces * 6u,
            1u,
            firstIndex + opaqueFaces * 6u,
            baseVertex,
            0u,
            waterFaces * 6u,
            1u,
            firstIndex + (opaqueFaces + alphaTestFaces) * 6u,
            baseVertex,
            0u,
            translucentFaces * 6u,
            1u,
            firstIndex + (opaqueFaces + alphaTestFaces + waterFaces) * 6u,
            baseVertex,
            0u
        ];

        // Write chunk mapping before enabling the commands.
        GL.NamedBufferSubData((int)meshBuffers.ChunkInfoBuffer, (IntPtr)(slot * sizeof(int)), sizeof(int), ref chunkIndex);

        unsafe
        {
            fixed (uint* cmdPtr = command)
            {
                GL.NamedBufferSubData((int)meshBuffers.IndirectDrawBuffer, (IntPtr)(slot * 80), 80, (IntPtr)cmdPtr);
            }
        }
    }

    private void RefreshRendererBuffers(string reason)
    {
        if (meshBuffers == null || terrainRenderer == null)
        {
            return;
        }

        var totalFaces = 0u;
        foreach (var kvp in activeChunks)
        {
            if (kvp.Value.State == TerrainChunkState.Ready)
            {
                totalFaces += (uint)Math.Max(0, kvp.Value.VisibleVoxelCount);
            }
        }

        terrainRenderer.SetupBuffers(meshBuffers, meshBuffers.CurrentVertexBufferEnd, totalFaces);
        Log.Debug($"ClientTerrainSystem: Renderer refreshed after {reason} (faces={totalFaces}, vertices={meshBuffers.CurrentVertexBufferEnd}, indices={meshBuffers.CurrentIndexBufferEnd})");
    }
}
