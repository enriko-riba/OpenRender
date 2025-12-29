using SpyroGame.World.Registry;
using SpyroGame.Shared.State;

namespace SpyroGame.Server.Mobs;

/// <summary>
/// Static, data-driven parameters for a mob type.
/// Server owns gameplay data; client uses model/animation paths for rendering.
/// </summary>
public sealed record MobDefinition(
    MobKind Kind,
    MobCategory Category,

    // === Hitbox & Physics ===
    float HitboxWidth,
    float HitboxHeight,
    float StepHeight,
    bool CanSwim,
    bool CanClimb,
    bool CanFly,

    // === Health & Combat ===
    float MaxHealth,
    float BaseDamage,
    float AttackRange,
    float AttackCooldownSeconds,
    float AttackSpeed,

    // === Movement ===
    float WalkSpeed,
    float RunSpeed,

    // === AI Behavior ===
    float AggroRange,
    float LoseAggroRange,

    // === Optional parameters (with defaults) ===
    float KnockbackStrength = 0.4f,
    float ArmorPoints = 0f,
    int SpawnLightLevelMin = 0,
    int SpawnLightLevelMax = 15,
    int SpawnWeight = 10,
    string? ModelPath = null,
    string? AnimationPath = null,
    float RenderScaleX = 1f,
    float RenderScaleY = 1f,
    float RenderScaleZ = 1f,
    /// <summary>
    /// Yaw offset in degrees to correct model facing direction.
    /// Use 180° for models where the visual front is on the geometric back face.
    /// </summary>
    float YawOffsetDegrees = 0f,
    DropTable? Drops = null,
    int XpDropMin = 0,
    int XpDropMax = 0);

/// <summary>
/// Defines items dropped when a mob dies.
/// </summary>
public sealed record DropTable(params DropEntry[] Entries);

/// <summary>
/// A single drop entry: item, count range, and probability.
/// </summary>
public sealed record DropEntry(
    GameObjectId Item,
    int MinCount,
    int MaxCount,
    float Probability = 1.0f);
