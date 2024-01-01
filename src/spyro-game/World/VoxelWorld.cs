using OpenRender;
using OpenTK.Mathematics;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpyroGame.World;

internal class VoxelWorld
{
    public static readonly Vector3i ChunkSize = new(VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, VoxelHelper.ChunkSideSize);
    public static readonly Vector3i ChunkHalfSize = ChunkSize / 2;

    //  key is chunk index, value is the chunk   
    internal readonly ConcurrentDictionary<int, Chunk> chunks = [];

    //  key is material id, value is the material
    internal readonly Dictionary<int, VoxelMaterial> materials = new() {
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
        { (int)BlockType.Water, new VoxelMaterial() {
                Diffuse = new Vector3(1, 1, 1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.7f, 0.7f, 0.8f),
                Shininess = 0.3f
            }
        }
    };

    //  key is texture name, value texture handle index 
    internal readonly Dictionary<BlockType, string> textures = new() {
        { BlockType.None, "Resources/voxel/box-unwrap.png" },
        { BlockType.Rock, "Resources/voxel/rock.png" },
        { BlockType.Dirt, "Resources/voxel/dirt.png" },
        { BlockType.GrassDirt, "Resources/voxel/grass-dirt.png" },
        { BlockType.Grass, "Resources/voxel/grass.png" },
        { BlockType.Sand, "Resources/voxel/sand.png" },
        { BlockType.Snow, "Resources/voxel/snow.png" },
        { BlockType.Water, "Resources/voxel/water.png" },
    };
    internal Stopwatch stopwatch = Stopwatch.StartNew();

    public void Initialize(int seed)
    {
        var options = new ParallelOptions() { MaxDegreeOfParallelism = VoxelHelper.WorldChunksXZ };

        for (var z = 0; z < VoxelHelper.WorldChunksXZ; z++)
        {
            //for (var x = 0; x < VoxelHelper.WorldChunksXZ; x++)
            Parallel.For(0, VoxelHelper.WorldChunksXZ, options, x =>
            {
                var index = z * VoxelHelper.WorldChunksXZ + x;
                var position = new Vector3i(x * VoxelHelper.ChunkSideSize, 0, z * VoxelHelper.ChunkSideSize);
                var chunk = new Chunk()
                {
                    Aabb = (position, position + ChunkSize)
                };
                chunk.Initialize(this, index, seed);
                _ = chunks.TryAdd(chunk.Index, chunk);
            });
            //}
        }

        Log.Highlight($"VoxelWorld created {chunks.Count:N0} chunks in {stopwatch.ElapsedMilliseconds:N0} ms, empty chunks {chunks.Values.Where(x => x.IsEmpty).Count()}");
    }

    public Chunk this[int index] => chunks[index];

    public bool GetChunkByGlobalPosition(in Vector3 worldPosition, out Chunk? chunk)
    {
        chunk = null;

        if (!VoxelHelper.IsGlobalPositionInWorld(worldPosition)) return false;

        var idx = VoxelHelper.GetChunkIndexFromGlobalPosition(worldPosition);
        if(chunks.TryGetValue(idx, out var resultChunk))
        {
            chunk = resultChunk;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Selects the closest chunks to the given position, calculates visible blocks per chunk and chunks aabb.
    /// </summary>
    /// <param name="centerPosition"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    public (Chunk, AABB, IEnumerable<BlockState>)[] GetImmediate(Vector3 centerPosition, int count)
    {
        var closest = chunks.Values
                .Where(chunk => chunk.Position.Y + ChunkSize.Y < centerPosition.Y)                          //  chunks top must be lower then camera position
                .OrderBy(chunk => Vector3.DistanceSquared(chunk.Position + ChunkHalfSize, centerPosition))  //  order by distance to camera
                .Take(count)                                                                                //  take the specified number and calculate chunk data
                .Select(chunk =>
                {
                    var aabb = (chunk.Position, chunk.Position + ChunkSize);
                    var vb = chunk.GetVisibleBlocks();
                    return (chunk, aabb, vb);
                })
                .ToArray();

        return closest;
    }
}