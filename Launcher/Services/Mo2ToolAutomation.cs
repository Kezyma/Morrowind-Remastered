using System.Diagnostics;
using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Automation of the MO2-driven tools that have no silent CLI — MCP's "Apply
/// chosen patches", MGE XE distant-land generation, and the Delta plugin merge.
/// Each runs the tool through <see cref="Mo2LaunchService"/> (inside MO2's VFS).
/// MO2, launched with the <c>-i/-p</c> form, closes automatically once the tool
/// exits, so waiting on ModOrganizer.exe is the completion signal.
///
/// Root Builder redirects any files the tool writes to MO2's overwrite folder;
/// after the run those are harvested into the tool's "Generated Files" mod so
/// they persist and the launcher can verify them.
/// </summary>
public sealed class Mo2ToolAutomation
{
    private readonly Mo2LaunchService _launch;
    private readonly InstallStateService _installState;
    private readonly ConfigService _config;

    public Mo2ToolAutomation(
        Mo2LaunchService launch, InstallStateService installState, ConfigService config)
    {
        _launch = launch;
        _installState = installState;
        _config = config;
    }

    /// <summary>
    /// Runs the Delta plugin merge: launches the "Delta Plugin" MO2 executable
    /// (which runs <c>delta.bat</c> → <c>delta_plugin merge</c>) and waits for
    /// it to finish. Fully non-interactive.
    /// </summary>
    public async Task DeltaMergeAsync(
        Edition edition, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Report(progress, "Merging plugins (Delta)… this can take a while.", true);
        await _launch.LaunchAsync(edition, "Delta Plugin", waitForExit: true, ct)
            .ConfigureAwait(false);
        Report(progress, "Delta merge finished.", false);
    }

    /// <summary>
    /// Runs Morrowind Code Patch through MO2, auto-clicks "Apply chosen patches",
    /// then harvests the patched exe + backup from overwrite into the MCP
    /// Generated Files mod. The auto-click is best-effort — if it can't find the
    /// button the user completes it manually; the harvest + verifier are the
    /// source of truth either way.
    /// </summary>
    public async Task ApplyMcpAsync(
        Edition edition, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var installDir = _installState.GetEditionInstallDir(edition);
        var before = SnapshotOverwrite(installDir);

        Report(progress, "Opening Morrowind Code Patch in Mod Organizer…", true);
        var mo2 = await _launch.LaunchAsync(edition, "Morrowind Code Patch", waitForExit: false, ct)
            .ConfigureAwait(false);

        // Best-effort GUI automation; harmless if the window/button isn't found.
        await McpAutomation.ApplyMorrowindCodePatchAsync(progress, ct).ConfigureAwait(false);

        await mo2.WaitForExitAsync(ct).ConfigureAwait(false);

        var moved = HarvestOverwriteIntoMod(installDir, before,
            _config.Current.Mo2Paths.McpGeneratedFilesMod, _config.Current.Mo2Paths.McpModTokens);
        Report(progress, moved > 0
            ? $"Morrowind Code Patch applied ({moved} file(s) saved)."
            : "Morrowind Code Patch closed (no new patched files detected).", false);
    }

    /// <summary>
    /// Runs MGE XE distant-land generation through MO2 and drives its wizard
    /// entirely off-screen (Distant Land tab → generator wizard → accept the
    /// stale-files warning → keep the default load order → run with saved/default
    /// settings → Finish), then harvests the generated <c>distantland</c> tree from
    /// overwrite into the MGE Generated Files mod. The GUI automation is
    /// best-effort — if a step can't be driven the user finishes it by hand; the
    /// harvest + verifier are the source of truth either way.
    /// </summary>
    public async Task GenerateDistantLandAsync(
        Edition edition, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var installDir = _installState.GetEditionInstallDir(edition);
        var before = SnapshotOverwrite(installDir);

        Report(progress, "Opening MGE XE in Mod Organizer…", true);
        var mo2 = await _launch.LaunchAsync(edition, "MGE XE", waitForExit: false, ct)
            .ConfigureAwait(false);

        // Best-effort off-screen GUI automation; harmless if a window/button isn't found.
        await MgeAutomation.GenerateDistantLandAsync(progress, ct).ConfigureAwait(false);

        await mo2.WaitForExitAsync(ct).ConfigureAwait(false);

        var moved = HarvestOverwriteIntoMod(installDir, before,
            _config.Current.Mo2Paths.MgeGeneratedFilesMod, _config.Current.Mo2Paths.MgeModTokens);
        Report(progress, moved > 0
            ? $"Distant land generated ({moved} file(s) saved)."
            : "MGE XE closed (no new distant-land files detected).", false);
    }

