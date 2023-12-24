using OpenTK.Graphics.OpenGL4;
using System.Runtime.CompilerServices;

namespace OpenRender;

public static class Log
{
    private static readonly object @lock = new();
    public const int LevelDebug = 0;
    public const int LevelInfo = 1;
    public const int LevelHighlight = 3;
    public const int LevelWarn = 5;
    public const int LevelError = 10;

    public static int MinimumLevel { get; set; }

    public static void Debug(string message) => WriteLog(LevelDebug, message);
    public static void Debug(string message, params object[] args) => Debug(string.Format(message, args));

    public static void Info(string message) => WriteLog(LevelInfo, message);
    public static void Info(string message, params object[] args) => Info(string.Format(message, args));

    public static void Highlight(string message) => WriteLog(LevelHighlight, message);
    public static void Highlight(string message, params object[] args) => Highlight(string.Format(message, args));


    public static void Warn(string message) => WriteLog(LevelWarn, message);
    public static void Warn(string message, params object[] args) => Warn(string.Format(message, args));

    public static void Error(string message) => WriteLog(LevelError, message);
    public static void Error(string message, params object[] args) => Error(string.Format(message, args));

    public static void CheckGlError([CallerMemberName] string name = "")
    {
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Warn($"Error: '{error}' in {name}");
        }
    }

    private static void WriteLog(int level, string message)
    {
        if (level >= MinimumLevel)
        {
            lock (@lock)
            {
                var textColor = ConsoleColor.DarkGray;
                switch (level)
                {
                    case LevelDebug:
                        WriteWithTimeStamp(ConsoleColor.DarkGray, "[DBG] ");
                        break;
                    case LevelInfo:
                        WriteWithTimeStamp(ConsoleColor.Green, "[INF] ");
                        textColor = ConsoleColor.Gray;
                        break;
                    case LevelHighlight:
                        WriteWithTimeStamp(ConsoleColor.Blue, "[INF] ");
                        textColor = ConsoleColor.Blue;
                        break;
                    case LevelWarn:
                        WriteWithTimeStamp(ConsoleColor.Yellow, "[WAR] ");
                        textColor = ConsoleColor.DarkYellow;
                        break;
                    default:
                        WriteWithTimeStamp(ConsoleColor.Red, "[ERR] ");
                        textColor = ConsoleColor.Yellow;
                        break;
                }
                WriteColored(textColor, message);
                Console.WriteLine();
            }
        }
    }

    private static void WriteWithTimeStamp(ConsoleColor color, string message)
    {
        WriteColored(ConsoleColor.DarkCyan, $"{DateTime.Now.ToLocalTime()} ");
        //Console.Write($"{DateTime.Now.ToLocalTime()} ");
        WriteColored(color, message);
    }

    private static void WriteColored(ConsoleColor color, string message)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(message);
        Console.ForegroundColor = currentColor;
    }
}
