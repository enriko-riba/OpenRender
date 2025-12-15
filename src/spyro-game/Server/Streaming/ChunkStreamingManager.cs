using OpenTK.Mathematics;
using SpyroGame.Shared.Abstractions;
using SpyroGame.Shared.State;
using SpyroGame.World;
using SpyroGame.World.Generation;
using System.Collections.Concurrent;
using System.IO;

namespace SpyroGame.Server.Streaming;

/// <summary>
/// Server-side chunk streaming + CPU generation.
///
/// Responsibilities:
/// - Track per-player chunk interest sets (based on player positions)
/// - Generate chunks on background worker threads
/// - Maintain a bounded in-memory voxel cache (evict chunks not needed by any player)
/// - Apply block edits to cached chunks and mark them dirty for replication
///
/// Non-responsibilities:
/// - NO meshing
/// - NO GPU/GL interaction
/// </summary>
public sealed class ChunkStreamingManager : IDisposable, IBlockEditService
{
    private readonly VoxelWorld world;
    private readonly ChunkVoxelDataCache voxelCache;
    private readonly ChunkGenerationJobSystem cpuGenerationJobs;

    private readonly Dictionary<PlayerId, Vector3> playerPositions = [];
    private readonly Dictionary<PlayerId, HashSet<int>> desiredChunksByPlayer = [];

    private readonly HashSet<int> readyChunks = [];
    private readonly HashSet<int> generatingChunks = [];
    private readonly HashSet<int> baseTerrainComplete = [];

    private readonly Dictionary<int, Dictionary<int, BlockId>> chunkEdits = [];
    private readonly ConcurrentQueue<int> changedChunkIndices = new();

    private TerrainConfig terrainConfig;
    private int generationSeed;

    public CollisionManager CollisionManager { get; } = new();

