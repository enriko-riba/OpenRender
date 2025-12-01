using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Represents the state of a single block in the world.
/// Contains position, chunk reference, and block type information.
/// </summary>
public struct BlockState
{
    /// <summary>
    /// Creates a BlockState from a linear voxel index within a chunk.
    /// </summary>
    public BlockState(int index, Chunk chunk)
    {
        Index = index;
        ChunkIndex = chunk.Index;
        var lx = index % VoxelHelper.ChunkSideSize;
        var ly = index / VoxelHelper.ChunkSideSizeSquare;
        var lz = (index / VoxelHelper.ChunkSideSize) % VoxelHelper.ChunkSideSize;
        LocalPosition = new Vector3i(lx, ly, lz);
        GlobalPosition = chunk.Position + LocalPosition;
        Aabb = new AABB(GlobalPosition, GlobalPosition + Vector3i.One);
        Block = BlockId.Air; // Default, should be set by caller
    }

    /// <summary>
    /// Creates a BlockState from a global position and block type.
    /// </summary>
    public BlockState(Vector3i globalPosition, BlockId block)
    {
        GlobalPosition = globalPosition;
        Block = block;
        ChunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(globalPosition);
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(ChunkIndex);
        LocalPosition = globalPosition - chunkPos;
        Index = LocalPosition.X + LocalPosition.Z * VoxelHelper.ChunkSideSize + LocalPosition.Y * VoxelHelper.ChunkSideSizeSquare;
        Aabb = new AABB(GlobalPosition, GlobalPosition + Vector3i.One);
    }

    /// <summary>
    /// Linear voxel index within the chunk.
    /// </summary>
    public int Index { get; private set; }

    /// <summary>
    /// Index of the chunk containing this block.
    /// </summary>
    public int ChunkIndex { get; private set; }

    /// <summary>
    /// Axis-aligned bounding box for collision detection.
    /// </summary>
    public AABB Aabb { get; private set; }
    
    /// <summary>
    /// The block type with embedded property flags.
    /// Use extension methods like IsSolid(), IsOpaque() etc. for property checks.
    /// </summary>
    public BlockId Block { get; set; }

    /// <summary>
    /// Gets or sets the blocks visibility.
    /// Note that only visible blocks are being rendered.
    /// </summary>
    public bool IsVisible { get; internal set; }

    /// <summary>
    /// Helper to check if this block is solid (for collision).
    /// Returns true for all solid blocks (uses BlockId.Solid flag).
    /// </summary>
    public readonly bool IsSolid => Block.IsSolid();

    /// <summary>
    /// Position within the chunk (0-15 for X/Z, 0-383 for Y).
    /// </summary>
    public Vector3i LocalPosition { get; private set; }

    /// <summary>
    /// World position of this block.
    /// </summary>
    public Vector3i GlobalPosition { get; private set; } 

    /// <inheritdoc/>
    public override readonly string ToString() => $"{Block}@{LocalPosition}/{ChunkIndex}";
}
