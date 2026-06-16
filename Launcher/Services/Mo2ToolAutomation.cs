using System.Diagnostics;
using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Automates the MO2-driven tools that have no silent CLI — MCP, MGE XE distant-land generation, and the Delta plugin merge.</summary>
/// <remarks>
/// Each runs the tool through <see cref="Mo2LaunchService"/> inside MO2's VFS; MO2 launched with the
/// <c>-i/-p</c> form closes automatically once the tool exits, so waiting on ModOrganizer.exe is the completion
/// signal. Root Builder redirects any files the tool writes to MO2's overwrite folder; after the run those are
/// harvested into the tool's "Generated Files" mod so they persist and the launcher can verify them.
/// </remarks>
public sealed class Mo2ToolAutomation
{
    private readonly Mo2LaunchService _launch;
    private readonly InstallStateService _installState;
    private readonly ConfigService _config;

    /// <summary>Creates the tool-automation service.</summary>
    public Mo2ToolAutomation(
        Mo2LaunchService launch, InstallStateService installState, ConfigService config)
    {
        _launch = launch;
        _installState = installState;
        _config = config;
    }

    /// <summary>Runs the Delta plugin merge (launches the "Delta Plugin" MO2 executable, which runs <c>delta.bat</c> → <c>delta_plugin merge</c>) and waits for it; fully non-interactive.</summary>
    public async Task DeltaMergeAsync(
        Edition edition, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress.Report("Setup", "Merging plugins (Delta)… this can take a while.", indeterminate: true, log: true);
        await _launch.LaunchAsync(edition, "Delta Plugin", waitForExit: true, ct)
            .ConfigureAwait(false);
        progress.Report("Setup", "Delta merge finished.", log: true);
    }

    /// <summary>Runs Morrowind Code Patch through MO2, best-effort auto-clicks "Apply chosen patches", then harvests the patched exe + backup from overwrite into the MCP Generated Files mod; the harvest + verifier are the source of truth if the click can't be driven.</summary>
    public async Task ApplyMcpAsync(
        Edition edition, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var installDir = _installState.GetEditionInstallDir(edition);
        var before = SnapshotOverwrite(installDir);

        progress.Report("Setup", "Opening Morrowind Code Patch in Mod Organizer…", indeterminate: true, log: true);
        var mo2 = await _launch.LaunchAsync(edition, "Morrowind Code Patch", waitForExit: false, ct)
            .ConfigureAwait(false);

        await McpAutomation.ApplyMorrowindCodePatchAsync(
            _config.Current.ToolAutomation.Mcp, progress, ct).ConfigureAwait(false);

        await mo2.WaitForExitAsync(ct).ConfigureAwait(false);

        var moved = HarvestOverwriteIntoMod(installDir, before,
            _config.Current.Mo2Paths.McpGeneratedFilesMod, _config.Current.Mo2Paths.McpModTokens);
        progress.Report("Setup", moved > 0
            ? $"Morrowind Code Patch applied ({moved} file(s) saved)."
            : "Morrowind Code Patch closed (no new patched files detected).", log: true);
    }

    /// <summary>Runs MGE XE distant-land generation through MO2, driving its wizard off-screen, then harvests the generated <c>distantland</c> tree from overwrite into the MGE Generated Files mod; the harvest + verifier are the source of truth if a step can't be driven.</summary>
    public async Task GenerateDistantLandAsync(
        Edition edition, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var installDir = _installState.GetEditionInstallDir(edition);
        var before = SnapshotOverwrite(installDir);

        progress.Report("Setup", "Opening MGE XE in Mod Organizer…", indeterminate: true, log: true);
        var mo2 = await _launch.LaunchAsync(edition, "MGE XE", waitForExit: false, ct)
            .ConfigureAwait(false);

        await MgeAutomation.GenerateDistantLandAsync(
            _config.Current.ToolAutomation.Mge, progress, ct).ConfigureAwait(false);

        await mo2.WaitForExitAsync(ct).ConfigureAwait(false);

        var moved = HarvestOverwriteIntoMod(installDir, before,
            _config.Current.Mo2Paths.MgeGeneratedFilesMod, _config.Current.Mo2Paths.MgeModTokens);
        progress.Report("Setup", moved > 0
            ? $"Distant land generated ({moved} file(s) saved)."
            : "MGE XE closed (no new distant-land files detected).", log: true);
    }

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
                catch { }
            }
        }
        return new OverwriteSnapshot(map);
    }

    /// <summary>Moves every overwrite file new or modified since <paramref name="before"/> into the matching Generated Files mod (preserving relative paths), and returns the number moved.</summary>
    public static int HarvestOverwriteIntoMod(
        string installDir, OverwriteSnapshot before, string preferredMod, params string[] modTokens)
    {
        var overwrite = AppPaths.OverwriteDir(installDir);
        if (!Directory.Exists(overwrite))
        {
            return 0;
        }

        var mod = PostSetupVerifier.ResolveMod(installDir, preferredMod, modTokens);
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

    /// <summary>Runtime logs/debug files the tools rewrite to overwrite every launch (MGE/MWSE logs, ProgramFlow/Warnings); not part of the generated output, so they are never harvested into a mod.</summary>
    private static readonly HashSet<string> RuntimeNoiseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mgeXE.log", "MWSE.log", "ProgramFlow.txt", "Warnings.txt", "openmw.log",
    };

    /// <summary>True when the file is a runtime log/debug file that should not be harvested.</summary>
    private static bool IsRuntimeNoise(string file)
    {
        var name = Path.GetFileName(file);
        return RuntimeNoiseNames.Contains(name)
            || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deletes any empty subdirectories left under <paramref name="root"/> after harvesting.</summary>
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
            catch { }
        }
    }

}
