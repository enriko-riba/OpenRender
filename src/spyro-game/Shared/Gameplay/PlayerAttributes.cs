using SpyroGame.Shared.State;

namespace SpyroGame.Shared.Gameplay;

/// <summary>
/// Minecraft-style player attributes.
/// Values are expressed in "half-hearts"/"half-drumsticks" style units (0..20).
/// </summary>
public sealed class PlayerAttributes
{
    // Health is traditionally 20 (10 hearts)
    public int MaxHealth { get; private set; } = 20;
    public int Health { get; private set; } = 20;

    // Hunger (food) is 20 (10 drumsticks)
    public int MaxFood { get; private set; } = 20;
    public int Food { get; private set; } = 20;

    // Saturation is 0..20 (in vanilla it can exceed hunger, but keeping it clamped simplifies)
    public float Saturation { get; private set; } = 5.0f;

    // Exhaustion: when it reaches 4, consumes saturation/food.
    public float Exhaustion { get; private set; }

    // Timers for regen/starvation.
    private double regenAccumulatorSeconds;
    private double starvationAccumulatorSeconds;

    public bool IsAlive => Health > 0;

    public void SetMaxHealth(int maxHealth)
    {
        MaxHealth = Math.Max(1, maxHealth);
        Health = Math.Clamp(Health, 0, MaxHealth);
    }

    public void SetMaxFood(int maxFood)
    {
        MaxFood = Math.Max(1, maxFood);
        Food = Math.Clamp(Food, 0, MaxFood);
        Saturation = Math.Clamp(Saturation, 0, MaxFood);
    }

    public void SetFood(int food, float? saturation = null)
    {
        Food = Math.Clamp(food, 0, MaxFood);
        if (saturation.HasValue)
        {
            Saturation = Math.Clamp(saturation.Value, 0, MaxFood);
        }
    }

    public void Heal(int amount)
    {
        if (amount <= 0) return;
        Health = Math.Clamp(Health + amount, 0, MaxHealth);
    }

    public void Damage(int amount)
    {
        if (amount <= 0) return;
        Health = Math.Clamp(Health - amount, 0, MaxHealth);
    }

    /// <summary>
    /// Adds exhaustion. When Exhaustion reaches 4, consumes Saturation first, then Food.
    /// </summary>
    public void AddExhaustion(float amount)
    {
        if (amount <= 0) return;
        Exhaustion += amount;

        while (Exhaustion >= 4.0f)
        {
            Exhaustion -= 4.0f;

            if (Saturation > 0.0f)
            {
                Saturation = Math.Max(0.0f, Saturation - 1.0f);
            }
            else
            {
                Food = Math.Max(0, Food - 1);
            }
        }
    }

    /// <summary>
    /// Basic hunger/regen loop.
    /// This is intentionally approximate (good enough for now, and easy to move server-side).
    /// </summary>
    public void Tick(double dtSeconds, PlayerAttributeTickContext ctx)
    {
        if (dtSeconds <= 0) return;
        if (!IsAlive) return;

        // Exhaustion from activity (server-authoritative later)
        // Increased rates for more noticeable hunger mechanics:
        // - Walking: ~100 seconds to lose 1 food point
        // - Sprinting: ~20 seconds to lose 1 food point  
        if (!ctx.IsGhostMode)
        {
            if (ctx.IsSprinting && ctx.IsMoving)
            {
                AddExhaustion(0.20f * (float)dtSeconds); // Sprint exhaustion
            }
            else if (ctx.IsMoving)
            {
                AddExhaustion(0.04f * (float)dtSeconds); // Walk exhaustion
            }
        }

        // Regen: if well-fed
        if (Food >= 18 && Health < MaxHealth)
        {
            regenAccumulatorSeconds += dtSeconds;
            // Roughly 1 HP (half-heart) every 4s.
            if (regenAccumulatorSeconds >= 4.0)
            {
                regenAccumulatorSeconds -= 4.0;
                Heal(1);
                AddExhaustion(0.6f);
            }
        }
        else
        {
            regenAccumulatorSeconds = 0;
        }

        // Starvation damage
        if (Food <= 0)
        {
            starvationAccumulatorSeconds += dtSeconds;
            if (starvationAccumulatorSeconds >= 4.0)
            {
                starvationAccumulatorSeconds -= 4.0;
                Damage(1);
            }
        }
        else
        {
            starvationAccumulatorSeconds = 0;
        }
    }

    /// <summary>
    /// Attempts to consume food, restoring hunger and saturation.
    /// </summary>
    /// <param name="nutrition">Amount of hunger to restore (half-drumsticks).</param>
    /// <param name="saturation">Amount of saturation to restore.</param>
    /// <param name="canAlwaysEat">If true, can eat even when hunger is full.</param>
    /// <returns>True if food was consumed, false if hunger was full and canAlwaysEat is false.</returns>
    public bool ConsumeFood(int nutrition, float saturation, bool canAlwaysEat = false)
    {
        // Can't eat if hunger is full (unless canAlwaysEat is true)
        if (Food >= MaxFood && !canAlwaysEat)
        {
            return false;
        }

        // Restore hunger
        Food = Math.Clamp(Food + nutrition, 0, MaxFood);

        // Restore saturation (capped at current food level in vanilla Minecraft)
        Saturation = Math.Clamp(Saturation + saturation, 0, Food);

        return true;
    }

    public PlayerAttributesSnapshot ToSnapshot() => new(
        MaxHealth: MaxHealth,
        Health: Health,
        MaxFood: MaxFood,
        Food: Food,
        Saturation: Saturation,
        Exhaustion: Exhaustion);

    public void ApplySnapshot(PlayerAttributesSnapshot snapshot)
    {
        MaxHealth = Math.Max(1, snapshot.MaxHealth);
        MaxFood = Math.Max(1, snapshot.MaxFood);

        Health = Math.Clamp(snapshot.Health, 0, MaxHealth);
        Food = Math.Clamp(snapshot.Food, 0, MaxFood);
        Saturation = Math.Clamp(snapshot.Saturation, 0, MaxFood);
        Exhaustion = Math.Max(0.0f, snapshot.Exhaustion);
    }
}

public readonly record struct PlayerAttributeTickContext(bool IsMoving, bool IsSprinting, bool IsGhostMode);
