using System.Globalization;
using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>The discrete post-install steps, used by the verifier, the runner, and Tools UI.</summary>
public enum PostSetupStep
{
    RepairPaths,
    ApplyDisplay,
    InstallOpenMw,
    InstallDelta,
    InstallMwse,
    ApplyMcp,
    GenerateDistantLand,
    DeltaMerge,

    /// <summary>Optional: add the launcher to Steam as a non-Steam shortcut. Never
    /// required, never auto-run, and excluded from <see cref="PostSetupVerifier.SetupStepsFor"/>.</summary>
    AddToSteam
}

/// <summary>A step's readiness, derived from real files/values.</summary>
public sealed record StepStatus(PostSetupStep Step, string Label, bool Done);

/// <summary>
/// Determines whether each post-install step has actually been completed by
/// inspecting real files, registry values and config — never the
/// <c>PostSetupComplete</c> flag. Backs the idempotent runner, the pre-launch
/// gate, and the Tools panel.
/// </summary>
public sealed class PostSetupVerifier
{
    private readonly ConfigService _config;
    private readonly InstallStateService _installState;
    private readonly GamePathService _gamePath;
    private readonly SteamService _steam;

    public PostSetupVerifier(
        ConfigService config, InstallStateService installState, GamePathService gamePath,
        SteamService steam)
    {
        _config = config;
        _installState = installState;
        _gamePath = gamePath;
        _steam = steam;
    }

    /// <summary>
    /// The ordered per-edition SETUP steps shown on the Play tab and auto-run before
    /// launch. Excludes RepairPaths (now part of Install — "update Mod Organizer
    /// paths") and the Wabbajack install itself. Delta merge is NOT a setup step
    /// (the list already includes the patch); it remains a manual tool. Display
    /// settings are applied LAST.
    /// </summary>
    public static IReadOnlyList<PostSetupStep> SetupStepsFor(Edition edition) => edition switch
    {
        Edition.OpenMW => new[]
        {
            PostSetupStep.InstallOpenMw, PostSetupStep.InstallDelta,
            PostSetupStep.ApplyDisplay
        },
        Edition.Mwse => new[]
        {
            PostSetupStep.InstallMwse, PostSetupStep.ApplyMcp, PostSetupStep.GenerateDistantLand,
            PostSetupStep.ApplyDisplay
        },
        _ => Array.Empty<PostSetupStep>()
    };

    /// <summary>
    /// Steps we can confirm 100% are applied (so the Run button is hidden once
    /// done). Others stay re-runnable.
    /// </summary>
    public static bool IsDefinitive(PostSetupStep step)
        => step is PostSetupStep.RepairPaths or PostSetupStep.ApplyDisplay;

    /// <summary>Steps that launch Mod Organizer (gated while MO2 is running).</summary>
    public static bool LaunchesMo2(PostSetupStep step)
        => step is PostSetupStep.ApplyMcp or PostSetupStep.GenerateDistantLand
            or PostSetupStep.DeltaMerge;

    public static string Label(PostSetupStep step) => step switch
    {
        PostSetupStep.RepairPaths => "Update Mod Organizer Paths",
        PostSetupStep.ApplyDisplay => "Apply Display Settings",
        PostSetupStep.InstallOpenMw => "Install OpenMW",
        PostSetupStep.InstallDelta => "Install Delta Plugin",
        PostSetupStep.InstallMwse => "Install MWSE",
        PostSetupStep.ApplyMcp => "Apply Morrowind Code Patch",
        PostSetupStep.GenerateDistantLand => "Generate Distant Land",
        PostSetupStep.DeltaMerge => "Merge plugins (Delta)",
        PostSetupStep.AddToSteam => "Add to Steam",
        _ => step.ToString()
    };

    /// <summary>Readiness of every applicable setup step for an edition (Play tab).</summary>
    public IReadOnlyList<StepStatus> Verify(Edition edition)
        => SetupStepsFor(edition)
            .Select(s => new StepStatus(s, Label(s), IsDone(edition, s)))
            .ToList();

    /// <summary>True when every applicable setup step is complete.</summary>
    public bool IsFullyConfigured(Edition edition) => Verify(edition).All(s => s.Done);

