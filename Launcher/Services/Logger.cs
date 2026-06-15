using System.IO;
using System.Text;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Tiny append-only file logger. Writes to
/// &lt;LauncherDir&gt;/Config/logs/morrowindremastered.log.
/// Thread-safe; never throws.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();

    /// <summary>Full path of the log file (for "Open Log" UI actions).</summary>
    public static string LogFile => Path.Combine(AppPaths.LogsDir, "morrowindremastered.log");

    /// <summary>
    /// Raised whenever an ERROR line is written, with a short single-line
    /// summary. Fired on the logging thread — UI subscribers must dispatch.
    /// </summary>
    public static event Action<string>? ErrorLogged;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex is not null)
        {
            sb.Append(" :: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            sb.AppendLine().Append(ex.StackTrace);
        }
        Write("ERROR", sb.ToString());

        try
        {
            var summary = ex is null ? message : $"{message} ({ex.Message})";
            ErrorLogged?.Invoke(summary);
        }
        catch
        {
            // Notification must never crash the app.
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFile, line);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
