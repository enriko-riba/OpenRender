using OpenTK.Mathematics;

namespace SpyroGame.World;

internal struct Chunk
{
    /// <summary>
    /// Size of the chunk: 32 x 32 x 32 blocks.
    /// </summary>
    internal const int Size = 32;
    
    private int index;
    private AABB aabb;
    private VoxelWorld world;

    /// <summary>
    /// Bottom left chunk corner position in the world.
    /// </summary>
    public Vector3i Position { get; set; }

    public BlockState[] Blocks { get; private set; }
    
    public readonly int Index => index;
    public readonly AABB AABB => aabb;


    public void Initialize(VoxelWorld world, int index, bool? isEmpty = false)
    {
        this.world = world;
        this.index = index;
        aabb = (Position, Position + VoxelWorld.ChunkSize);
        Blocks = new BlockState[Size * Size * Size];       

        for (var x = 0; x < Size; x++)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var z = 0; z < Size; z++)
                {
                    var block = new BlockState
                    {
                        Index = x + y * Size + z * ChunkSizeSquare,
                        BlockType = BlockType.Rock,
                        IsDestroyed = isEmpty ?? false
                    };
                    Blocks[x + y * Size + z * ChunkSizeSquare] = block;
                }
            }
        }
    }

    public readonly IEnumerable<BlockState> GetVisibleBlocks()
    {
        List<BlockState> visibleBlocks = [];

        for (var x = 0; x < Size; x++)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var z = 0; z < Size; z++)
                {
                    var index = x + y * Size + z * ChunkSizeSquare;
                    var block = Blocks[index];

                    if (!block.IsDestroyed && IsExternallyVisible(x, y, z))
                    {
                        visibleBlocks.Add(block);
                    }
                }
            }
        }

        return visibleBlocks;
    }

    private readonly bool IsExternallyVisible(int x, int y, int z)
    {
        // Check if the block is at the chunk boundary
        if (x == 0 || y == 0 || z == 0 || x == ChunkSizeMinusOne || y == ChunkSizeMinusOne || z == ChunkSizeMinusOne)
        {            
            if (IsWorldBoundary(x, y, z)) return true;

            //  not on world boundary so lets check if the block has destroyed neighboring blocks in the adjacent chunks

            // Check if any neighboring block in adjacent chunks is destroyed
            return
                (x == 0 && IsAdjacentChunkBlockDestroyed(x - 1, y, z)) ||
                (x == Size - 1 && IsAdjacentChunkBlockDestroyed(x + 1, y, z)) ||
                (y == 0 && IsAdjacentChunkBlockDestroyed(x, y - 1, z)) ||
                (y == Size - 1 && IsAdjacentChunkBlockDestroyed(x, y + 1, z)) ||
                (z == 0 && IsAdjacentChunkBlockDestroyed(x, y, z - 1)) ||
                (z == Size - 1 && IsAdjacentChunkBlockDestroyed(x, y, z + 1));
        }

        // Check if any neighboring block is destroyed
        return IsBlockDestroyed(x - 1, y, z) || IsBlockDestroyed(x + 1, y, z) ||
            IsBlockDestroyed(x, y - 1, z) || IsBlockDestroyed(x, y + 1, z) ||
            IsBlockDestroyed(x, y, z - 1) || IsBlockDestroyed(x, y, z + 1);
    }

    private readonly bool IsAdjacentChunkBlockDestroyed(int x, int y, int z)
    {
        // Find the world position of the neighboring block
        var blockWorldPosition = Position + new Vector3i(x, y, z);

        // Get the chunk index of the adjacent chunk
        var adjacentChunkIndex = GetChunkIndexFromBlockWorldPosition(blockWorldPosition);

        if (adjacentChunkIndex != -1)
        {
            // Retrieve the adjacent chunk using the index
            var adjacentChunk = world[adjacentChunkIndex];

            // Get the block local position in its owner chunk
            var (cx, cy, cz) = blockWorldPosition - adjacentChunk.Position;

            return adjacentChunk.IsBlockDestroyed((int)cx, (int)cy, (int)cz);

        }

        // The block is outside the world boundaries
        return true;
    }

    private readonly bool IsWorldBoundary(int x, int y, int z)
    {
        var cX = index % VoxelWorld.Size;
        var cY = (index / VoxelWorld.Size) % VoxelWorld.Size;
        var cZ = index / WorldSizeSquare;

        return (x == 0 && cX == 0) ||
            (y == 0 && cY == 0) ||
            (z == 0 && cZ == 0) ||
            (x == Size - 1 && cX == WorldSizeMinusOne) ||
            (y == Size - 1 && cY == WorldSizeMinusOne) ||
            (z == Size - 1 && cZ == WorldSizeMinusOne);
    }

    internal readonly bool IsBlockDestroyed(int x, int y, int z) => Blocks[x + y * Size + z * ChunkSizeSquare].IsDestroyed;

    private static int GetChunkIndexFromBlockWorldPosition(Vector3i position)
    {
        //  TODO: this is a hack, it is division by chunk size but is faster than division by 32
        var chunkX = position.X / Size;
        var chunkY = position.Y / Size;
        var chunkZ = position.Z / Size;
        return chunkZ * WorldSizeSquare + chunkY * VoxelWorld.Size + chunkX;
    }

    private const int WorldSizeSquare = VoxelWorld.Size * VoxelWorld.Size;
    private const int WorldSizeMinusOne = VoxelWorld.Size - 1;
    private const int ChunkSizeSquare = Size * Size;
    private const int ChunkSizeMinusOne = Size - 1;
}
