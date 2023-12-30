using OpenTK.Mathematics;

namespace SpyroGame.World;

internal struct Chunk
{
    private const bool WorldBorderBlocksVisible = true;


    private int index;
    private VoxelWorld world;
    public bool IsEmpty;


    public AABB Aabb { get; internal set; }

    public BlockState[] Blocks { get; private set; }

    public readonly int Index => index;

    /// <summary>
    /// Bottom left chunk corner position in the world.
    /// </summary>
    public readonly Vector3i Position => Aabb.min;

    public float[] HeightData { get; private set; }

    public readonly float GetHeightNormalized(int x, int z)
    {
        var noiseIndex = x + z * VoxelHelper.ChunkSideSize;
        var height = HeightData[noiseIndex];
        height = (height + 1f) / 2f;
        return height;
    }

    public void Initialize(VoxelWorld world, int index, int seed)
    {
        this.world = world;
        this.index = index;

        //var worldX = index % VoxelHelper.WorldChunksXZ;
        //var worldZ = index / VoxelHelper.WorldSizeXZSquared;
        var worldY = (index / VoxelHelper.WorldChunksXZ) % VoxelHelper.WorldChunksXZ;

        HeightData = VoxelHelper.CalcChunkHeightGrid(index, seed);

        Blocks = new BlockState[VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize];
        var nonEmptyCount = 0;
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            for (var y = 0; y < VoxelHelper.ChunkSideSize; y++)
            {
                for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
                {
                    var height = GetHeightNormalized(x, z) * VoxelHelper.HeightAmplitude;
                    var blockAltitude = worldY * VoxelHelper.ChunkSideSize + y;

                    BlockType bt;
                    if (blockAltitude <= height + 1 && blockAltitude >= height)
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
                        //  replace air with water
                        if (bt == BlockType.None)
                        {
                            bt = BlockType.Water;
                        }

                        if ((bt != BlockType.None) && (bt != BlockType.Water) && (blockAltitude >= VoxelHelper.WaterLevel-1))
                        {
                            bt = BlockType.Sand;
                        }
                        else if ((bt != BlockType.None) && (bt != BlockType.Water) && (blockAltitude < VoxelHelper.WaterLevel - 1))
                        {
                            //  replace top layer underwater solid blocks with bedrock
                            bt = BlockType.Rock;
                        }
                    }
                    var blockIdx = x + y * VoxelHelper.ChunkSideSize + z * VoxelHelper.ChunkSideSizeSquare;
                    var block = new BlockState
                    {
                        Index = blockIdx,
                        BlockType = bt,
                    };
                    Blocks[blockIdx] = block;
                    nonEmptyCount += bt == BlockType.None ? 0 : 1;
                }
            }
        }

        IsEmpty = nonEmptyCount == 0;
    }

    public readonly IEnumerable<BlockState> GetVisibleBlocks()
    {
        if (IsEmpty) return [];

        List<BlockState> visibleBlocks = [];
        for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
        {
            for (var y = 0; y < VoxelHelper.ChunkSideSize; y++)
            {
                for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
                {
                    var idx = x + y * VoxelHelper.ChunkSideSize + z * VoxelHelper.ChunkSideSizeSquare;
                    var block = Blocks[idx];

                    if (!block.IsAir && IsExternallyVisible(x, y, z))
                    {
                        visibleBlocks.Add(block);
                    }
                }
            }
        }
        //return Blocks;
        return visibleBlocks;
    }

    private readonly bool IsExternallyVisible(int x, int y, int z)
    {
        // Check if the block is at the chunk boundary
        if (x == 0 || y == 0 || z == 0 || x == VoxelHelper.ChunkSizeMinusOne || y == VoxelHelper.ChunkSizeMinusOne || z == VoxelHelper.ChunkSizeMinusOne)
        {
            var worldPosition = Position + new Vector3i(x, y, z);
            if (VoxelHelper.IsGlobalPositionOnWorldBoundary(worldPosition.X, worldPosition.Y, worldPosition.Z)) return WorldBorderBlocksVisible;

            var isAdjacentAir =
                (IsAdjacentChunkBlockTransparent(worldPosition.X - 1, worldPosition.Y, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X + 1, worldPosition.Y, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y - 1, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y + 1, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z - 1)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z + 1));
            return isAdjacentAir;
        }

        // Check if any neighboring block is destroyed
        return 
            IsBlockTransparent(x - 1, y, z) || IsBlockTransparent(x + 1, y, z) ||
            IsBlockTransparent(x, y - 1, z) || IsBlockTransparent(x, y + 1, z) ||
            IsBlockTransparent(x, y, z - 1) || IsBlockTransparent(x, y, z + 1);
    }

    /// <summary>
    /// Based on the given block world position, checks if the adjacent chunk block is transparent.
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="z"></param>
    /// <returns></returns>
    private readonly bool IsAdjacentChunkBlockTransparent(int x, int y, int z)
    {
        // Find the world position of the neighboring block
        var blockWorldPosition = new Vector3i(x, y, z);

        // Get the chunk index of the adjacent chunk
        var adjacentChunkIndex = VoxelHelper.GetChunkIndexFromGlobalPosition(blockWorldPosition);

        if (adjacentChunkIndex is >= 0 and < VoxelHelper.TotalChunks)
        {
            // Retrieve the adjacent chunk using the index
            var adjacentChunk = world[adjacentChunkIndex];

            // Get the block local position in its owner chunk
            var (cx, cy, cz) = blockWorldPosition - adjacentChunk.Position;

            return adjacentChunk.IsBlockTransparent(cx, cy, cz);
        }

        // The block is outside the world boundaries
        return true;
    }

    internal readonly bool IsBlockAir(int x, int y, int z) => Blocks[x + y * VoxelHelper.ChunkSideSize + z * VoxelHelper.ChunkSideSizeSquare].IsAir;

    internal readonly bool IsBlockTransparent(int x, int y, int z) => Blocks[x + y * VoxelHelper.ChunkSideSize + z * VoxelHelper.ChunkSideSizeSquare].IsTransparent;

}
