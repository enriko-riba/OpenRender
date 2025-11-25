using OpenTK.Mathematics;

namespace SpyroGame.World;

public struct BlockState
{
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
        Descriptor = BlockDescriptor.Air; // Default, should be set by caller
    }

    public BlockState(Vector3i globalPosition, BlockDescriptor descriptor)
    {
        GlobalPosition = globalPosition;
        Descriptor = descriptor;
        // Calculate local position and chunk index from global position
        ChunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(globalPosition);
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(ChunkIndex);
        LocalPosition = globalPosition - chunkPos;
        Index = LocalPosition.X + LocalPosition.Z * VoxelHelper.ChunkSideSize + LocalPosition.Y * VoxelHelper.ChunkSideSizeSquare;
        Aabb = new AABB(GlobalPosition, GlobalPosition + Vector3i.One);
    }

    public int Index { get; private set; }

    public int ChunkIndex { get; private set; }

    public AABB Aabb { get; private set; }
    
    /// <summary>
    /// The block descriptor from GPU terrain generation (primary collision data).
    /// </summary>
    public BlockDescriptor Descriptor { get; set; }

    /// <summary>
    /// DEPRECATED: Legacy Geology property for backward compatibility during transition.
    /// Use Descriptor property instead for new code.
    /// </summary>
    [Obsolete("Use Descriptor property instead. Geology has been renamed to Descriptor.")]
    public BlockDescriptor Geology
    {
        get => Descriptor;
        set => Descriptor = value;
    }

    /// <summary>
    /// DEPRECATED: Legacy BlockType for backward compatibility.
    /// Use Descriptor property instead for new code.
    /// </summary>
    [Obsolete("Use Descriptor property instead. BlockType is being phased out in favor of BlockDescriptor.")]
    public BlockType BlockType
    {
        get => Descriptor switch
        {
            BlockDescriptor.Air => BlockType.None,
            BlockDescriptor.Water => BlockType.WaterLevel,
            BlockDescriptor.Surface => BlockType.GrassDirt,
            BlockDescriptor.Subsurface => BlockType.Dirt,
            BlockDescriptor.DeepSubsurface => BlockType.Rock,
            BlockDescriptor.UnderwaterSurface => BlockType.Sand,
            BlockDescriptor.UnderwaterSubsurface => BlockType.Sand,
            BlockDescriptor.ShoreLine => BlockType.Sand,
            _ => BlockType.Rock
        };
        set
        {
            // Convert BlockType to BlockDescriptor for backward compatibility
            Descriptor = value switch
            {
                BlockType.None => BlockDescriptor.Air,
                BlockType.WaterLevel => BlockDescriptor.Water,
                BlockType.GrassDirt or BlockType.Grass => BlockDescriptor.Surface,
                BlockType.Dirt => BlockDescriptor.Subsurface,
                BlockType.Rock or BlockType.BedRock => BlockDescriptor.DeepSubsurface,
                BlockType.Sand => BlockDescriptor.UnderwaterSurface,
                _ => BlockDescriptor.Surface
            };
        }
    }

    /// <summary>
    /// Gets or sets the blocks visibility.
    /// Note that only visible blocks are being rendered.
    /// </summary>
    public bool IsVisible { get; internal set; }

    /// <summary>
    /// Helper to check if this block is solid (for collision).
    /// Returns true for all descriptors except Air and Water.
    /// </summary>
    public readonly bool IsSolid => Descriptor != BlockDescriptor.Air && Descriptor != BlockDescriptor.Water;

    /// <summary>
    /// Helper to check if this block is fluid.
    /// </summary>
    public readonly bool IsFluid => Descriptor == BlockDescriptor.Water;

    public Vector3i LocalPosition { get; private set; }
    public Vector3i GlobalPosition { get; private set; } 

    public override readonly string ToString() => $"{Descriptor}@{LocalPosition}/{ChunkIndex}";
}
