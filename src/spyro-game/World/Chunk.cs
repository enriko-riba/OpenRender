using OpenRender;
using OpenTK.Mathematics;

namespace SpyroGame.World;

public class Chunk(VoxelWorld world, int index)
{
    private bool isInitialized;
    private bool isProcessed;
    private Vector2i chunkPosition;
    private readonly int[,] maxHeights = new int[VoxelHelper.ChunkSideSize, VoxelHelper.ChunkSideSize];

    private readonly Dictionary<int, BlockState> changedBlocks = [];

    #region Initialization
    public void Initialize(TerrainBuilder terrainBuilder)
    {
        if (isInitialized) return;

        chunkPosition = new(index % VoxelHelper.WorldChunksXZ, index / VoxelHelper.WorldChunksXZ);

        Blocks = new BlockState[VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize];

        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                maxHeights[x, z] = terrainBuilder.GetHeightNormalizedChunkLocal(index, x, z);
            }
        }

        for (var y = VoxelHelper.MaxBlockPositionY; y >= 0; y--)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    var blockIdx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
                    var block = new BlockState(blockIdx, this)
                    {
                        BlockType = terrainBuilder.GenerateChunkBlockType(maxHeights[x, z], x, y, z),
                    };
                    Blocks[block.Index] = block;
                }
            }
        }
        isInitialized = true;
    }

    /// <summary>
    /// Calculates visible blocks in the chunk and sets the <see cref="BlockState.IsVisible"/> property for each block.
    /// </summary>
    public void CalcVisibleBlocks(bool force = false)
    {
        if (!force && isProcessed) return;

        for (var y = VoxelHelper.MaxBlockPositionY; y >= 0; y--)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
                {
                    var idx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
                    var block = Blocks[idx];
                    if (block.BlockType != BlockType.None)
                    {
                        var visibleFormOutside = IsExternallyVisible(x, y, z);
                        block.IsVisible = visibleFormOutside;
                        if (block.IsVisible && y > MaxHeight)
                        {
                            MaxHeight = y;
                        }
                    }
                    Blocks[idx] = block;
                }
            }
        }
        isProcessed = true;
    }

    /// <summary>
    /// Calculates ambient occlusion.
    /// </summary>
    public void CalcAO()
    {
        for (var y = VoxelHelper.MaxBlockPositionY; y >= 0; y--)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
                {
                    var idx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
                    var block = Blocks[idx];

                    //  no AO for transparent or top most or underwater blocks
                    if (!block.IsTransparent && block.LocalPosition.Y > VoxelHelper.WaterLevel && block.LocalPosition.Y < MaxHeight)
                    {
                        var worldPosition = Position + block.LocalPosition;

                        //  neighbor blocks one layer above
                        var factor = 0;
                        var west = world.GetBlockByPositionGlobalSafe(worldPosition.X - 1, worldPosition.Y + 1, worldPosition.Z);
                        var northWest = world.GetBlockByPositionGlobalSafe(worldPosition.X - 1, worldPosition.Y + 1, worldPosition.Z - 1);
                        var north = world.GetBlockByPositionGlobalSafe(worldPosition.X, worldPosition.Y + 1, worldPosition.Z - 1);
                        var northEast = world.GetBlockByPositionGlobalSafe(worldPosition.X + 1, worldPosition.Y + 1, worldPosition.Z - 1);
                        var east = world.GetBlockByPositionGlobalSafe(worldPosition.X + 1, worldPosition.Y + 1, worldPosition.Z);
                        var southEast = world.GetBlockByPositionGlobalSafe(worldPosition.X + 1, worldPosition.Y + 1, worldPosition.Z + 1);
                        var south = world.GetBlockByPositionGlobalSafe(worldPosition.X, worldPosition.Y + 1, worldPosition.Z + 1);
                        var southWest = world.GetBlockByPositionGlobalSafe(worldPosition.X - 1, worldPosition.Y + 1, worldPosition.Z + 1);

                        //  TODO: this was one of the stupidest ideas ever, it's not working, it's not even close to working
                        //        - it's not taking into account blocks from neighboring chunks
                        //        - it's applied per block instead per vertex or at least per face
                        factor += west != null ? 5 : 0;
                        factor += northWest != null ? 1 : 0;
                        factor += north != null ? 5 : 0;
                        factor += northEast != null ? 1 : 0;
                        factor += east != null ? 5 : 0;
                        factor += southEast != null ? 1 : 0;
                        factor += south != null ? 5 : 0;
                        factor += southWest != null ? 1 : 0;
                        var r = (byte)(factor);
                        block.Reserved1 = r;
                        //Log.Debug($"{block}, reserved {r}, b2:{(r & 0xff00) >> 8}, ao: {(r / 8.0) * 0.02}");
                        Blocks[idx] = block;
                    }
                }
            }
        }
    }

    public void LoadChangedBlocks(IEnumerable<BlockState> blocks)
    {
        changedBlocks.Clear();
        foreach (var block in blocks)
        {
            Blocks[block.Index] = block;
            changedBlocks[block.Index] = block;
        }
    }
    #endregion

    /// <summary>
    /// Returns the changed blocks since the last call to <see cref="LoadChangedBlocks"/>.
    /// Changed blocks include deleted and added blocks.
    /// </summary>
    public Dictionary<int, BlockState> ChangedBlocks => changedBlocks;

    /// <summary>
    /// Returns the height of the top face of the highest block.
    /// </summary>
    /// <param name="x"></param>
    /// <param name="z"></param>
    /// <returns></returns>
    public int GetTerrainHeightAt(int x, int z) => maxHeights[x, z] + 1;

    public Vector2i ChunkPosition => chunkPosition;

    public bool IsProcessed => isProcessed;

    public bool IsInitialized => isInitialized;

    public AABB Aabb { get; internal set; }

    public BlockState[] Blocks { get; private set; } = default!;

    internal void UpdateBlock(ref BlockState block)
    {
        Blocks[block.Index] = block;
        changedBlocks[block.Index] = block;
        IsDirty = true;
    }

    /// <summary>
    /// Returns true if the chunk has been modified since last save.
    /// </summary>
    internal bool IsDirty { get; set; }

    public int Index => index;

    /// <summary>
    /// Max height is in number of blocks from the bottom of the chunk.
    /// Note that the height in world units is MaxHeight + 1
    /// </summary>
    public int MaxHeight { get; internal set; }

    /// <summary>
    /// Bottom left chunk corner position in the world.
    /// </summary>
    public Vector3i Position => Aabb.Min;

    #region Rendering data
    public volatile uint BlocksSSBO;
    public int SolidCount;
    public volatile uint TransparentBlocksSSBO;
    public int TransparentCount;
    public bool Visible;

    public IEnumerable<BlockState> VisibleBlocks => Blocks.Where(x => x.IsVisible && !x.IsTransparent);

    public IEnumerable<BlockState> TransparentBlocks => Blocks.Where(x => x.IsVisible && x.IsTransparent);

    internal ChunkState State { get; set; }
    #endregion


    //public BlockState GetBlockAtGlobalXZ(Vector3 position)
    //{
    //    //  convert to chunk local position
    //    var localPosition = position - Position;

    //    var startY = world.terrainBuilder.GetHeightNormalizedGlobal((int)localPosition.X, (int)localPosition.Z);
    //    var index = (int)position.X + (int)position.Z * VoxelHelper.ChunkSideSize + (int)Math.Ceiling(startY) * VoxelHelper.ChunkSideSizeSquare;
    //    do
    //    {
    //        var block = Blocks[index];
    //        if (!block.IsAir)
    //            return block;
    //        index -= VoxelHelper.ChunkSideSizeSquare;
    //    } while (index >= 0);
    //    return default;
    //}

    public BlockState GetBlockAtLocalPosition(Vector3 localPosition)
    {
        if (!isProcessed) return default;
        var x = (int)localPosition.X;
        var z = (int)localPosition.Z;
        var y = (int)localPosition.Y;
        var index = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
        var b = Blocks[index];
        return b;
    }

    //public BlockState GetTopBlockAtLocalXZ(Vector3 localPosition)
    //{
    //    if (!isProcessed) return default;
    //    var x = (int)localPosition.X;
    //    var z = (int)localPosition.Z;
    //    var y = maxHeights[x, z];
    //    var index = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
    //    var b = Blocks[index];
    //    return b;
    //}

    public override string ToString() => $"{chunkPosition} ({State})";

    private bool IsExternallyVisible(int x, int y, int z)
    {
        //----------------------------------------------------------------------
        // Check if any neighboring block is destroyed, transparent or none
        //----------------------------------------------------------------------

        //  y - 1 is the most common case, so check it first
        if ((y > 0) && (y < VoxelHelper.MaxBlockPositionY) && (IsBlockTransparent(x, y + 1, z) || IsBlockTransparent(x, y - 1, z))) return true;

        if ((x > 0) && (x < VoxelHelper.ChunkSizeXZMinusOne) && (IsBlockTransparent(x - 1, y, z) || IsBlockTransparent(x + 1, y, z))) return true;
        if ((z > 0) && (z < VoxelHelper.ChunkSizeXZMinusOne) && (IsBlockTransparent(x, y, z - 1) || IsBlockTransparent(x, y, z + 1))) return true;

        // Check if the block is at the chunk boundary
        if (x == 0 || y == 0 || z == 0 || x == VoxelHelper.ChunkSizeXZMinusOne || y == VoxelHelper.MaxBlockPositionY || z == VoxelHelper.ChunkSizeXZMinusOne)
        {
            // make blocks on world edge visible except the bottom block layer
            // TODO: fix this logic, it introduces more visible blocks then needed. Only top most, non air, world edge blocks should be visible.
            var worldPosition = Position + new Vector3i(x, y, z);
            var isWorldEdge = VoxelHelper.IsGlobalPositionOnWorldBoundary(worldPosition.X, worldPosition.Y, worldPosition.Z);
            if (isWorldEdge)
                return worldPosition.Y > 0;

            var isAdjacentBlockTransparent =
                (IsAdjacentChunkBlockTransparent(worldPosition.X - 1, worldPosition.Y, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X + 1, worldPosition.Y, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y - 1, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y + 1, worldPosition.Z)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z - 1)) ||
                (IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z + 1));
            return isAdjacentBlockTransparent;
        }

        return false;
    }

    /// <summary>
    /// Based on the given blocks world position, checks if the adjacent chunk block is transparent.
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="z"></param>
    /// <returns></returns>
    private bool IsAdjacentChunkBlockTransparent(int x, int y, int z)
    {
        // Find the world position of the neighboring block
        var blockWorldPosition = new Vector3i(x, y, z);

        // Get the chunk index of the adjacent chunk
        var adjacentChunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(blockWorldPosition);

        var chunkWorldPosition = VoxelHelper.GetChunkPositionGlobal(adjacentChunkIndex);

        // Get the block local position in its owner chunk
        var (cx, cy, cz) = blockWorldPosition - chunkWorldPosition;

        if (adjacentChunkIndex is >= 0 and < VoxelHelper.TotalChunks)
        {
            // Retrieve the adjacent chunk using the index
            var adjacentChunk = world[adjacentChunkIndex];

            //  if the chunk has been added above the Blocks is null
            if (adjacentChunk?.Blocks is not null)
            {
                return adjacentChunk.IsBlockTransparent(cx, cy, cz);
            }
            else
            {
                var blockType = world.terrainBuilder.GenerateChunkBlockLocal(adjacentChunkIndex, cx, cy, cz);
                return blockType is BlockType.WaterLevel or BlockType.None;
            }
        }

        // The block is outside the world boundaries
        return false;
    }

    private bool IsBlockAir(int x, int y, int z) => Blocks[x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare].IsAir;

    private bool IsBlockTransparent(int x, int y, int z) => Blocks[x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare].IsTransparent;

}

public enum ChunkState
{
    /// <summary>
    /// The chunk has been loaded (and fully initialized).
    /// </summary>
    Loaded,

    /// <summary>
    /// The chunk has been added to the chunk renderer, OpenGL buffers created.
    /// </summary>
    Added,

    /// <summary>
    /// The chunk has been marked for removal but needs to be sent to chunk renderer for OpenGL cleanup.
    /// </summary>
    ToBeRemoved,

    /// <summary>
    /// The chunk has been removed from the chunk renderer, OpenGL resources are freed and the chunk can be safely destroyed.
    /// </summary>
    SafeToRemove,
}