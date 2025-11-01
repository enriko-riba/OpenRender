using OpenRender;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.Components;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SpyroGame.World;

public class VoxelWorld
{
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
    public static VoxelMaterial[] GetMaterials()
    {
        return Enumerable.Range(0, materials.Keys.Max() + 1)
            .Select(index => materials.TryGetValue(index, out var value) ? value : default)
            .ToArray();
    }
    #endregion

    public static readonly Vector3i ChunkSize = new(VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, VoxelHelper.ChunkSideSize);
    public static readonly Vector3i ChunkHalfSize = ChunkSize / 2;

    /// <summary>
    /// Queue of chunks to be created by background threads.
    /// </summary>
    private readonly ConcurrentQueue<int> workQueue = new();

    //  key is chunk index, value is the chunk   
    private readonly ConcurrentDictionary<int, Chunk> loadedChunks = [];
    private readonly ConcurrentDictionary<int, Chunk> cachedChunks = [];

    internal Stopwatch stopwatch = Stopwatch.StartNew();

    private readonly int seed;
    private readonly HashSet<int> surroundingChunkSet = [];
    private readonly Thread workerThread;
    private long lastStreamingUpdateMs;
    private long lastCacheMaintenanceMs;
    private const int CacheCapacity = 2048;
    private readonly Queue<int> cacheFifo = new();
    private readonly object cacheLock = new();
    internal readonly TerrainBuilder terrainBuilder;
    internal ChunkInitializer? ChunkInitializer { get; private set; }

    internal void EnsureChunkInitializer()
    {
        if (ChunkInitializer is null)
        {
            ChunkInitializer = new ChunkInitializer(this);
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

        isRunning = true;
        workerThread = new Thread(WorkQueueProcessor)
        {
            IsBackground = true
        };
        workerThread.Start();
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
            camera = value;
            camera.CameraChanged += Camera_CameraChanged;
        }
    }

    public int Seed => seed;

    public IReadOnlyCollection<int> SurroundingChunkIndices => surroundingChunkSet;

