using OpenRender;
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

    private ICamera camera = default!;
    private int lastCameraChunkIndex = -1;

    public volatile int ProcessedStartingChunks;
    public int TotalStartingChunks;

    public ChunkRenderer ChunkRenderer { get; private set; }

    public int WorkerQueueLength => workQueue.Count;
    
    private volatile bool isRunning;
    private Thread workerThread;

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

    public int LoadedChunks => loadedChunks.Values.Count;
    public int CachedChunks => cachedChunks.Values.Count;

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
                LoadChangedChunkBlocks(chunk);
                chunk.CalcVisibleBlocks();
            }

            //chunk.CalcAO();
            Interlocked.Increment(ref ProcessedStartingChunks);
        });
        Log.Debug($"{processed} chunks initialized in: {stopwatch.ElapsedMilliseconds - start} ms");
    }

    public BlockState? PickBlock(Vector3 origin, Vector3 direction, float pickingDistance = VoxelHelper.MaxPickingDistance)
    {
        var start = stopwatch.ElapsedTicks;
        //  first get the chunks intersecting with the ray
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
            //if (!loadedChunks.TryGetValue(c.Index, out var chunk)) cachedChunks.TryGetValue(c.Index, out chunk);
            //if (chunk is null) continue;
            var chunkPosition = chunk.Aabb.Min;
            var visibleBlocks = chunk.Blocks.Where(x => x.IsVisible && x.BlockType != BlockType.WaterLevel && x.BlockType != BlockType.None)
                .Where(x => (x.Aabb.Min - (Vector3i)origin).ManhattanLength < VoxelHelper.MaxPickingDistance);

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
        new(0, 1, 0),       //  above
        //new (1, 1, 0),      //  above right
        //new (-1, 1, 0),     //  above left
        //new (-1, 1, 1),     //  above back left
        //new (1, 1, 1),      //  above front right
        //new (1, 1, -1),     //  above back right
        //new (-1, 1, -1),    //  above back left
        //new (0, 1, 1),      //  above front
        //new (0, 1, -1),     //  above back
        new (0, 0, 1),      //  front
        new (0, 0, -1),     //  back
        new (1, 0, 0),      //  right
        new (-1, 0, 0),     //  left
        //new (-1, 0, 1),     //  front left
        //new (1, 0, 1),      //  front right
        //new (1, 0, -1),     //  back right
        //new (-1, 0, -1),    //  back left
        new (0, -1, 0),     //  below
        //new (1, -1, 0),     //  below right
        //new (-1, -1, 0),    //  below left
        //new (-1, -1, 1),    //  below back left
        //new (1, -1, 1),     //  below front right
        //new (1, -1, -1),    //  below back right
        //new (-1, -1, -1),   //  below back left
        //new (0, -1, 1),     //  below front
        //new (0, -1, -1),    //  below back        
    ];

    public BlockState?[] GetNeighboringBlocks(ref BlockState block)
    {
        var neighboringBlocks = new BlockState?[26];
        var globalPosition = block.GlobalPosition;
        for(var i = 0; i <  neighboringOffsets.Length; i++)
        {
            var offset = neighboringOffsets[i];
            var neighborPosition = globalPosition + offset;
            var neighborBlock = GetBlockByPositionGlobalSafe(neighborPosition.X, neighborPosition.Y, neighborPosition.Z);
            neighboringBlocks[i] = neighborBlock;
        }
        return neighboringBlocks;
    }
    #endregion

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

        block.BlockType = BlockType.None;
        block.IsVisible = false;
        chunk.UpdateBlock(ref block);

        //  make neighbor blocks visible
        var updatedChunks = new HashSet<Chunk>();
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

        var neighbors = GetNeighboringBlocks(ref block);
        foreach (var neighbor in neighbors)
        {
            MakeNeighborBlockVisible(neighbor);
        }

        foreach (var updatedChunk in updatedChunks)
        { 
            ChunkRenderer.chunksStreamingQueue.Enqueue(updatedChunk);
        }

        //BlockState? neighborBlock;
        //var worldPosition = block.GlobalPosition;
        //neighborBlock = GetBlockByPositionGlobalSafe(worldPosition.X - 1, worldPosition.Y, worldPosition.Z);
        //MakeNeighborBlockVisible(neighborBlock);

        //neighborBlock = GetBlockByPositionGlobalSafe(worldPosition.X + 1, worldPosition.Y, worldPosition.Z);
        //MakeNeighborBlockVisible(neighborBlock);

        //neighborBlock = GetBlockByPositionGlobalSafe(worldPosition.X, worldPosition.Y - 1, worldPosition.Z);
        //MakeNeighborBlockVisible(neighborBlock);

        //neighborBlock = GetBlockByPositionGlobalSafe(worldPosition.X, worldPosition.Y + 1, worldPosition.Z);
        //MakeNeighborBlockVisible(neighborBlock);

        //neighborBlock = GetBlockByPositionGlobalSafe(worldPosition.X, worldPosition.Y, worldPosition.Z - 1);
        //MakeNeighborBlockVisible(neighborBlock);

        //neighborBlock = GetBlockByPositionGlobalSafe(worldPosition.X, worldPosition.Y, worldPosition.Z + 1);
        //MakeNeighborBlockVisible(neighborBlock);

        //ChunkRenderer.chunksStreamingQueue.Enqueue(chunk);
    }

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
        foreach(var chunk in cachedChunks.Values)
        {
            SaveChangedChunkBlocks(chunk);
        }
        Log.Info("VoxelWorld.Close() all done!");
    }

    #region terrain streaming
    private void Camera_CameraChanged(object? sender, EventArgs e)
    {
        var index = VoxelHelper.GetChunkIndexFromPositionGlobal(camera.Position);
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
                if (cachedChunks.TryGetValue(chunkIndex, out var cachedChunk))
                {
                    cachedChunk.State = ChunkState.ToBeRemoved;
                    ChunkRenderer.chunksStreamingQueue.Enqueue(cachedChunk);
                }
                else if (loadedChunks.TryGetValue(chunkIndex, out var chunk))
                {
                    chunk.State = ChunkState.ToBeRemoved;
                    ChunkRenderer.chunksStreamingQueue.Enqueue(chunk);
                }
                else
                {
                    Log.Debug($"removed chunk: {chunkIndex} not found in loaded/cached chunks");
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
        //surroundingChunkIndices = new HashSet<int>(newChunkSetIndices);
        return (added, removed);
    }

    /// <summary>
    /// If chunk not already loaded or in cache, creates a new chunk, initializes it and adds to loaded collection.
    /// </summary>
    /// <param name="chunkIndex"></param>
    /// <returns></returns>
    private bool CreateChunkInitializeAndAddToLoaded(int chunkIndex, out Chunk chunk)
    {
        //  first check cache
        if (cachedChunks.TryRemove(chunkIndex, out chunk))
        {
            loadedChunks[chunkIndex] = chunk;
        }

        //  if not in cache and not already loaded, create new chunk
        if (chunk == null && !loadedChunks.TryGetValue(chunkIndex, out chunk))
        {
            var position = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
            chunk = new Chunk(this, chunkIndex)
            {

                Aabb = (position, position + ChunkSize),
                State = ChunkState.Loaded,
            };
            loadedChunks[chunkIndex] = chunk;
        }

        //  chunk must be either previously loaded or newly created, if loaded and initialized bail out
        if (chunk.IsInitialized) return false;

        chunk.Initialize(terrainBuilder);
        return true;
    }

    private void WorkQueueProcessor()
    {
        while (isRunning)
        {
            var queueLength = workQueue.Count;
            if (queueLength > 0)
            {
                //Parallel.For(0, queueLength, i => ProcessWorkItem());
                ProcessWorkItem();
            }
            else
            {
                if (camera is not null)
                {
                    // calculate the camera chunk indices based on its position
                    var centerPosition = camera.Position;
                    var cameraChunkX = (int)((centerPosition.X + 0.5f) / VoxelHelper.ChunkSideSize);
                    var cameraChunkZ = (int)((centerPosition.Z + 0.5f) / VoxelHelper.ChunkSideSize);
                    var cameraXZ = new Vector2i(cameraChunkX, cameraChunkZ);

                    MoveChunksToCache(cameraXZ);
                    EvictChunksFromCache(cameraXZ);
                }
            }
        }
    }

    /// <summary>
    /// Preloads the chunk if not already loaded or in cache and stores the chunk in cachedChunks.
    /// </summary>
    /// <param name="chunkIndex"></param>
    private void PreloadChunk(int chunkIndex)
    {
        if(loadedChunks.TryGetValue(chunkIndex, out _))
        {
            return;
        }

        if(cachedChunks.TryGetValue(chunkIndex, out _))
        {
            return;
        }
        var position = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
        var chunk = new Chunk(this, chunkIndex)
        {
            Aabb = (position, position + ChunkSize),
            State = ChunkState.Loaded,
        };
        chunk.Initialize(terrainBuilder);
        chunk.CalcVisibleBlocks();
        cachedChunks[chunkIndex] = chunk;
    }

    private void EvictChunksFromCache(Vector2i cameraXZ)
    {
        const int DistanceThreshold = 5;
        var chunks = cachedChunks.Values
            .Where(x => (x.ChunkPosition - cameraXZ).ManhattanLength > VoxelHelper.MaxDistanceInChunks + DistanceThreshold);

        if (chunks.Any())
        {
            var counter = 0;
            foreach (var chunk in chunks)
            {
                if (chunk.State is ChunkState.SafeToRemove or ChunkState.Loaded)
                {
                    SaveChangedChunkBlocks(chunk);
                    cachedChunks.Remove(chunk.Index, out _);
                    counter++;
                }
            }
            if (counter > 0) Log.Debug($"cached chunks evicted {counter}");
        }
    }

    private void MoveChunksToCache(Vector2i cameraXZ)
    {
        var chunks = loadedChunks.Values.Where(x => !surroundingChunkIndices.Contains(x.Index) &&
            (x.ChunkPosition - cameraXZ).ManhattanLength > VoxelHelper.MaxDistanceInChunks);

        if (chunks.Any())
        {
            var counter = 0;
            foreach (var chunk in chunks)
            {
                if (loadedChunks.TryRemove(chunk.Index, out var loadedChunk))
                {
                    cachedChunks[chunk.Index] = loadedChunk;
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
            var start = stopwatch.ElapsedMilliseconds;

            if (CreateChunkInitializeAndAddToLoaded(index, out var chunk))
            {
                var initialized = stopwatch.ElapsedMilliseconds - start;
                var processingStart = stopwatch.ElapsedMilliseconds;
                LoadChangedChunkBlocks(chunk);
                chunk.CalcVisibleBlocks();

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

            Debug.Assert((chunk.IsInitialized) && (chunk.IsProcessed), "loaded chunk not initialized!");
            ChunkRenderer.chunksStreamingQueue.Enqueue(chunk);
        }
    }

    private void SaveChangedChunkBlocks(Chunk chunk)
    { 
        if(chunk.ChangedBlocks.Count > 0)
        {
            var fileName = $"world-{seed}_chunk-{chunk.Index}.bin";
            var path = Path.Combine(Environment.CurrentDirectory, "save", fileName);
            
            var dirName = Path.GetDirectoryName(path);
            if (dirName is not null) Directory.CreateDirectory(dirName);

            using var stream = File.Create(path, 1024, FileOptions.SequentialScan);
            foreach(var block in chunk.ChangedBlocks.Values)
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

    private void LoadChangedChunkBlocks(Chunk chunk)
    {
        var fileName = $"world-{seed}_chunk-{chunk.Index}.bin";
        var path = Path.Combine(Environment.CurrentDirectory, "save", fileName);
        if(File.Exists(path))
        {
            int bytesRead;
            var buffer = new byte[60];
            using var stream = File.OpenRead(path);
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var bufferIndex = 0;
                while (bufferIndex < bytesRead)
                {
                    var index = BitConverter.ToInt32(buffer, bufferIndex);
                    var blockType = (BlockType)buffer[bufferIndex + 4];
                    var frontDirection = (BlockDirection)buffer[bufferIndex + 5];
                    var block = chunk.Blocks[index];
                    block.BlockType = blockType;
                    block.FrontDirection = frontDirection;
                    chunk.UpdateBlock(ref block);
                    bufferIndex += 6;

                    //  we need to maintain it even after saving as otherwise the next 
                    //  save would overwrite this changes and only save new changes
                    chunk.ChangedBlocks[index] = block;
                }
            }
        }
    }
    #endregion
}