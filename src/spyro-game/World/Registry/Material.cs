namespace SpyroGame.World.Registry;

/// <summary>
/// Represents a crafting material that cannot be placed or consumed.
/// Examples: sticks, leather, bones.
/// </summary>
/// <param name="id">The unique identifier for this material.</param>
public class CraftingMaterial(GameObjectId id) : GameObject(id)
{
    /// <inheritdoc/>
    public override GameObjectCategory Category => GameObjectCategory.Material;

    /// <inheritdoc/>
    public override bool CanPlace => false;
}

/// <summary>
/// Represents an item that can be placed in the world but isn't a standard block.
/// Examples: saplings, seeds.
/// </summary>
/// <param name="id">The unique identifier for this placeable.</param>
/// <param name="placesBlock">The block ID that gets placed, or Air if special handling.</param>
public class Placeable(GameObjectId id, GameObjectId placesBlock) : GameObject(id)
{
    /// <summary>
    /// The block that gets placed when this item is used.
    /// Air means special handling is required.
    /// </summary>
    public GameObjectId PlacesBlock { get; } = placesBlock;

    /// <inheritdoc/>
    public override GameObjectCategory Category => GameObjectCategory.Placeable;

    /// <summary>
    /// Whether this placeable requires a specific block type underneath.
    /// </summary>
    public virtual bool RequiresSolidGround { get; init; } = true;

    /// <summary>
    /// Block IDs that this item can be placed on. Empty means any solid block.
    /// </summary>
    public virtual GameObjectId[] ValidPlacementBlocks { get; init; } = [];
}

/// <summary>
/// Represents a combat item like arrows.
/// </summary>
/// <param name="id">The unique identifier for this combat item.</param>
public class CombatItem(GameObjectId id) : GameObject(id)
{
    /// <inheritdoc/>
    public override GameObjectCategory Category => GameObjectCategory.Combat;

    /// <inheritdoc/>
    public override bool CanPlace => false;
}
