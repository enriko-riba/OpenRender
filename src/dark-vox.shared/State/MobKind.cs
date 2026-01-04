namespace DarkVox.Shared.State;

/// <summary>
/// High-level mob type. This is the stable identifier used in snapshots.
/// Expand as new mobs are added.
/// </summary>
public enum MobKind : byte
{
    Unknown = 0,
    Zombie = 1,
    Skeleton = 2,
    Cow = 3,
    Pig = 4,
}
