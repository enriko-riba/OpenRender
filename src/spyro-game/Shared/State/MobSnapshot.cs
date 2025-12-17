using OpenTK.Mathematics;

namespace SpyroGame.Shared.State;

/// <summary>
/// Server-to-client snapshot of a mob entity.
/// The server is authoritative; the client should interpolate these.
/// </summary>
public readonly record struct MobSnapshot(
    MobId Id,
    MobKind Kind,
    Vector3 Position,
    Vector3 Velocity,
    float YawDegrees,
    float PitchDegrees,
    float Health,
    float MaxHealth,
    MobSnapshotFlags Flags);

[System.Flags]
public enum MobSnapshotFlags : ushort
{
    None = 0,
    OnGround = 1 << 0,
    Aggro = 1 << 1,
    Dead = 1 << 2,
}