    public int LoadedChunksCount => loadedChunks.Count;
    public int CachedChunksCount => cachedChunks.Count;

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
        UpdateSurroundingChunkIndices(position);
        TotalStartingChunks = surroundingChunkSet.Count;
    }

    /// <summary>
    /// Calculates surrounding chunks, initializes them and adds to the world.
    /// Note this is a initialization method and should only be called only once.
    /// </summary>
    /// <param name="position"></param>
    public void AddStartingChunks(Vector3 position)
    {
        UpdateSurroundingChunkIndices(position);

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

        if (cachedChunks.TryRemove(chunkIndex, out var cached))
        {
            cached.State = ChunkState.Loaded;
            loadedChunks[chunkIndex] = cached;
            return cached;
        }

        var position = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var chunk = new Chunk(this, chunkIndex)
        {
            Aabb = (position, position + ChunkSize),
            ChunkPosition = new(position.X, position.Z),
            State = ChunkState.Loaded,
        };
        loadedChunks[chunkIndex] = chunk;
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
        if (loadedChunks.TryGetValue(chunkIndex, out var loaded) && loaded.Blocks is not null)
        {
            var local = globalPosition - loaded.Position;
            return loaded.Blocks[local.X + local.Z * VoxelHelper.ChunkSideSize + local.Y * VoxelHelper.ChunkSideSizeSquare].BlockType;
        }

        if (cachedChunks.TryGetValue(chunkIndex, out var cached) && cached.Blocks is not null)
        {
            var local = globalPosition - cached.Position;
            return cached.Blocks[local.X + local.Z * VoxelHelper.ChunkSideSize + local.Y * VoxelHelper.ChunkSideSizeSquare].BlockType;
        }

        var chunkOrigin = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var lx = globalPosition.X - chunkOrigin.X;
        var ly = globalPosition.Y - chunkOrigin.Y;
        var lz = globalPosition.Z - chunkOrigin.Z;
        return terrainBuilder.GenerateChunkBlockLocal(chunkIndex, lx, ly, lz);
    }

    public BlockState? PickBlock(Vector3 origin, Vector3 direction, float pickingDistance = VoxelHelper.MaxPickingDistance)
    {
        //  first get the chunks intersecting with the ray
        var start = stopwatch.ElapsedTicks;
        var intersections = ChunkRenderer.VisibleChunks
            .Select(x => (distance: VoxelHelper.RayIntersect(origin, direction, x.Aabb), chunk: x))
            .Where(x => x.distance.HasValue && x.distance < pickingDistance);

        var chunkIntersectionTime = stopwatch.ElapsedTicks - start;
        //Log.Debug($"found {intersections.Length} chunks intersecting in {chunkIntersectionTime:N0} ticks");
        start = stopwatch.ElapsedTicks;

        var minDistance = float.MaxValue;
        var totalBlocks = 0;
        var totalChunks = 0;
        BlockState? nearestBlock = null;
        foreach (var (distance, chunk) in intersections)
        {
            totalChunks++;
            var chunkPosition = chunk.Aabb.Min;
            var visibleBlocks = chunk.Blocks.Where(x => x.IsVisible && x.BlockType != BlockType.WaterLevel && x.BlockType != BlockType.None)
                //.Where(x => (x.Aabb.Min - (Vector3i)origin).ManhattanLength < pickingDistance);
                .Where(x =>
                 {
                     var d = x.Aabb.Min - origin;
                     return d.LengthSquared < pickingDistance * pickingDistance;
                 });
            //Log.Debug($"testing block intersections for chunk: {chunk}, visible blocks; {visibleBlocks.Count()}");
            foreach (var block in visibleBlocks)
            {
                totalBlocks++;
                var blockDistance = VoxelHelper.RayIntersect(origin, direction, block.Aabb);
                if (blockDistance.HasValue && blockDistance < pickingDistance)
                {
                    //Log.Debug($"block intersection: {block.LocalPosition} {block.BlockType}, world position: {blockBB}");
                    if (blockDistance.Value < minDistance)
                    {
                        minDistance = blockDistance.Value;
                        nearestBlock = block;
                        //Log.Debug($"nearest block: {nearestBlock} at distance: {minDistance}");
                    }
                }
            }
        }
        //var resultString = nearestBlock is not null ? $"block: {nearestBlock} at distance: {minDistance:N2}" : "no intersection found";
        //Log.Debug($"{resultString}, chunks ({totalChunks}) intersection time {chunkIntersectionTime:N0} ticks, blocks ({totalBlocks}) picking time {stopwatch.ElapsedTicks - start:N0} ticks");

        return nearestBlock;
    }

    public void BreakBlock(BlockState block)
    {
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

        var updatedChunks = new HashSet<Chunk>();
        block.BlockType = BlockType.None;
        block.IsVisible = false;
        chunk.UpdateBlock(ref block, true);
        updatedChunks.Add(chunk);

        //  make neighbor blocks visible
        void MakeNeighborBlockVisible(BlockState? blockState)
        {
            if (blockState is not null && !blockState.Value.IsTransparent)
            {
                block = blockState.Value;
                block.IsVisible = true;
                var chunk = this[block.ChunkIndex];
                if (chunk is not null)
                {
                    chunk.UpdateBlock(ref block);
                    updatedChunks.Add(chunk);
                }
            }
        }

        var neighbors = GetNeighboringBlocks(block);
        foreach (var neighbor in neighbors)
        {
            MakeNeighborBlockVisible(neighbor);
        }

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
        Log.Info("VoxelWorld.Close() stopping background worker...");
        isRunning = false;
        workerThread.Join();
        Log.Info("VoxelWorld.Close() saving chunk changes...");
        foreach (var chunk in loadedChunks.Values)
        {
            SaveChangedChunkBlocks(chunk);
        }
        foreach (var chunk in cachedChunks.Values)
        {
            SaveChangedChunkBlocks(chunk);
        }
        ChunkInitializer?.Dispose();
        ChunkInitializer = null;
        Log.Info("VoxelWorld.Close() all done!");
    }

    private readonly AutoResetEvent cameraChangedEvent = new(false);

    private void Camera_CameraChanged(object? sender, EventArgs e) => cameraChangedEvent.Set();

    private void CalculateTerrainStreamingChanges()
    {
        if (camera is null) return;

        var (added, removed, cameraChunkX, cameraChunkZ) = UpdateSurroundingChunkIndices(camera.Position);
        if (added.Length + removed.Length > 0)
        {
            Log.Debug($"CalculateTerrainStreamingChanges() adding {added.Length} removing {removed.Length} chunks, worker queue items: {workQueue.Count}");
        }

        Array.Sort(added, (a, b) =>
        {
            var ax = a % VoxelHelper.WorldChunksXZ;
            var az = a / VoxelHelper.WorldChunksXZ;
            var bx = b % VoxelHelper.WorldChunksXZ;
            var bz = b / VoxelHelper.WorldChunksXZ;
            var da = (ax - cameraChunkX) * (ax - cameraChunkX) + (az - cameraChunkZ) * (az - cameraChunkZ);
            var db = (bx - cameraChunkX) * (bx - cameraChunkX) + (bz - cameraChunkZ) * (bz - cameraChunkZ);
            return da.CompareTo(db);
        });

        foreach (var chunkIndex in added)
        {
            if (loadedChunks.TryGetValue(chunkIndex, out var chunk) && chunk.State != ChunkState.Added)
            {
                chunk.State = ChunkState.Loaded;
            }
            workQueue.Enqueue(chunkIndex);
        }

        foreach (var chunkIndex in removed)
        {
            if (cachedChunks.TryGetValue(chunkIndex, out var cachedChunk))
            {
                cachedChunk.State = ChunkState.ToBeRemoved;
                cachedChunk.Visible = false;
                EnqueueChunkForUpload(cachedChunk);
            }
            else if (loadedChunks.TryGetValue(chunkIndex, out var chunk))
            {
                chunk.State = ChunkState.ToBeRemoved;
                chunk.Visible = false;
                EnqueueChunkForUpload(chunk);
            }
            else
            {
                Log.Debug($"removed chunk: {chunkIndex} not found in loaded/cached chunks");
            }
        }

        // Slight edge padding to reduce near-edge flicker during rotation.
        const float EdgeCullingMarginBlocks = 2f; // expand XZ by a couple of blocks
        var inFrustumCount = 0;
        foreach (var kv in loadedChunks)
        {
            var chunk = kv.Value;
            var (min, max) = chunk.Aabb;
            var pad = new Vector3(EdgeCullingMarginBlocks, 0f, EdgeCullingMarginBlocks);
            var padded = (min - pad, max + pad);
            var visible = CullingHelper.IsAabbInFrustum(padded, camera.Frustum.Planes);
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
    /// Updates the surrounding chunk indices based on the given center position.
    /// </summary>
    /// <param name="centerPosition"></param>
    /// <returns></returns>
    private (int[] added, int[] removed, int cameraChunkX, int cameraChunkZ) UpdateSurroundingChunkIndices(in Vector3 centerPosition)
    {
        var newChunkSetIndices = new HashSet<int>(1024);

        // calculate the camera chunk indices based on its position
        var cameraChunkX = (int)((centerPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)((centerPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);

        const int GuardBandChunks = 0; // strict radius; caching + FIFO eviction prevents thrash
        var r = VoxelHelper.MaxDistanceInChunks + GuardBandChunks;

        // calculate the range of chunks to check in X and Z directions
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

        int[] added;
        int[] removed;
        lock (surroundingChunkSet)
        {
            added = newChunkSetIndices.Except(surroundingChunkSet).ToArray();
            removed = surroundingChunkSet.Except(newChunkSetIndices).ToArray();

            surroundingChunkSet.Clear();
            foreach (var i in newChunkSetIndices) surroundingChunkSet.Add(i);
        }
        return (added, removed, cameraChunkX, cameraChunkZ);
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

    private void WorkQueueProcessor()
    {
        while (isRunning)
        {
            // Non-blocking camera event drain to avoid stalling work processing
            var hadEvent = false;
            while (cameraChangedEvent.WaitOne(0))
            {
                hadEvent = true;
            }
            if (hadEvent)
            {
                CalculateTerrainStreamingChanges();
                lastStreamingUpdateMs = stopwatch.ElapsedMilliseconds;
            }

            // Periodic streaming update as a fallback (in case camera events coalesce)
            var now = stopwatch.ElapsedMilliseconds;
            if (now - lastStreamingUpdateMs > 100)
            {
                CalculateTerrainStreamingChanges();
                lastStreamingUpdateMs = now;
            }

            if (!workQueue.IsEmpty)
            {
                // Process a small batch per tick with a time budget
                const int MaxItemsPerTick = 64;
                const int TimeBudgetMs = 30;
                var start = stopwatch.ElapsedMilliseconds;
                var processed = 0;
                while (processed < MaxItemsPerTick && (stopwatch.ElapsedMilliseconds - start) < TimeBudgetMs && !workQueue.IsEmpty)
                {
                    ProcessWorkItem();
                    processed++;
                }

                // Perform periodic cache maintenance even while there is work
                if (camera is not null)
                {
                    var now2 = stopwatch.ElapsedMilliseconds;
                    if (now2 - lastCacheMaintenanceMs > 200)
                    {
                        var centerPosition = camera.Position;
                        var cameraChunkX = (int)((centerPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
                        var cameraChunkZ = (int)((centerPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);
                        var cameraXZ = new Vector2i(cameraChunkX, cameraChunkZ);
                        MoveChunksToCache(cameraXZ);
                        EvictChunksFromCache(cameraXZ);
                        lastCacheMaintenanceMs = now2;
                    }
                }
                continue; // do not idle-wait when there is work
            }

            // Idle path: maintain cache and wait briefly for camera events
            if (camera is not null)
            {
                var centerPosition = camera.Position;
                var cameraChunkX = (int)((centerPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
                var cameraChunkZ = (int)((centerPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);
                var cameraXZ = new Vector2i(cameraChunkX, cameraChunkZ);

                MoveChunksToCache(cameraXZ);
                EvictChunksFromCache(cameraXZ);
            }

            // Short wait to avoid busy-spin when idle
            cameraChangedEvent.WaitOne(5);
        }
    }

    private void EvictChunksFromCache(Vector2i cameraXZ)
    {
        lock (cacheLock)
        {
            var before = cachedChunks.Count;
            EvictCacheIfNeeded();
            var after = cachedChunks.Count;
            var counter = before - after;
            if (counter > 0) Log.Debug($"cached chunks evicted {counter}");
        }
    }

    private void MoveChunksToCache(Vector2i cameraXZ)
    {
        var chunks = loadedChunks.Where(x => !surroundingChunkSet.Contains(x.Value.Index));

        if (chunks.Any())
        {
            var counter = 0;
            foreach (var chunk in chunks)
            {
                if (loadedChunks.TryRemove(chunk.Value.Index, out var loadedChunk))
                {
                    cachedChunks[chunk.Value.Index] = loadedChunk;
                    lock (cacheLock)
                    {
                        cacheFifo.Enqueue(chunk.Value.Index);
                        EvictCacheIfNeeded();
                    }
                    counter++;
                }
            }
            if (counter > 0) Log.Debug($"chunks moved to cache: {counter}");
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
                return;
            }

            var start = stopwatch.ElapsedMilliseconds;

            if (CreateChunkInitializeAndAddToLoaded(index, out var chunk))
            {
                var initialized = stopwatch.ElapsedMilliseconds - start;
                var processingStart = stopwatch.ElapsedMilliseconds;
                var hasChanges = LoadChangedChunkBlocks(chunk);
                if (!chunk.IsProcessed || hasChanges)
                {
                    chunk.RecomputeLighting(force: true, includeNeighborData: true);
                }
                var processed = stopwatch.ElapsedMilliseconds - processingStart;
                Log.Debug($"ProcessWorkItem() chunk: {chunk}, init time:{initialized} ms, process time:{processed} ms");
            }
            else
            {
                var initialized = stopwatch.ElapsedMilliseconds - start;
                Log.Debug($"ProcessWorkItem() reusing existing chunk: {chunk}, time:{initialized} ms");
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
        }
    }
    #endregion

    private void RefreshChunkBorders(Chunk chunk)
    {
        if (chunk.Blocks is null)
            return;

        var refreshed = new HashSet<int>();
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

    private void EvictCacheIfNeeded()
    {
        while (cachedChunks.Count > CacheCapacity && cacheFifo.Count > 0)
        {
            var idx = cacheFifo.Dequeue();
            if (cachedChunks.TryRemove(idx, out var evicted))
            {
                if (evicted.State is ChunkState.SafeToRemove or ChunkState.Loaded)
                {
                    SaveChangedChunkBlocks(evicted);
                }
            }
        }
    }

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
    internal bool UseGpuStreaming { get; set; } = false;

    internal void ProcessGpuStreamingOnGlThread(int submitMax = 32)
    {
        if (!UseGpuStreaming) return;
        EnsureChunkInitializer();
        if (ChunkInitializer is null) return;

        // Submit a batch if none is in flight
        if (!ChunkInitializer.HasInFlightBatch)
        {
            var batch = new List<int>(submitMax);
            while (batch.Count < submitMax && workQueue.TryDequeue(out var idx))
            {
                bool isStillNeeded;
                lock (surroundingChunkSet)
                {
                    isStillNeeded = surroundingChunkSet.Contains(idx);
                }
                if (!isStillNeeded) continue;
                batch.Add(idx);
            }
            if (batch.Count > 0)
            {
                ChunkInitializer.SubmitBatch(batch.ToArray());
            }
        }

        // Try to complete the in-flight batch without blocking
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
        if (chunk.PendingUpload) return;
        chunk.PendingUpload = true;
        ChunkRenderer.chunksStreamingQueue.Enqueue(chunk);
    }

    internal void ProcessPendingWorkOnGlThread(int maxBatch = 16)
    {
        if (workQueue.IsEmpty) return;

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

        ChunkInitializer?.ProcessChunkData(batch.ToArray());

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
}
