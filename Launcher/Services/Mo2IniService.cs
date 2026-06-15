using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Repairs the paths baked into an edition's <c>ModOrganizer.ini</c> after a
/// Wabbajack install. The list is compiled against the author's machine, so it
/// still points at the author's game path (e.g. <c>D:\Wabbajack\Morrowind</c>)
/// and — if the install was relocated — a stale mods root. This rewrites:
///   1. the <c>gamePath = @ByteArray(...)</c> value, and every other occurrence
///      of the author's game path (Morrowind.exe, Morrowind Launcher.exe,
///      workingDirectories, arguments) — in BOTH the forward-slash and
///      escaped-backslash encodings MO2 uses; and
///   2. every tool path under a <c>mods</c> folder, re-rooted to this install's
///      actual mods folder (skipping the game path itself, which may contain
///      "mods").
/// The operation is idempotent: re-running on an already-repaired file is a
/// no-op.
/// </summary>
public sealed class Mo2IniService
{
    private readonly ConfigService _config;
    private readonly GamePathService _gamePath;
    private readonly InstallStateService _installState;

    // gamePath = @ByteArray(D:\\Wabbajack\\Morrowind)
    private static readonly Regex GamePathRegex =
        new(@"^(?<prefix>\s*gamePath\s*=\s*@ByteArray\()(?<path>.*?)(?<suffix>\)\s*)$",
            RegexOptions.Compiled);

    public Mo2IniService(
        ConfigService config, GamePathService gamePath, InstallStateService installState)
    {
        _config = config;
        _gamePath = gamePath;
        _installState = installState;
    }

    /// <summary>Outcome of a repair run.</summary>
    public sealed record RepairResult(bool Success, string? Error, int Replacements);

    /// <summary>
    /// Rewrites the edition's ModOrganizer.ini in place. Requires a selected,
    /// valid game path.
    /// </summary>
    public RepairResult RepairPaths(Edition edition)
    {
        var gameDir = _gamePath.GameDirectory(_config.Current.GameExePath);
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            return new RepairResult(false, "No Morrowind game path is selected.", 0);
        }

        var installDir = _installState.GetEditionInstallDir(edition);
        var iniPath = AppPaths.Mo2Ini(installDir);
        if (!File.Exists(iniPath))
        {
            return new RepairResult(false, $"ModOrganizer.ini not found at {iniPath}.", 0);
        }

