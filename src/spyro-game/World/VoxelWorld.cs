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
    private readonly PriorityQueue<IReadOnlyList<int>, int> priorityWorkQueue = new();

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


    private void Camera_CameraChanged(object? sender, EventArgs e)
    {
        var index = VoxelHelper.GetChunkIndexFromGlobalPosition(camera.Position);
        if (lastCameraChunkIndex != index)
        {
            Log.Debug($"Camera_CameraChanged() chunk index changed from {lastCameraChunkIndex} to {index}");
            lastCameraChunkIndex = index;

            //  update surrounding chunk indices
            UpdateSurroundingChunkIndices(camera.Position, camera.FarPlaneDistance);

            //  enqueue chunks for loading on background thread
            priorityWorkQueue.Enqueue([.. surroundingChunkIndices], 10);
        }
    }

    private void UpdateSurroundingChunkIndices(in Vector3 centerPosition, float farPlaneDistance)
    {
        surroundingChunkIndices.Clear();

        // Calculate the camera chunk indices based on its position
        var cameraChunkX = (int)(centerPosition.X / VoxelHelper.ChunkSideSize);
        var cameraChunkZ = (int)(centerPosition.Z / VoxelHelper.ChunkSideSize);

        // Calculate the maximum distance a chunk can be from the camera in terms of chunks
        var maxChunkDistanceInChunks = (int)Math.Ceiling(farPlaneDistance / VoxelHelper.ChunkSideSize) + 1;

        // Calculate the range of chunks to check in X and Z directions
        var minX = Math.Max(0, cameraChunkX - maxChunkDistanceInChunks);
        var maxX = Math.Min(VoxelHelper.WorldChunksXZ - 1, cameraChunkX + maxChunkDistanceInChunks);
        var minZ = Math.Max(0, cameraChunkZ - maxChunkDistanceInChunks);
        var maxZ = Math.Min(VoxelHelper.WorldChunksXZ - 1, cameraChunkZ + maxChunkDistanceInChunks);

        for (var z = minZ; z <= maxZ; z++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var chunkIndex = x + z * VoxelHelper.WorldChunksXZ;
                surroundingChunkIndices.Add(chunkIndex);
            }
        }
    }

    //private void UpdateSurroundingChunkIndicesUnused(in Vector3 cameraPosition, float farPlaneDistance)
    //{
    //    surroundingChunkIndices.Clear();

    //    // Calculate the camera chunk indices based on its position
    //    var cameraChunkX = (int)(cameraPosition.X / VoxelHelper.ChunkSideSize);
    //    var cameraChunkZ = (int)(cameraPosition.Z / VoxelHelper.ChunkSideSize);

    //    // Calculate the maximum distance a chunk can be from the camera in terms of chunks
    //    var maxChunkDistanceInChunks = (int)Math.Ceiling(farPlaneDistance / VoxelHelper.ChunkSideSize) + 1;

    //    // Calculate the range of chunks to check in X and Z directions
    //    var minX = Math.Max(0, cameraChunkX - maxChunkDistanceInChunks);
    //    var maxX = Math.Min(VoxelHelper.WorldChunksXZ - 1, cameraChunkX + maxChunkDistanceInChunks);
    //    var minZ = Math.Max(0, cameraChunkZ - maxChunkDistanceInChunks);
    //    var maxZ = Math.Min(VoxelHelper.WorldChunksXZ - 1, cameraChunkZ + maxChunkDistanceInChunks);

    //    List<int> batch = [];
    //    for (var z = minZ; z <= maxZ; z++)
    //    {
    //        for (var x = minX; x <= maxX; x++)
    //        {
    //            // calculate the min and max points of the chunk
    //            //var chunkMin = new Vector3i(x * VoxelHelper.ChunkSideSize, 0, z * VoxelHelper.ChunkSideSize);
    //            //var chunkMax = new Vector3i((x + 1) * VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, (z + 1) * VoxelHelper.ChunkSideSize);

    //            var chunkIndex = x + z * VoxelHelper.WorldChunksXZ;
    //            surroundingChunkIndices.Add(chunkIndex);
    //            if (!loadedChunks.ContainsKey(chunkIndex))
    //            {
    //                //var center = chunkMin + ChunkHalfSize;
    //                //var distanceSquared = Vector3.DistanceSquared(center, cameraPosition);
    //                batch.Add(chunkIndex);
    //                //priorityWorkQueue.Enqueue(chunkIndex, (int)MathF.Round(distanceSquared));
    //                //EnqueueChunkRequest(chunkIndex, (int)MathF.Round(distanceSquared));
    //            }
    //            // Check if the chunk is inside or intersects with the frustum
    //            //if (CullingHelper.IsAABBInFrustum((chunkMin, chunkMax), frustumPlanes))
    //            //{
    //            //    if (!chunks.ContainsKey(chunkIndex))
    //            //    {
    //            //        var center = chunkMin + ChunkHalfSize;
    //            //        var distanceSquared = Vector3.DistanceSquared(center, cameraPosition);
    //            //        EnqueueChunkRequest(chunkIndex, (int)MathF.Round(distanceSquared));
    //            //    }
    //            //}
    //        }
    //    }

    //    if (batch.Count > 0)
    //    {
    //        priorityWorkQueue.Enqueue(batch, 10);
    //    }
    //}

    //public void EnqueueChunkRequest(int chunkIndex, int priority)
    //{
    //    Log.Debug($"EnqueueChunkRequest() enqueued item: {chunkIndex}, priority: {priority}");
    //    priorityWorkQueue.Enqueue(chunkIndex, priority);
    //}


    /// <summary>
    /// Selects the closest chunks to the given position, calculates visible blocks per chunk and chunks aabb.
    /// </summary>
    /// <param name="centerPosition"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    //public (Chunk, AABB, IEnumerable<BlockState>)[] GetImmediate(Vector3 centerPosition, int count)
    //{
    //    var closest = chunks.Values
    //            //.Where(chunk => chunk.Position.Y + ChunkSize.Y < centerPosition.Y)                          //  chunks top must be lower then camera position
    //            .OrderBy(chunk => Vector3.DistanceSquared(chunk.Position + ChunkHalfSize, centerPosition))  //  order by distance to camera
    //            .Take(count)                                                                                //  take the specified number and calculate chunk data
    //            .Select(chunk =>
    //            {
    //                var aabb = (chunk.Position, chunk.Position + ChunkSize);
    //                var vb = chunk.GetVisibleBlocks();
    //                return (chunk, aabb, vb);
    //            })
    //            .ToArray();

    //    return closest;
    //}

    private Action<Chunk>? onChunkCompleteAction;

    private bool InitializeChunk(int chunkIndex)
    {
        if (loadedChunks.ContainsKey(chunkIndex)) return false;
        var startTime = stopwatch.ElapsedMilliseconds;
        var x = chunkIndex % VoxelHelper.WorldChunksXZ;
        var z = chunkIndex / VoxelHelper.WorldChunksXZ;

        var position = new Vector3i(x * VoxelHelper.ChunkSideSize, 0, z * VoxelHelper.ChunkSideSize);
        var chunk = new Chunk()
        {
            Aabb = (position, position + ChunkSize)
        };
        chunk.Initialize(this, chunkIndex, seed);
        if (loadedChunks.TryAdd(chunk.Index, chunk))
        {
            //Log.Debug($"InitializeChunk() chunk: {chunk} completed in {stopwatch.ElapsedMilliseconds - startTime} ms");
        }
        else
        {
            Log.Warn($"InitializeChunk() failed to add chunk: {chunk}");
        }
        return true;
    }

    private static readonly ParallelOptions parallelOptions = new()
    {
        MaxDegreeOfParallelism = 128
    };

    private void ChunkProcessor()
    {
        while (true)
        {
            var queueLength = priorityWorkQueue.Count;
            if (queueLength > 0)
            {
                var hasWork = false;
                var skipped = 0;
                IReadOnlyList<int>? batch = null;
                while (priorityWorkQueue.Count > 0)
                {
                    skipped++;
                    priorityWorkQueue.TryDequeue(out batch, out _);
                    hasWork = true;
                }

                //if (priorityWorkQueue.TryDequeue(out var batch, out _))
                if (hasWork && batch?.Count > 0)
                {
                    Log.Debug($"start batch processing, skipped batches: {skipped - 1}");
                    var start = stopwatch.ElapsedMilliseconds;
                    var processed = 0;
                    Parallel.For(0, batch.Count, parallelOptions, i =>
                    {
                        try
                        {
                            var index = batch[i];                            
                            if (InitializeChunk(index))
                            {
                                Interlocked.Increment(ref processed);                                
                            }
                            //chunk.CalcVisibleBlocks();
                            //loadedChunks[index] = chunk;
                            //onChunkCompleteAction?.Invoke(chunk);
                        }
                        catch (Exception ex)
                        {
                            throw;
                        }

                    });
                    Log.Debug($"batch initialization time: {stopwatch.ElapsedMilliseconds - start} ms, chunks initialized: {processed}");

                    // batch has been built, calc visible blocks
                    processed = 0;
                    start = stopwatch.ElapsedMilliseconds;
                    Parallel.For(0, batch.Count, parallelOptions, i =>
                    {
                        var index = batch[i];
                        var chunk = loadedChunks[index];
                        if (!chunk.IsProcesses)
                        {
                            chunk.CalcVisibleBlocks();
                            loadedChunks[index] = chunk;
                            Interlocked.Increment(ref processed);
                            onChunkCompleteAction?.Invoke(chunk);
                        }
                    });
                    Log.Debug($"batch visibility calculation time: {stopwatch.ElapsedMilliseconds - start} ms, chunks recalculated: {processed}");

                }
            }
            else
            {
                Thread.Yield();
            }
        }
    }
}