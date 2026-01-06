using DarkVox.Shared.Abstractions;

namespace DarkVox.Shared.Diagnostics;

public sealed class ConsoleLogger : ILog
{
    private static readonly Lock @lock = new();

    public void Info(string message) => Write(ConsoleColor.Green, "[INF] ", ConsoleColor.Gray, message);
    public void Warn(string message) => Write(ConsoleColor.Yellow, "[WAR] ", ConsoleColor.DarkYellow, message);
    public void Error(string message) => Write(ConsoleColor.Red, "[ERR] ", ConsoleColor.Yellow, message);

    private static void Write(ConsoleColor prefixColor, string prefix, ConsoleColor textColor, string message)
    {
        using (@lock.EnterScope())
        {
            WriteColored(ConsoleColor.DarkCyan, $"{DateTime.Now:MM/dd/yyyy HH:mm:ss.fff} ");
            WriteColored(prefixColor, prefix);
            WriteColored(textColor, message);
            Console.WriteLine();
        }
    }

    private static void WriteColored(ConsoleColor color, string message)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(message);
        Console.ForegroundColor = currentColor;
    }
}
