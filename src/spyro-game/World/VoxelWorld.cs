using OpenRender;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.Components;
using SpyroGame.Noise;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpyroGame.World;

internal class VoxelWorld
{
    #region Textures and materials
    //  key is material id, value is the material
    public static readonly Dictionary<int, VoxelMaterial> materials = new() {
        { (int)BlockType.None, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.7f, 0.8f, 0.9f),
                Shininess = 0.999f
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
        { BlockType.None, "Resources/voxel/box-unwrap.png" },
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

    internal Stopwatch stopwatch = Stopwatch.StartNew();

    private readonly int seed;
    private readonly List<int> surroundingChunkIndices = [];
    private readonly float[] heightData;
    private readonly Frustum frustum;
    private readonly Action<Chunk> onChunkCompleteAction = default!;

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

        heightData = NoiseData.CreateFromEncoding("BwA=", 0, 0, VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize, VoxelHelper.NoiseFrequency, seed, out var minmax);
        ChunkRenderer = ChunkRenderer.Create(this, GetTextureHandles(), GetMaterials());
        onChunkCompleteAction = ChunkRenderer.initializedChunksQueue.Enqueue;

        var thread = new Thread(ChunkProcessor)
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

    public float GetHeightNormalizedGlobal(int globalX, int globalZ)
    {
        var noiseIndex = globalX + globalZ * VoxelHelper.WorldChunksXZ * VoxelHelper.ChunkSideSize;
        var height = heightData[noiseIndex];
        height = height * VoxelHelper.HeightAmplitude + VoxelHelper.WaterLevel;
        return height;
    }

    public float GetHeightNormalizedChunkLocal(int chunkIndex, int x, int z)
    {
        //  get chunks world position
        var worldX = chunkIndex % VoxelHelper.WorldChunksXZ;
        var worldZ = chunkIndex / VoxelHelper.WorldChunksXZ;
        var cx = worldX * VoxelHelper.ChunkSideSize;
        var cz = worldZ * VoxelHelper.ChunkSideSize;

        //  get height data from global position
        return GetHeightNormalizedGlobal(x + cx, z + cz);
    }

    /// <summary>
    /// Gets a loaded chunk containing the given global position.
    /// </summary>
    /// <param name="globalPosition"></param>
    /// <param name="chunk"></param>
    /// <returns>True if chunk is loaded else false</returns>
    public bool GetChunkByGlobalPosition(in Vector3 globalPosition, out Chunk? chunk)
    {
        chunk = null;

        if (!VoxelHelper.IsGlobalPositionInWorld(globalPosition)) return false;

        var idx = VoxelHelper.GetChunkIndexFromGlobalPosition(globalPosition);
        chunk = this[idx];
        return chunk != null;
    }

    public Chunk? this[int index] => loadedChunks.TryGetValue(index, out var chunk) ? chunk : null;

    /// <summary>
    /// Generates a block from chunk local coordinates.
    /// </summary>
    /// <param name="index"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="z"></param>
    /// <returns></returns>
    public BlockState GenerateBlockChunkLocal(int index, int x, int y, int z)
    {        
        var height = GetHeightNormalizedChunkLocal(index, x, z);
        var blockAltitude = y;

        BlockType bt;
        if (blockAltitude >= height && blockAltitude <= height + 1)
        {
            bt = BlockType.GrassDirt;
        }
        else if (blockAltitude < height && blockAltitude >= height - 1)
        {
            bt = BlockType.Dirt;
        }
        else if (blockAltitude < height - 1)
        {
            bt = BlockType.Rock;
        }
        else
        {
            bt = BlockType.None;
        }

        //  special cases
        if (blockAltitude <= VoxelHelper.WaterLevel)
        {
            if ((bt != BlockType.None) && (bt != BlockType.WaterLevel) && (blockAltitude >= VoxelHelper.WaterLevel - 1))
            {
                bt = BlockType.Sand;
            }
            else if ((bt != BlockType.None) && (bt != BlockType.WaterLevel) && (blockAltitude < VoxelHelper.WaterLevel - 1))
            {
                //  replace top layer underwater solid blocks with bedrock
                bt = BlockType.Rock;
            }
            else if ((bt == BlockType.None) && blockAltitude == VoxelHelper.WaterLevel)
            {
                bt = BlockType.WaterLevel;
            }
            else
            {
                bt = BlockType.None;
            }
        }

        /*
        // for debugging        
        bt = blockAltitude <= height + 1 && blockAltitude >= height ? x == 0 ? BlockType.Sand :
            x == VoxelHelper.ChunkSizeXZMinusOne ? BlockType.Dirt :
            z == 0 ? BlockType.Rock :
            z == VoxelHelper.ChunkSizeXZMinusOne ? BlockType.Snow :
            BlockType.Grass : BlockType.None;
        */

        var blockIdx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
        var block = new BlockState
        {
            Index = blockIdx,
            BlockType = bt,
        };
        return block;
    }

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
            Interlocked.Increment(ref ProcessedStartingChunks);
        });
        Log.Debug($"{processed} chunks initialized in: {stopwatch.ElapsedMilliseconds - start} ms");
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
    private bool CreateChunkInitializeAndAddToLoaded(int chunkIndex, out Chunk? chunk)
    {
        //  if not loaded create new chunk
        if (!loadedChunks.TryGetValue(chunkIndex, out chunk))
        {
            var position = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
            chunk = new Chunk()
            {
                Aabb = (position, position + ChunkSize),
                State = ChunkState.Loaded,
            };
            loadedChunks[chunkIndex] = chunk;
        }

        //  chunk must be either already loaded or newly created, if loaded and initialized bail out
        if (chunk.IsInitialized) return false;

        chunk.Initialize(this, chunkIndex, seed);
        return true;
    }

    private void ChunkProcessor()
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
                var toUnload = loadedChunks.Values.Where(x => x.State == ChunkState.SafeToRemove).ToArray();
                var count = toUnload.Length;
                if (count > 0)
                {
                    //Log.Debug($"unloading: {count} / {loadedChunks.Count} chunks");
                    var counter = 0;
                    foreach (var chunk in toUnload)
                    {
                        if (chunk.State == ChunkState.SafeToRemove)
                        {
                            loadedChunks.TryRemove(chunk.Index, out _);
                            counter++;
                        }
                    }
                    Log.Debug($"chunks unloaded {counter}");
                }
            }
        }
    }

    private void ProcessWorkItem()
    {
        if (workQueue.TryDequeue(out var index))
        {
            var initialized = 0L;
            var processed = 0L;
            var start = stopwatch.ElapsedMilliseconds;

            if (loadedChunks.TryGetValue(index, out var chunk))
            {
                //  either the chunk is flagged for removal or it is already initialized
                if (chunk.State == ChunkState.ToBeRemoved)
                {
                    //Log.Debug($"chunk: {index} flagged for removal");
                    onChunkCompleteAction?.Invoke(chunk);
                    return;
                }
            }

            if (CreateChunkInitializeAndAddToLoaded(index, out chunk))
            {
                initialized = stopwatch.ElapsedMilliseconds - start;
                var processingStart = stopwatch.ElapsedMilliseconds;
                chunk?.CalcVisibleBlocks();
                processed = stopwatch.ElapsedMilliseconds - processingStart;
            }
            Debug.Assert((chunk?.IsInitialized ?? false) && (chunk?.IsProcessed ?? false), "loaded chunk not initialized!");
            onChunkCompleteAction?.Invoke(chunk);
        }
    }
}