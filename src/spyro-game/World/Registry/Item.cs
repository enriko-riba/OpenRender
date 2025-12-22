namespace SpyroGame.World.Registry;

/// <summary>
/// Represents an item type in the game.
/// This is a singleton instance that defines the behavior and properties of an item.
/// </summary>
public class Item(ItemId id)
{
    public ItemId Id { get; init; } = id;
    public virtual string Name => Id.ToString();
    public virtual int MaxStackSize => 64;
    
    /// <summary>
    /// Multiplier for mining speed. 
    /// 1.0 is hand speed. Tools increase this (e.g. Diamond Pickaxe = 8.0).
    /// </summary>
    public virtual float MiningSpeedMultiplier { get; init; } = 1.0f;
}