        try
        {
            var lines = File.ReadAllLines(iniPath);

            var (rewritten, replacements, error) = Rewrite(lines, gameDir, installDir);
            if (error is not null)
            {
                return new RepairResult(false, error, 0);
            }

            WriteAtomic(iniPath, rewritten);

            // The list ships a ModSetup MO2 plugin that, on launch, runs the old
            // ModSetup.exe and kills ModOrganizer — disable it so the launcher's
            // own post-setup (and all MO2 launches) work.
            DisableModSetupPlugin(installDir);

            Logger.Info($"Repaired MO2 paths for {edition}: {replacements} line(s) updated " +
                        $"(game path → \"{gameDir.TrimEnd('\\', '/')}\").");
            return new RepairResult(true, null, replacements);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to repair MO2 paths for {edition}", ex);
            return new RepairResult(false, ex.Message, 0);
        }
    }

    /// <summary>
    /// Pure transformation of ModOrganizer.ini lines (extracted for testability).
    /// Returns the rewritten lines, the number of changed lines, and an error
    /// message when the author game path can't be found.
    /// </summary>
    public static (string[] Lines, int Replacements, string? Error) Rewrite(
        string[] lines, string gameDir, string installDir)
    {
        var origRaw = ExtractOriginalGamePath(lines);
        if (origRaw is null)
        {
            return (lines, 0, "Couldn't find gamePath in ModOrganizer.ini.");
        }

        var newRaw = gameDir.TrimEnd('\\', '/');
        var modsRoot = Path.Combine(installDir, "mods").TrimEnd('\\', '/');

        var result = (string[])lines.Clone();
        var replacements = 0;
        for (var i = 0; i < result.Length; i++)
        {
            var original = result[i];
            var line = original;

            // 1. Author game path → selected game path, both encodings.
            line = ReplaceIgnoreCase(line, ToForward(origRaw), ToForward(newRaw));
            line = ReplaceIgnoreCase(line, ToEscaped(origRaw), ToEscaped(newRaw));

            // 2. Re-root mods paths to this install's mods folder, unless the
            //    value is under the (already-correct) game path.
            line = RerootMods(line, modsRoot, newRaw);

            if (line != original)
            {
                replacements++;
                result[i] = line;
            }
        }

        return (result, replacements, null);
    }

    /// <summary>
    /// Disables the list's ModSetup MO2 plugin by renaming
    /// <c>plugins\ModSetup.py</c> to <c>ModSetup.py.disabled</c> (MO2 only loads
    /// <c>.py</c> files). That plugin spawns the old <c>ModSetup.exe</c> and runs
    /// <c>taskkill /im ModOrganizer.exe</c> on launch, which the launcher
    /// replaces. Idempotent and safe to call before every MO2 launch.
    /// </summary>
    public static void DisableModSetupPlugin(string installDir)
    {
        try
        {
            var plugin = Path.Combine(installDir, "plugins", "ModSetup.py");
            if (File.Exists(plugin))
            {
                var disabled = plugin + ".disabled";
                if (File.Exists(disabled))
                {
                    File.Delete(disabled);
                }
                File.Move(plugin, disabled);
                Logger.Info($"Disabled ModSetup plugin at \"{plugin}\".");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't disable ModSetup plugin: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the MO2 download directory for an install by reading
    /// <c>download_directory</c> from its ModOrganizer.ini. Expands the
    /// <c>%BASE_DIR%</c> placeholder (against <c>base_directory</c>, default the
    /// install dir), unescapes the stored path, and falls back to
    /// <c>&lt;installDir&gt;/downloads</c> when unset or unreadable. Used so the
    /// Clear Downloads button works against an embedded MO2 install's own cache.
    /// </summary>
    public static string ResolveDownloadDirectory(string mo2InstallDir)
    {
        var fallback = Path.Combine(mo2InstallDir, "downloads");
        try
        {
            var iniPath = AppPaths.Mo2Ini(mo2InstallDir);
            if (!File.Exists(iniPath))
            {
                return fallback;
            }

            var lines = File.ReadAllLines(iniPath);
            var raw = IniEditor.GetValue(lines, "Settings", "download_directory");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            // MO2 stores Windows paths with escaped backslashes; normalise them.
            var path = raw.Replace(@"\\", @"\").Trim();

            // Expand %BASE_DIR% (base_directory, defaulting to the install dir).
            if (path.Contains("%BASE_DIR%", StringComparison.OrdinalIgnoreCase))
            {
                var baseRaw = IniEditor.GetValue(lines, "Settings", "base_directory");
                var baseDir = string.IsNullOrWhiteSpace(baseRaw)
                    ? mo2InstallDir
                    : baseRaw.Replace(@"\\", @"\").Trim();
                path = Regex.Replace(path, "%BASE_DIR%", baseDir.TrimEnd('\\', '/'),
                    RegexOptions.IgnoreCase);
            }

            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't resolve MO2 download directory: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>Decodes the author's game path from the gamePath ByteArray line.</summary>
    private static string? ExtractOriginalGamePath(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = GamePathRegex.Match(line);
            if (m.Success)
            {
                // Stored with escaped backslashes; decode to a real path.
                return m.Groups["path"].Value.Replace(@"\\", @"\").Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// If the line holds a path under a <c>mods</c> folder, replace the prefix
    /// before that segment with this install's mods root (matching the line's
    /// slash style). Skips paths under the game path.
    /// </summary>
    private static string RerootMods(string line, string modsRoot, string gameDir)
    {
        foreach (var (sep, modsToken) in new[] { ('/', "/mods/"), ('\\', @"\\mods\\") })
        {
            var idx = line.IndexOf(modsToken, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            // Find the start of the path value on this line (drive letter).
            var driveIdx = FindDriveStart(line, idx);
            if (driveIdx < 0)
            {
                continue;
            }

            var before = line[..driveIdx];
            var pathPart = line[driveIdx..];

            // Don't touch the game path (it may itself contain "mods").
            if (pathPart.StartsWith(ToForward(gameDir), StringComparison.OrdinalIgnoreCase) ||
                pathPart.StartsWith(ToEscaped(gameDir), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Re-root: everything from "mods<sep>" onward, prefixed with the
            // install's mods root in the same slash style.
            var tokenInPath = pathPart.IndexOf(modsToken, StringComparison.OrdinalIgnoreCase);
            var tail = pathPart[(tokenInPath + modsToken.Length)..];
            var root = sep == '/' ? ToForward(modsRoot) : ToEscaped(modsRoot);
            var rerooted = root + (sep == '/' ? "/" : @"\\") + tail;
            return before + rerooted;
        }
        return line;
    }

    /// <summary>
    /// Walks back from a mods-token index to the drive letter ("X:") that starts
    /// the path value, so the prefix swap doesn't capture the ini key.
    /// </summary>
    private static int FindDriveStart(string line, int fromIdx)
    {
        for (var i = fromIdx; i >= 1; i--)
        {
            if (line[i] == ':' && char.IsLetter(line[i - 1]))
            {
                return i - 1;
            }
        }
        return -1;
    }

    private static string ToForward(string path) => path.Replace('\\', '/');

    private static string ToEscaped(string path) => path.Replace(@"\", @"\\");

    private static string ReplaceIgnoreCase(string input, string search, string replacement)
    {
        if (string.IsNullOrEmpty(search) || !input.Contains(search[0]))
        {
            // Cheap reject; full check below.
        }
        return Regex.Replace(input, Regex.Escape(search), replacement.Replace("$", "$$"),
            RegexOptions.IgnoreCase);
    }

    private static void WriteAtomic(string path, string[] lines)
    {
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
