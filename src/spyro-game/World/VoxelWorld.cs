using OpenRender;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpyroGame.World;

internal class VoxelWorld
{
    public static readonly Vector3i ChunkSize = new(VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, VoxelHelper.ChunkSideSize);
    public static readonly Vector3i ChunkHalfSize = ChunkSize / 2;

    /// <summary>
    /// Queue of chunks to be created by background threads.
    /// </summary>
    private readonly Queue<int> workQueue = new();

    private readonly Queue<int[]> batchWorkQueue = new();

    //  key is chunk index, value is the chunk   
    private readonly ConcurrentDictionary<int, Chunk> loadedChunks = [];

    internal Stopwatch stopwatch = Stopwatch.StartNew();

    private readonly int seed;
    private readonly List<int> surroundingChunkIndices = [];

    private readonly ICamera camera;
    private readonly Frustum frustum;
    private int lastCameraChunkIndex = -1;

    public VoxelWorld(Scene scene, int seed)
    {
        frustum = scene.Renderer!.Frustum;
        camera = scene.Camera!;
        camera.CameraChanged += Camera_CameraChanged;

        this.seed = seed;
        var thread = new Thread(ChunkProcessor)
        {
            IsBackground = true
        };
        thread.Start();
    }

    public int Seed => seed;

    public IReadOnlyList<int> SurroundingChunkIndices => surroundingChunkIndices;

    public IEnumerable<Chunk> SurroundingChunks
    {
        get
        {
            var chunks = new Chunk[surroundingChunkIndices.Count];
            for (var i = 0; i < surroundingChunkIndices.Count; i++)
            {
                var index = surroundingChunkIndices[i];
                var chunk = this[index];
                if (chunk is null)
                {
                    var x = index % VoxelHelper.WorldChunksXZ;
                    var z = index / VoxelHelper.WorldChunksXZ;
                    var position = new Vector3i(x * VoxelHelper.ChunkSideSize, 0, z * VoxelHelper.ChunkSideSize);
                    chunk = new Chunk()
                    {
                        Aabb = (position, position + ChunkSize)
                    };
                }
                chunks[i] = chunk!.Value;
            }
            return chunks;
        }
    }

    public void Initialize(Action<Chunk> onChunkCompleteAction)
    {
        this.onChunkCompleteAction = onChunkCompleteAction;
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

    internal void AddChunk(int index, Chunk chunk) => loadedChunks[index] = chunk;

    private void Camera_CameraChanged(object? sender, EventArgs e)
    {
        var index = VoxelHelper.GetChunkIndexFromGlobalPosition(camera.Position);
        if (lastCameraChunkIndex != index)
        {
            Log.Debug($"Camera_CameraChanged() chunk index changed from {lastCameraChunkIndex} to {index}");
            lastCameraChunkIndex = index;

            //  update surrounding chunk indices
            UpdateSurroundingChunkIndices(camera.Position);

            var ordered = surroundingChunkIndices.OrderByDescending(x => (camera.Position - VoxelHelper.GetChunkPositionGlobal(x) - ChunkHalfSize).LengthSquared);
            foreach (var chunkIndex in ordered)
            {
                workQueue.Enqueue(chunkIndex);
            }
            //  enqueue chunks for loading on background thread
            //workQueue.Enqueue([.. surroundingChunkIndices]);
        }
    }

    private void UpdateSurroundingChunkIndices(in Vector3 centerPosition)
    {
        surroundingChunkIndices.Clear();

        // calculate the camera chunk indices based on its position
        var cameraChunkX = (int)(centerPosition.X / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)(centerPosition.Z / VoxelHelper.ChunkSideSize);

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
                surroundingChunkIndices.Add(chunkIndex);
            }
        }
    }

    private Action<Chunk>? onChunkCompleteAction;

    private bool InitializeChunk(int chunkIndex)
    {
        if (!loadedChunks.TryGetValue(chunkIndex, out var chunk))
        {
            var position = VoxelHelper.GetChunkPositionGlobal(chunkIndex);
            chunk = new Chunk()
            {
                Aabb = (position, position + ChunkSize)
            };
        }

        if (chunk.IsInitialized) return false;

        chunk.Initialize(this, chunkIndex, seed);
        loadedChunks[chunkIndex] = chunk;
        return true;
    }


    public void AddStartingChunks(Vector3 position, Action<Chunk> onChunkCompleteAction)
    {
        this.onChunkCompleteAction = onChunkCompleteAction;

        UpdateSurroundingChunkIndices(position);
        int[] batch = [.. surroundingChunkIndices];

        var parallelOptions = new ParallelOptions()
        {
            MaxDegreeOfParallelism = batch.Length / 2
        };
        var processed = 0;
        var start = stopwatch.ElapsedMilliseconds;
        Parallel.For(0, batch.Length, parallelOptions, i =>
        {
            var index = batch[i];
            if (InitializeChunk(index))
            {
                Interlocked.Increment(ref processed);
            }
            var chunk = loadedChunks[index];
            Debug.Assert(chunk.IsInitialized, "loaded chunk not initialized!");

            if (!chunk.IsProcessed)
            {
                chunk.CalcVisibleBlocks();
                loadedChunks[index] = chunk;
                onChunkCompleteAction?.Invoke(chunk);
            }
        });
        Log.Debug($"{processed} chunks initialized in: {stopwatch.ElapsedMilliseconds - start} ms");
    }


    private void ChunkProcessor()
    {
        while (true)
        {
            var queueLength = workQueue.Count;
            if (queueLength > 0)
            {
                if (workQueue.TryDequeue(out var index))
                {
                    var init = 0L;
                    var processed = 0L;
                    var start = stopwatch.ElapsedMilliseconds;

                    if (InitializeChunk(index))
                    {
                        init = stopwatch.ElapsedMilliseconds - start;
                    }
                    var chunk = loadedChunks[index];
                    Debug.Assert(chunk.IsInitialized, "loaded chunk not initialized!");

                    start = stopwatch.ElapsedMilliseconds;
                    if (!chunk.IsProcessed)
                    {
                        chunk.CalcVisibleBlocks();
                        loadedChunks[index] = chunk;
                        processed = stopwatch.ElapsedMilliseconds - start;
                        onChunkCompleteAction?.Invoke(chunk);
                    }
                    if (init > 0 || processed > 0)
                    {
                        var total = stopwatch.ElapsedMilliseconds - start;
                        Log.Debug($"chunk: {index} total time: {total} ms ({init} : {processed})");
                    }
                }
            }
            else
            {
                Thread.Yield();
            }
        }
    }
}