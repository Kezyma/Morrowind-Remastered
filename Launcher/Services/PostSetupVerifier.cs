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

    /// <summary>Optional: add the launcher to Steam as a non-Steam shortcut; never required, never auto-run, excluded from <see cref="PostSetupVerifier.SetupStepsFor"/>.</summary>
    AddToSteam
}

/// <summary>A step's readiness, derived from real files/values.</summary>
public sealed record StepStatus(PostSetupStep Step, string Label, bool Done);

/// <summary>Determines whether each post-install step has actually been completed by inspecting real files, registry values and config — never the cached <c>PostSetupComplete</c> flag.</summary>
/// <remarks>Backs the idempotent runner, the pre-launch gate, and the Tools panel.</remarks>
public sealed class PostSetupVerifier
{
    private readonly ConfigService _config;
    private readonly InstallStateService _installState;
    private readonly GamePathService _gamePath;
    private readonly SteamService _steam;

    /// <summary>Creates the verifier.</summary>
    public PostSetupVerifier(
        ConfigService config, InstallStateService installState, GamePathService gamePath,
        SteamService steam)
    {
        _config = config;
        _installState = installState;
        _gamePath = gamePath;
        _steam = steam;
    }

    /// <summary>The ordered per-edition setup steps shown on the Play tab and auto-run before launch (display settings last); excludes RepairPaths (part of Install), the Wabbajack install, and Delta merge (a manual tool, since the list already includes the patch).</summary>
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

    /// <summary>Steps we can confirm are fully applied (so the Run button hides once done); others stay re-runnable.</summary>
    public static bool IsDefinitive(PostSetupStep step)
        => step is PostSetupStep.RepairPaths or PostSetupStep.ApplyDisplay;

    /// <summary>Steps that launch Mod Organizer (gated while MO2 is running).</summary>
    public static bool LaunchesMo2(PostSetupStep step)
        => step is PostSetupStep.ApplyMcp or PostSetupStep.GenerateDistantLand
            or PostSetupStep.DeltaMerge;

    /// <summary>The user-facing label for a step.</summary>
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

    /// <summary>True when the given step is actually done for the edition, judged from real files/registry/config.</summary>
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

    /// <summary>True when ModOrganizer.ini's gamePath points at the selected game dir and the author's Wabbajack staging path is gone.</summary>
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
        var wantEscaped = $"@ByteArray({gameDir.TrimEnd('\\', '/').Replace(@"\", @"\\")})";
        return text.Contains(wantEscaped, StringComparison.OrdinalIgnoreCase)
            && !text.Contains(@"Wabbajack\\Morrowind", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Wabbajack/Morrowind", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the configured display settings are reflected in OpenMW's settings.cfg, or (for MWSE) the screen registry plus MGE ini.</summary>
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

    /// <summary>True when <paramref name="value"/> parses to within a small tolerance of <paramref name="expected"/>.</summary>
    private static bool ScaleMatches(string? value, double expected)
        => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
           && Math.Abs(v - expected) < 0.001;

    /// <summary>True when MCP has been applied, judged by the presence of its backed-up Morrowind.Original.exe in the harvested MCP Generated Files mod.</summary>
    private bool McpApplied()
    {
        var installDir = _installState.GetEditionInstallDir(Edition.Mwse);
        var paths = _config.Current.Mo2Paths;
        var mod = ResolveMod(installDir, paths.McpGeneratedFilesMod, paths.McpModTokens);
        return mod is not null
            && File.Exists(Path.Combine(mod, "Root", "Morrowind.Original.exe"));
    }

    /// <summary>Resolves a mod folder by exact configured name, falling back to a token match (tolerates renames).</summary>
    public static string? ResolveMod(string installDir, string configuredName, params string[] tokens)
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

    /// <summary>Finds the "… Generated Files" mod for a tool by matching a folder name that contains all the given tokens (case-insensitive); robust to name drift such as a "(MWSE)" suffix.</summary>
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

    /// <summary>True when distant land has been generated, judged by the harvested MGE Generated Files mod containing distant-land data (world.dds or a statics folder).</summary>
    private bool DistantLandGenerated(string installDir)
    {
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

    /// <summary>True when the Delta merge has run, judged by an output <c>.omwaddon</c> in the Delta mod's Data Files.</summary>
    private bool DeltaMerged(string installDir)
    {
        var deltaMod = Path.Combine(installDir, _config.Current.Mo2Paths.DeltaModDir);
        return Directory.Exists(deltaMod)
            && Directory.EnumerateFiles(deltaMod, "*.omwaddon", SearchOption.AllDirectories).Any();
    }
}
