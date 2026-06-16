using System.IO;
using System.Text;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Tiny thread-safe, never-throwing append-only file logger writing to the launcher's log file.</summary>
public static class Logger
{
    /// <summary>Serialises writes to the log file.</summary>
    private static readonly object Gate = new();

    /// <summary>Full path of the log file (for "Open Log" UI actions).</summary>
    public static string LogFile => Path.Combine(AppPaths.LogsDir,
        ConfigService.Instance?.Current.Paths.LogFileName ?? "morrowindremastered.log");

    /// <summary>Raised with a short summary whenever an ERROR is written; fired on the logging thread, so UI subscribers must dispatch.</summary>
    public static event Action<string>? ErrorLogged;

    /// <summary>Writes an INFO line.</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Writes a WARN line.</summary>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Writes an ERROR line (with exception detail if given) and raises <see cref="ErrorLogged"/>.</summary>
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
        }
    }

    /// <summary>Appends one timestamped, leveled line to the log file; swallows all failures so logging never crashes the app.</summary>
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
        }
    }
}
