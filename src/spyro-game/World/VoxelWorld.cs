using OpenRender;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.Components;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpyroGame.World;

public class VoxelWorld
{
    // GPU compaction outputs (Stage 2)
    public volatile uint CompactedAtlasSSBO;
    public volatile int[]? CompactedBases;
    public volatile int[]? CompactedCounts;
    public volatile int[]? CompactedChunkIndices;
    public volatile int CompactionVersion;
    public int VoxelIndexCount { get; internal set; }

    // Pending (ping-pong) compaction results guarded by a GPU fence; swapped when signaled
    public volatile uint PendingAtlasSSBO;
    public volatile int[]? PendingBases;
    public volatile int[]? PendingCounts;
    public volatile int[]? PendingChunkIndices;
    public volatile IntPtr PendingFence;

    public void SetPendingCompaction(uint atlas, int[] indices, int[] bases, int[] counts, IntPtr fence)
    {
        PendingAtlasSSBO = atlas;
        PendingChunkIndices = indices;
        PendingBases = bases;
        PendingCounts = counts;
        PendingFence = fence;
    }

    public void TrySwapPendingCompaction()
    {
        var fence = PendingFence;
        if (fence == IntPtr.Zero) return;
        // Non-blocking test
        var res = GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, 0);
        if (res is WaitSyncStatus.AlreadySignaled or WaitSyncStatus.ConditionSatisfied)
        {
            try
            {
                GL.DeleteSync(fence);
            }
            catch { }
            PendingFence = IntPtr.Zero;

            // Swap to pending
            if (CompactedAtlasSSBO != 0 && PendingAtlasSSBO != 0 && CompactedAtlasSSBO != PendingAtlasSSBO)
            {
                try { GL.DeleteBuffer(CompactedAtlasSSBO); } catch { }
            }
            CompactedAtlasSSBO = PendingAtlasSSBO;
            CompactedChunkIndices = PendingChunkIndices;
            CompactedBases = PendingBases;
            CompactedCounts = PendingCounts;
            unchecked { CompactionVersion++; }

        // reduced logging

            // Clear pending refs
            PendingAtlasSSBO = 0;
            PendingChunkIndices = null;
            PendingBases = null;
            PendingCounts = null;
        }
    }
    #region Textures and materials
    //  key is material id, value is the material
    public static readonly Dictionary<int, VoxelMaterial> materials = new() {
        { (int)BlockType.None, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0.3f),
                Specular = new Vector3(1),
                Shininess = 5f
            }
        },
        { (int)BlockType.Rock, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.5f, 0.5f, 0.5f),
                Shininess = 0.15f
            }
        },
        { (int)BlockType.Dirt, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.0f, 0.0f, 0.0f),
                Shininess = 0.0f
            }
        },
        { (int)BlockType.GrassDirt, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.3f, 0.5f, 0.3f),
                Shininess = 0.4f
            }
        },
        { (int)BlockType.Grass, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.3f, 0.5f, 0.3f),
                Shininess = 0.5f
            }
        },
        { (int)BlockType.Sand, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(1),
                Shininess = 0.3f
            }
        },
        { (int)BlockType.Snow, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0.01f, 0.01f, 0.01f),
                Specular = new Vector3(1),
                Shininess = 1.65f
            }
        },
        { (int)BlockType.WaterLevel, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(1),
                Shininess = 5.3f
            }
        },
        { (int)BlockType.BedRock, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0),
                Shininess = 0f
            }
        }
    };

    //  key is texture name, value texture handle index 
    public static readonly Dictionary<BlockType, string> textures = new() {
        { BlockType.None, "Resources/voxel/outline.png" },  //  we misuse the air block type that never gets rendered to hold the outline texture 
        //{ BlockType.UnderWater, "Resources/voxel/under-water.png" },
        { BlockType.WaterLevel, "Resources/voxel/water.png" },
        { BlockType.Rock, "Resources/voxel/rock.png" },
        { BlockType.Sand, "Resources/voxel/sand.png" },
        { BlockType.Dirt, "Resources/voxel/dirt.png" },
        { BlockType.GrassDirt, "Resources/voxel/grass-dirt.png" },
        { BlockType.Grass, "Resources/voxel/grass.png" },
        { BlockType.Snow, "Resources/voxel/snow.png" },
        { BlockType.BedRock, "Resources/voxel/bedrock.png" },
    };

    /// <summary>
    /// Prepares all textures for the voxel world.
    /// </summary>
    /// <param name="world"></param>
    /// <returns></returns>
    public static ulong[] GetTextureHandles()
    {
        var textureNames = Enumerable.Range(0, textures.Keys.Cast<int>().Max() + 1)
            .Select(index => textures.ContainsKey((BlockType)index) ? textures[(BlockType)index] : null)
            .ToArray();
        var sampler = Sampler.Create(TextureMinFilter.NearestMipmapNearest, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
        var handles = textureNames
                        .Select(x => string.IsNullOrEmpty(x) ? null : Texture.FromFile([x!]))
                        .Select(x => x?.GetBindlessHandle(sampler) ?? 0)
                        .ToArray();
        return handles;
    }

    /// <summary>
    /// Prepares all materials for the voxel world.
    /// </summary>
    /// <param name="world"></param>
    /// <returns></returns>
    public static VoxelMaterial[] GetMaterials() => [.. Enumerable.Range(0, materials.Keys.Max() + 1).Select(index => materials.TryGetValue(index, out var value) ? value : default)];
    #endregion

    public static readonly Vector3i ChunkSize = new(VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, VoxelHelper.ChunkSideSize);
    public static readonly Vector3i ChunkHalfSize = ChunkSize / 2;

    /// <summary>
    /// Queue of chunks to be created by background threads.
    /// </summary>
    private readonly ConcurrentQueue<int> workQueue = new();

    //  key is chunk index, value is the chunk   
    // Unified active chunk set: holds both visible-near and cached-far chunks.
    // Eviction trims this set when it grows too large.
    private readonly ConcurrentDictionary<int, Chunk> loadedChunks = [];

    internal Stopwatch stopwatch = Stopwatch.StartNew();

    private readonly int seed;
    private readonly HashSet<int> surroundingChunkSet = [];
    // Multi-worker CPU generation
    private readonly List<Thread> workerThreads = [];
    private readonly int workerCount;
    private long lastStreamingUpdateMs;
    private long lastCompactionRebuildMs;
    private long lastCompactionRepackMs;
    // Allocator state (Phase 2.5+): free draw slots and free atlas ranges harvested from evicted draws
    internal List<int> FreeDrawSlots { get; } = [];
    internal List<(int Base, int Length)> FreeAtlasRanges { get; } = [];
    private const int CacheCapacity = 4096; // unified cap for active chunks (surrounding + cache)
    private readonly Queue<int> cacheFifo = new();
    private readonly object cacheLock = new();
    internal readonly TerrainBuilder terrainBuilder;
    internal ChunkInitializer? ChunkInitializer { get; private set; }
    private readonly ConcurrentDictionary<int, long> pendingUploadSince = new();
    private readonly ConcurrentDictionary<int, long> pendingComputeSince = new();
    private Vector3 lastCameraPosition;
    private Quaternion lastCameraOrientation;
    private int lastCameraChunkX = int.MinValue;
    private int lastCameraChunkZ = int.MinValue;

    internal void EnsureChunkInitializer()
    {
        if (ChunkInitializer is null)
        {
            ChunkInitializer = new ChunkInitializer(this);
            // Preallocate buffers for the full surrounding set to avoid invalid dispatches during movement
            try { ChunkInitializer.PreallocateForMaxSurrounding(); } catch { }
        }
    }

    internal void OnChunkInitializerDisposed(ChunkInitializer initializer)
    {
        if (ReferenceEquals(ChunkInitializer, initializer))
        {
            ChunkInitializer = null;
        }
    }

    private ICamera camera = default!;

    public volatile int ProcessedStartingChunks;
    public int TotalStartingChunks;

    public ChunkRenderer ChunkRenderer { get; private set; }

    public int WorkerQueueLength => workQueue.Count;

    private volatile bool isRunning;

    public VoxelWorld(int seed)
    {
        this.seed = seed;

        terrainBuilder = new TerrainBuilder(seed);
        ChunkRenderer = ChunkRenderer.Create(this, GetTextureHandles(), GetMaterials());

        // GPU-first pipeline: disable CPU worker threads
        workerCount = 0;
        isRunning = false;
    }

    /// <summary>
    /// Total number of chunks passing the culling test.
    /// </summary>
    public int ChunksInFrustum { get; private set; }

    public ICamera Camera
    {
        get => camera;
        set
        {
            // Unhook any previous camera to avoid duplicate events
            if (camera is not null)
            {
                camera.CameraChanged -= Camera_CameraChanged;
            }
            camera = value;
            camera.CameraChanged += Camera_CameraChanged;

            // Initialize camera pose tracking and compute initial visibility/culling
            lastCameraPosition = camera.Position;
            lastCameraOrientation = camera.Orientation;
            lastCameraChunkX = (int)((camera.Position.X + 0.5f) / VoxelHelper.ChunkSideSize);
            lastCameraChunkZ = (int)((camera.Position.Z + 0.5f) / VoxelHelper.ChunkSideSize);
            CalculateTerrainStreamingChanges();
            lastStreamingUpdateMs = stopwatch.ElapsedMilliseconds;
        }
    }

    public int Seed => seed;

    public IReadOnlyCollection<int> SurroundingChunkIndices => surroundingChunkSet;

    public int LoadedChunksCount => loadedChunks.Count;
    // Number of chunks currently in active set but outside the surrounding area.
    public int CachedChunksCount
    {
        get
        {
            lock (surroundingChunkSet)
            {
                return loadedChunks.Count - surroundingChunkSet.Count;
            }
        }
    }

    public IEnumerable<Chunk> SurroundingChunks
    {
        get
        {
            var sc = loadedChunks.Where(x => surroundingChunkSet.Contains(x.Value.Index));
            return sc.Select(x => x.Value);
        }
    }


    /// <summary>
    /// Gets a loaded chunk containing the given global XZ position.
    /// Note: the globalPosition.Y is ignored.
    /// </summary>
    /// <param name="globalPosition"></param>
    /// <param name="chunk"></param>
    /// <returns>True if chunk is loaded else false</returns>
    public bool GetChunkByGlobalPosition(Vector3 globalPosition, out Chunk? chunk)
    {
        chunk = null;
        globalPosition.Y = VoxelHelper.MaxBlockPositionY;
        if (!VoxelHelper.IsGlobalPositionInWorld(globalPosition)) return false;

        var idx = VoxelHelper.GetChunkIndexFromPositionGlobal(globalPosition);
        chunk = this[idx];
        return chunk != null;
    }

    public Chunk? this[int index] => loadedChunks.TryGetValue(index, out var chunk) ? chunk : null;

    public void PrepareStartingChunks(Vector3 position)
    {
        var cameraChunkX = (int)((position.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)((position.Z + 0.5f) / VoxelHelper.ChunkSideSize);
        var set = BuildSurroundingSet(cameraChunkX, cameraChunkZ);
        lock (surroundingChunkSet)
        {
            surroundingChunkSet.Clear();
            foreach (var i in set) surroundingChunkSet.Add(i);
        }
        TotalStartingChunks = surroundingChunkSet.Count;

        // Bootstrap GPU compaction atlas for the initial surrounding set
        try
        {
            EnsureChunkInitializer();
            if (ChunkInitializer is not null)
            {
                var indices = surroundingChunkSet.ToArray();
                if (indices.Length > 0)
                {
                    ChunkInitializer.ProcessChunkData(indices);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Calculates surrounding chunks, initializes them and adds to the world.
    /// Note this is a initialization method and should only be called only once.
    /// </summary>
    /// <param name="position"></param>
    public void AddStartingChunks(Vector3 position)
    {
        var cameraChunkX = (int)((position.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)((position.Z + 0.5f) / VoxelHelper.ChunkSideSize);
        var set = BuildSurroundingSet(cameraChunkX, cameraChunkZ);
        lock (surroundingChunkSet)
        {
            surroundingChunkSet.Clear();
            foreach (var i in set) surroundingChunkSet.Add(i);
        }

        //  note: this is split into two steps to avoid the case where visibility calculation
        //        needs to reference neighboring chunk having not yet initialized blocks 
        var start = stopwatch.ElapsedMilliseconds;
        var indices = surroundingChunkSet.ToArray();
        TotalStartingChunks = indices.Length;

        Parallel.For(0, surroundingChunkSet.Count, i =>
        {
            var index = indices[i];
            var chunk = CreateChunk(index);
            _ = LoadChangedChunkBlocks(chunk);
            Interlocked.Increment(ref ProcessedStartingChunks);
        });
        Log.Debug($"{indices.Length} chunks initialized in: {stopwatch.ElapsedMilliseconds - start} ms");


        ProcessedStartingChunks = 0;
        start = stopwatch.ElapsedMilliseconds;
        Parallel.For(0, surroundingChunkSet.Count, i =>
        {
            var index = indices[i];
            var chunk = loadedChunks[index];
            chunk.RecomputeLighting(true);
            Interlocked.Increment(ref ProcessedStartingChunks);
        });
        Log.Debug($"{indices.Length} chunks visibility calculated in: {stopwatch.ElapsedMilliseconds - start} ms");

        foreach (var index in indices)
        {
            if (loadedChunks.TryGetValue(index, out var chunk) && chunk.Blocks is not null)
            {
                RefreshChunkBorders(chunk);
            }
        }
    }

    private Chunk CreateChunkContainer(int chunkIndex)
    {
        if (loadedChunks.TryGetValue(chunkIndex, out var loaded))
            return loaded;

        // unified active set: no separate cached collection

        var position = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var chunk = new Chunk(this, chunkIndex)
        {
            Aabb = (position, position + ChunkSize),
            ChunkPosition = new(position.X, position.Z),
            State = ChunkState.Loaded,
        };
        loadedChunks[chunkIndex] = chunk;
        lock (cacheLock) { cacheFifo.Enqueue(chunkIndex); }
        return chunk;
    }

    public Chunk CreateChunk(int chunkIndex)
    {
        var chunk = CreateChunkContainer(chunkIndex);
        if (!chunk.IsInitialized)
        {
            var generationData = terrainBuilder.BuildChunkData(chunkIndex);
            chunk.ApplyGenerationData(terrainBuilder, generationData);
            chunk.RecomputeLighting(force: true, includeNeighborData: true);
        }
        return chunk;
    }

    public Chunk GetOrCreateChunkContainer(int chunkIndex) => CreateChunkContainer(chunkIndex);

    internal void EnsureChunkGenerated(Chunk chunk)
    {
        if (chunk.IsInitialized && chunk.IsProcessed) return;

        if (!chunk.IsDirty)
        {
            EnsureChunkInitializer();
            if (ChunkInitializer is not null)
            {
                ChunkInitializer.ProcessChunkData([chunk.Index]);
                return;
            }
        }

        if (!chunk.IsInitialized)
        {
            var generationData = terrainBuilder.BuildChunkData(chunk.Index);
            chunk.ApplyGenerationData(terrainBuilder, generationData);
            chunk.RecomputeLighting(force: true, includeNeighborData: true);
        }
        if (!chunk.IsProcessed)
        {
            chunk.RecomputeLighting(includeNeighborData: true);
        }
    }

    [ThreadStatic]
    private static Dictionary<long, BlockType>? borderQueryCache;

    private static long PackKey(int x, int y, int z) => ((long)(x & 0x3FFFFF) << 42) | ((long)(y & 0x3FF) << 32) | (long)(z & 0x3FFFFF);

    public BlockType GetBlockTypeGlobal(Vector3i globalPosition)
    {
        if (globalPosition.Y < 0)
            return BlockType.BedRock;
        if (globalPosition.Y > VoxelHelper.MaxBlockPositionY)
            return BlockType.None;
        if (globalPosition.X < 0 || globalPosition.Z < 0 ||
            globalPosition.X > VoxelHelper.MaxBlockPositionXZ ||
            globalPosition.Z > VoxelHelper.MaxBlockPositionXZ)
        {
            return BlockType.None;
        }

        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(globalPosition);
        var key = PackKey(globalPosition.X, globalPosition.Y, globalPosition.Z);
        if (borderQueryCache is not null && borderQueryCache.TryGetValue(key, out var cached))
            return cached;
        if (loadedChunks.TryGetValue(chunkIndex, out var loaded) && loaded.Blocks is not null)
        {
            var local = globalPosition - loaded.Position;
            var bt = loaded.Blocks[local.X + local.Z * VoxelHelper.ChunkSideSize + local.Y * VoxelHelper.ChunkSideSizeSquare].BlockType;
            borderQueryCache?[key] = bt;
            return bt;
        }

        // GPU-first collision: if we have column heights, synthesize a block type quickly to match GPU terrain
        if (loadedChunks.TryGetValue(chunkIndex, out loaded))
        {
            var origin2 = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
            var lx2 = globalPosition.X - origin2.X;
            var lz2 = globalPosition.Z - origin2.Z;
            if ((uint)lx2 < (uint)VoxelHelper.ChunkSideSize && (uint)lz2 < (uint)VoxelHelper.ChunkSideSize)
            {
                // Use per-column max height; treat below as solid, above as air; water occupies air below water level
                int terrainH = loaded.GetTerrainHeightAt(lx2, lz2);
                var y = globalPosition.Y - origin2.Y;
                BlockType bt;
                if (y < terrainH)
                {
                    // choose a representative solid type; collision only needs solid vs non-solid
                    bt = BlockType.Rock;
                }
                else if (globalPosition.Y <= VoxelHelper.WaterLevel)
                {
                    bt = BlockType.WaterLevel;
                }
                else
                {
                    bt = BlockType.None;
                }
                borderQueryCache?[key] = bt;
                return bt;
            }
        }

        // unified: no separate cached collection

        var chunkOrigin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var lx = globalPosition.X - chunkOrigin.X;
        var ly = globalPosition.Y - chunkOrigin.Y;
        var lz = globalPosition.Z - chunkOrigin.Z;
        var result = terrainBuilder.GenerateChunkBlockLocal(chunkIndex, lx, ly, lz);
        borderQueryCache?[key] = result;
        return result;
    }

    public BlockState? PickBlock(Vector3 origin, Vector3 direction, float pickingDistance = VoxelHelper.MaxPickingDistance)
    {
        // GPU-first: DDA voxel traversal using GetBlockByPositionGlobalSafe()
        if (direction.LengthSquared < 1e-8f) return null;
        var dir = direction;
        dir.Normalize();

        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);

        int stepX = dir.X > 0 ? 1 : (dir.X < 0 ? -1 : 0);
        int stepY = dir.Y > 0 ? 1 : (dir.Y < 0 ? -1 : 0);
        int stepZ = dir.Z > 0 ? 1 : (dir.Z < 0 ? -1 : 0);

        float tMaxX, tMaxY, tMaxZ;
        float tDeltaX = stepX != 0 ? MathF.Abs(1f / dir.X) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? MathF.Abs(1f / dir.Y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? MathF.Abs(1f / dir.Z) : float.PositiveInfinity;

        float fx = origin.X - MathF.Floor(origin.X);
        float fy = origin.Y - MathF.Floor(origin.Y);
        float fz = origin.Z - MathF.Floor(origin.Z);

        tMaxX = stepX > 0 ? (1f - fx) * (tDeltaX == float.PositiveInfinity ? 1f : tDeltaX) : (fx) * (tDeltaX == float.PositiveInfinity ? 1f : tDeltaX);
        tMaxY = stepY > 0 ? (1f - fy) * (tDeltaY == float.PositiveInfinity ? 1f : tDeltaY) : (fy) * (tDeltaY == float.PositiveInfinity ? 1f : tDeltaY);
        tMaxZ = stepZ > 0 ? (1f - fz) * (tDeltaZ == float.PositiveInfinity ? 1f : tDeltaZ) : (fz) * (tDeltaZ == float.PositiveInfinity ? 1f : tDeltaZ);

        // Check starting cell
        var startBlock = GetBlockByPositionGlobalSafe(x, y, z);
        if (startBlock is not null && startBlock.Value.BlockType is not BlockType.None and not BlockType.WaterLevel)
        {
            return startBlock;
        }

        float t = 0f;
        int maxSteps = (int)(pickingDistance * 4) + 4; // guard
        for (int i = 0; i < maxSteps && t <= pickingDistance; i++)
        {
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                x += stepX; t = tMaxX; tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                y += stepY; t = tMaxY; tMaxY += tDeltaY;
            }
            else
            {
                z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ;
            }

            if (y is < 0 or > VoxelHelper.MaxBlockPositionY) break;
            if (x < 0 || z < 0 || x > VoxelHelper.MaxBlockPositionXZ || z > VoxelHelper.MaxBlockPositionXZ) break;

            var b = GetBlockByPositionGlobalSafe(x, y, z);
            if (b is not null && b.Value.BlockType is not BlockType.None and not BlockType.WaterLevel)
            {
                return b;
            }
        }
        return null;
    }

    public void BreakBlock(BlockState block)
    {
        var updatedChunks = new HashSet<Chunk>();
        if (block.BlockType is BlockType.None or BlockType.WaterLevel)
        {
            Log.Warn("BreakBlock() invalid block type!");
            return;
        }

        var chunk = this[block.ChunkIndex];
        if (chunk is null)
        {
            Log.Warn("BreakBlock() chunk not found for block {0}", block);
            return;
        }

        // If CPU Blocks[] exists, update it; otherwise rely on GPU path only
        if (chunk.Blocks is not null)
        {

            block.BlockType = BlockType.None;
            block.IsVisible = false;
            chunk.UpdateBlock(ref block, true);
            updatedChunks.Add(chunk);
        }

        // Set a 3D break mask bit for the exact voxel, and also set column mask
        var origin = VoxelHelper.GetChunkPositionGlobal(block.ChunkIndex);
        var lx = block.GlobalPosition.X - origin.X;
        var lz = block.GlobalPosition.Z - origin.Z;
        var ly = block.GlobalPosition.Y - origin.Y;
        SetBreakMask3DBit(block.ChunkIndex, lx, ly, lz, hidden: true);
        SetBreakMaskBit(block.ChunkIndex, lx, lz, hidden: true);

        // Minimal GPU update: owner + cardinals only
        try
        {
            EnsureChunkInitializer();
            if (ChunkInitializer is not null)
            {
                var idx = block.ChunkIndex;
                var side = VoxelHelper.WorldChunksXZ;
                var cx = idx % side; var cz = idx / side;
                var primaries = new List<int>(5) { idx };
                var west = (cx > 0) ? idx - 1 : -1;
                var east = (cx + 1 < side) ? idx + 1 : -1;
                var north = (cz > 0) ? idx - side : -1;
                var south = (cz + 1 < side) ? idx + side : -1;
                if (west >= 0) primaries.Add(west);
                if (east >= 0) primaries.Add(east);
                if (north >= 0) primaries.Add(north);
                if (south >= 0) primaries.Add(south);
                var ok = ChunkInitializer.UpdateChunksInPlace([.. primaries]);
                if (!ok)
                {
                    // Fallback: rebuild current surrounding set if in-place update fails
                    int[] desired;
                    lock (surroundingChunkSet)
                    {
                        desired = surroundingChunkSet.Count > 0 ? [.. surroundingChunkSet] : [idx];
                    }
                    ChunkInitializer.ProcessChunkData(desired);
                }
            }
        }
        catch { }

        //  make neighbor blocks visible
        void MakeNeighborBlockVisible(BlockState? blockState)
        {
            if (blockState is not null && !blockState.Value.IsTransparent)
            {
                block = blockState.Value;
                block.IsVisible = true;
                var chunk = this[block.ChunkIndex];
                if (chunk is not null && chunk.Blocks is not null) { chunk.UpdateBlock(ref block); updatedChunks.Add(chunk); }
            }
        }

        var neighbors = GetNeighboringBlocks(block);
        foreach (var neighbor in neighbors)
        {
            MakeNeighborBlockVisible(neighbor);
        }

        if (updatedChunks.Count > 0) {
            foreach (var updatedChunk in updatedChunks)
            {
                var chunkToProcess = updatedChunk;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    chunkToProcess.RecomputeLighting(force: true, includeNeighborData: true);
                    RefreshChunkBorders(chunkToProcess);
                });
            }
        }
    }

    public static bool IsSphereBlockCollision(in AABB aabb, in Vector3 spherePosition, float sphereRadius)
    {
        // Calculate the closest point to the sphere on the AABB
        var closestX = Math.Max(aabb.Min.X, Math.Min(spherePosition.X, aabb.Max.X));
        var closestY = Math.Max(aabb.Min.Y, Math.Min(spherePosition.Y, aabb.Max.Y));
        var closestZ = Math.Max(aabb.Min.Z, Math.Min(spherePosition.Z, aabb.Max.Z));

        // Calculate the distance between the closest point and the sphere's center
        var distanceSquared = (closestX - spherePosition.X) * (closestX - spherePosition.X) +
                              (closestY - spherePosition.Y) * (closestY - spherePosition.Y) +
                              (closestZ - spherePosition.Z) * (closestZ - spherePosition.Z);

        // Check if the distance is less than the sphere's radius squared
        return distanceSquared <= (sphereRadius * sphereRadius);
    }

    public BlockState? GetBlockByPositionGlobalSafe(int x, int y, int z)
    {
        var blockWorldPosition = new Vector3i(x, y, z);
        if (!VoxelHelper.IsGlobalPositionInWorld(blockWorldPosition)) return null;

        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(blockWorldPosition);
        var chunk = this[chunkIndex];
        if (chunk is null) return null;
        // Prefer GPU column heights for collision stability; spans are used as a fallback.
        if (chunk.HasGpuColumns)
        {
            var origin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
            var lx = x - origin.X;
            var ly = y - origin.Y;
            var lz = z - origin.Z;
            if ((uint)lx >= (uint)VoxelHelper.ChunkSideSize || (uint)lz >= (uint)VoxelHelper.ChunkSideSize || (uint)ly >= (uint)VoxelHelper.ChunkYSize)
                return null;
            var h = chunk.GetTerrainHeightAt(lx, lz); // top face local y (one past top solid)
            var maxSolid = h - 1;

            // Classify via TerrainBuilder to match CPU material layering.
            var bt = BlockType.None;
            if (ly <= maxSolid)
            {
                bt = TerrainBuilder.GenerateChunkBlockType(maxSolid, lx, ly, lz);
                // Treat WaterLevel as air for CPU queries/HUD/collision
                if (bt == BlockType.WaterLevel) bt = BlockType.None;
            }

            // Apply 3D break mask (1 bit per voxel) to carve tunnels/holes
            try
            {
                var mask = GetBreakMask3D(chunkIndex);
                if (mask is not null)
                {
                    int area = VoxelHelper.ChunkSideSizeSquare;
                    int linear = lx + lz * VoxelHelper.ChunkSideSize + ly * area;
                    int byteIndex = linear >> 3; // 8 voxels per byte
                    int bit = linear & 7;
                    if ((uint)byteIndex < (uint)mask.Length)
                    {
                        if ((mask[byteIndex] & (byte)(1 << bit)) != 0)
                            bt = BlockType.None;
                    }
                }
            }
            catch { }

            var idx = lx + lz * VoxelHelper.ChunkSideSize + ly * VoxelHelper.ChunkSideSizeSquare;
            return new BlockState(idx, chunk) { BlockType = bt, IsVisible = bt != BlockType.None };
        }
        if (chunk.HasGpuSpans)
        {
            var origin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
            var lx = x - origin.X; var ly = y - origin.Y; var lz = z - origin.Z;
            if ((uint)lx >= (uint)VoxelHelper.ChunkSideSize || (uint)lz >= (uint)VoxelHelper.ChunkSideSize || (uint)ly >= (uint)VoxelHelper.ChunkYSize)
                return null;
            var solid = chunk.IsSolidBySpans(lx, ly, lz, maxSpans: 3);
            var idx = lx + lz * VoxelHelper.ChunkSideSize + ly * VoxelHelper.ChunkSideSizeSquare;
            var bt = solid ? BlockType.Rock : BlockType.None;
            return new BlockState(idx, chunk) { BlockType = bt, IsVisible = solid };
        }
        // Fallback to CPU blocks only if GPU columns are not ready
        if (!chunk.IsInitialized || chunk.Blocks is null) return null;

        var chunkWorldPosition = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var (cx, cy, cz) = blockWorldPosition - chunkWorldPosition;
        var block = chunk.Blocks[cx + cz * VoxelHelper.ChunkSideSize + cy * VoxelHelper.ChunkSideSizeSquare];
        return block;
    }

    #region Block neighbors
    private readonly Vector3i[] neighboringOffsets = [
        new (0, 0, 1),      //  front
        new (0, 0, -1),     //  back
        new (1, 0, 0),      //  right
        new (-1, 0, 0),     //  left
    ];

    /// <summary>
    /// Returns adjacent blocks to the central block: front, back, right, left, above, and below.
    /// </summary>
    /// <param name="centralBlock"></param>
    /// <returns></returns>
    public BlockState?[] GetNeighboringBlocks(in BlockState centralBlock)
    {
        BlockState? neighborBlock;
        var neighboringBlocks = new BlockState?[6];
        var globalPosition = centralBlock.GlobalPosition;

        for (var i = 0; i < neighboringOffsets.Length; i++)
        {
            var offset = neighboringOffsets[i];
            var neighborPosition = globalPosition + offset;
            neighborBlock = GetBlockByPositionGlobalSafe(neighborPosition.X, neighborPosition.Y, neighborPosition.Z);
            neighboringBlocks[i] = neighborBlock;
        }

        //  add block bellow
        neighborBlock = GetBlockByPositionGlobalSafe(globalPosition.X, globalPosition.Y - 1, globalPosition.Z);
        neighboringBlocks[^1] = neighborBlock;

        //  add block above
        neighborBlock = GetBlockByPositionGlobalSafe(globalPosition.X, globalPosition.Y + 1, globalPosition.Z);
        neighboringBlocks[^2] = neighborBlock;

        return neighboringBlocks;
    }

    /// <summary>
    /// Returns blocks that can collide with the given center block. Returned blocks are front, left, right, back, and above.
    /// If height is greater than 1, also returns same block configuration for layers above the center block.
    /// Note: block bellow is not a colliding candidate block and there is only one block above instead one for every Y layer.
    /// </summary>
    /// <param name="centerBlock"></param>
    /// <param name="heightInBlocks"></param>
    /// <returns></returns>
    public BlockState?[] GetCollideCandidateBlocks(in BlockState centerBlock, int heightInBlocks = 1)
    {
        BlockState? neighborBlock;
        var neighboringBlocks = new BlockState?[4 * heightInBlocks + 1];
        var globalPosition = centerBlock.GlobalPosition;
        for (var height = 0; height < heightInBlocks; height++)
        {
            for (var i = 0; i < neighboringOffsets.Length; i++)
            {
                var offset = neighboringOffsets[i];
                var neighborPosition = globalPosition + offset;
                neighborBlock = GetBlockByPositionGlobalSafe(neighborPosition.X, neighborPosition.Y + height, neighborPosition.Z);
                neighboringBlocks[i + height * neighboringOffsets.Length] = neighborBlock;
            }
        }

        //  add block above
        neighborBlock = GetBlockByPositionGlobalSafe(globalPosition.X, globalPosition.Y + heightInBlocks, globalPosition.Z);
        neighboringBlocks[^1] = neighborBlock;

        return neighboringBlocks;
    }
    #endregion

    #region terrain streaming

    public void Close()
    {
        Log.Info("VoxelWorld.Close() stopping background workers...");
        isRunning = false;
        foreach (var t in workerThreads)
        {
            try { t.Join(); } catch { /* ignore */ }
        }
        Log.Info("VoxelWorld.Close() saving chunk changes...");
        foreach (var chunk in loadedChunks.Values)
        {
            SaveChangedChunkBlocks(chunk);
        }
        // unified: nothing else to do
        ChunkInitializer?.Dispose();
        ChunkInitializer = null;
        Log.Info("VoxelWorld.Close() all done!");
    }

    private readonly AutoResetEvent cameraChangedEvent = new(false);

    private void Camera_CameraChanged(object? sender, EventArgs e) => cameraChangedEvent.Set();

    private void CalculateTerrainStreamingChanges()
    {
        if (camera is null) return;

        // Determine current camera chunk tile
        var camPos = camera.Position;
        var cameraChunkX = (int)((camPos.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)((camPos.Z + 0.5f) / VoxelHelper.ChunkSideSize);

        // Build desired surrounding set around current tile
        var desired = BuildSurroundingSet(cameraChunkX, cameraChunkZ);
        lock (surroundingChunkSet)
        {
            surroundingChunkSet.Clear();
            foreach (var idx in desired) surroundingChunkSet.Add(idx);
        }

        // Ensure generation/upload for desired set
        int toGenerate = 0, toUpload = 0, ready = 0;
        var nowMs = stopwatch.ElapsedMilliseconds;
        foreach (var idx in desired)
        {
            if (!loadedChunks.TryGetValue(idx, out var chunk))
            {
                // Create container and schedule generation
                var ch = CreateChunkContainer(idx);
                if (!ch.PendingCompute)
                {
                    ch.PendingCompute = true;
                    workQueue.Enqueue(idx);
                    pendingComputeSince[idx] = nowMs;
                }
                toGenerate++;
            }
            else
            {
                if (!chunk.IsInitialized || !chunk.IsProcessed)
                {
                    if (!chunk.PendingCompute)
                    {
                        chunk.PendingCompute = true;
                        workQueue.Enqueue(idx);
                        pendingComputeSince[idx] = nowMs;
                    }
                    toGenerate++;
                }
                else if (chunk.BlocksSSBO == 0u || chunk.State != ChunkState.Added)
                {
                    EnqueueChunkForUpload(chunk);
                    toUpload++;
                }
                else ready++;
            }
        }
        //Log.Debug($"Streaming: desired={desired.Count}, gen={toGenerate}, upload={toUpload}, ready={ready}, workQ={workQueue.Count}");

        // Update visibility from a frustum snapshot
        var planes = (Vector4[])camera.Frustum.Planes.Clone();
        UpdateChunkVisibility(planes);

        // Append-only streaming: on tile change, compute additions and append only those.
        var now = stopwatch.ElapsedMilliseconds;
        const int RebuildDebounceMs = 120; // slightly tighter debounce
        if ((lastCameraChunkX != cameraChunkX || lastCameraChunkZ != cameraChunkZ) && (now - lastCompactionRebuildMs) >= RebuildDebounceMs)
        {
            EnsureChunkInitializer();
            if (ChunkInitializer is not null)
            {
                int[] desiredArr;
                lock (surroundingChunkSet) { desiredArr = [.. surroundingChunkSet]; }
                if (desiredArr.Length > 0 && !ChunkInitializer.HasInFlightBatch)
                {
                    var current = CompactedChunkIndices ?? [];
                    var currentSet = new HashSet<int>(current);
                    var toAdd = desiredArr.Where(i => !currentSet.Contains(i)).ToArray();
                    // Harvest freelists from resident draws not in desired set
                    FreeDrawSlots.Clear();
                    FreeAtlasRanges.Clear();
                    if (CompactedChunkIndices != null && CompactedBases != null && CompactedCounts != null)
                    {
                        var desiredSet = new HashSet<int>(desiredArr);
                        for (int i = 0; i < CompactedChunkIndices.Length; i++)
                        {
                            var idxRes = CompactedChunkIndices[i];
                            if (!desiredSet.Contains(idxRes))
                            {
                                FreeDrawSlots.Add(i);
                                var cnt = CompactedCounts[i];
                                if (cnt > 0) FreeAtlasRanges.Add((CompactedBases[i], cnt));
                            }
                        }
                    }
                    if (CompactedAtlasSSBO == 0 || current.Length == 0)
                    {
                        ChunkInitializer.ProcessChunkData(desiredArr);
                    }
                    else if (toAdd.Length > 0)
                    {
                        try { ChunkInitializer.AppendChunks(toAdd); } catch { }
                    }
                    lastCompactionRebuildMs = now;
                    lastCameraChunkX = cameraChunkX;
                    lastCameraChunkZ = cameraChunkZ;
                }
            }
        }

        // Phase 2.5: periodic eviction/repack when resident overhead is high
        // If resident draw array is much larger than desired, rebuild exactly for desired
        var resident = CompactedChunkIndices ?? [];
        var desiredCountNow = desired.Count;
        const float OverheadFactor = 1.6f;   // rebuild if resident > 1.6x desired
        const int MinOverhead = 256;         // and at least 256 extra draws
        const int RepackDebounceMs = 500;    // don’t spam repacks
        if (resident.Length > 0 && desiredCountNow > 0)
        {
            var extra = resident.Length - desiredCountNow;
            if (extra > 0 && (resident.Length > desiredCountNow * OverheadFactor) && extra >= MinOverhead)
            {
                if (now - lastCompactionRepackMs >= RepackDebounceMs && ChunkInitializer is not null && !ChunkInitializer.HasInFlightBatch)
                {
                    int[] desiredArr;
                    lock (surroundingChunkSet) { desiredArr = [.. surroundingChunkSet]; }
                    try { ChunkInitializer.ProcessChunkData(desiredArr); lastCompactionRepackMs = now; } catch { }
                }
            }
        }
    }

    // Global MDI buffers prepared by the publisher; renderer consumes them directly
    public volatile uint PreparedIndirectCmdBuffer; // GL_DRAW_INDIRECT_BUFFER
    public volatile uint PreparedDrawDataSSBO;      // binding=5
    public volatile uint PreparedDrawCountSSBO;     // binding=15 (optional for CountARB)
    public volatile IntPtr PreparedDrawCountPtr;    // mapped read pointer (fallback when CountARB not used)
    public volatile int PreparedCapacity;

    public void SetPreparedMdi(uint indirect, uint drawData, uint drawCount, IntPtr drawCountPtr, int capacity)
    {
        PreparedIndirectCmdBuffer = indirect;
        PreparedDrawDataSSBO = drawData;
        PreparedDrawCountSSBO = drawCount;
        PreparedDrawCountPtr = drawCountPtr;
        PreparedCapacity = capacity;
    }

    /// <summary>
    /// Updates the surrounding chunk indices based on the given center position.
    /// </summary>
    /// <param name="centerPosition"></param>
    /// <returns></returns>
    private static HashSet<int> BuildSurroundingSet(int cameraChunkX, int cameraChunkZ)
    {
        var newChunkSetIndices = new HashSet<int>(1024);

        const int GuardBandChunks = 0; // strict radius
        var r = VoxelHelper.MaxDistanceInChunks + GuardBandChunks;

        var minX = Math.Max(0, cameraChunkX - r);
        var maxX = Math.Min(VoxelHelper.WorldChunksXZ - 1, cameraChunkX + r);
        var minZ = Math.Max(0, cameraChunkZ - r);
        var maxZ = Math.Min(VoxelHelper.WorldChunksXZ - 1, cameraChunkZ + r);

        for (var z = minZ; z <= maxZ; z++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                newChunkSetIndices.Add(x + z * VoxelHelper.WorldChunksXZ);
            }
        }
        return newChunkSetIndices;
    }

    private void UpdateChunkVisibility(in Vector4[] planes)
    {
        // Slight edge padding to reduce near-edge flicker during rotation.
        const float EdgeCullingMarginBlocks = 2f; // expand XZ by a couple of blocks
        var inFrustumCount = 0;
        foreach (var kv in loadedChunks)
        {
            var chunk = kv.Value;
            if (!chunk.IsProcessed)
            {
                chunk.Visible = false;
                continue;
            }
            var (min, max) = chunk.Aabb;
            var pad = new Vector3(EdgeCullingMarginBlocks, 0f, EdgeCullingMarginBlocks);
            var padded = (min - pad, max + pad);
            var visible = CullingHelper.IsAabbInFrustum(padded, planes);
            if (visible)
            {
                chunk.Visible = true;
                chunk.VisibleLinger = 2; // keep visible for a couple of frames to avoid flicker
                inFrustumCount++;
            }
            else
            {
                if (chunk.VisibleLinger > 0)
                {
                    chunk.VisibleLinger--;
                    chunk.Visible = true;
                    inFrustumCount++;
                }
                else
                {
                    chunk.Visible = false;
                }
            }
        }
        ChunksInFrustum = inFrustumCount;
    }

    /// <summary>
    /// If chunk not already loaded or in cache, creates a new chunk, initializes it and adds to loaded collection.
    /// </summary>
    /// <param name="chunkIndex"></param>
    /// <returns></returns>
    private bool CreateChunkInitializeAndAddToLoaded(int chunkIndex, out Chunk chunk)
    {
        //  first check cache
        chunk = CreateChunkContainer(chunkIndex);

        //  chunk must be either previously loaded or newly created, if loaded and initialized bail out
        if (chunk.IsInitialized) return false;

        var generationData = terrainBuilder.BuildChunkData(chunkIndex);
        chunk.ApplyGenerationData(terrainBuilder, generationData);
        chunk.RecomputeLighting(force: true, includeNeighborData: true);
        return true;
    }

    //private void WorkQueueProcessor()
    //{
    //    if (UseGpuStreaming)
    //    {
    //        // GPU-first mode: disable CPU work queue processing
    //        Thread.Sleep(10);
    //        return;
    //    }
    //    while (isRunning)
    //    {
    //        // Non-blocking camera event drain to avoid stalling work processing
    //        var hadEvent = false;
    //        while (cameraChangedEvent.WaitOne(0))
    //        {
    //            hadEvent = true;
    //        }
    //        if (hadEvent && camera is not null)
    //        {
    //            // Only recompute streaming if the camera chunk tile changed (simplified trigger)
    //            var poseChanged = HasCameraPoseChanged(camera);
    //            if (poseChanged)
    //            {
    //                CalculateTerrainStreamingChanges();
    //                lastStreamingUpdateMs = stopwatch.ElapsedMilliseconds;
    //            }
    //        }

    //        // Failsafe: if camera chunk tile changed but we missed the event, trigger streaming.
    //        if (camera is not null)
    //        {
    //            var posFS = camera.Position;
    //            var ccxFS = (int)((posFS.X + 0.5f) / VoxelHelper.ChunkSideSize);
    //            var cczFS = (int)((posFS.Z + 0.5f) / VoxelHelper.ChunkSideSize);
    //            if (ccxFS != lastCameraChunkX || cczFS != lastCameraChunkZ)
    //            {
    //                lastCameraChunkX = ccxFS;
    //                lastCameraChunkZ = cczFS;
    //                CalculateTerrainStreamingChanges();
    //                lastStreamingUpdateMs = stopwatch.ElapsedMilliseconds;
    //            }
    //        }

    //        if (!workQueue.IsEmpty)
    //        {
    //            // Process a small batch per tick with a time budget
    //            const int MaxItemsPerTick = 64;
    //            const int TimeBudgetMs = 30;
    //            var start = stopwatch.ElapsedMilliseconds;
    //            var processed = 0;
    //            while (processed < MaxItemsPerTick && (stopwatch.ElapsedMilliseconds - start) < TimeBudgetMs && !workQueue.IsEmpty)
    //            {
    //                ProcessWorkItem();
    //                processed++;
    //            }

    //            // Perform periodic cache maintenance and watchdog even while there is work
    //            if (camera is not null)
    //            {
    //                var now2 = stopwatch.ElapsedMilliseconds;
    //                if (now2 - lastCacheMaintenanceMs > 500)
    //                {
    //                    var centerPosition = camera.Position;
    //                    var cameraChunkX = (int)((centerPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
    //                    var cameraChunkZ = (int)((centerPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);
    //                    var cameraXZ = new Vector2i(cameraChunkX, cameraChunkZ);
    //                    EvictIfOverCapacity(cameraXZ);
    //                    lastCacheMaintenanceMs = now2;
    //                }
    //                if (now2 - lastWatchdogMs > 1000)
    //                {
    //                    WatchdogReenqueue();
    //                    lastWatchdogMs = now2;
    //                }
    //            }
    //            continue; // do not idle-wait when there is work
    //        }

    //        // Idle path: maintain cache and wait briefly for camera events
    //        if (camera is not null)
    //        {
    //            var now3 = stopwatch.ElapsedMilliseconds;
    //            if (now3 - lastCacheMaintenanceMs > 1000)
    //            {
    //                var centerPosition = camera.Position;
    //                var cameraChunkX = (int)((centerPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
    //                var cameraChunkZ = (int)((centerPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);
    //                var cameraXZ = new Vector2i(cameraChunkX, cameraChunkZ);
    //                EvictIfOverCapacity(cameraXZ);
    //                lastCacheMaintenanceMs = now3;
    //            }
    //            if (now3 - lastWatchdogMs > 1000)
    //            {
    //                WatchdogReenqueue();
    //                lastWatchdogMs = now3;
    //            }
    //        }

    //        // Short wait to avoid busy-spin when idle
    //        cameraChangedEvent.WaitOne(5);
    //    }
    //}

    // Unified eviction: trim active set if it grows past capacity. Prefer evicting far chunks not in surrounding set.
    private void EvictIfOverCapacity(in Vector2i cameraXZ)
    {
        lock (cacheLock)
        {
            while (loadedChunks.Count > CacheCapacity && cacheFifo.Count > 0)
            {
                var idx = cacheFifo.Dequeue();
                bool shouldEvict;
                lock (surroundingChunkSet)
                {
                    var outside = !surroundingChunkSet.Contains(idx);
                    if (!loadedChunks.TryGetValue(idx, out var chk)) continue;
                    var dist = (chk.ChunkPosition - cameraXZ).ManhattanLength;
                    shouldEvict = outside && dist > VoxelHelper.MaxDistanceInChunks + 1;
                }
                if (!shouldEvict)
                {
                    // Not a good eviction candidate; push it back to the end to try later.
                    cacheFifo.Enqueue(idx);
                    continue;
                }
                if (loadedChunks.TryRemove(idx, out var evicted))
                {
                    evicted.State = ChunkState.ToBeRemoved;
                    evicted.Visible = false;
                    ChunkRenderer.chunksStreamingQueue.Enqueue(evicted);
                    SaveChangedChunkBlocks(evicted);
                }
            }
        }
    }

    private void ProcessWorkItem()
    {
        if (workQueue.TryDequeue(out var index))
        {
            bool isStillNeeded;
            lock (surroundingChunkSet)
            {
                isStillNeeded = surroundingChunkSet.Contains(index);
            }
            if (!isStillNeeded)
            {
                // Clear pending so it can be rescheduled later when needed again
                if (loadedChunks.TryGetValue(index, out var ch0))
                {
                    ch0.PendingCompute = false;
                    pendingComputeSince.TryRemove(index, out _);
                }
                return;
            }

            var start = stopwatch.ElapsedMilliseconds;

            // Mark compute in progress
            if (loadedChunks.TryGetValue(index, out var inProgChunk))
            {
                inProgChunk.ComputeInProgress = true;
            }

            if (CreateChunkInitializeAndAddToLoaded(index, out var chunk))
            {
                //var initialized = stopwatch.ElapsedMilliseconds - start;
                //var processingStart = stopwatch.ElapsedMilliseconds;
                var hasChanges = LoadChangedChunkBlocks(chunk);
                if (!chunk.IsProcessed || hasChanges)
                {
                    chunk.RecomputeLighting(force: true, includeNeighborData: true);
                }
                //var processed = stopwatch.ElapsedMilliseconds - processingStart;
                //Log.Debug($"ProcessWorkItem() chunk: {chunk}, init time:{initialized} ms, process time:{processed} ms");
            }
            else
            {
                //var initialized = stopwatch.ElapsedMilliseconds - start;
                //Log.Debug($"ProcessWorkItem() reusing existing chunk: {chunk}, time:{initialized} ms");
                if (chunk.State is ChunkState.SafeToRemove)
                    chunk.State = ChunkState.Loaded;
                else if (chunk.State is ChunkState.ToBeRemoved)
                    chunk.State = ChunkState.Added;
            }

            // Only enqueue fully initialized + processed chunks to the renderer.
            if (chunk.IsInitialized && chunk.IsProcessed)
            {
                EnqueueChunkForUpload(chunk);
            }

            RefreshChunkBorders(chunk);

            // Clear compute pending now that this work item has been handled
            chunk.PendingCompute = false;
            pendingComputeSince.TryRemove(index, out _);
            chunk.ComputeInProgress = false;
        }
    }

    // Public per-frame visibility refresh (called from GL thread)
    public void UpdateVisibilityFromCamera(ICamera cam)
    {
        var planes = (Vector4[])cam.Frustum.Planes.Clone();
        UpdateChunkVisibility(planes);
    }

    /// <summary>
    /// Public hook for the GL thread to drive terrain streaming when the camera moves.
    /// Triggers a streaming recompute on camera tile change and updates the debounce timer.
    /// </summary>
    public void UpdateStreamingFromCamera()
    {
        if (camera is null) return;
        // Detect tile change without mutating lastCameraChunk* beforehand,
        // so CalculateTerrainStreamingChanges() can see the delta and publish.
        var camPos = camera.Position;
        var camChunkX = (int)((camPos.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var camChunkZ = (int)((camPos.Z + 0.5f) / VoxelHelper.ChunkSideSize);
        if (camChunkX != lastCameraChunkX || camChunkZ != lastCameraChunkZ)
        {
            CalculateTerrainStreamingChanges();
            lastStreamingUpdateMs = stopwatch.ElapsedMilliseconds;
        }
    }
    #endregion

    private void RefreshChunkBorders(Chunk chunk)
    {
        if (chunk.Blocks is null)
            return;

        var refreshed = new HashSet<int>();
        borderQueryCache = new Dictionary<long, BlockType>(8 * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize);
        void RefreshChunk(Chunk target)
        {
            if (target.Blocks is null) return;
            if (!refreshed.Add(target.Index)) return;

            target.RefreshBorderLighting();
            EnqueueChunkForUpload(target);
        }

        RefreshChunk(chunk);

        foreach (var neighborIndex in EnumerateCardinalNeighbors(chunk.Index))
        {
            if (loadedChunks.TryGetValue(neighborIndex, out var neighbor) && neighbor.Blocks is not null)
            {
                RefreshChunk(neighbor);
            }
        }
        borderQueryCache = null;
    }

    private static IEnumerable<int> EnumerateCardinalNeighbors(int chunkIndex)
    {
        var x = chunkIndex % VoxelHelper.WorldChunksXZ;
        var z = chunkIndex / VoxelHelper.WorldChunksXZ;

        if (x > 0)
            yield return chunkIndex - 1;
        if (x < VoxelHelper.WorldChunksXZ - 1)
            yield return chunkIndex + 1;
        if (z > 0)
            yield return chunkIndex - VoxelHelper.WorldChunksXZ;
        if (z < VoxelHelper.WorldChunksXZ - 1)
            yield return chunkIndex + VoxelHelper.WorldChunksXZ;
    }

    // EvictCacheIfNeeded removed in unified model

    #region Save/load
    private static string GetSaveFileName(int seed, int chunkIndex) => $"world-{seed}_chunk-{chunkIndex}.bin";

    private void SaveChangedChunkBlocks(Chunk chunk)
    {
        if (chunk.ChangedBlocks.Count > 0)
        {
            var fileName = GetSaveFileName(seed, chunk.Index);
            var path = Path.Combine(Environment.CurrentDirectory, "save", fileName);

            var dirName = Path.GetDirectoryName(path);
            if (dirName is not null) Directory.CreateDirectory(dirName);

            using var stream = File.Create(path, 60 * 1024, FileOptions.SequentialScan);
            foreach (var block in chunk.ChangedBlocks.Values)
            {
                //  write the blocks Index and BlockType to the stream
                var index = BitConverter.GetBytes(block.Index);
                var type = (byte)block.BlockType;   //  TODO: if the number of block types exceeds 255, we need to cast to short and write 2 bytes
                var frontDirection = (byte)block.FrontDirection;
                stream.Write(index);
                stream.WriteByte(type);
                stream.WriteByte(frontDirection);
            }
        }
    }

    internal bool LoadChangedChunkBlocks(Chunk chunk)
    {
        var fileName = GetSaveFileName(seed, chunk.Index);
        var path = Path.Combine(Environment.CurrentDirectory, "save", fileName);

        if (!File.Exists(path))
        {
            return false;
        }

        var hasChanges = false;
        using var stream = File.OpenRead(path);
        Span<byte> rec = stackalloc byte[6];
        while (stream.Read(rec) == 6)
        {
            hasChanges = true;
            var index = BitConverter.ToInt32(rec[..4]);
            var blockType = (BlockType)rec[4];
            var frontDirection = (BlockDirection)rec[5];

            var block = chunk.Blocks[index];
            block.BlockType = blockType;
            block.FrontDirection = frontDirection;
            chunk.UpdateBlock(ref block);

            chunk.ChangedBlocks[index] = block; // keep history after reload
        }
        return hasChanges;
    }

    // --- GPU streaming integration ---
    internal bool UseGpuStreaming { get; set; } = true;

    internal void ProcessGpuStreamingOnGlThread(int submitMax = 32)
    {
        if (!UseGpuStreaming) return;
        EnsureChunkInitializer();
        if (ChunkInitializer is null) return;

        // If a batch is in flight, try completing it quickly (non-blocking)
        if (ChunkInitializer.HasInFlightBatch)
        {
            _ = ChunkInitializer.TryCompleteBatch(out var _);
        }

        // Periodic full rebuild disabled: publish only on camera tile change (CalculateTerrainStreamingChanges)

        // Skip per-batch streaming submits for now; rely on frequent full rebuilds for coherence
        // (reduces churn and avoids partial atlas publishes during movement)

        // Try to complete any in-flight batch at the end of this call
        if (ChunkInitializer.TryCompleteBatch(out var completed))
        {
            foreach (var idx in completed)
            {
                var chunk = GetOrCreateChunkContainer(idx);
                if (chunk.State is ChunkState.SafeToRemove)
                    chunk.State = ChunkState.Loaded;
                chunk.Visible = true;
                EnqueueChunkForUpload(chunk);
            }
        }
    }

    internal void EnqueueChunkForUpload(Chunk chunk)
    {
        if (chunk is null) return;
        // If GPU compaction drives rendering, skip legacy per-chunk uploads to avoid churn
        if (ChunkRenderer != null && CompactedAtlasSSBO != 0)
        {
            chunk.PendingUpload = false;
            return;
        }
        if (chunk.PendingUpload) return;
        chunk.PendingUpload = true;
        pendingUploadSince[chunk.Index] = stopwatch.ElapsedMilliseconds;
        ChunkRenderer.chunksStreamingQueue.Enqueue(chunk);
    }

    internal void ProcessPendingWorkOnGlThread(int maxBatch = 16)
    {
        if (UseGpuStreaming || workQueue.IsEmpty) return;

        EnsureChunkInitializer();
        var batch = new List<int>(maxBatch);
        while (batch.Count < maxBatch && workQueue.TryDequeue(out var idx))
        {
            bool isStillNeeded;
            lock (surroundingChunkSet)
            {
                isStillNeeded = surroundingChunkSet.Contains(idx);
            }
            if (!isStillNeeded) continue;
            batch.Add(idx);
        }
        if (batch.Count == 0) return;

        ChunkInitializer?.ProcessChunkData([.. batch]);

        foreach (var idx in batch)
        {
            var chunk = GetOrCreateChunkContainer(idx);
            if (chunk.State is ChunkState.SafeToRemove)
                chunk.State = ChunkState.Loaded;
            chunk.Visible = true;
            EnqueueChunkForUpload(chunk);
            // Border refresh is handled elsewhere to avoid stalling GL thread.
        }
    }
    #endregion

    // --- Helpers & watchdog ---
    private bool HasCameraPoseChanged(ICamera cam)
    {
        // Thresholds tuned to ignore micro jitter
        const float posEpsSq = 0.01f; // ~10cm
        const float oriEps = 0.0005f; // ~small quaternion delta

        var pos = cam.Position;
        var ori = cam.Orientation;
        var camChunkX = (int)((pos.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var camChunkZ = (int)((pos.Z + 0.5f) / VoxelHelper.ChunkSideSize);

        var posChanged = (pos - lastCameraPosition).LengthSquared > posEpsSq;
        var dot = Math.Abs(ori.X * lastCameraOrientation.X + ori.Y * lastCameraOrientation.Y + ori.Z * lastCameraOrientation.Z + ori.W * lastCameraOrientation.W);
        var oriChanged = (1f - dot) > oriEps;
        var tileChanged = camChunkX != lastCameraChunkX || camChunkZ != lastCameraChunkZ;

        if (posChanged || oriChanged || tileChanged)
        {
            lastCameraPosition = pos;
            lastCameraOrientation = ori;
            lastCameraChunkX = camChunkX;
            lastCameraChunkZ = camChunkZ;
            return true;
        }
        return false;
    }

    //private void WatchdogReenqueue()
    //{
    //    // Self-heal chunks that are CPU-ready but missing GPU data or stuck PendingUpload
    //    List<int> indices;
    //    lock (surroundingChunkSet)
    //    {
    //        indices = surroundingChunkSet.ToList();
    //    }
    //    var now = stopwatch.ElapsedMilliseconds;
    //    foreach (var idx in indices)
    //    {
    //        if (!loadedChunks.TryGetValue(idx, out var chunk)) continue;
    //        // CPU compute watchdog (conservative): only enqueue if not already pending and not in progress
    //        if (!chunk.IsProcessed)
    //        {
    //            if (!chunk.PendingCompute && !chunk.ComputeInProgress)
    //            {
    //                chunk.PendingCompute = true;
    //                workQueue.Enqueue(idx);
    //                pendingComputeSince[idx] = now;
    //                Log.Debug($"Watchdog compute enqueue {idx}");
    //            }
    //            continue;
    //        }

    //        // GPU upload watchdog (only for processed chunks)
    //        if (CompactedAtlasSSBO == 0)
    //        {
    //            var missingGpu = chunk.BlocksSSBO == 0u && chunk.State != ChunkState.ToBeRemoved && chunk.State != ChunkState.SafeToRemoved;
    //            var stuck = chunk.PendingUpload && pendingUploadSince.TryGetValue(idx, out var since) && (now - since) > 1000;
    //            if (missingGpu || stuck)
    //            {
    //                if (stuck) chunk.PendingUpload = false; // allow re-enqueue
    //                EnqueueChunkForUpload(chunk);
    //                // reduced log noise
    //            }
    //        }
    //    }
    //}

    // Per-chunk column masks for edits (e.g., broken top-of-column). 1 = hidden, 0 = normal
    private readonly ConcurrentDictionary<int, byte[]> breakMasks = new(); // per-column (legacy)
    // Per-chunk 3D break mask bitset: 1 bit per voxel (length = VoxelsCount/8)
    private readonly ConcurrentDictionary<int, byte[]> breakMasks3D = new();

    internal byte[]? GetBreakMask(int chunkIndex) => breakMasks.TryGetValue(chunkIndex, out var mask) ? mask : null;

    // Returns 3D bitset as bytes (will be reinterpreted as uints when uploading)
    internal byte[]? GetBreakMask3D(int chunkIndex) => breakMasks3D.TryGetValue(chunkIndex, out var mask) ? mask : null;

    private void SetBreakMaskBit(int chunkIndex, int lx, int lz, bool hidden)
    {
        var size = VoxelHelper.ChunkSideSize;
        var area = VoxelHelper.ChunkSideSizeSquare;
        var mask = breakMasks.GetOrAdd(chunkIndex, _ => new byte[area]);
        var idx = lx + lz * size;
        mask[idx] = hidden ? (byte)1 : (byte)0;
    }

    private void SetBreakMask3DBit(int chunkIndex, int lx, int ly, int lz, bool hidden)
    {
        var area = VoxelHelper.ChunkSideSizeSquare;
        var voxels = area * VoxelHelper.ChunkYSize;
        var bytesLen = (voxels + 7) / 8;
        var mask = breakMasks3D.GetOrAdd(chunkIndex, _ => new byte[bytesLen]);
        int linear = lx + lz * VoxelHelper.ChunkSideSize + ly * area;
        int byteIndex = linear >> 3;
        int bit = linear & 7;
        byte b = mask[byteIndex];
        if (hidden) b = (byte)(b | (1 << bit)); else b = (byte)(b & ~(1 << bit));
        mask[byteIndex] = b;
    }
}









