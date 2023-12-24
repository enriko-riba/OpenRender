using OpenRender;
using OpenTK.Mathematics;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpyroGame.World;

internal class VoxelWorld
{
    public const int Size = 10;
    public const int GroundBaseLevel = (int)(Size * 0.75f);
    public static readonly Vector3i ChunkHalfSize = new(Chunk.Size / 2);
    public static readonly Vector3i ChunkSize = new(Chunk.Size);

    //  key is chunk world position, value is the chunk   
    internal readonly Dictionary<Vector3i, Chunk> chunks = [];

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
                Diffuse = new Vector3(1, 1, 1),
                Emissive = new Vector3(0.0f, 0.0f, 0.0f),
                Specular = new Vector3(0.5f, 0.5f, 0.5f),
                Shininess = 0.15f
            }
        },
        { (int)BlockType.Dirt, new VoxelMaterial() {
                Diffuse = new Vector3(1, 1, 1),
                Emissive = new Vector3(0.0f, 0.0f, 0.0f),
                Specular = new Vector3(0.0f, 0.0f, 0.0f),
                Shininess = 0.0f
            }
        },
        { (int)BlockType.Grass, new VoxelMaterial() {
                Diffuse = new Vector3(1, 1, 1),
                Emissive = new Vector3(0.0f, 0.0f, 0.0f),
                Specular = new Vector3(0.3f, 0.5f, 0.3f),
                Shininess = 0.5f
            }
        }
    };

    //  key is texture name, value texture handle index 
    internal readonly Dictionary<BlockType, string> textures = new() {
        { BlockType.None, "Resources/voxel/box-unwrap.png" },
        { BlockType.Rock, "Resources/voxel/rock.png" },
        { BlockType.Dirt, "Resources/voxel/dirt.png" },
        { BlockType.Grass, "Resources/voxel/grass.png" },
    };
    internal Stopwatch stopwatch = Stopwatch.StartNew();

    public VoxelWorld()
    {
        // create chunks
        for (var z = 0; z < Size; z++)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var chunk = new Chunk()
                    {
                        Position = new Vector3i(x * Chunk.Size, y * Chunk.Size, z * Chunk.Size),
                    };
                    var index = z * Size * Size + y * Size + x;
                    chunk.Initialize(this, index, y > GroundBaseLevel);
                    chunks.Add(chunk.Position, chunk);
                }
            }
        }
        Log.Highlight($"VoxelWorld created {chunks.Count} chunks in {stopwatch.ElapsedMilliseconds} ms");
    }

    public Chunk this[int index] => chunks.Values.ElementAt(index);
       
    public bool GetChunkByWorldPosition(Vector3i worldPosition, out Chunk chunk) => chunks.TryGetValue(worldPosition, out chunk);

    /// <summary>
    /// Selects the closest chunks to the given position, calculates visible blocks per chunk and chunks aabb.
    /// </summary>
    /// <param name="centerPosition"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    public (Chunk, AABB, IEnumerable<BlockState>)[] GetImmediate(Vector3 centerPosition, int count)
    {       
        var closest = chunks.Values
                .OrderBy(x => Vector3.DistanceSquared(x.Position + ChunkHalfSize, centerPosition))
                .Take(count)
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

/// <summary>
/// Exposes static methods for voxel world operations, mainly focused on calculating positions and indices.
/// </summary>
public static class VoxelHelper
{

}

/// <summary>
/// Streams visible chunks blocks based on a priority queue.
/// </summary>
internal class VoxelStreamer
{
    private readonly ConcurrentQueue<int> priorityIndices = [];

    public void EnqueueChunkIndex(int index) => priorityIndices.Enqueue(index);
}
