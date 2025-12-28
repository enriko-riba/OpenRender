namespace SpyroGame.World.Registry;

/// <summary>
/// Represents a food item that can be consumed to restore hunger and saturation.
/// Nutrition values follow Minecraft conventions (half-drumsticks).
/// </summary>
public class FoodItem : Item
{
    /// <summary>
    /// Amount of hunger (food points) restored when consumed.
    /// In Minecraft, this is measured in half-drumsticks (0-20 scale).
    /// </summary>
    public int Nutrition { get; init; }

    /// <summary>
    /// Saturation modifier. Actual saturation restored = Nutrition * SaturationModifier * 2.
    /// Higher values mean the food keeps you full longer.
    /// </summary>
    public float SaturationModifier { get; init; }

    /// <summary>
    /// Calculated saturation restored when consumed.
    /// </summary>
    public float SaturationRestored => Nutrition * SaturationModifier * 2f;

    /// <summary>
    /// Whether this food can be eaten even when the hunger bar is full.
    /// Some special foods (like golden apples) can always be eaten.
    /// </summary>
    public bool CanAlwaysEat { get; init; }

    public FoodItem(ItemId id, int nutrition, float saturationModifier, bool canAlwaysEat = false)
        : base(id)
    {
        Nutrition = nutrition;
        SaturationModifier = saturationModifier;
        CanAlwaysEat = canAlwaysEat;
    }
}
