
using OpenTK.Mathematics;

namespace SpyroGame.World;

internal class Chunk
{
    private int index;
    private VoxelWorld world = default!;

    private bool isInitialized;
    private bool isProcessed;
    private Vector2i chunkPosition;

    public bool IsProcessed => isProcessed;
    public bool IsInitialized => isInitialized;

    public AABB Aabb { get; internal set; }

    public BlockState[] Blocks { get; private set; } = default!;

    public int Index => index;

    /// <summary>
    /// Bottom left chunk corner position in the world.
    /// </summary>
    public Vector3i Position => Aabb.min;
    public BlockState[] VisibleBlocks => Blocks.Where(x => x.IsVisible && !x.IsTransparent).ToArray();
    public BlockState[] TransparentBlocks => Blocks.Where(x => x.IsVisible && x.IsTransparent).ToArray();

    public void Initialize(VoxelWorld world, int index, int seed)
    {
        if (isInitialized) return;
        this.world = world;
        this.index = index;
        chunkPosition = new(index % VoxelHelper.WorldChunksXZ, index / VoxelHelper.WorldChunksXZ);

        //HeightData = VoxelHelper.CalcTerrainData(index, seed);

        Blocks = new BlockState[VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize];

        for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
        {
            for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
            {
                for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
                {
                    CreateBlock(x, y, z);
                }
            }
        }
        isInitialized = true;
    }


    public BlockState GetBlockAtGlobalXZ(Vector3 position)
    {
        //  convert to chunk local position
        var localPosition = position - Position;

        var startY = world.GetHeightNormalizedGlobal((int)localPosition.X, (int)localPosition.Z);
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

    public BlockState GetTopBlockAtLocalXZ(Vector3 localPosition)
    {
        if (!isProcessed) return default;
        var x = (int)MathF.Round(localPosition.X);
        var z = (int)MathF.Round(localPosition.Z);
        //var startY = world.GetHeightNormalized(x, z) + 1;
        var startY = world.GetHeightNormalizedChunkLocal(Index, x, z) + 1;
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

    /// <summary>
    /// Calculates visible blocks in the chunk and sets the <see cref="BlockState.IsVisible"/> property for each block.
    /// </summary>
    public void CalcVisibleBlocks()
    {
        if (isProcessed) return;
        for (var y = VoxelHelper.ChunkYSize - 1; y >= 0; y--)
        {
            for (var x = 0; x < VoxelHelper.ChunkSideSize; x++)
            {
                for (var z = 0; z < VoxelHelper.ChunkSideSize; z++)
                {
                    var idx = x + z * VoxelHelper.ChunkSideSize + y * VoxelHelper.ChunkSideSizeSquare;
                    var block = Blocks[idx];
                    block.IsVisible = !block.IsAir;
                    if (block.IsVisible)
                    {
                        var visibleFormOutside = IsExternallyVisible(x, y, z);
                        block.IsVisible = visibleFormOutside;
                    }
                    Blocks[idx] = block;
                }
            }
        }
        isProcessed = true;
    }

    public override string ToString() => $"{Index}@{chunkPosition}";

    internal ChunkState State { get; set; }

    private void CreateBlock(int x, int y, int z)
    {
        var block = world.GenerateBlockChunkLocal(index, x, y, z);
        Blocks[block.Index] = block;
    }

    private bool IsExternallyVisible(int x, int y, int z)
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
        var adjacentChunkIndex = VoxelHelper.GetChunkIndexFromGlobalPosition(blockWorldPosition);

        var chunkWorldPosition = VoxelHelper.GetChunkPositionGlobal(adjacentChunkIndex);

        // Get the block local position in its owner chunk
        var (cx, cy, cz) = blockWorldPosition - chunkWorldPosition;

        if (adjacentChunkIndex is >= 0 and < VoxelHelper.TotalChunks)
        {
            // Retrieve the adjacent chunk using the index
            var adjacentChunk = world[adjacentChunkIndex];
            /*
            //  workaround for chunk not loaded
            if (adjacentChunk == null)
            {
                //adjacentChunk = new Chunk()
                //{
                //    index = adjacentChunkIndex,
                //    chunkPosition = new(adjacentChunkIndex % VoxelHelper.WorldChunksXZ, adjacentChunkIndex / VoxelHelper.WorldChunksXZ),
                //    Aabb = (chunkWorldPosition, chunkWorldPosition + VoxelWorld.ChunkSize),
                //    //HeightData = VoxelHelper.CalcTerrainData(adjacentChunkIndex, world.Seed),
                //};
                //world.AddChunk(adjacentChunkIndex, adjacentChunk);

                //return false;   //  chunk not loaded yet, pretend there will be a solid block, therefore the return false
            }
            */

            //  if the chunk has been added above the Blocks is null
            if (adjacentChunk?.Blocks is not null)
            {
                return adjacentChunk.IsBlockTransparent(cx, cy, cz);
            }
            else
            {
                //  TODO: fix with proper world.GenerateBlock reference
                //return false;
                //  generate block on the fly
                var block = world.GenerateBlockChunkLocal(adjacentChunkIndex, cx, cy, cz);
                return block.IsTransparent;
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
    /// The chunk has been removed from the chunk renderer, OpenGL resources are freed and can be safely removed from the world.
    /// </summary>
    SafeToRemove,
}