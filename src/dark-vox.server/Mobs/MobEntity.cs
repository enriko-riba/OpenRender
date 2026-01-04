using OpenTK.Mathematics;
using DarkVox.Shared.State;

namespace DarkVox.Server.Mobs;

public enum MobSpawnLayer : byte
{
    Surface = 0,
    Cave = 1,
}

public enum MobAiState
{
    Idle,
    Wander,
    Chase
}

/// <summary>
/// Server-authoritative mob entity state.
/// </summary>
public sealed class MobEntity
{
    public MobId Id { get; init; }
    public MobDefinition Definition { get; init; } = default!;

    public Vector3 Position;
    public Vector3 Velocity;
    public float YawDegrees;
    public float PitchDegrees;

    public float Health;
    public bool OnGround;
    public bool IsDead;

    public PlayerId? TargetPlayer;

    // Spawning/despawning bookkeeping (server-side only).
    public MobSpawnLayer SpawnLayer;
    public float TimeInRandomDespawnRangeSeconds;

    // AI State
    public MobAiState AiState;
    public float AiTimer;
    public Vector3? WanderTarget;

    // Combat State
    /// <summary>Time remaining before mob can attack again.</summary>
    public float AttackCooldownRemaining;
    /// <summary>Time remaining in hurt animation / invulnerability.</summary>
    public float HurtTimeRemaining;

    public MobSnapshot ToSnapshot()
        => new(
            Id: Id,
            Kind: Definition.Kind,
            Position: Position,
            Velocity: Velocity,
            YawDegrees: YawDegrees,
            PitchDegrees: PitchDegrees,
            Health: Health,
            MaxHealth: Definition.MaxHealth,
            Flags: (OnGround ? MobSnapshotFlags.OnGround : 0) |
                   (TargetPlayer.HasValue ? MobSnapshotFlags.Aggro : 0) |
                   (IsDead ? MobSnapshotFlags.Dead : 0) |
                   (HurtTimeRemaining > 0 ? MobSnapshotFlags.Hurt : 0));
}
