namespace SpyroGame.World.Registry;

/// <summary>
/// Categorizes game objects for UI grouping, texture loading, and behavior determination.
/// </summary>
public enum GameObjectCategory : byte
{
    /// <summary>Standard block that can be placed in the world.</summary>
    Block = 0,

    /// <summary>Tool with durability and special mining capabilities.</summary>
    Tool,

    /// <summary>Consumable food that restores hunger/saturation.</summary>
    Food,

    /// <summary>Crafting material (sticks, leather, etc.).</summary>
    Material,

    /// <summary>Placeable item like saplings or seeds.</summary>
    Placeable,

    /// <summary>Combat item like arrows.</summary>
    Combat
}

/// <summary>
/// Base class for all game objects (blocks, tools, food, materials).
/// Provides shared properties for inventory, display, and basic behavior.
/// </summary>
/// <param name="id">The unique identifier for this game object.</param>
public class GameObject(GameObjectId id)
{
    /// <summary>
    /// The unique identifier for this game object.
    /// </summary>
    public GameObjectId Id { get; } = id;

    /// <summary>
    /// Display name of the object. Defaults to the enum name.
    /// </summary>
    public virtual string Name => Id.ToString();

    /// <summary>
    /// Category of this object for UI grouping and behavior.
    /// </summary>
    public virtual GameObjectCategory Category => GameObjectCategory.Block;

    /// <summary>
    /// Maximum number of this object that can stack in one inventory slot.
    /// </summary>
    public virtual int MaxStackSize => 64;

    /// <summary>
    /// Path to the texture file relative to Resources directory.
    /// Null means use default texture resolution logic.
    /// </summary>
    public virtual string? TexturePath { get; init; }

    /// <summary>
    /// Returns true if this object can be placed in the world.
    /// </summary>
    public virtual bool CanPlace => Category is GameObjectCategory.Block or GameObjectCategory.Placeable;

    /// <summary>
    /// Returns true if this object is consumable (food).
    /// </summary>
    public bool IsConsumable => Category == GameObjectCategory.Food;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Id})";
}
