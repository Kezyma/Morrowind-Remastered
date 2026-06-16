using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Repairs the paths baked into an edition's <c>ModOrganizer.ini</c> after a Wabbajack install; idempotent (re-running on a repaired file is a no-op).</summary>
/// <remarks>
/// The list is compiled against the author's machine, so it still points at the author's game path (e.g.
/// <c>D:\Wabbajack\Morrowind</c>) and — if relocated — a stale mods root. This rewrites the
/// <c>gamePath = @ByteArray(...)</c> value and every other occurrence of the author's game path (Morrowind.exe,
/// Morrowind Launcher.exe, workingDirectories, arguments) in BOTH the forward-slash and escaped-backslash
/// encodings MO2 uses, and re-roots every tool path under a <c>mods</c> folder to this install's mods folder
/// (skipping the game path itself, which may contain "mods").
/// </remarks>
public sealed class Mo2IniService
{
    private readonly ConfigService _config;
    private readonly GamePathService _gamePath;
    private readonly InstallStateService _installState;

    /// <summary>Matches the <c>gamePath = @ByteArray(...)</c> line and captures the encoded path.</summary>
    private static readonly Regex GamePathRegex =
        new(@"^(?<prefix>\s*gamePath\s*=\s*@ByteArray\()(?<path>.*?)(?<suffix>\)\s*)$",
            RegexOptions.Compiled);

    /// <summary>Creates the MO2 ini repair service.</summary>
    public Mo2IniService(
        ConfigService config, GamePathService gamePath, InstallStateService installState)
    {
        _config = config;
        _gamePath = gamePath;
        _installState = installState;
    }

    /// <summary>Outcome of a repair run.</summary>
    public sealed record RepairResult(bool Success, string? Error, int Replacements);

    /// <summary>Rewrites the edition's ModOrganizer.ini in place (and disables the ModSetup plugin); requires a selected, valid game path.</summary>
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

    /// <summary>Pure transformation of ModOrganizer.ini lines (extracted for testability); returns the rewritten lines, the count of changed lines, and an error when the author game path can't be found.</summary>
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

            line = ReplaceIgnoreCase(line, ToForward(origRaw), ToForward(newRaw));
            line = ReplaceIgnoreCase(line, ToEscaped(origRaw), ToEscaped(newRaw));

            line = RerootMods(line, modsRoot, newRaw);

            if (line != original)
            {
                replacements++;
                result[i] = line;
            }
        }

        return (result, replacements, null);
    }

    /// <summary>Disables the list's ModSetup MO2 plugin by renaming <c>plugins\ModSetup.py</c> to <c>ModSetup.py.disabled</c> (MO2 only loads <c>.py</c>); idempotent and safe to call before every MO2 launch.</summary>
    /// <remarks>That plugin spawns the old <c>ModSetup.exe</c> and runs <c>taskkill /im ModOrganizer.exe</c> on launch (killing MO2), which the launcher replaces — so it must be disabled or no MO2 launch survives.</remarks>
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

    /// <summary>Resolves an install's MO2 download directory from its ModOrganizer.ini (expanding <c>%BASE_DIR%</c> and unescaping the path), falling back to <c>&lt;installDir&gt;/downloads</c> when unset or unreadable, so Clear Downloads targets an embedded MO2 install's own cache.</summary>
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

            var path = raw.Replace(@"\\", @"\").Trim();

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
                return m.Groups["path"].Value.Replace(@"\\", @"\").Trim();
            }
        }
        return null;
    }

    /// <summary>If the line holds a path under a <c>mods</c> folder, replaces the prefix before that segment with this install's mods root (matching the line's slash style); skips paths under the game path, which may itself contain "mods".</summary>
    private static string RerootMods(string line, string modsRoot, string gameDir)
    {
        foreach (var (sep, modsToken) in new[] { ('/', "/mods/"), ('\\', @"\\mods\\") })
        {
            var idx = line.IndexOf(modsToken, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var driveIdx = FindDriveStart(line, idx);
            if (driveIdx < 0)
            {
                continue;
            }

            var before = line[..driveIdx];
            var pathPart = line[driveIdx..];

            if (pathPart.StartsWith(ToForward(gameDir), StringComparison.OrdinalIgnoreCase) ||
                pathPart.StartsWith(ToEscaped(gameDir), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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

    /// <summary>Converts a Windows path to forward-slash form.</summary>
    private static string ToForward(string path) => path.Replace('\\', '/');

    /// <summary>Converts a Windows path to MO2's escaped-backslash form.</summary>
    private static string ToEscaped(string path) => path.Replace(@"\", @"\\");

    /// <summary>Case-insensitive literal string replacement.</summary>
    private static string ReplaceIgnoreCase(string input, string search, string replacement)
        => Regex.Replace(input, Regex.Escape(search), replacement.Replace("$", "$$"),
            RegexOptions.IgnoreCase);

    /// <summary>Writes the lines to a temp file and atomically moves it over the target (UTF-8, no BOM).</summary>
    private static void WriteAtomic(string path, string[] lines)
    {
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