    public ChunkStreamingManager(VoxelWorld world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));

        terrainConfig = LoadTerrainConfig();
        voxelCache = new ChunkVoxelDataCache();
        cpuGenerationJobs = new ChunkGenerationJobSystem(voxelCache, terrainConfig, metrics: null);
    }

    public void Initialize(int seed)
    {
        generationSeed = seed != 0 ? seed : generationSeed;
        terrainConfig.Seed = generationSeed;
        cpuGenerationJobs.UpdateConfig(terrainConfig);
        LoadEdits();
    }

    public void UpdatePlayer(PlayerId playerId, Vector3 position)
    {
        playerPositions[playerId] = position;
    }

    /// <summary>
    /// Allocation-free check for whether a chunk is currently (1) desired by this player and (2) ready.
    /// Useful for filtering payload queues without constructing per-tick ready sets.
    /// </summary>
    public bool IsChunkReadyForPlayer(PlayerId playerId, int chunkIndex)
    {
        return desiredChunksByPlayer.TryGetValue(playerId, out var desired)
            && desired.Contains(chunkIndex)
            && readyChunks.Contains(chunkIndex);
    }

    /// <summary>
    /// Returns the subset of chunks that are (1) desired by this player and (2) ready.
    /// </summary>
    public HashSet<int> GetReadyChunksForPlayer(PlayerId playerId)
    {
        if (!desiredChunksByPlayer.TryGetValue(playerId, out var desired))
        {
            return [];
        }

        var result = new HashSet<int>();
        foreach (var idx in desired)
        {
            if (readyChunks.Contains(idx))
            {
                result.Add(idx);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a snapshot copy of the player's desired chunk set.
    /// This is safe to use for gating/loading decisions without exposing internal mutable state.
    /// </summary>
    public bool TryGetDesiredChunksForPlayer(PlayerId playerId, out HashSet<int> desiredChunks)
    {
        if (desiredChunksByPlayer.TryGetValue(playerId, out var desired))
        {
            desiredChunks = new HashSet<int>(desired);
            return true;
        }

        desiredChunks = [];
        return false;
    }

    public bool TryGetLoadingProgress(PlayerId playerId, out LoadingProgressSnapshot progress)
    {
        if (!desiredChunksByPlayer.TryGetValue(playerId, out var desired))
        {
            progress = default;
            return false;
        }

        // Ready is per-player intersection; generating is global (best-effort signal).
        var readyCount = 0;
        foreach (var idx in desired)
        {
            if (readyChunks.Contains(idx))
            {
                readyCount++;
            }
        }

        progress = new LoadingProgressSnapshot(
            DesiredChunkCount: desired.Count,
            ReadyChunkCount: readyCount,
            GeneratingChunkCount: generatingChunks.Count);
        return true;
    }

    public bool TryGetChunkPayloadBytes(int chunkIndex, out byte[] payload)
    {
        payload = [];

        if (!voxelCache.TryGetChunkData(chunkIndex, out var chunkData) || chunkData == null)
        {
            return false;
        }

        using var ms = new MemoryStream(capacity: 64 * 1024);
        using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            chunkData.Serialize(writer);
        }

        payload = ms.ToArray();
        return true;
    }

    public bool TryDequeueChangedChunk(out int chunkIndex)
        => changedChunkIndices.TryDequeue(out chunkIndex);

    public void Tick(double elapsedSeconds)
    {
        UpdateDesiredSets();
        StartMissingGenerations();
        DrainGenerationResults();
        EvictUnreferencedChunks();
    }

    public void ApplyBlockEdit(Vector3i worldPosition, BlockId blockId, bool isBreaking)
    {
        if (!VoxelHelper.IsGlobalPositionInWorld(worldPosition))
        {
            return;
        }

        var chunkIdx = VoxelHelper.GetChunkIndexFromPositionGlobal(worldPosition);
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);
        var localX = worldPosition.X - chunkPos.X;
        var localZ = worldPosition.Z - chunkPos.Z;
        var localY = worldPosition.Y;

        // Persist edit in dictionary for future regen/serialization.
        var edits = GetOrCreateChunkEdits(chunkIdx);
        var localLinear = localY * VoxelHelper.ChunkSideSizeSquare + localZ * VoxelHelper.ChunkSideSize + localX;
        edits[localLinear] = blockId;

        // If voxel data exists, apply immediately.
        if (!voxelCache.TryGetChunkData(chunkIdx, out var chunkData) || chunkData == null)
        {
            return;
        }

        var oldBlock = chunkData.GetBlock(localX, localY, localZ);
        if (oldBlock == blockId)
        {
            return;
        }

        chunkData.SetBlock(localX, localY, localZ, blockId);

        // Update collision for this column.
        var chunk = world.GetOrCreateChunkContainer(chunkIdx);
        chunk.RebuildColumnSpans(localX, localZ, chunkData);
        CollisionManager.RebuildColumnFromVoxelData(chunkIdx, localX, localZ, chunkData);

        // Update lighting in cached chunk data so the client can re-mesh with correct light.
        var affectedByLight = RecalculateLightingForBlockEdit(chunkIdx, localX, localY, localZ, oldBlock, blockId);
        if (affectedByLight.Count == 0)
        {
            MarkChunkChanged(chunkIdx);
            return;
        }

        foreach (var affectedChunk in affectedByLight)
        {
            MarkChunkChanged(affectedChunk);
        }
    }

    private ChunkData? GetChunkDataOrNull(int chunkIndex)
        => voxelCache.TryGetChunkData(chunkIndex, out var data) ? data : null;

    private HashSet<int> RecalculateLightingForBlockEdit(
        int chunkIdx,
        int localX,
        int localY,
        int localZ,
        BlockId oldBlock,
        BlockId newBlock)
    {
        var oldLightValue = BlockRegistry.GetLightValue(oldBlock);
        var newLightValue = BlockRegistry.GetLightValue(newBlock);
        var oldIsOpaque = oldBlock.IsOpaque();
        var newIsOpaque = newBlock.IsOpaque();

        // Changing opacity can expose/block skylight and affect a larger volume.
        if (oldIsOpaque != newIsOpaque)
        {
            return LightingCalculator.RecalculateLightingAroundBlock(
                chunkIdx,
                localX, localY, localZ,
                GetChunkDataOrNull);
        }

        // Removing a light source: clear stale block light across chunk boundaries.
        if (oldLightValue > 0 && newLightValue == 0)
        {
            return LightingCalculator.RemoveBlockLight(
                chunkIdx,
                localX, localY, localZ,
                oldLightValue,
                GetChunkDataOrNull);
        }

        // Placing a light source: propagate the new light across chunk boundaries.
        if (newLightValue > 0)
        {
            return LightingCalculator.AddBlockLight(
                chunkIdx,
                localX, localY, localZ,
                newLightValue,
                GetChunkDataOrNull);
        }

        return [];
    }

    public void Dispose()
    {
        cpuGenerationJobs.Dispose();
        voxelCache.Dispose();
    }

    private void MarkChunkChanged(int chunkIndex)
    {
        changedChunkIndices.Enqueue(chunkIndex);
    }

    private void UpdateDesiredSets()
    {
        foreach (var (playerId, pos) in playerPositions)
        {
            var desired = GetOrCreateDesiredSet(playerId);
            desired.Clear();

            var centerIdx = VoxelHelper.GetChunkIndexFromPositionGlobal(pos);
            var centerX = centerIdx % VoxelHelper.WorldChunksXZ;
            var centerZ = centerIdx / VoxelHelper.WorldChunksXZ;

            var radius = Math.Clamp(VoxelHelper.MaxDistanceInChunks, 1, VoxelHelper.WorldChunksXZ - 1);

            // IMPORTANT: Use a square interest set (not a disk).
            // With radius=14 this yields 29x29 = 841 chunks, matching the expected "~800 chunks around player".
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = centerX + dx;
                    var z = centerZ + dz;
                    if (x < 0 || z < 0 || x >= VoxelHelper.WorldChunksXZ || z >= VoxelHelper.WorldChunksXZ)
                    {
                        continue;
                    }

                    desired.Add(z * VoxelHelper.WorldChunksXZ + x);
                }
            }
        }
    }

    private void StartMissingGenerations()
    {
        var needed = GetUnionDesired();

        foreach (var idx in needed)
        {
            if (readyChunks.Contains(idx) || generatingChunks.Contains(idx))
            {
                continue;
            }

            // Ensure chunk container exists for collision application.
            world.GetOrCreateChunkContainer(idx);

            var edits = BuildBlockIdEdits(idx);
            cpuGenerationJobs.Enqueue(idx, edits, GenerationJobType.BaseTerrain);
            generatingChunks.Add(idx);
        }
    }

    private void DrainGenerationResults()
    {
        while (cpuGenerationJobs.TryDequeueResult(out var result))
        {
            // Generation job stores voxel data in cache already.
            ApplyCollisionResults(result.ChunkIndex, result.Generation);

            // Two-stage generation: BaseTerrain -> Decoration.
            // Only mark a chunk Ready after Decoration has run, so clients receive final voxels including vegetation.
            if (result.Type == GenerationJobType.BaseTerrain)
            {
                baseTerrainComplete.Add(result.ChunkIndex);

                // Decoration uses cached base terrain + biome data.
                cpuGenerationJobs.Enqueue(result.ChunkIndex, blockIdEdits: null, GenerationJobType.Decoration);
                continue;
            }

            // Decoration or DecorationRepair completes the chunk.
            baseTerrainComplete.Remove(result.ChunkIndex);
            generatingChunks.Remove(result.ChunkIndex);

            readyChunks.Add(result.ChunkIndex);
            MarkChunkChanged(result.ChunkIndex);
        }
    }

    private void ApplyCollisionResults(int chunkIdx, CpuTerrainGenerator.ChunkGenerationResult result)
    {
        CollisionManager.UpdateChunkData(chunkIdx, result.Collision);

        var chunk = world.GetOrCreateChunkContainer(chunkIdx);
        chunk.ApplyColumnSpansForCollision(result.SpanPairs, result.SpanCounts, result.SpanTypes);
    }

    private void EvictUnreferencedChunks()
    {
        var needed = GetUnionDesired();

        // Evict chunks not needed by any player.
        foreach (var idx in readyChunks.ToArray())
        {
            if (needed.Contains(idx))
            {
                continue;
            }

            readyChunks.Remove(idx);
            voxelCache.TryRelease(idx);
            CollisionManager.RemoveChunkData(idx);
        }
    }

    private HashSet<int> GetUnionDesired()
    {
        var union = new HashSet<int>();
        foreach (var desired in desiredChunksByPlayer.Values)
        {
            union.UnionWith(desired);
        }
        return union;
    }

    private HashSet<int> GetOrCreateDesiredSet(PlayerId playerId)
    {
        if (desiredChunksByPlayer.TryGetValue(playerId, out var set))
        {
            return set;
        }

        set = [];
        desiredChunksByPlayer[playerId] = set;
        return set;
    }

    private Dictionary<int, BlockId> GetOrCreateChunkEdits(int chunkIdx)
    {
        if (chunkEdits.TryGetValue(chunkIdx, out var edits))
        {
            return edits;
        }

        edits = new Dictionary<int, BlockId>();
        chunkEdits[chunkIdx] = edits;
        return edits;
    }

    private IReadOnlyDictionary<int, BlockId>? BuildBlockIdEdits(int chunkIdx)
    {
        if (!chunkEdits.TryGetValue(chunkIdx, out var edits) || edits.Count == 0)
        {
            return null;
        }

        return edits;
    }

    private static TerrainConfig LoadTerrainConfig()
    {
        const string configFileName = "terrain_config.json";
        var path = Path.Combine(Environment.CurrentDirectory, configFileName);
        if (File.Exists(path))
        {
            try
            {
                return TerrainConfig.Load(path);
            }
            catch
            {
            }
        }

        return new TerrainConfig();
    }

    private void LoadEdits()
    {
        // Keep behavior minimal for now: no disk edits load. This method is a seam for later.
    }
}
