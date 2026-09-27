using System.IO;

namespace TimeTracker.Core.Services;

/// <summary>
/// Journal minimal vers %APPDATA%\TimeTracker\log.txt (diagnostic des plantages).
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TimeTracker", "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception ex) =>
        Write("ERROR", $"{context}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            lock (_lock)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* le logging ne doit jamais planter l'appli */ }
    }
}
