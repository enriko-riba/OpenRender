namespace DarkVox.Shared.World.Registry;

/// <summary>
/// Represents a consumable food item that restores hunger and saturation.
/// Nutrition values follow Minecraft conventions (half-drumsticks).
/// </summary>
/// <param name="id">The unique identifier for this food.</param>
/// <param name="nutrition">Amount of hunger restored (half-drumsticks).</param>
/// <param name="saturationModifier">Modifier for saturation calculation.</param>
/// <param name="canAlwaysEat">Whether this food can be eaten when hunger is full.</param>
public class Food(GameObjectId id, int nutrition, float saturationModifier, bool canAlwaysEat = false) : GameObject(id)
{
    /// <summary>
    /// Amount of hunger (food points) restored when consumed.
    /// Measured in half-drumsticks (0-20 scale).
    /// </summary>
    public int Nutrition { get; } = nutrition;

    /// <summary>
    /// Saturation modifier. Actual saturation restored = Nutrition * SaturationModifier * 2.
    /// Higher values mean the food keeps you full longer.
    /// </summary>
    public float SaturationModifier { get; } = saturationModifier;

    /// <summary>
    /// Calculated saturation restored when consumed.
    /// </summary>
    public float SaturationRestored => Nutrition * SaturationModifier * 2f;

    /// <summary>
    /// Whether this food can be eaten even when the hunger bar is full.
    /// Some special foods (like golden apples) can always be eaten.
    /// </summary>
    public bool CanAlwaysEat { get; } = canAlwaysEat;

    /// <inheritdoc/>
    public override GameObjectCategory Category => GameObjectCategory.Food;
}
