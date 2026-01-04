using DarkVox.Shared.State;

namespace DarkVox.Server;

/// <summary>
/// Owns server-authoritative in-game time progression.
/// Current convention: 1 real second = 1 in-game minute.
/// </summary>
public sealed class WorldTimeService
{
    private const int SecondsPerDay = 24 * 60 * 60;
    private const double GameMinutesPerRealSecond = 1.0;

    private double totalGameMinutes;
    private readonly int startTimeOfDaySeconds;

    public WorldTimeService(int startTimeOfDaySeconds = 7 * 60 * 60)
    {
        this.startTimeOfDaySeconds = ((startTimeOfDaySeconds % SecondsPerDay) + SecondsPerDay) % SecondsPerDay;
    }

    public void Tick(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return;
        totalGameMinutes += elapsedSeconds * GameMinutesPerRealSecond;
    }

    public WorldTimeSnapshot GetSnapshot()
    {
        var totalSeconds = (long)Math.Floor(totalGameMinutes * 60.0);
        var secondsOfDay = (int)((startTimeOfDaySeconds + (int)(totalSeconds % SecondsPerDay) + SecondsPerDay) % SecondsPerDay);
        var dayIndex = (int)((startTimeOfDaySeconds + totalSeconds) / SecondsPerDay);
        if (dayIndex < 0) dayIndex = 0;
        return new WorldTimeSnapshot(dayIndex, secondsOfDay);
    }

    public static bool IsDaytime(int timeOfDaySeconds)
    {
        // Simple band: day from 06:00 inclusive to 18:00 exclusive.
        var hour = (timeOfDaySeconds / 3600) % 24;
        return hour >= 6 && hour < 18;
    }
}
