namespace DarkVox.Shared.Abstractions;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    private NullLog() { }

    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}
