using DarkVox.Shared.State;

namespace DarkVox.Shared.Gameplay;

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
        Saturation = Math.Clamp(Saturation, 0, Food);
    }

    public void SetFood(int food, float? saturation = null)
    {
        Food = Math.Clamp(food, 0, MaxFood);
        if (saturation.HasValue)
        {
            Saturation = Math.Clamp(saturation.Value, 0, Food);
        }
        else
        {
            Saturation = Math.Clamp(Saturation, 0, Food);
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
    /// Basic hunger/regen loop following Minecraft mechanics.
    /// Exhaustion accumulates from various activities:
    /// - Walking: 0.01 per meter (approx 0.1 per second at walking speed)
    /// - Sprinting: 0.1 per meter (approx 0.6 per second at sprint speed)  
    /// - Jumping: 0.05 per jump (0.2 while sprinting)
    /// - Attacking: 0.1 per attack
    /// - Breaking blocks: 0.005 per block
    /// - Natural regeneration: 6.0 per half-heart healed
    /// When exhaustion reaches 4.0, it consumes 1 saturation (or 1 food if saturation is 0).
    /// </summary>
    public void Tick(double dtSeconds, PlayerAttributeTickContext ctx)
    {
        if (dtSeconds <= 0) return;
        if (!IsAlive) return;

        // Ghost mode = no hunger mechanics
        if (ctx.IsGhostMode) return;

        // === Exhaustion from activities ===
        
        // Movement exhaustion (based on Minecraft rates)
        // Walking speed ~4.3 blocks/sec, sprint ~5.6 blocks/sec
        if (ctx.IsSprinting && ctx.IsMoving)
        {
            // Sprinting: ~0.1 exhaustion per block * 5.6 blocks/sec = 0.56/sec
            AddExhaustion(0.56f * (float)dtSeconds);
        }
        else if (ctx.IsMoving)
        {
            // Walking: ~0.01 exhaustion per block * 4.3 blocks/sec = 0.043/sec
            AddExhaustion(0.043f * (float)dtSeconds);
        }

        // Jump exhaustion
        if (ctx.JumpedThisTick)
        {
            if (ctx.IsSprinting)
            {
                AddExhaustion(0.2f); // Sprint-jumping
            }
            else
            {
                AddExhaustion(0.05f); // Normal jumping
            }
        }

        // Attack exhaustion
        if (ctx.AttackedThisTick)
        {
            AddExhaustion(0.1f);
        }

        // Block breaking exhaustion
        if (ctx.BlocksBrokenThisTick > 0)
        {
            AddExhaustion(0.005f * ctx.BlocksBrokenThisTick);
        }

        // === Natural regeneration ===
        // In Minecraft, regeneration happens when food >= 18 (9 drumsticks)
        if (Food >= 18 && Health < MaxHealth)
        {
            regenAccumulatorSeconds += dtSeconds;
            // 1 half-heart every 4 seconds when well-fed
            if (regenAccumulatorSeconds >= 4.0)
            {
                regenAccumulatorSeconds -= 4.0;
                Heal(1);
                AddExhaustion(6.0f); // Significant exhaustion cost for healing
            }
        }
        else
        {
            regenAccumulatorSeconds = 0;
        }

        // === Starvation damage ===
        // In Minecraft, starvation deals 1 damage every 4 seconds when food = 0
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

    public PlayerAttributesSnapshot BuildSnapshot() => ToSnapshot();

    public void ApplySnapshot(PlayerAttributesSnapshot snapshot)
    {
        MaxHealth = Math.Max(1, snapshot.MaxHealth);
        MaxFood = Math.Max(1, snapshot.MaxFood);

        Health = Math.Clamp(snapshot.Health, 0, MaxHealth);
        Food = Math.Clamp(snapshot.Food, 0, MaxFood);
        Saturation = Math.Clamp(snapshot.Saturation, 0, Food);
        Exhaustion = Math.Max(0.0f, snapshot.Exhaustion);

        // Maintain invariant: Exhaustion should be < 4.0, applying any pending drains immediately.
        // This prevents a large persisted exhaustion value from causing an instant multi-point
        // saturation drop on the next tiny exhaustion event (which feels like "eating reduced saturation").
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
}

/// <summary>
/// Context for player attribute ticking.
/// Tracks actions that cause exhaustion (Minecraft-style).
/// </summary>
/// <param name="IsMoving">Whether the player is currently moving.</param>
/// <param name="IsSprinting">Whether the player is sprinting.</param>
/// <param name="IsGhostMode">Whether the player is in ghost/creative mode (no hunger drain).</param>
/// <param name="JumpedThisTick">Whether the player jumped this tick.</param>
/// <param name="AttackedThisTick">Whether the player attacked this tick.</param>
/// <param name="BlocksBrokenThisTick">Number of blocks broken this tick.</param>
public readonly record struct PlayerAttributeTickContext(
    bool IsMoving, 
    bool IsSprinting, 
    bool IsGhostMode,
    bool JumpedThisTick = false,
    bool AttackedThisTick = false,
    int BlocksBrokenThisTick = 0);
