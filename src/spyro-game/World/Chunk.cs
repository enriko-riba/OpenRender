using OpenTK.Mathematics;
using System.Diagnostics;

namespace SpyroGame.World;

public class Chunk(VoxelWorld world, int index)
{
    private bool isInitialized;
    private bool isProcessed;
    private readonly int[,] maxHeights = new int[VoxelHelper.ChunkSideSize, VoxelHelper.ChunkSideSize];

    private readonly Dictionary<int, BlockState> changedBlocks = [];

    #region Initialization
    public void Initialize(TerrainBuilder terrainBuilder, bool calculateBlockType)
    {
        if (isInitialized) return;

        Blocks = new BlockState[VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize];

        for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                maxHeights[x, z] = terrainBuilder.GetHeightNormalizedChunkLocal(index, x, z);
            }
        }
        Parallel.For(0, VoxelHelper.ChunkSideSize, x =>
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                var h = maxHeights[x, z];
                for (var y = 0; y <= VoxelHelper.MaxBlockPositionY; y++)
                {
                    var i = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
                    var block = new BlockState(i, this);
                    if(calculateBlockType)
                    {
                        block.BlockType = TerrainBuilder.GenerateChunkBlockType(h, x, y, z);
                    };
                    Blocks[i] = block;
                }
            }
        });
        isInitialized = true;
    }

    /// <summary>
    /// Calculates visible blocks in the chunk and sets the <see cref="BlockState.IsVisible"/> property for each block.
    /// </summary>
    public void CalcVisibleBlocks(bool force = false)
    {
        if (!force && isProcessed) return;

        Parallel.For(0, VoxelHelper.ChunkSideSize, x =>
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                var h = maxHeights[x, z];
                var yMin = Math.Max(0, h - 4);
                var yMax = Math.Min(VoxelHelper.MaxBlockPositionY, h + 1);

                for (var y = yMin; y <= yMax; y++)
                {
                    var idx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
                    var block = Blocks[idx];
                    if (block.BlockType != BlockType.None)
                        block.IsVisible = IsExternallyVisible(x, y, z);
                    Blocks[idx] = block;
                }
            }
        });
        isProcessed = true;
    }
    #endregion

    /// <summary>
    /// Returns the changed blocks since the chunk generation.
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

    public Vector2i ChunkPosition { get; internal set; }

    public bool IsProcessed => isProcessed;

    public bool IsInitialized => isInitialized;

    public AABB Aabb { get; internal set; }

    public BlockState[] Blocks { get; internal set; } = default!;

    internal void UpdateBlock(ref BlockState block, bool addToChangedBlocks = false)
    {
        Blocks[block.Index] = block;
        if ((addToChangedBlocks))
        {
            changedBlocks[block.Index] = block;
            IsDirty = true;
        }
    }

    /// <summary>
    /// Returns true if the chunk has been modified since last save.
    /// </summary>
    internal bool IsDirty { get; set; }

    public int Index => index;

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

    public override string ToString() => $"{ChunkPosition} ({State})";

    private bool IsExternallyVisible(int x, int y, int z)
    {
        //----------------------------------------------------------------------
        // Check if any neighboring block is destroyed, water level or none
        //----------------------------------------------------------------------

        //  y - 1 is the most common case, so check it first
        if ((y > 0) && (y < VoxelHelper.MaxBlockPositionY) && (IsBlockTransparent(x, y + 1, z) || IsBlockTransparent(x, y - 1, z))) return true;
        if ((x > 0) && (x < VoxelHelper.ChunkSizeXZMinusOne) && (IsBlockTransparent(x - 1, y, z) || IsBlockTransparent(x + 1, y, z))) return true;
        if ((z > 0) && (z < VoxelHelper.ChunkSizeXZMinusOne) && (IsBlockTransparent(x, y, z - 1) || IsBlockTransparent(x, y, z + 1))) return true;

        // Check if the block is at the chunk boundary
        if (x == 0 || y == 0 || z == 0 || x == VoxelHelper.ChunkSizeXZMinusOne || y == VoxelHelper.MaxBlockPositionY || z == VoxelHelper.ChunkSizeXZMinusOne)
        {
            // make blocks on world edge visible except the bottom block layer
            var worldPosition = Position + new Vector3i(x, y, z);
            var isWorldEdge = VoxelHelper.IsGlobalPositionOnWorldBoundary(worldPosition.X, worldPosition.Y, worldPosition.Z);
            if (isWorldEdge)
            {
                // check outward directions only
                var outwardTransparent =
                    (x == 0 && IsAdjacentChunkBlockTransparent(worldPosition.X - 1, worldPosition.Y, worldPosition.Z)) ||
                    (x == VoxelHelper.ChunkSizeXZMinusOne && IsAdjacentChunkBlockTransparent(worldPosition.X + 1, worldPosition.Y, worldPosition.Z)) ||
                    (z == 0 && IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z - 1)) ||
                    (z == VoxelHelper.ChunkSizeXZMinusOne && IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z + 1)) ||
                    (y == 0 && IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y - 1, worldPosition.Z)) ||
                    (y == VoxelHelper.MaxBlockPositionY && IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y + 1, worldPosition.Z));
                return outwardTransparent;
            }

            var isAdjacentBlockTransparent = false;
            if (worldPosition.X > 0)
                isAdjacentBlockTransparent |= IsAdjacentChunkBlockTransparent(worldPosition.X - 1, worldPosition.Y, worldPosition.Z);
            if (worldPosition.X < VoxelHelper.MaxBlockPositionXZ)
                isAdjacentBlockTransparent |= IsAdjacentChunkBlockTransparent(worldPosition.X + 1, worldPosition.Y, worldPosition.Z);

            if (worldPosition.Y > 0)
                isAdjacentBlockTransparent |= IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y - 1, worldPosition.Z);
            if (worldPosition.Y < VoxelHelper.MaxBlockPositionY)
                isAdjacentBlockTransparent |= IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y + 1, worldPosition.Z);

            if (worldPosition.Z > 0)
                isAdjacentBlockTransparent |= IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z - 1);
            if (worldPosition.Z < VoxelHelper.MaxBlockPositionXZ)
                isAdjacentBlockTransparent |= IsAdjacentChunkBlockTransparent(worldPosition.X, worldPosition.Y, worldPosition.Z + 1);

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
        if (x < 0 || x > VoxelHelper.MaxBlockPositionXZ ||
            z < 0 || z > VoxelHelper.MaxBlockPositionXZ ||
            y > VoxelHelper.MaxBlockPositionY)
        {
            return true;   // outward faces (sides/top) expose geometry
        }
        if (y < 0)
        {
            return false;  // keep bottom sealed
        }

        // Get the chunk index of the adjacent chunk
        var adjacentChunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(blockWorldPosition);

        var chunkWorldPosition = VoxelHelper.GetChunkPositionGlobal(adjacentChunkIndex);

        // Get the block local position in its owner chunk
        var (cx, cy, cz) = blockWorldPosition - chunkWorldPosition;
        if (cx < 0 || cx >= VoxelHelper.ChunkSideSize ||
            cz < 0 || cz >= VoxelHelper.ChunkSideSize ||
            cy < 0 || cy > VoxelHelper.MaxBlockPositionY)
        {
            // outside valid local range → treat as non-transparent to avoid OOB
            return false;
        }

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

    /// <summary>
    /// Returns true if the block is None or WaterLevel.
    /// </summary>
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