using OpenTK.Mathematics;

namespace SpyroGame.World;

internal struct Chunk
{
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
        height = height * VoxelHelper.HeightAmplitude + VoxelHelper.WaterLevel;
        return height;
    }

    public void Initialize(VoxelWorld world, int index, int seed)
    {
        this.world = world;
        this.index = index;

        HeightData = VoxelHelper.CalcTerrainData(index, seed);

        Blocks = new BlockState[VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize];
        var nonEmptyCount = 0;

        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    var height = GetHeightNormalized(x, z);
                    var blockAltitude = y;

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

                        if ((bt != BlockType.None) && (bt != BlockType.Water) && (blockAltitude >= VoxelHelper.WaterLevel - 1))
                        {
                            bt = BlockType.Sand;
                        }
                        else if ((bt != BlockType.None) && (bt != BlockType.Water) && (blockAltitude < VoxelHelper.WaterLevel - 1))
                        {
                            //  replace top layer underwater solid blocks with bedrock
                            bt = BlockType.Rock;
                        }
                    }
                    var blockIdx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
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
        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
                {
                    var idx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
                    var block = Blocks[idx];

                    if (!block.IsAir && IsExternallyVisible(x, y, z))
                    {
                        visibleBlocks.Add(block);
                    }
                }
            }
        }
        //return Blocks;
        var transparentBlocks = visibleBlocks.Where(b => b.IsTransparent).ToArray();
        return visibleBlocks;
    }

    private readonly bool IsExternallyVisible(int x, int y, int z)
    {
        // Check if the block is at the chunk boundary
        if (x == 0 || y == 0 || z == 0 || x == VoxelHelper.ChunkSizeXZMinusOne || y == VoxelHelper.ChunkYSize - 1 || z == VoxelHelper.ChunkSizeXZMinusOne)
        {
            var worldPosition = Position + new Vector3i(x, y, z);
            if (VoxelHelper.IsGlobalPositionOnWorldBoundary(worldPosition.X, worldPosition.Y, worldPosition.Z)) return false;

            var isAdjacentBlockTransparent =
                (IsAdjacentChunkBlockTransparent(worldPosition.X - 1, worldPosition.Y, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X + 1, worldPosition.Y, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y - 1, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y + 1, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z - 1)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z + 1));
            return isAdjacentBlockTransparent;
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
        return false;
    }

    internal readonly bool IsBlockAir(int x, int y, int z) => Blocks[x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare].IsAir;

    internal readonly bool IsBlockTransparent(int x, int y, int z) => Blocks[x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare].IsTransparent;

    public override readonly string ToString() => $"{Index}@{Aabb}";

    public BlockState GetBlockAtGlobalXZ(Vector3 position)
    {        
        //  convert to chunk local position
        var localPosition = position - Position;

        var startY = GetHeightNormalized((int)localPosition.X, (int)localPosition.Z);
        var index = (int)position.X + (int)position.Z * VoxelHelper.ChunkSideSize + (int)Math.Ceiling(startY) * VoxelHelper.ChunkSideSizeSquare;
        do
        {
            var block = Blocks[index];
            if (!block.IsAir) 
                return block;
            index -= VoxelHelper.ChunkSideSizeSquare;
        } while (index >= 0);
        return default;
    }

    public BlockState GetBlockAtLocalXZ(Vector3 localPosition)
    {
        var x = (int)MathF.Round(localPosition.X);
        var z = (int)MathF.Round(localPosition.Z);
        var startY = GetHeightNormalized(x, z) + 1;
        var index = x + z * VoxelHelper.ChunkSideSize + (int)Math.Ceiling(startY) * VoxelHelper.ChunkSideSizeSquare;
        do
        {
            var block = Blocks[index];
            if (!block.IsAir)
                return block;
            index -= VoxelHelper.ChunkSideSizeSquare;
        } while (index >= 0);
        return default;
    }
}