    public bool IsDone(Edition edition, PostSetupStep step)
    {
        var installDir = _installState.GetEditionInstallDir(edition);
        var paths = _config.Current.Mo2Paths;
        try
        {
            return step switch
            {
                PostSetupStep.RepairPaths => PathsRepaired(installDir),
                PostSetupStep.ApplyDisplay => DisplayApplied(edition, installDir),
                PostSetupStep.InstallOpenMw =>
                    File.Exists(Path.Combine(installDir, paths.OpenMwModDir, "OpenMW", "openmw.exe")),
                PostSetupStep.InstallDelta =>
                    File.Exists(Path.Combine(installDir, paths.DeltaModDir, "delta_plugin.exe")),
                PostSetupStep.InstallMwse =>
                    Directory.Exists(Path.Combine(installDir, paths.MwseModDir, "MWSE")),
                PostSetupStep.ApplyMcp => McpApplied(),
                PostSetupStep.GenerateDistantLand => DistantLandGenerated(installDir),
                PostSetupStep.DeltaMerge => DeltaMerged(installDir),
                PostSetupStep.AddToSteam => _steam.IsLauncherShortcutPresent(),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    // ---- individual checks ----

    private bool PathsRepaired(string installDir)
    {
        var gameDir = _gamePath.GameDirectory(_config.Current.GameExePath);
        if (gameDir is null)
        {
            return false;
        }
        var ini = AppPaths.Mo2Ini(installDir);
        if (!File.Exists(ini))
        {
            return false;
        }
        var text = File.ReadAllText(ini);
        // gamePath ByteArray points at the selected game dir, and the author's
        // Wabbajack staging path is gone.
        var wantEscaped = $"@ByteArray({gameDir.TrimEnd('\\', '/').Replace(@"\", @"\\")})";
        return text.Contains(wantEscaped, StringComparison.OrdinalIgnoreCase)
            && !text.Contains(@"Wabbajack\\Morrowind", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Wabbajack/Morrowind", StringComparison.OrdinalIgnoreCase);
    }

    private bool DisplayApplied(Edition edition, string installDir)
    {
        var d = _config.Current.Display;
        if (d.IsUnset)
        {
            return false;
        }

        if (edition == Edition.OpenMW)
        {
            var cfg = AppPaths.OpenMwSettingsCfg(installDir, _config.Current.Mo2Paths.OpenMwProfile);
            if (!File.Exists(cfg))
            {
                return false;
            }
            var lines = File.ReadAllLines(cfg);
            return IniEditor.GetValue(lines, "Video", "resolution x") == d.ResolutionX.ToString()
                && IniEditor.GetValue(lines, "Video", "resolution y") == d.ResolutionY.ToString()
                && ScaleMatches(IniEditor.GetValue(lines, "GUI", "scaling factor"), d.UiScale);
        }

        // MWSE: registry mode + MGE ini.
        var reg = _gamePath.ReadScreenSettings();
        if (reg is null || reg.Value.Width != d.ResolutionX ||
            reg.Value.Height != d.ResolutionY || reg.Value.RefreshHz != d.RefreshHz)
        {
            return false;
        }
        var mge = PostSetupConfigService.FindMgeIni(installDir, _config.Current.Mo2Paths.MgeConfigMod,
            _config.Current.Mo2Paths.MwseProfile);
        if (mge is null)
        {
            return false;
        }
        var mlines = File.ReadAllLines(mge);
        return ScaleMatches(IniEditor.GetValue(mlines, "Render State", "UI Scaling"), d.UiScale)
            && IniEditor.GetValue(mlines, "Global Graphics", "Refresh Rate") == d.RefreshHz.ToString();
    }

    private static bool ScaleMatches(string? value, double expected)
        => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
           && Math.Abs(v - expected) < 0.001;

    private bool McpApplied()
    {
        // Root Builder redirects MCP's output to overwrite, which the launcher
        // harvests into the MCP Generated Files mod. MCP backs up the unpatched
        // exe as Morrowind.Original.exe, so its presence there confirms a patch.
        var installDir = _installState.GetEditionInstallDir(Edition.Mwse);
        var paths = _config.Current.Mo2Paths;
        var mod = ResolveMod(installDir, paths.McpGeneratedFilesMod, paths.McpModTokens);
        return mod is not null
            && File.Exists(Path.Combine(mod, "Root", "Morrowind.Original.exe"));
    }

    /// <summary>
    /// Resolves a mod folder: prefers the exact configured name, then falls back to
    /// the first mod whose name contains all the given tokens (case-insensitive),
    /// so renames like "(Legacy)"→"(MWSE)" keep working.
    /// </summary>
    private static string? ResolveMod(string installDir, string configuredName, params string[] tokens)
    {
        var modsDir = AppPaths.Mo2ModsDir(installDir);
        if (!Directory.Exists(modsDir))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            var exact = Path.Combine(modsDir, configuredName);
            if (Directory.Exists(exact))
            {
                return exact;
            }
        }
        return FindGeneratedFilesMod(installDir, tokens);
    }

    /// <summary>
    /// Finds the enabled "… Generated Files" mod for a tool (e.g. MCP, MGE) by
    /// matching a mod folder name that contains all the given tokens
    /// (case-insensitive). Robust to name drift such as the "(MWSE)" suffix.
    /// </summary>
    public static string? FindGeneratedFilesMod(string installDir, params string[] tokens)
    {
        var modsDir = AppPaths.Mo2ModsDir(installDir);
        if (!Directory.Exists(modsDir))
        {
            return null;
        }
        foreach (var dir in Directory.GetDirectories(modsDir))
        {
            var name = Path.GetFileName(dir);
            if (tokens.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                return dir;
            }
        }
        return null;
    }

    private bool DistantLandGenerated(string installDir)
    {
        // MGE writes distant-land data to the virtual Data Files (mapped by MO2 to
        // overwrite), which the launcher harvests into the MGE "Generated Files"
        // mod at its data root — so the marker is "<mod>/<distantland>/…".
        var paths = _config.Current.Mo2Paths;
        var mod = ResolveMod(installDir, paths.MgeGeneratedFilesMod, paths.MgeModTokens);
        if (mod is null)
        {
            return false;
        }
        var distantland = Path.Combine(mod, paths.DistantLandSubdir);
        return File.Exists(Path.Combine(distantland, "world.dds"))
            || Directory.Exists(Path.Combine(distantland, "statics"));
    }

    private bool DeltaMerged(string installDir)
    {
        // delta_plugin merge writes its output omwaddon into the mod's Data Files.
        var deltaMod = Path.Combine(installDir, _config.Current.Mo2Paths.DeltaModDir);
        return Directory.Exists(deltaMod)
            && Directory.EnumerateFiles(deltaMod, "*.omwaddon", SearchOption.AllDirectories).Any();
    }
}
