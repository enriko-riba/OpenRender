using OpenTK.Mathematics;

namespace SpyroGame.World;

public class Chunk(VoxelWorld world, int index)
{
    private bool isInitialized;
    private bool isProcessed;
    private readonly int[,] maxHeights = new int[VoxelHelper.ChunkSideSize, VoxelHelper.ChunkSideSize];
    public ColumnInfo[] Columns { get; private set; } = new ColumnInfo[VoxelHelper.ChunkSideSizeSquare];

    private readonly Dictionary<int, BlockState> changedBlocks = [];

    #region Initialization
   

    public void ApplyGenerationData(TerrainBuilder terrainBuilder, TerrainBuilder.ChunkGenerationData generationData)
    {
        if (isInitialized) return;

        var blockTypes = generationData.BlockTypes;
        var columnHeights = generationData.ColumnHeights;
        var columnInfos = generationData.Columns;
        var blockAttributes = generationData.BlockAttributes;
        if (blockTypes.Length != VoxelHelper.ChunkSideSizeSquare * VoxelHelper.ChunkYSize)
            throw new ArgumentException("BlockTypes length mismatch", nameof(generationData));
        if (columnHeights.Length != VoxelHelper.ChunkSideSizeSquare)
            throw new ArgumentException("ColumnHeights length mismatch", nameof(generationData));
        if (columnInfos.Length != VoxelHelper.ChunkSideSizeSquare)
            throw new ArgumentException("ColumnInfos length mismatch", nameof(generationData));
        if (blockAttributes.Length != blockTypes.Length)
            throw new ArgumentException("BlockAttributes length mismatch", nameof(generationData));

        Blocks = new BlockState[VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize];
        var size = VoxelHelper.ChunkSideSize;
        var sizeSquare = VoxelHelper.ChunkSideSizeSquare;

        isProcessed = false;
        Columns = columnInfos;

        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                var columnIndex = x + z * size;
                var h = columnHeights[columnIndex];
                maxHeights[x, z] = h;

                for (var y = 0; y <= VoxelHelper.MaxBlockPositionY; y++)
                {
                    var i = x + z * size + y * sizeSquare;
                    var block = new BlockState(i, this)
                    {
                        BlockType = blockTypes[i],
                        IsVisible = false,
                        PackedAO = 0
                    };
                    Blocks[i] = block;
                }
            }
        }
        isInitialized = true;
    }
    

    /// <summary>
    /// Recomputes visibility and ambient occlusion for all blocks in the chunk.
    /// </summary>
    public void RecomputeLighting(bool force = false, bool includeNeighborData = false, bool bordersOnly = false)
    {
        if (!force && isProcessed) return;

        var size = VoxelHelper.ChunkSideSize;
        var area = VoxelHelper.ChunkSideSizeSquare;

        bool IsRenderable(BlockType type) => type != BlockType.None;
        bool Occludes(BlockType type) => type != BlockType.None && type != BlockType.WaterLevel;
        bool IsTransparentNeighbor(int x, int y, int z) => !Occludes(GetBlockType(x, y, z));
        BlockType GetBlockType(int x, int y, int z)
        {
            if (!includeNeighborData)
            {
                if (x < 0 || x >= size || z < 0 || z >= size)
                    return BlockType.Rock;
                if (y < 0)
                    return BlockType.Rock;
                if (y >= VoxelHelper.ChunkYSize)
                    return BlockType.None;
                return Blocks[x + z * size + y * area].BlockType;
            }
            return SampleBlockTypeWithNeighbors(x, y, z);
        }

        uint PackAo(uint east, uint west, uint up, uint down, uint south, uint north) =>
            (east & 0xFu)
            | ((west & 0xFu) << 4)
            | ((up & 0xFu) << 8)
            | ((down & 0xFu) << 12)
            | ((south & 0xFu) << 16)
            | ((north & 0xFu) << 20);

        uint FaceAoEast(int x, int y, int z)
        {
            uint count = 0;
            if (Occludes(GetBlockType(x + 1, y, z + 1))) count++;
            if (Occludes(GetBlockType(x + 1, y, z - 1))) count++;
            if (Occludes(GetBlockType(x + 1, y - 1, z))) count++;
            return Math.Min(count * 4u, 15u);
        }

        uint FaceAoWest(int x, int y, int z)
        {
            uint count = 0;
            if (Occludes(GetBlockType(x - 1, y, z + 1))) count++;
            if (Occludes(GetBlockType(x - 1, y, z - 1))) count++;
            if (Occludes(GetBlockType(x - 1, y - 1, z))) count++;
            return Math.Min(count * 4u, 15u);
        }

        uint FaceAoUp(int x, int y, int z)
        {
            uint count = 0;
            if (Occludes(GetBlockType(x + 1, y + 1, z)) || Occludes(GetBlockType(x + 1, y + 2, z))) count++;
            if (Occludes(GetBlockType(x - 1, y + 1, z)) || Occludes(GetBlockType(x - 1, y + 2, z))) count++;
            if (Occludes(GetBlockType(x, y + 1, z + 1)) || Occludes(GetBlockType(x, y + 2, z + 1))) count++;
            if (Occludes(GetBlockType(x, y + 1, z - 1)) || Occludes(GetBlockType(x, y + 2, z - 1))) count++;
            return Math.Min(count * 4u, 15u);
        }

        uint FaceAoDown(int x, int y, int z)
        {
            uint count = 0;
            if (Occludes(GetBlockType(x + 1, y - 1, z)) || Occludes(GetBlockType(x + 1, y - 2, z))) count++;
            if (Occludes(GetBlockType(x - 1, y - 1, z)) || Occludes(GetBlockType(x - 1, y - 2, z))) count++;
            if (Occludes(GetBlockType(x, y - 1, z + 1)) || Occludes(GetBlockType(x, y - 2, z + 1))) count++;
            if (Occludes(GetBlockType(x, y - 1, z - 1)) || Occludes(GetBlockType(x, y - 2, z - 1))) count++;
            return Math.Min(count * 4u, 15u);
        }

        uint FaceAoSouth(int x, int y, int z)
        {
            uint count = 0;
            if (Occludes(GetBlockType(x + 1, y, z + 1))) count++;
            if (Occludes(GetBlockType(x - 1, y, z + 1))) count++;
            if (Occludes(GetBlockType(x, y - 1, z + 1))) count++;
            return Math.Min(count * 4u, 15u);
        }

        uint FaceAoNorth(int x, int y, int z)
        {
            uint count = 0;
            if (Occludes(GetBlockType(x + 1, y, z - 1))) count++;
            if (Occludes(GetBlockType(x - 1, y, z - 1))) count++;
            if (Occludes(GetBlockType(x, y - 1, z - 1))) count++;
            return Math.Min(count * 4u, 15u);
        }

        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                if (bordersOnly && x > 0 && x < size - 1 && z > 0 && z < size - 1)
                {
                    continue;
                }

                for (var y = 0; y < VoxelHelper.ChunkYSize; y++)
                {
                    var idx = x + z * size + y * area;
                    ref var block = ref Blocks[idx];
                    if (!IsRenderable(block.BlockType))
                    {
                        block.IsVisible = false;
                        block.PackedAO = 0;
                        continue;
                    }

                    var eastTrans = IsTransparentNeighbor(x + 1, y, z);
                    var westTrans = IsTransparentNeighbor(x - 1, y, z);
                    var upTrans = IsTransparentNeighbor(x, y + 1, z);
                    var downTrans = IsTransparentNeighbor(x, y - 1, z);
                    var southTrans = IsTransparentNeighbor(x, y, z + 1);
                    var northTrans = IsTransparentNeighbor(x, y, z - 1);

                    var visible = eastTrans || westTrans || upTrans || downTrans || southTrans || northTrans;

                    if (y == 0 && block.BlockType == BlockType.BedRock)
                    {
                        visible = false;
                    }

                    uint packedAo = 0;
                    if (visible)
                    {
                        packedAo = PackAo(
                            FaceAoEast(x, y, z),
                            FaceAoWest(x, y, z),
                            FaceAoUp(x, y, z),
                            FaceAoDown(x, y, z),
                            FaceAoSouth(x, y, z),
                            FaceAoNorth(x, y, z));
                    }

                    block.IsVisible = visible;
                    block.PackedAO = packedAo;
                }
            }
        }

        isProcessed = true;
    }
    #endregion

    internal void ApplyBlockAttributes(uint[] blockAttributes)
    {
        if (Blocks is null || blockAttributes.Length == 0) return;

        var length = Math.Min(Blocks.Length, blockAttributes.Length);
        for (var i = 0; i < length; i++)
        {
            ref var block = ref Blocks[i];
            var attrib = blockAttributes[i];
            block.IsVisible = (attrib & 1u) != 0;
            block.PackedAO = attrib >> 1;
        }
        isProcessed = true;
    }

    private BlockType SampleBlockTypeWithNeighbors(int x, int y, int z)
    {
        var size = VoxelHelper.ChunkSideSize;
        var area = VoxelHelper.ChunkSideSizeSquare;

        if (y < 0)
        {
            return BlockType.BedRock;
        }
        if (y >= VoxelHelper.ChunkYSize)
        {
            return BlockType.None;
        }

        if (x >= 0 && x < size && z >= 0 && z < size)
        {
            return Blocks[x + z * size + y * area].BlockType;
        }

        var global = Position + new Vector3i(x, y, z);
        return world.GetBlockTypeGlobal(global);
    }

    public void RefreshBorderLighting()
    {
        if (!isInitialized || Blocks is null)
            return;

        // Only recompute borders using neighbor data to fix edge seams
        // without reprocessing the full chunk.
        RecomputeLighting(force: true, includeNeighborData: true, bordersOnly: true);
    }

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
        isProcessed = false;
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
    

    public void ApplyColumnHeightsForCollision(int[] columnHeights)
    {
        if (columnHeights == null || columnHeights.Length != VoxelHelper.ChunkSideSizeSquare) return;
        var size = VoxelHelper.ChunkSideSize;
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                var idx = x + z * size;
                maxHeights[x, z] = columnHeights[idx];
            }
        }
    }

        public Vector3i Position => Aabb.Min;
