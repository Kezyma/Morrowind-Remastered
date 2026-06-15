namespace MorrowindRemasteredLauncher.Services;

/// <summary>Shared helper for reporting <see cref="InstallProgress"/> updates from install/setup steps.</summary>
public static class InstallProgressReporter
{
    /// <summary>Reports a progress update for the given stage, optionally logging the line first.</summary>
    public static void Report(
        this IProgress<InstallProgress>? progress,
        string stage, string? line, double? percent = null, bool indeterminate = false, bool log = false)
    {
        if (log && line is not null)
        {
            Logger.Info(line);
        }
        progress?.Report(new InstallProgress(stage, line, percent, indeterminate));
    }
}
