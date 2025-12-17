using OpenTK.Mathematics;
using SpyroGame.Shared.State;

namespace SpyroGame.Server.Mobs;

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
                   (IsDead ? MobSnapshotFlags.Dead : 0));
}
