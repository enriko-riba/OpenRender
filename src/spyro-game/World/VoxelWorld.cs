global using PickedBlock = (int chunkIndex, int blockIndex, float distance);

using OpenRender;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.Components;
using SpyroGame.Noise;
using System.Collections.Concurrent;
using System.Diagnostics;


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
                Diffuse = new Vector3(1, 1, 1),
                Emissive = new Vector3(0.01f, 0.01f, 0.01f),
                Specular = new Vector3(1),
                Shininess = 0.65f
            }
        },
        { (int)BlockType.WaterLevel, new VoxelMaterial() {
                Diffuse = new Vector3(1, 1, 1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.7f, 0.7f, 0.8f),
                Shininess = 0.3f
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
    };

    /// <summary>
    /// Prepares all textures for the voxel world.
    /// </summary>
    /// <param name="world"></param>
    /// <returns></returns>
    public ulong[] GetTextureHandles()
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
    public VoxelMaterial[] GetMaterials()
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
    private readonly List<int> surroundingChunkIndices = [];
    internal readonly TerrainBuilder terrainBuilder;
    private readonly Frustum frustum;

    private ICamera camera = default!;
    private int lastCameraChunkIndex = -1;

    public volatile int ProcessedStartingChunks;
    public int TotalStartingChunks;

    public ChunkRenderer ChunkRenderer { get; private set; }

    public int WorkerQueueLength => workQueue.Count;

    public VoxelWorld(Frustum frustum, int seed)
    {
        this.frustum = frustum;
        this.seed = seed;

        terrainBuilder = new TerrainBuilder(seed);
        ChunkRenderer = ChunkRenderer.Create(this, GetTextureHandles(), GetMaterials());

        var thread = new Thread(WorkQueueProcessor)
        {
            IsBackground = true
        };
        thread.Start();
    }

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

    public IReadOnlyList<int> SurroundingChunkIndices => surroundingChunkIndices;

    public ICollection<Chunk> LoadedChunks => loadedChunks.Values;

    public IEnumerable<Chunk> SurroundingChunks
    {
        get
        {
            var sc = loadedChunks.Values.Where(x => surroundingChunkIndices.Contains(x.Index));
            return sc;
        }
    }


    /// <summary>
    /// Gets a loaded chunk containing the given global XZ position.
    /// </summary>
    /// <param name="globalPosition"></param>
    /// <param name="chunk"></param>
    /// <returns>True if chunk is loaded else false</returns>
    public bool GetChunkByGlobalPosition(Vector3 globalPosition, out Chunk? chunk)
    {
        chunk = null;
        if (globalPosition.Y >= VoxelHelper.MaxBlockPositionY) globalPosition.Y = VoxelHelper.MaxBlockPositionY;
        if (!VoxelHelper.IsGlobalPositionInWorld(globalPosition)) return false;

        var idx = VoxelHelper.GetChunkIndexFromGlobalPosition(globalPosition);
        chunk = this[idx];
        return chunk != null;
    }

    public Chunk? this[int index] => loadedChunks.TryGetValue(index, out var chunk) ? chunk : null;



    /// <summary>
    /// Calculates surrounding chunks, initializes them and adds to the world.
    /// Note this is a initialization method and should only be called only once.
    /// </summary>
    /// <param name="position"></param>
    public void AddStartingChunks(Vector3 position)
    {
        UpdateSurroundingChunkIndices(position);
        TotalStartingChunks = surroundingChunkIndices.Count;

        var parallelOptions = new ParallelOptions()
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };
        var processed = 0;
        var start = stopwatch.ElapsedMilliseconds;
        //for (var i = 0; i < surroundingChunkIndices.Count; i++)
        Parallel.For(0, surroundingChunkIndices.Count, parallelOptions, i =>
        {
            var index = surroundingChunkIndices[i];
            if (CreateChunkInitializeAndAddToLoaded(index, out var chunk))
            {
                Interlocked.Increment(ref processed);
            }
            else
            {
                Debug.Assert(false, "not initialized!");
            }

            Debug.Assert(chunk?.IsInitialized ?? false, "loaded chunk not initialized!");

            if (!chunk.IsProcessed)
            {
                chunk.CalcVisibleBlocks();
            }

            //chunk.CalcAO();
            Interlocked.Increment(ref ProcessedStartingChunks);
        });
        Log.Debug($"{processed} chunks initialized in: {stopwatch.ElapsedMilliseconds - start} ms");
    }

    public PickedBlock? PickBlock()
    {
        const int MaxPickingDistance = 8;

        //  create the ray
        var origin = camera.Position;
        var direction = camera.Front;

        var start = stopwatch.ElapsedTicks;
        //  first get the chunks intersecting with the ray
        var chunks = ChunkRenderer.VisibleChunks;
        var intersections = chunks.Select(x => (distance: VoxelHelper.RayIntersect(origin, direction, x.Aabb), chunk: x))
            .Where(x => x.distance.HasValue && x.distance < MaxPickingDistance)
            .OrderBy(x => x.distance)
            .ToArray();

        var chunkIntersectionTime = stopwatch.ElapsedTicks - start;
        //Log.Debug($"found {intersections.Length} chunks intersecting in {chunkIntersectionTime:N0} ticks");
        start = stopwatch.ElapsedTicks;

        var minDistance = float.MaxValue;
        var nearestBlockIndex = -1;
        var nearestBlockChunkIndex = -1;
        var totalBlocks = 0;
        var totalChunks = 0;
        foreach (var (distance, c) in intersections)
        {
            totalChunks++;
            var chunk = loadedChunks[c.Index];
            var chunkPosition = chunk.Position;
            var visibleBlocks = chunk.Blocks.Where(x => x.IsVisible && x.BlockType != BlockType.WaterLevel && x.BlockType != BlockType.None)
                .Select(x => (block: x, blockBB: new AABB(x.LocalPosition + chunkPosition, x.LocalPosition + Vector3i.One + chunkPosition)));

            //Log.Debug($"testing block intersections for chunk: {chunk}, visible blocks; {visibleBlocks.Count()}");
            foreach (var (block, blockBB) in visibleBlocks)
            {
                totalBlocks++;
                var blockDistance = VoxelHelper.RayIntersect(origin, direction, blockBB);
                if (blockDistance.HasValue && blockDistance < MaxPickingDistance)
                {
                    //Log.Debug($"block intersection: {block.LocalPosition} {block.BlockType}, world position: {blockBB}");
                    if (blockDistance.Value < minDistance)
                    {
                        minDistance = blockDistance.Value;
                        nearestBlockIndex = block.Index;
                        nearestBlockChunkIndex = chunk.Index;
                        //Log.Debug($"nearest block: {nearestBlockIndex} in chunk: {nearestBlockChunkIndex} at distance: {minDistance}");
                    }
                }
            }
        }
        var resultString = nearestBlockIndex >= 0 ? $"block: {nearestBlockIndex} in chunk: {nearestBlockChunkIndex} at distance: {minDistance:N2}" : "no intersection found";
        Log.Debug($"{resultString}, chunks ({totalChunks}) intersection time {chunkIntersectionTime:N0} ticks, blocks ({totalBlocks}) picking time {stopwatch.ElapsedTicks - start:N0} ticks");

        return nearestBlockIndex >= 0 ? (nearestBlockChunkIndex, nearestBlockIndex, minDistance) : null;
    }

    private void Camera_CameraChanged(object? sender, EventArgs e)
    {
        var index = VoxelHelper.GetChunkIndexFromGlobalPosition(camera.Position);
        if (lastCameraChunkIndex != index)
        {
            Log.Debug($"Camera_CameraChanged() chunk index changed from {lastCameraChunkIndex} to {index}");
            lastCameraChunkIndex = index;

            //  update surrounding chunk indices
            var (added, removed) = UpdateSurroundingChunkIndices(camera.Position);
            Log.Debug($"Camera_CameraChanged() adding {added.Length} removing {removed.Length} chunks, worker queue items: {workQueue.Count}");

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
                if (loadedChunks.TryGetValue(chunkIndex, out var chunk))
                {
                    chunk.State = ChunkState.ToBeRemoved;
                    workQueue.Enqueue(chunkIndex);
                }
                else
                {
                    Log.Debug($"removed chunk: {chunkIndex} not found in loaded chunks");
                }
            }
        }
    }

    /// <summary>
    /// Updates the surrounding chunk indices based on the given center position.
    /// </summary>
    /// <param name="centerPosition"></param>
    /// <returns></returns>
    private (int[] added, int[] removed) UpdateSurroundingChunkIndices(in Vector3 centerPosition)
    {
        var newChunkSetIndices = new List<int>();

        // calculate the camera chunk indices based on its position
        var cameraChunkX = (int)((centerPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)((centerPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);

        // calculate the range of chunks to check in X and Z directions
        var minX = Math.Max(0, cameraChunkX - VoxelHelper.MaxDistanceInChunks);
        var maxX = Math.Min(VoxelHelper.WorldChunksXZ - 1, cameraChunkX + VoxelHelper.MaxDistanceInChunks);
        var minZ = Math.Max(0, cameraChunkZ - VoxelHelper.MaxDistanceInChunks);
        var maxZ = Math.Min(VoxelHelper.WorldChunksXZ - 1, cameraChunkZ + VoxelHelper.MaxDistanceInChunks);

        for (var z = minZ; z <= maxZ; z++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var chunkIndex = x + z * VoxelHelper.WorldChunksXZ;
                newChunkSetIndices.Add(chunkIndex);
            }
        }

        var added = newChunkSetIndices.Except(surroundingChunkIndices).ToArray();
        var removed = surroundingChunkIndices.Except(newChunkSetIndices).ToArray();

        surroundingChunkIndices.Clear();
        surroundingChunkIndices.AddRange(newChunkSetIndices);

        return (added, removed);
    }

    /// <summary>
    /// If chunk not already loaded, creates a new chunk, initializes it and adds to loaded collection.
    /// </summary>
    /// <param name="chunkIndex"></param>
    /// <returns></returns>
    private bool CreateChunkInitializeAndAddToLoaded(int chunkIndex, out Chunk chunk)
    {
        if(cachedChunks.TryRemove(chunkIndex, out chunk))
        {            
            loadedChunks[chunkIndex] = chunk;
        }

        //  if not loaded create new chunk
        if (!loadedChunks.TryGetValue(chunkIndex, out chunk))
        {
            var position = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
            chunk = new Chunk(this, chunkIndex)
            {

                Aabb = (position, position + ChunkSize),
                State = ChunkState.Loaded,
            };
            loadedChunks[chunkIndex] = chunk;
        }

        //  chunk must be either already loaded or newly created, if loaded and initialized bail out
        if (chunk.IsInitialized) return false;

        chunk.Initialize(terrainBuilder);
        return true;
    }

    private void WorkQueueProcessor()
    {
        while (true)
        {
            var queueLength = workQueue.Count;
            if (queueLength > 0)
            {
                Parallel.For(0, queueLength, i => ProcessWorkItem());
            }
            else
            {
                MoveChunksToCache();
                //var toUnload = loadedChunks.Values.Where(x => x.State == ChunkState.SafeToRemove).ToArray();
                //var count = toUnload.Length;
                //if (count > 0)
                //{
                //    //Log.Debug($"unloading: {count} / {loadedChunks.Count} chunks");
                //    var counter = 0;
                //    foreach (var chunk in toUnload)
                //    {
                //        if (chunk.State == ChunkState.SafeToRemove)
                //        {
                //            loadedChunks.TryRemove(chunk.Index, out _);
                //            counter++;
                //        }
                //    }
                //    //Log.Debug($"chunks unloaded {counter}");
                //}
            }
        }
    }

    private void MoveChunksToCache()
    {
        if(camera is null) return;
        // calculate the camera chunk indices based on its position
        var centerPosition = camera.Position; 
        var cameraChunkX = (int)((centerPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)((centerPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);
        var cameraXZ = new Vector2i(cameraChunkX, cameraChunkZ);
        var toUnload = loadedChunks.Values.Where(x => (x.ChunkPosition - cameraXZ).EuclideanLength > VoxelHelper.MaxDistanceInChunks);      
        if (toUnload.Any())
        {
            //Log.Debug($"unloading: {count} / {loadedChunks.Count} chunks");
            var counter = 0;
            foreach (var chunk in toUnload)
            {
                //if (chunk.State == ChunkState.SafeToRemove)
                {
                    if(loadedChunks.TryRemove(chunk.Index, out var loadedChunk))
                    {
                        cachedChunks[chunk.Index] = loadedChunk;
                        counter++;
                    }
                }
            }
            if(counter > 0) Log.Debug($"chunks moved to cache: {counter}");
        }
    }

    private void ProcessWorkItem()
    {
        if (workQueue.TryDequeue(out var index))
        {
            var start = stopwatch.ElapsedMilliseconds;

            if (loadedChunks.TryGetValue(index, out var chunk))
            {
                //  either the chunk is flagged for removal or it is already initialized
                if (chunk.State == ChunkState.ToBeRemoved)
                {
                    //Log.Debug($"chunk: {index} flagged for removal");
                    ChunkRenderer.initializedChunksQueue.Enqueue(chunk);
                    return;
                }
            }

            if (chunk is not null)
            {
                Log.Debug($"ProcessWorkItem() chunk: {chunk}");
            }

            if (CreateChunkInitializeAndAddToLoaded(index, out chunk))
            {
                var initialized = stopwatch.ElapsedMilliseconds - start;
                var processingStart = stopwatch.ElapsedMilliseconds;
                chunk.CalcVisibleBlocks();
                var processed = stopwatch.ElapsedMilliseconds - processingStart;
                ChunkRenderer.initializedChunksQueue.Enqueue(chunk);
                Log.Debug($"ProcessWorkItem() chunk: {chunk}, init time:{initialized} ms, process time:{processed} ms");
            }
            Debug.Assert((chunk.IsInitialized) && (chunk.IsProcessed), "loaded chunk not initialized!");
        }
    }

    public BlockState? GetBlockByGlobalPositionSafe(int x, int y, int z)
    {
        var blockWorldPosition = new Vector3i(x, y, z);
        if (!VoxelHelper.IsGlobalPositionInWorld(blockWorldPosition)) return null;

        var chunkIndex = VoxelHelper.GetChunkIndexFromGlobalPosition(blockWorldPosition);
        var chunk = this[chunkIndex];
        if (chunk is null) return null;

        var chunkWorldPosition = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var (cx, cy, cz) = blockWorldPosition - chunkWorldPosition;
        var block = chunk.Blocks[cx + cz * VoxelHelper.ChunkSideSize + cy * VoxelHelper.ChunkSideSizeSquare];
        return block;
    }
}