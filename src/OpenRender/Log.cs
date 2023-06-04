namespace OpenRender;

public static class Log
{
    public static void Debug(string message, params object[] args)
    {
        WriteColored(ConsoleColor.White, "[DEBUG]");
        Console.WriteLine(" " + message, args);
    }

    public static void Info(string message, params object[] args)
    {
        WriteColored(ConsoleColor.Green, "[INFO]");
        Console.WriteLine(" " + message, args);
    }

    public static void Warn(string message, params object[] args)
    {
        WriteColored(ConsoleColor.Yellow, "[WARN]");
        Console.WriteLine(" " + message, args);
    }

    public static void Error(string message, params object[] args)
    {
        WriteColored(ConsoleColor.Red, "[ERROR]");
        Console.WriteLine(" " + message, args);
    }

    private static void WriteColored(ConsoleColor color, string message)
    {
        Console.Write($"{DateTime.Now.ToLocalTime()} ");
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(message);
        Console.ForegroundColor = currentColor;
    }
}
