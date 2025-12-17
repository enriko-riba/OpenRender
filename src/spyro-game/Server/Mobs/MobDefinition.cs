using SpyroGame.Shared.State;

namespace SpyroGame.Server.Mobs;

/// <summary>
/// Static, data-driven parameters for a mob type.
/// Keep this server-owned; the client only needs rendering/animation data.
/// </summary>
public sealed record MobDefinition(
    MobKind Kind,
    MobCategory Category,
    float HitboxWidth,
    float HitboxHeight,
    float MaxHealth,
    float WalkSpeed,
    float RunSpeed,
    float StepHeight,
    bool CanSwim,
    bool CanClimb,
    bool CanFly,
    float BaseDamage,
    float AttackRange,
    float AttackCooldownSeconds,
    float AggroRange,
    float LoseAggroRange);
