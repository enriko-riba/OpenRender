using OpenRender;
using OpenTK.Mathematics;
using SpyroGame.Components;
using SpyroGame.Server.World;
using SpyroGame.Server.World.Generation;
using SpyroGame.Shared.Abstractions;
using SpyroGame.Shared.State;
using SpyroGame.World;
using SpyroGame.World.Registry;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

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
    private const string SaveRootFolderName = "save";
    private const string ChunkFilePrefix = "chunk_";
    private const string ChunkEditsSuffix = "_edits";
    public const string ChunkSaveFileExtension = ".dat";

    private const string TerrainConfigFileName = "terrain_config.json";

    private const string ChunkStateMagic = "CHNK";
    private const int ChunkStateSaveFileVersion = 2;

    // Keep saving light-weight: we persist only the edit dictionary (seed + edits regenerates world).
    private const double AutoSaveEditsIntervalSeconds = 2.0;

    // Full chunk state saves (fast-load path). Throttled to avoid stalling the game loop.
    private const double AutoSaveChunkStateIntervalSeconds = 2.0;
    private const int MaxChunkStateSaveSnapshotsPerTick = 2;
    private const int MaxChunkStateLoadResultsToApplyPerTick = 2;
    private const int MaxChunkStateSaveResultsToApplyPerTick = 8;
    private const int MaxChunkStateLoadRequestsEnqueuedPerTick = 8;

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

    private readonly object editsSaveLock = new();
    private readonly HashSet<int> dirtyEditedChunks = [];
    private DateTime lastEditsSaveUtc;

    private readonly object chunkStateSaveLock = new();
    private readonly HashSet<int> dirtyChunkStates = [];
    private DateTime lastChunkStateSaveUtc;

    private readonly object chunkStateLoadLock = new();
    private readonly HashSet<int> loadingChunkStates = [];
    private readonly HashSet<int> missingChunkStatesOnDisk = [];
    private readonly HashSet<int> knownChunkStatesOnDisk = [];

    // Monotonic counter for per-chunk edits so we can safely "bake" edits into full saves.
    private readonly Dictionary<int, long> chunkEditsVersion = [];

    private readonly BlockingCollection<ChunkStateLoadRequest> chunkStateLoadQueue = new(boundedCapacity: 2048);
    private readonly ConcurrentQueue<ChunkStateLoadResult> chunkStateLoadResults = new();
    private readonly CancellationTokenSource chunkStateLoadCts = new();
    private readonly Task[] chunkStateLoadWorkers;

    private readonly BlockingCollection<ChunkStateSaveRequest> chunkStateSaveQueue = new(boundedCapacity: 2048);
    private readonly ConcurrentQueue<ChunkStateSaveResult> chunkStateSaveResults = new();
    private readonly CancellationTokenSource chunkStateSaveCts = new();
    private readonly Task[] chunkStateSaveWorkers;

    private readonly TerrainConfig terrainConfig;
    private int generationSeed;

    public CollisionManager CollisionManager { get; } = new();
    public ChunkVoxelDataCache VoxelCache => voxelCache;

    public ChunkStreamingManager(VoxelWorld world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));

        terrainConfig = LoadTerrainConfig();
        voxelCache = new ChunkVoxelDataCache();
        cpuGenerationJobs = new ChunkGenerationJobSystem(voxelCache, terrainConfig, metrics: null);

        // Disk IO must not run on the tick thread; worker loops handle load/save.
        // Keep IO/decompression concurrency low to avoid starving the main/render thread.
        var ioParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
        chunkStateLoadWorkers = new Task[ioParallelism];
        for (var i = 0; i < chunkStateLoadWorkers.Length; i++)
        {
            chunkStateLoadWorkers[i] = Task.Factory.StartNew(
                ChunkStateLoadWorkerLoop,
                chunkStateLoadCts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        chunkStateSaveWorkers = new Task[ioParallelism];
        for (var i = 0; i < chunkStateSaveWorkers.Length; i++)
        {
            chunkStateSaveWorkers[i] = Task.Factory.StartNew(
                ChunkStateSaveWorkerLoop,
                chunkStateSaveCts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Initialize(int seed)
    {
        generationSeed = seed != 0 ? seed : generationSeed;
        terrainConfig.Seed = generationSeed;
        cpuGenerationJobs.UpdateConfig(terrainConfig);
        LoadEdits();
        IndexChunkStateFilesOnDisk();
        lastEditsSaveUtc = DateTime.UtcNow;
        lastChunkStateSaveUtc = DateTime.UtcNow;
    }

    public void UpdatePlayer(PlayerId playerId, Vector3 position) => playerPositions[playerId] = position;

    /// <summary>
    /// Allocation-free check for whether a chunk is currently (1) desired by this player and (2) ready.
    /// Useful for filtering payload queues without constructing per-tick ready sets.
    /// </summary>
    public bool IsChunkReadyForPlayer(PlayerId playerId, int chunkIndex) => desiredChunksByPlayer.TryGetValue(playerId, out var desired)
            && desired.Contains(chunkIndex)
            && readyChunks.Contains(chunkIndex);

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
            desiredChunks = [.. desired];
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

        if (!voxelCache.TryAcquireChunkData(chunkIndex, out var lease))
        {
            return false;
        }

        using (lease)
        {
            var chunkData = lease.Data;

            using var ms = new MemoryStream(capacity: 64 * 1024);
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                chunkData.Serialize(writer);

                // Optional biome payload (network-only).
                // The client-side mesher/debug overlay needs per-column biome ids.
                // In the client-server architecture ChunkBiomeData is owned by the server,
                // so include a compact copy when available.
                if (voxelCache.TryGetBiomeData(chunkIndex, out var biomeData) && biomeData != null)
                {
                    const uint biomeMagic = 0x4D4F4942; // 'BIOM' in little-endian
                    const byte biomeVersion = 1;
                    writer.Write(biomeMagic);
                    writer.Write(biomeVersion);

                    // Only send per-column biome ids (16x16 = 256 bytes) to keep bandwidth low.
                    writer.Write(ChunkBiomeData.ColumnCount);
                    for (var i = 0; i < ChunkBiomeData.ColumnCount; i++)
                    {
                        writer.Write((byte)biomeData.ColumnBiomes[i]);
                    }
                }
            }

            payload = ms.ToArray();
            return true;
        }
    }

    public bool TryDequeueChangedChunk(out int chunkIndex)
        => changedChunkIndices.TryDequeue(out chunkIndex);

    public void Tick(double elapsedSeconds)
    {
        UpdateDesiredSets();
        StartMissingGenerations();
        DrainGenerationResults();
        DrainLoadedChunkStates();
        DrainSavedChunkStates();
        EnqueuePeriodicChunkStateSaves();
        EvictUnreferencedChunks();
        CheckPeriodicEditsSave();
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

        chunkEditsVersion[chunkIdx] = chunkEditsVersion.TryGetValue(chunkIdx, out var v) ? v + 1 : 1;

        lock (editsSaveLock)
        {
            dirtyEditedChunks.Add(chunkIdx);
        }

        // If voxel data exists, apply immediately.
        if (!voxelCache.TryAcquireChunkData(chunkIdx, out var lease))
        {
            return;
        }

        HashSet<int> affectedByLight;
        using (lease)
        {
            var chunkData = lease.Data;
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
            affectedByLight = RecalculateLightingForBlockEdit(chunkIdx, localX, localY, localZ, oldBlock, blockId);
        }
        if (affectedByLight.Count == 0)
        {
            MarkChunkChanged(chunkIdx);
            MarkChunkStateDirty(chunkIdx);
            return;
        }

        // Persist full chunk state only for chunks that have voxel edits.
        // Neighbor chunks may be re-lit/re-meshed due to propagation, but their lighting is derived.
        // Keeping full-state saves to "voxel-edited" chunks prevents one edit from generating dozens of *.dat files.
        MarkChunkStateDirty(chunkIdx);

        foreach (var affectedChunk in affectedByLight)
        {
            MarkChunkChanged(affectedChunk);
        }
    }

    private HashSet<int> RecalculateLightingForBlockEdit(
        int chunkIdx,
        int localX,
        int localY,
        int localZ,
        BlockId oldBlock,
        BlockId newBlock)
    {
        var leases = new List<ChunkVoxelDataCache.ChunkDataLease>(capacity: 8);

        ChunkData? GetChunkDataLeaseBacked(int chunkIndex)
        {
            if (!voxelCache.TryAcquireChunkData(chunkIndex, out var lease))
            {
                return null;
            }

            leases.Add(lease);
            return lease.Data;
        }

        var oldLightValue = BlockRegistry.GetLightValue(oldBlock);
        var newLightValue = BlockRegistry.GetLightValue(newBlock);
        var oldIsOpaque = oldBlock.IsOpaque();
        var newIsOpaque = newBlock.IsOpaque();

        try
        {

            // Changing opacity can expose/block skylight and affect a larger volume.
            if (oldIsOpaque != newIsOpaque)
            {
                return LightingCalculator.RecalculateLightingAroundBlock(
                    chunkIdx,
                    localX, localY, localZ,
                    GetChunkDataLeaseBacked);
            }

            // Removing a light source: clear stale block light across chunk boundaries.
            if (oldLightValue > 0 && newLightValue == 0)
            {
                return LightingCalculator.RemoveBlockLight(
                    chunkIdx,
                    localX, localY, localZ,
                    oldLightValue,
                    GetChunkDataLeaseBacked);
            }

            // Placing a light source: propagate the new light across chunk boundaries.
            if (newLightValue > 0)
            {
                return LightingCalculator.AddBlockLight(
                    chunkIdx,
                    localX, localY, localZ,
                    newLightValue,
                    GetChunkDataLeaseBacked);
            }

            return [];
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    public void Dispose()
    {
        // Persist full chunk state + edits on shutdown so users always get save files after edits.
        FlushChunkStateSaves();
        SaveDirtyEdits(force: true);
        ShutdownChunkStateWorkers();
        cpuGenerationJobs.Dispose();
        voxelCache.Dispose();
    }

    private void MarkChunkStateDirty(int chunkIdx)
    {
        lock (chunkStateSaveLock)
        {
            dirtyChunkStates.Add(chunkIdx);
        }
    }

    private void EnqueuePeriodicChunkStateSaves()
    {
        var hasDirty = false;
        lock (chunkStateSaveLock)
        {
            hasDirty = dirtyChunkStates.Count > 0;
        }

        if (!hasDirty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastChunkStateSaveUtc).TotalSeconds < AutoSaveChunkStateIntervalSeconds)
        {
            return;
        }

        var candidates = new List<int>(MaxChunkStateSaveSnapshotsPerTick);
        lock (chunkStateSaveLock)
        {
            foreach (var idx in dirtyChunkStates)
            {
                candidates.Add(idx);
                if (candidates.Count >= MaxChunkStateSaveSnapshotsPerTick)
                {
                    break;
                }
            }

            foreach (var idx in candidates)
            {
                dirtyChunkStates.Remove(idx);
            }
        }

        var snapshotted = 0;
        foreach (var idx in candidates)
        {
            if (TryEnqueueChunkStateSaveSnapshot(idx))
            {
                snapshotted++;
            }
            else
            {
                // Couldn't snapshot right now; retry later.
                MarkChunkStateDirty(idx);
            }
        }

        if (snapshotted > 0)
        {
            lastChunkStateSaveUtc = now;
        }
    }

    private bool TryEnqueueChunkStateSaveSnapshot(int chunkIdx)
    {
        if (!voxelCache.TryAcquireChunkData(chunkIdx, out var lease))
        {
            return false;
        }

        var snapshot = new ChunkData();
        using (lease)
        {
            var liveData = lease.Data;
            liveData.CloneTo(snapshot);
            snapshot.ChunkIndex = chunkIdx;
        }

        voxelCache.TryGetBiomeData(chunkIdx, out var biomeData);

        var editsVersionAtSnapshot = chunkEditsVersion.TryGetValue(chunkIdx, out var v) ? v : 0;
        var path = GetChunkStatePath(chunkIdx);

        return chunkStateSaveQueue.TryAdd(new ChunkStateSaveRequest(
            ChunkIndex: chunkIdx,
            Path: path,
            Data: snapshot,
            BiomeData: biomeData,
            EditsVersionAtSnapshot: editsVersionAtSnapshot));
    }

    private void TryDeleteChunkEditsFile(int chunkIdx)
    {
        try
        {
            var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);
            var folderName = $"{terrainConfig.WorldName}_{generationSeed}";
            var editsFileName = $"{ChunkFilePrefix}{chunkPos.X}_{chunkPos.Z}{ChunkEditsSuffix}{ChunkSaveFileExtension}";
            var editsPath = Path.Combine(ResolveDataRoot(), SaveRootFolderName, folderName, editsFileName);
            if (File.Exists(editsPath))
            {
                File.Delete(editsPath);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    private static bool TryLoadChunkStateFromPath(string path, int chunkIdx, out ChunkData? data, out ChunkBiomeData? biomeData)
    {
        data = null;
        biomeData = null;

        var fileSizeBytes = TryGetFileSizeBytes(path);
        string? magic = null;
        int? version = null;

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var fileStream = File.OpenRead(path);
            using var headerReader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true);
            magic = headerReader.ReadString();
            if (!string.Equals(magic, ChunkStateMagic, StringComparison.Ordinal))
            {
                Log.Warn($"Server ChunkStreamingManager: Invalid chunk state header; expectedMagic='{ChunkStateMagic}' actualMagic='{magic}' chunkIdx={chunkIdx} fileSizeBytes={fileSizeBytes} path='{path}'");
                TryQuarantineCorruptChunkStateFile(path);
                return false;
            }

            version = headerReader.ReadInt32();

            if (version >= 2)
            {
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzipStream, Encoding.UTF8, leaveOpen: true);
                data = ChunkData.Deserialize(reader);
                if (reader.ReadBoolean())
                {
                    biomeData = ChunkBiomeData.Deserialize(reader);
                }
            }
            else
            {
                data = ChunkData.Deserialize(headerReader);
                if (headerReader.ReadBoolean())
                {
                    biomeData = ChunkBiomeData.Deserialize(headerReader);
                }
            }

            if (data == null)
            {
                Log.Warn($"Server ChunkStreamingManager: Failed to deserialize chunk state (null) chunkIdx={chunkIdx} version={version} fileSizeBytes={fileSizeBytes} path='{path}'");
                TryQuarantineCorruptChunkStateFile(path);
                return false;
            }

            // Validate before forcing ChunkIndex so we can detect wrong-file loads.
            if (!TryValidateChunkState(data, biomeData, expectedChunkIdx: chunkIdx, out var invalidReason))
            {
                Log.Warn($"Server ChunkStreamingManager: Invalid/corrupt chunk state; reason='{invalidReason}' chunkIdx={chunkIdx} serializedChunkIdx={data.ChunkIndex} version={version} fileSizeBytes={fileSizeBytes} path='{path}'");
                TryQuarantineCorruptChunkStateFile(path);
                data = null;
                biomeData = null;
                return false;
            }

            data.ChunkIndex = chunkIdx;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Server ChunkStreamingManager: Exception while loading chunk state; chunkIdx={chunkIdx} magic='{magic ?? "<unread>"}' version='{(version.HasValue ? version.Value.ToString() : "<unread>")}' fileSizeBytes={fileSizeBytes} exType='{ex.GetType().Name}' exMessage='{ex.Message}' path='{path}'");
            TryQuarantineCorruptChunkStateFile(path);
            return false;
        }
    }

    private static bool TryValidateChunkState(ChunkData data, ChunkBiomeData? biomeData, int expectedChunkIdx, out string invalidReason)
    {
        invalidReason = string.Empty;

        // Wrong-file guard: prevents loading a different chunk's data into this chunk index.
        if (data.ChunkIndex != expectedChunkIdx)
        {
            invalidReason = $"ChunkIndexMismatch expected={expectedChunkIdx} actual={data.ChunkIndex}";
            return false;
        }

        // Shape/size checks: protects against old formats or partial writes.
        if (data.VoxelData is null)
        {
            invalidReason = "VoxelDataNull";
            return false;
        }
        if (data.VoxelData.Length < VoxelHelper.ChunkVoxelCount)
        {
            invalidReason = $"VoxelDataTooSmall expectedAtLeast={VoxelHelper.ChunkVoxelCount} actual={data.VoxelData.Length}";
            return false;
        }

        if (data.LightData is null)
        {
            invalidReason = "LightDataNull";
            return false;
        }
        if (data.LightData.Length < VoxelHelper.ChunkVoxelCount)
        {
            invalidReason = $"LightDataTooSmall expectedAtLeast={VoxelHelper.ChunkVoxelCount} actual={data.LightData.Length}";
            return false;
        }

        if (data.SurfaceHeights is null)
        {
            invalidReason = "SurfaceHeightsNull";
            return false;
        }
        if (data.SurfaceHeights.Length != VoxelHelper.ChunkSideSizeSquare)
        {
            invalidReason = $"SurfaceHeightsLengthMismatch expected={VoxelHelper.ChunkSideSizeSquare} actual={data.SurfaceHeights.Length}";
            return false;
        }

        if (data.Biomes is null)
        {
            invalidReason = "BiomesNull";
            return false;
        }
        if (data.Biomes.Length != 16)
        {
            invalidReason = $"BiomesLengthMismatch expected=16 actual={data.Biomes.Length}";
            return false;
        } // 4x4

        if (data.Palette is null)
        {
            invalidReason = "PaletteNull";
            return false;
        }
        // ChunkData keeps paletteCount private; infer a safe upper bound from array length.
        // Also enforce that index 0 is Air after deserialize repair.
        if (data.Palette.Length == 0)
        {
            invalidReason = "PaletteEmpty";
            return false;
        }
        if (data.Palette[0] != BlockId.Air)
        {
            invalidReason = $"PaletteAirNotZero palette0={(ushort)data.Palette[0]}";
            return false;
        }
        if (data.Palette.Length > 128)
        {
            invalidReason = $"PaletteTooLarge max=128 actual={data.Palette.Length}";
            return false;
        }

        // Palette-index sanity: corrupted voxel data can reference beyond palette.
        // This is a common source of "random solid pillars" after load.
        var paletteLimit = data.Palette.Length;
        for (var i = 0; i < VoxelHelper.ChunkVoxelCount; i++)
        {
            if (data.VoxelData[i] >= paletteLimit)
            {
                invalidReason = $"VoxelPaletteIndexOutOfRange paletteLen={paletteLimit} firstBadIndex={i} voxelValue={data.VoxelData[i]}";
                return false;
            }
        }

        if (biomeData != null)
        {
            // Biomes should always include per-column biome ids.
            if (biomeData.ColumnBiomes is null || biomeData.ColumnBiomes.Length != ChunkBiomeData.ColumnCount)
            {
                invalidReason = $"BiomeColumnsInvalid expected={ChunkBiomeData.ColumnCount} actual={(biomeData.ColumnBiomes is null ? "<null>" : biomeData.ColumnBiomes.Length)}";
                return false;
            }
        }

        invalidReason = "";
        return true;
    }

    private static long TryGetFileSizeBytes(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void TryQuarantineCorruptChunkStateFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var quarantinedPath = path + ".corrupt";
            if (File.Exists(quarantinedPath))
            {
                // Keep the first copy; avoid repeated renames.
                return;
            }

            File.Move(path, quarantinedPath);
        }
        catch
        {
            // Best-effort.
        }
    }

    private void DrainLoadedChunkStates()
    {
        var applied = 0;
        while (applied < MaxChunkStateLoadResultsToApplyPerTick && chunkStateLoadResults.TryDequeue(out var result))
        {
            applied++;

            lock (chunkStateLoadLock)
            {
                loadingChunkStates.Remove(result.ChunkIndex);

                if (result.ShouldFallbackToGeneration)
                {
                    missingChunkStatesOnDisk.Add(result.ChunkIndex);
                    // Treat corrupt/missing states as not-known so generation can proceed.
                    knownChunkStatesOnDisk.Remove(result.ChunkIndex);
                }
                else
                {
                    missingChunkStatesOnDisk.Remove(result.ChunkIndex);

                    if (result.Succeeded)
                    {
                        knownChunkStatesOnDisk.Add(result.ChunkIndex);
                    }
                }
            }

            if (!result.Succeeded || result.Data == null)
            {
                continue;
            }

            // Hybrid correctness: if edits exist, apply on top.
            if (chunkEdits.TryGetValue(result.ChunkIndex, out var edits) && edits.Count > 0)
            {
                ApplyEditsToChunkData(result.Data, edits);
                LightingCalculator.CalculateLighting(result.Data);
            }

            voxelCache.Store(result.Data);
            if (result.BiomeData != null)
            {
                voxelCache.StoreBiomeData(result.ChunkIndex, result.BiomeData);
            }

            RebuildCollisionFromChunkData(result.ChunkIndex, result.Data);

            readyChunks.Add(result.ChunkIndex);
            MarkChunkChanged(result.ChunkIndex);
        }
    }

    private void DrainSavedChunkStates()
    {
        var applied = 0;
        while (applied < MaxChunkStateSaveResultsToApplyPerTick && chunkStateSaveResults.TryDequeue(out var result))
        {
            applied++;

            if (!result.Succeeded)
            {
                MarkChunkStateDirty(result.ChunkIndex);
                continue;
            }

            lock (chunkStateLoadLock)
            {
                knownChunkStatesOnDisk.Add(result.ChunkIndex);
                missingChunkStatesOnDisk.Remove(result.ChunkIndex);
            }

            var currentEditsVersion = chunkEditsVersion.TryGetValue(result.ChunkIndex, out var v) ? v : 0;
            if (currentEditsVersion == result.EditsVersionAtSnapshot)
            {
                TryDeleteChunkEditsFile(result.ChunkIndex);
                lock (editsSaveLock)
                {
                    dirtyEditedChunks.Remove(result.ChunkIndex);
                    chunkEdits.Remove(result.ChunkIndex);
                }
            }
        }
    }

    private bool IsChunkStateKnownOnDisk(int chunkIdx)
    {
        lock (chunkStateLoadLock)
        {
            // If we've already attempted to load this chunk state and determined it is missing/corrupt,
            // treat it as NOT known so the generator can take over.
            return knownChunkStatesOnDisk.Contains(chunkIdx)
                && !missingChunkStatesOnDisk.Contains(chunkIdx);
        }
    }

    private bool IsChunkStateLoading(int chunkIdx)
    {
        lock (chunkStateLoadLock)
        {
            return loadingChunkStates.Contains(chunkIdx);
        }
    }

    private bool TryEnqueueChunkStateLoad(int chunkIdx)
    {
        lock (chunkStateLoadLock)
        {
            if (loadingChunkStates.Contains(chunkIdx))
            {
                return true;
            }

            if (missingChunkStatesOnDisk.Contains(chunkIdx))
            {
                return false;
            }
        }

        var path = GetChunkStatePath(chunkIdx);

        if (!chunkStateLoadQueue.TryAdd(new ChunkStateLoadRequest(chunkIdx, path)))
        {
            return false;
        }

        lock (chunkStateLoadLock)
        {
            loadingChunkStates.Add(chunkIdx);
        }

        return true;
    }

    private string GetChunkStatePath(int chunkIdx)
    {
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);
        var folderName = $"{terrainConfig.WorldName}_{generationSeed}";
        var fileName = $"{ChunkFilePrefix}{chunkPos.X}_{chunkPos.Z}{ChunkSaveFileExtension}";
        return Path.Combine(ResolveDataRoot(), SaveRootFolderName, folderName, fileName);
    }

    private static void ApplyEditsToChunkData(ChunkData data, Dictionary<int, BlockId> edits)
    {
        foreach (var (localLinear, blockId) in edits)
        {
            var localY = localLinear / VoxelHelper.ChunkSideSizeSquare;
            var rem = localLinear - localY * VoxelHelper.ChunkSideSizeSquare;
            var localZ = rem / VoxelHelper.ChunkSideSize;
            var localX = rem - localZ * VoxelHelper.ChunkSideSize;
            if (ChunkData.IsWithinBounds(localX, localY, localZ))
            {
                data.SetBlock(localX, localY, localZ, blockId);
            }
        }
    }

    private void RebuildCollisionFromChunkData(int chunkIdx, ChunkData chunkData)
    {
        var chunk = world.GetOrCreateChunkContainer(chunkIdx);

        // Rebuild spans once, then upload collision in one shot.
        // This avoids scanning voxel columns twice (spans + collision manager) and prevents chunk-border hitches.
        chunk.RebuildAllCollisionSpans(chunkData);
        CollisionManager.UpdateChunkData(chunkIdx, chunk.ToChunkCollisionData());
    }

    private void CheckPeriodicEditsSave()
    {
        // Cheap guard: avoid taking the lock if nothing is dirty.
        var hasDirty = false;
        lock (editsSaveLock)
        {
            hasDirty = dirtyEditedChunks.Count > 0;
        }

        if (!hasDirty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastEditsSaveUtc).TotalSeconds < AutoSaveEditsIntervalSeconds)
        {
            return;
        }

        SaveDirtyEdits(force: false);
        lastEditsSaveUtc = now;
    }

    private void SaveDirtyEdits(bool force)
    {
        List<int> toSave;
        lock (editsSaveLock)
        {
            if (!force && dirtyEditedChunks.Count == 0)
            {
                return;
            }

            toSave = [.. dirtyEditedChunks];
            dirtyEditedChunks.Clear();
        }

        if (toSave.Count == 0)
        {
            return;
        }

        var saved = 0;
        foreach (var idx in toSave)
        {
            if (TrySaveChunkEdits(idx))
            {
                saved++;
            }
        }

        if (saved > 0)
        {
            Log.Info($"Server ChunkStreamingManager: Saved edits for {saved} chunks");
        }
    }

    private bool TrySaveChunkEdits(int chunkIdx)
    {
        if (!chunkEdits.TryGetValue(chunkIdx, out var edits) || edits.Count == 0)
        {
            return false;
        }

        try
        {
            var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);
            var folderName = $"{terrainConfig.WorldName}_{generationSeed}";
            var fileName = $"{ChunkFilePrefix}{chunkPos.X}_{chunkPos.Z}{ChunkEditsSuffix}{ChunkSaveFileExtension}";
            var path = Path.Combine(ResolveDataRoot(), SaveRootFolderName, folderName, fileName);

            var dirName = Path.GetDirectoryName(path);
            if (dirName is not null)
            {
                Directory.CreateDirectory(dirName);
            }

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(edits.Count);
            foreach (var kvp in edits)
            {
                writer.Write(kvp.Key);
                writer.Write((ushort)kvp.Value);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Server ChunkStreamingManager: Failed to save edits for chunk {chunkIdx}: {ex.Message}");
            return false;
        }
    }

    private void MarkChunkChanged(int chunkIndex) => changedChunkIndices.Enqueue(chunkIndex);

    private void UpdateDesiredSets()
    {
        foreach (var (playerId, pos) in playerPositions)
        {
            var desired = GetOrCreateDesiredSet(playerId);
            desired.Clear();

            // Fix: Calculate center chunk coordinates directly from position to avoid wrapping/truncation issues
            // with VoxelHelper.GetChunkIndexFromPositionGlobal when near world boundaries or using negative coordinates.
            var centerX = (int)MathF.Floor(pos.X / VoxelHelper.ChunkSideSize);
            var centerZ = (int)MathF.Floor(pos.Z / VoxelHelper.ChunkSideSize);

            var radius = Math.Clamp(VoxelHelper.MaxDistanceInChunks, 1, VoxelHelper.WorldChunksXZ - 1);
            var radiusSq = radius * radius;

            // Use an actual radius (disk) so chunks outside MaxDistanceInChunks unload.
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dz * dz > radiusSq)
                    {
                        continue;
                    }

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

        var enqueuedLoadsThisTick = 0;

        foreach (var idx in needed)
        {
            if (readyChunks.Contains(idx) || generatingChunks.Contains(idx) || IsChunkStateLoading(idx))
            {
                continue;
            }

            // If we KNOW there is a full chunk state on disk, do not generate it.
            // Always load (or defer until we can enqueue a load) so saved edits are applied reliably.
            if (IsChunkStateKnownOnDisk(idx))
            {
                if (TryEnqueueChunkStateLoad(idx))
                {
                    continue;
                }

                // Defer until we can enqueue the load (e.g., queue is temporarily full).
                // Generation would overwrite the saved state.
                continue;
            }

            // Fast path: if we have a full chunk state on disk, load it via background workers.
            // IMPORTANT: no disk IO or decompression on the tick thread.
            if (enqueuedLoadsThisTick < MaxChunkStateLoadRequestsEnqueuedPerTick
                && TryEnqueueChunkStateLoad(idx))
            {
                enqueuedLoadsThisTick++;
                continue;
            }

            // Ensure chunk container exists for collision application.
            world.GetOrCreateChunkContainer(idx);

            var edits = BuildBlockIdEdits(idx);
            cpuGenerationJobs.Enqueue(idx, edits, GenerationJobType.BaseTerrain);
            generatingChunks.Add(idx);
        }
    }

    private void IndexChunkStateFilesOnDisk()
    {
        // Best-effort: build an index of chunk state files so we can prioritize loading saves
        // and avoid generating over them when the per-tick load enqueue budget is hit.
        try
        {
            var folderName = $"{terrainConfig.WorldName}_{generationSeed}";
            var dirPath = Path.Combine(ResolveDataRoot(), SaveRootFolderName, folderName);
            if (!Directory.Exists(dirPath))
            {
                lock (chunkStateLoadLock)
                {
                    knownChunkStatesOnDisk.Clear();
                }
                return;
            }

            var files = Directory.GetFiles(dirPath, $"{ChunkFilePrefix}*{ChunkSaveFileExtension}");
            var indexed = new HashSet<int>();

            foreach (var file in files)
            {
                // Skip edits files.
                if (file.EndsWith($"{ChunkEditsSuffix}{ChunkSaveFileExtension}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split('_');
                if (parts.Length < 3
                    || !int.TryParse(parts[1], out var worldX)
                    || !int.TryParse(parts[2], out var worldZ))
                {
                    continue;
                }

                // Files encode chunk origin in world block coordinates, not chunk indices.
                var chunkX = worldX / VoxelHelper.ChunkSideSize;
                var chunkZ = worldZ / VoxelHelper.ChunkSideSize;
                if (chunkX < 0 || chunkZ < 0 || chunkX >= VoxelHelper.WorldChunksXZ || chunkZ >= VoxelHelper.WorldChunksXZ)
                {
                    continue;
                }

                indexed.Add(chunkZ * VoxelHelper.WorldChunksXZ + chunkX);
            }

            lock (chunkStateLoadLock)
            {
                knownChunkStatesOnDisk.Clear();
                foreach (var idx in indexed)
                {
                    knownChunkStatesOnDisk.Add(idx);
                }
            }
        }
        catch
        {
            // Best-effort.
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

            // Only persist full chunk state if the chunk has player edits.
            // Pure generation is deterministic from seed and should not create save files.
            if (chunkEdits.TryGetValue(result.ChunkIndex, out var edits) && edits.Count > 0)
            {
                MarkChunkStateDirty(result.ChunkIndex);
            }
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

        var evictionSnapshotsThisTick = 0;

        // Evict chunks not needed by any player.
        foreach (var idx in readyChunks.ToArray())
        {
            if (needed.Contains(idx))
            {
                continue;
            }

            // Never block on disk IO here. If dirty, enqueue a save snapshot (budgeted) before eviction.
            var isDirty = false;
            lock (chunkStateSaveLock)
            {
                isDirty = dirtyChunkStates.Contains(idx);
            }

            if (isDirty)
            {
                if (evictionSnapshotsThisTick >= MaxChunkStateSaveSnapshotsPerTick)
                {
                    // Keep it around until we can snapshot it on a later tick.
                    continue;
                }

                if (!TryEnqueueChunkStateSaveSnapshot(idx))
                {
                    // Can't snapshot right now; keep it in memory and retry later.
                    continue;
                }

                evictionSnapshotsThisTick++;
                lock (chunkStateSaveLock)
                {
                    dirtyChunkStates.Remove(idx);
                }
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

        edits = [];
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
        var root = ResolveDataRoot();
        var path = Path.Combine(root, TerrainConfigFileName);
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

    private static string ResolveDataRoot()
    {
        static bool LooksLikeDataRoot(string dir)
            => File.Exists(Path.Combine(dir, TerrainConfigFileName))
               || Directory.Exists(Path.Combine(dir, SaveRootFolderName));

        var cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(cwd) && LooksLikeDataRoot(cwd))
        {
            return cwd;
        }

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir) && LooksLikeDataRoot(baseDir))
        {
            return baseDir;
        }

        // Walk up a few parents from the executable directory. This handles running from bin/Debug
        // while keeping saves/config in the project folder.
        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (var i = 0; i < 6 && dir.Parent != null; i++)
            {
                dir = dir.Parent;
                if (LooksLikeDataRoot(dir.FullName))
                {
                    return dir.FullName;
                }
            }
        }
        catch
        {
            // Best-effort.
        }

        return baseDir;
    }

    private void LoadEdits()
    {
        // Load persisted chunk edit dictionaries (seed + edits -> regenerated chunks match).
        var folderName = $"{terrainConfig.WorldName}_{generationSeed}";
        var dirPath = Path.Combine(ResolveDataRoot(), SaveRootFolderName, folderName);
        if (!Directory.Exists(dirPath))
        {
            return;
        }

        var loadedCount = 0;

        // Load legacy edit files first (.bin), then current format (.dat) so newer wins.
        var legacyFiles = Directory.GetFiles(dirPath, $"{ChunkFilePrefix}*.bin");
        var files = Directory.GetFiles(dirPath, $"{ChunkFilePrefix}*{ChunkEditsSuffix}{ChunkSaveFileExtension}");

        foreach (var file in legacyFiles)
        {
            TryLoadEditsFile(file, deleteOnSuccess: true, ref loadedCount);
        }

        foreach (var file in files)
        {
            TryLoadEditsFile(file, deleteOnSuccess: false, ref loadedCount);
        }

        if (loadedCount > 0)
        {
            Log.Info($"Server ChunkStreamingManager: Loaded edits for {loadedCount} chunks from {dirPath}");
        }
    }

    private void TryLoadEditsFile(string file, bool deleteOnSuccess, ref int loadedCount)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('_');
            if (parts.Length < 3 || !int.TryParse(parts[1], out var worldX) || !int.TryParse(parts[2], out var worldZ))
            {
                return;
            }

            // Files encode chunk origin in world block coordinates, not chunk indices.
            var chunkX = worldX / VoxelHelper.ChunkSideSize;
            var chunkZ = worldZ / VoxelHelper.ChunkSideSize;

            if (chunkX < 0 || chunkZ < 0 || chunkX >= VoxelHelper.WorldChunksXZ || chunkZ >= VoxelHelper.WorldChunksXZ)
            {
                return;
            }

            var chunkIdx = chunkZ * VoxelHelper.WorldChunksXZ + chunkX;

            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);
            var count = reader.ReadInt32();

            // Expected file size for current ushort format: 4 + count * (int + ushort)
            var expectedSize = 4 + count * (sizeof(int) + sizeof(ushort));
            if (stream.Length != expectedSize)
            {
                // If it looks like the old byte format, delete it (can't safely interpret).
                var expectedOldSize = 4 + count * (sizeof(int) + sizeof(byte));
                if (stream.Length == expectedOldSize)
                {
                    stream.Close();
                    try { File.Delete(file); } catch { }
                }
                return;
            }

            var edits = new Dictionary<int, BlockId>(count);
            for (var i = 0; i < count; i++)
            {
                var voxelIdx = reader.ReadInt32();
                var type = (BlockId)reader.ReadUInt16();
                edits[voxelIdx] = type;
            }

            chunkEdits[chunkIdx] = edits;
            loadedCount++;

            if (deleteOnSuccess)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Ignore corrupted edit files.
        }
    }

    private void FlushChunkStateSaves()
    {
        List<int> toSnapshot;
        lock (chunkStateSaveLock)
        {
            toSnapshot = [.. dirtyChunkStates];
            dirtyChunkStates.Clear();
        }

        foreach (var idx in toSnapshot)
        {
            if (!TryEnqueueChunkStateSaveSnapshot(idx))
            {
                // Best-effort on shutdown; if snapshotting fails, we keep edits persistence as fallback.
                MarkChunkStateDirty(idx);
            }
        }
    }

    private void ShutdownChunkStateWorkers()
    {
        try { chunkStateLoadQueue.CompleteAdding(); } catch { }
        try { chunkStateSaveQueue.CompleteAdding(); } catch { }

        try { Task.WaitAll(chunkStateSaveWorkers, TimeSpan.FromSeconds(5)); } catch { }
        try { Task.WaitAll(chunkStateLoadWorkers, TimeSpan.FromSeconds(5)); } catch { }

        try { chunkStateSaveCts.Cancel(); } catch { }
        try { chunkStateLoadCts.Cancel(); } catch { }

        try { chunkStateSaveCts.Dispose(); } catch { }
        try { chunkStateLoadCts.Dispose(); } catch { }

        try { chunkStateSaveQueue.Dispose(); } catch { }
        try { chunkStateLoadQueue.Dispose(); } catch { }
    }

    private void ChunkStateLoadWorkerLoop()
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            foreach (var request in chunkStateLoadQueue.GetConsumingEnumerable(chunkStateLoadCts.Token))
            {
                var succeeded = TryLoadChunkStateFromPath(request.Path, request.ChunkIndex, out var data, out var biomeData);
                // Any failure means we should fall back to generation (avoids endless retries + tick-thread IO).
                var shouldFallback = !succeeded;
                chunkStateLoadResults.Enqueue(new ChunkStateLoadResult(
                    ChunkIndex: request.ChunkIndex,
                    Succeeded: succeeded,
                    ShouldFallbackToGeneration: shouldFallback,
                    Data: data,
                    BiomeData: biomeData));
            }
        }
        catch
        {
            // Shutdown path.
        }
    }

    private void ChunkStateSaveWorkerLoop()
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            foreach (var request in chunkStateSaveQueue.GetConsumingEnumerable(chunkStateSaveCts.Token))
            {
                var succeeded = TrySaveChunkStateToPath(request.Path, request.Data, request.BiomeData);
                chunkStateSaveResults.Enqueue(new ChunkStateSaveResult(
                    ChunkIndex: request.ChunkIndex,
                    Succeeded: succeeded,
                    EditsVersionAtSnapshot: request.EditsVersionAtSnapshot));
            }
        }
        catch
        {
            // Shutdown path.
        }
    }

    private static bool TrySaveChunkStateToPath(string path, ChunkData data, ChunkBiomeData? biomeData)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmpPath = path + ".tmp";

            using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var headerWriter = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: true))
            {
                headerWriter.Write(ChunkStateMagic);
                headerWriter.Write(ChunkStateSaveFileVersion);

                using var gzipStream = new GZipStream(fileStream, CompressionLevel.SmallestSize, leaveOpen: true);
                using var writer = new BinaryWriter(gzipStream, Encoding.UTF8, leaveOpen: true);
                data.Serialize(writer);

                if (biomeData != null)
                {
                    writer.Write(true);
                    biomeData.Serialize(writer);
                }
                else
                {
                    writer.Write(false);
                }
            }
            File.Move(tmpPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Server ChunkStreamingManager: Failed to save chunk state; chunkIdx={data.ChunkIndex} exType='{ex.GetType().Name}' exMessage='{ex.Message}' path='{path}'");
            return false;
        }
    }

    private readonly record struct ChunkStateLoadRequest(int ChunkIndex, string Path);

    private readonly record struct ChunkStateLoadResult(
        int ChunkIndex,
        bool Succeeded,
        bool ShouldFallbackToGeneration,
        ChunkData? Data,
        ChunkBiomeData? BiomeData);

    private readonly record struct ChunkStateSaveRequest(
        int ChunkIndex,
        string Path,
        ChunkData Data,
        ChunkBiomeData? BiomeData,
        long EditsVersionAtSnapshot);

    private readonly record struct ChunkStateSaveResult(
        int ChunkIndex,
        bool Succeeded,
        long EditsVersionAtSnapshot);
}
