namespace SpyroGame.World.Registry;

/// <summary>
/// Represents a block type in the world.
/// This is a singleton instance that defines the behavior and properties of a block.
/// </summary>
public class Block(BlockId id)
{
    public BlockId Id { get; init; } = id;
    public virtual string Name => Id.ToString();

    // Properties
    public virtual bool IsSolid { get; init; } = true;
    public virtual bool IsOpaque { get; init; } = true;
    public virtual bool IsLiquid { get; init; } = false;
    public virtual bool IsTranslucent { get; init; } = false;
    public virtual bool IsReplaceable { get; init; } = false;
    public virtual bool IsEmissive { get; init; } = false;
    public virtual bool IsTree { get; init; } = false;
    public virtual bool IsVegetation { get; init; } = false;

    /// <summary>
    /// Hardness of the block. Determines how long it takes to break.
    /// -1.0f means unbreakable (e.g. Bedrock).
    /// Standard values: Dirt=0.5, Stone=1.5, Obsidian=50.
    /// </summary>
    public virtual float Hardness { get; init; } = 1.0f;

    public virtual byte LightValue { get; init; } = 0;
    public virtual byte LightFilter { get; init; } = 15; // Default opaque blocks light
    public virtual RenderMethod RenderMethod { get; init; } = RenderMethod.Opaque;
    public virtual BlockRenderShape Shape { get; init; } = BlockRenderShape.FullCube;

    // Helper for light decay
    public int LightDecay => LightFilter >= 15 ? 15 : Math.Max(1, (int)LightFilter);
}