#region Rendering data
    public volatile uint BlocksSSBO;
    public int SolidCount;
    public int SolidCapacity;
    public volatile uint TransparentBlocksSSBO;
    public int TransparentCount;
    public int TransparentCapacity;
    public bool Visible;
    internal byte VisibleLinger;
    internal volatile bool PendingUpload;
    internal volatile bool PendingCompute;
    internal volatile bool ComputeInProgress;

    public IEnumerable<BlockState> VisibleBlocks => Blocks is null ? [] : Blocks.Where(x => x.IsVisible && !x.IsTransparent);
    
    public IEnumerable<BlockState> TransparentBlocks => Blocks is null ? [] : Blocks.Where(x => x.IsVisible && x.IsTransparent);

    internal ChunkState State { get; set; }

    // Indicates that GPU column heights were read back and applied
    public bool HasGpuColumns { get; internal set; }

    // GPU-provided collision spans per XZ column: up to MaxSpans pairs (yStart,yEnd) per column
    // Flattened pairs array: length = ChunkSideSizeSquare * MaxSpans * 2
    private int[]? columnSpanPairs;
    private byte[]? columnSpanCounts;
    public bool HasGpuSpans { get; private set; }

    internal void ApplyColumnSpansForCollision(int[] spansPairs, byte[] counts)
    {
        columnSpanPairs = spansPairs;
        columnSpanCounts = counts;
        HasGpuSpans = true;
    }

    internal bool IsSolidBySpans(int lx, int ly, int lz, int maxSpans)
    {
        if (!HasGpuSpans || columnSpanPairs is null || columnSpanCounts is null) return false;
        var size = VoxelHelper.ChunkSideSize;
        var col = lx + lz * size;
        var c = columnSpanCounts[col];
        if (c == 0) return false;
        var baseIdx = col * maxSpans * 2;
        for (int i = 0; i < c && i < maxSpans; i++)
        {
            var y0 = columnSpanPairs[baseIdx + i * 2 + 0];
            var y1 = columnSpanPairs[baseIdx + i * 2 + 1];
            if (ly >= y0 && ly < y1) return true;
        }
        return false;
    }

    /// <summary>
    /// Initializes CPU Blocks[] from GPU-provided column heights so CPU-side systems (picking/edit) have a coherent view.
    /// Uses the same material classification as the CPU generator.
    /// </summary>
    public void InitializeFromGpuColumns()
    {
        if (isInitialized) return;

        var size = VoxelHelper.ChunkSideSize;
        var area = VoxelHelper.ChunkSideSizeSquare;
        Blocks = new BlockState[area * VoxelHelper.ChunkYSize];

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                var h = maxHeights[x, z];
                var maxHeight = h - 1; // top solid local y
                for (int y = 0; y <= VoxelHelper.MaxBlockPositionY; y++)
                {
                    var i = x + z * size + y * area;
                    var bt = TerrainBuilder.GenerateChunkBlockType(maxHeight, x, y, z);
                    Blocks[i] = new BlockState(i, this)
                    {
                        BlockType = bt,
                        IsVisible = false,
                        PackedAO = 0
                    };
                }
            }
        }

        isInitialized = true;
        // Compute basic visibility for CPU consumers (cheap path)
        RecomputeLighting(force: true, includeNeighborData: true, bordersOnly: true);
    }
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



