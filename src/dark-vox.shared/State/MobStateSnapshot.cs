namespace DarkVox.Shared.State;

/// <summary>
/// Batch snapshot containing all mobs relevant to a specific player (nearby/loaded chunks).
/// </summary>
public readonly record struct MobStateSnapshot(
    ulong TickId,
    double ServerTimeSeconds,
    MobSnapshot[] Mobs);
