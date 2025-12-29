namespace SpyroGame.World.Registry;

/// <summary>
/// Represents a block type that can be placed in the world.
/// Extends <see cref="GameObject"/> with world-specific properties.
/// </summary>
public class Block : GameObject
{
    /// <summary>
    /// Creates a block from a GameObjectId.
    /// </summary>
    public Block(GameObjectId id) : base(id)
    {
        BlockId = id.ToBlockId();
    }

    /// <summary>
    /// Creates a block from a BlockId.
    /// </summary>
    public Block(BlockId blockId) : base(blockId.ToGameObjectId())
    {
        BlockId = blockId;
    }

    /// <summary>
    /// The block identifier for world storage.
    /// </summary>
    public BlockId BlockId { get; }

    /// <inheritdoc/>
    public override string Name => BlockId.ToString();

    /// <inheritdoc/>
    public override GameObjectCategory Category => GameObjectCategory.Block;

    #region Physical Properties

    /// <summary>
    /// Whether this block is solid (blocks movement and needs faces rendered).
    /// </summary>
    public virtual bool IsSolid { get; init; } = true;

    /// <summary>
    /// Whether this block is opaque (blocks light and culls neighbor faces).
    /// </summary>
    public virtual bool IsOpaque { get; init; } = true;

    /// <summary>
    /// Whether this block is a liquid (water, lava).
    /// </summary>
    public virtual bool IsLiquid { get; init; } = false;

    /// <summary>
    /// Whether this block is translucent (partial transparency).
    /// </summary>
    public virtual bool IsTranslucent { get; init; } = false;

    /// <summary>
    /// Whether this block can be replaced by other blocks (air, water, tall grass).
    /// </summary>
    public virtual bool IsReplaceable { get; init; } = false;

    /// <summary>
    /// Whether this block emits light.
    /// </summary>
    public virtual bool IsEmissive { get; init; } = false;

    /// <summary>
    /// Whether this block is part of a tree (logs, leaves).
    /// </summary>
    public virtual bool IsTree { get; init; } = false;

    /// <summary>
    /// Whether this block is vegetation (plants, flowers, grass).
    /// </summary>
    public virtual bool IsVegetation { get; init; } = false;

    #endregion

    #region Breaking Properties

    /// <summary>
    /// Hardness of the block determining how long it takes to break.
    /// -1.0f means unbreakable (bedrock). Standard values: Dirt=0.5, Stone=1.5.
    /// </summary>
    public virtual float Hardness { get; init; } = 1.0f;

    /// <summary>
    /// Loot table defining what items drop when this block is broken.
    /// Default drops the block itself.
    /// </summary>
    public virtual LootTable LootTable { get; init; } = LootTable.Self;

    #endregion

    #region Lighting Properties

    /// <summary>
    /// Light value emitted by this block (0-15). 0 means no light.
    /// </summary>
    public virtual byte LightValue { get; init; } = 0;

    /// <summary>
    /// Light filter (opacity) for this block (0-15). 15 blocks all light.
    /// </summary>
    public virtual byte LightFilter { get; init; } = 15;

    /// <summary>
    /// Effective light decay when light passes through this block.
    /// </summary>
    public int LightDecay => LightFilter >= 15 ? 15 : Math.Max(1, (int)LightFilter);

    #endregion

    #region Rendering Properties

    /// <summary>
    /// How the GPU should render this block.
    /// </summary>
    public virtual RenderMethod RenderMethod { get; init; } = RenderMethod.Opaque;

    /// <summary>
    /// Geometric shape used for mesh generation.
    /// </summary>
    public virtual BlockRenderShape Shape { get; init; } = BlockRenderShape.FullCube;

    #endregion
}