    // ---------------------------------------------------- overwrite harvesting

    /// <summary>Relative-path → last-write-time map of the overwrite folder.</summary>
    public sealed record OverwriteSnapshot(IReadOnlyDictionary<string, DateTime> Files);

    /// <summary>Records the overwrite folder state before a tool runs.</summary>
    public static OverwriteSnapshot SnapshotOverwrite(string installDir)
    {
        var dir = AppPaths.OverwriteDir(installDir);
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { map[Path.GetRelativePath(dir, f)] = File.GetLastWriteTimeUtc(f); }
                catch { /* ignore */ }
            }
        }
        return new OverwriteSnapshot(map);
    }

    /// <summary>
    /// Moves every overwrite file that is new or modified since
    /// <paramref name="before"/> into the matching Generated Files mod,
    /// preserving its relative path. Returns the number moved.
    /// </summary>
    public static int HarvestOverwriteIntoMod(
        string installDir, OverwriteSnapshot before, string preferredMod, params string[] modTokens)
    {
        var overwrite = AppPaths.OverwriteDir(installDir);
        if (!Directory.Exists(overwrite))
        {
            return 0;
        }

        // Prefer the configured mod folder name; fall back to the token match.
        var modsDir = AppPaths.Mo2ModsDir(installDir);
        string? mod = null;
        if (!string.IsNullOrWhiteSpace(preferredMod))
        {
            var exact = Path.Combine(modsDir, preferredMod);
            if (Directory.Exists(exact))
            {
                mod = exact;
            }
        }
        mod ??= PostSetupVerifier.FindGeneratedFilesMod(installDir, modTokens);
        if (mod is null)
        {
            Logger.Warn($"No Generated Files mod \"{preferredMod}\" or matching " +
                        $"[{string.Join(", ", modTokens)}]; left overwrite untouched.");
            return 0;
        }

        var moved = 0;
        foreach (var file in Directory.GetFiles(overwrite, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(overwrite, file);
            if (IsRuntimeNoise(file))
            {
                continue;
            }
            var isNewOrChanged = !before.Files.TryGetValue(rel, out var prev)
                                 || File.GetLastWriteTimeUtc(file) > prev;
            if (!isNewOrChanged)
            {
                continue;
            }

            var dest = Path.Combine(mod, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest, overwrite: true);
                moved++;
                Logger.Info($"Harvested overwrite \"{rel}\" → \"{Path.GetFileName(mod)}\".");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Couldn't harvest \"{rel}\": {ex.Message}");
            }
        }

        RemoveEmptyDirectories(overwrite);
        Logger.Info($"Harvested {moved} file(s) into \"{Path.GetFileName(mod)}\".");
        return moved;
    }

    /// <summary>
    /// Runtime logs/debug files the tools write to overwrite each run (MGE/MWSE
    /// logs, MWSE's ProgramFlow/Warnings). These regenerate every launch and aren't
    /// part of the generated output, so we never harvest them into a mod.
    /// </summary>
    private static readonly HashSet<string> RuntimeNoiseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mgeXE.log", "MWSE.log", "ProgramFlow.txt", "Warnings.txt", "openmw.log",
    };

    private static bool IsRuntimeNoise(string file)
    {
        var name = Path.GetFileName(file);
        return RuntimeNoiseNames.Contains(name)
            || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch { /* ignore */ }
        }
    }

    private static void Report(IProgress<InstallProgress>? progress, string line, bool indeterminate)
    {
        Logger.Info(line);
        progress?.Report(new InstallProgress("Setup", line, null, indeterminate));
    }
}
