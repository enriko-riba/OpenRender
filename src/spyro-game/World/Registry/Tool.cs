namespace SpyroGame.World.Registry;

/// <summary>
/// Types of tools and their primary use cases.
/// </summary>
public enum ToolType : byte
{
    /// <summary>No specific tool type.</summary>
    None = 0,

    /// <summary>Pickaxe - effective against stone, ores.</summary>
    Pickaxe,

    /// <summary>Axe - effective against wood, logs.</summary>
    Axe,

    /// <summary>Shovel - effective against dirt, sand, gravel.</summary>
    Shovel,

    /// <summary>Sword - combat weapon, effective against mobs.</summary>
    Sword,

    /// <summary>Hoe - for tilling soil.</summary>
    Hoe
}

/// <summary>
/// Represents a tool with durability and specialized mining/combat capabilities.
/// </summary>
/// <param name="id">The unique identifier for this tool.</param>
/// <param name="toolType">The type of tool (pickaxe, axe, etc.).</param>
/// <param name="miningSpeed">Mining speed multiplier for appropriate blocks.</param>
/// <param name="maxDurability">Maximum durability before the tool breaks.</param>
public class Tool(GameObjectId id, ToolType toolType, float miningSpeed, int maxDurability) : GameObject(id)
{
    /// <summary>
    /// The type of tool determining which blocks it's effective against.
    /// </summary>
    public ToolType ToolType { get; } = toolType;

    /// <summary>
    /// Mining speed multiplier for appropriate blocks.
    /// 1.0 is hand speed. Diamond tools are typically 8.0.
    /// </summary>
    public float MiningSpeed { get; } = miningSpeed;

    /// <summary>
    /// Maximum durability of the tool. -1 means unbreakable.
    /// </summary>
    public int MaxDurability { get; } = maxDurability;

    /// <inheritdoc/>
    public override GameObjectCategory Category => GameObjectCategory.Tool;

    /// <inheritdoc/>
    public override int MaxStackSize => 1;

    /// <summary>
    /// Base damage dealt when used as a weapon.
    /// </summary>
    public virtual float AttackDamage => ToolType switch
    {
        ToolType.Sword => 4.0f + MiningSpeed,
        ToolType.Axe => 3.0f + MiningSpeed * 0.5f,
        _ => 1.0f
    };
}
