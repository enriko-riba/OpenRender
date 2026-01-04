namespace DarkVox.Shared.State;

/// <summary>
/// Server-authoritative in-game time snapshot.
/// Time-of-day is expressed as seconds since midnight in the game world.
/// </summary>
public readonly record struct WorldTimeSnapshot(int DayIndex, int TimeOfDaySeconds);
