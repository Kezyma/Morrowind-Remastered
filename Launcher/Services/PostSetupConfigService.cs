using System.Globalization;
using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Applies the launcher's display settings (resolution, refresh, UI scale) to an edition's game configuration.</summary>
/// <remarks>
/// OpenMW writes <c>settings.cfg</c> (<c>[Video]</c> resolution, <c>[GUI]</c>
/// scaling); MWSE writes the registry screen mode plus the active MGE config ini
/// (<c>[Render State] UI Scaling</c>, <c>[Global Graphics] Refresh Rate</c>).
/// Values come from <see cref="LauncherConfig.Display"/>, seeded from the primary
/// monitor on first use.
/// </remarks>
public sealed class PostSetupConfigService
{
    /// <summary>Persisted launcher config (display settings, MO2 paths).</summary>
    private readonly ConfigService _config;
    /// <summary>Resolves the shared install directory for an edition.</summary>
    private readonly InstallStateService _installState;
    /// <summary>Reads/writes the registry screen mode.</summary>
    private readonly GamePathService _gamePath;
    /// <summary>Queries the monitor for seeding display settings.</summary>
    private readonly DisplayService _display;

    /// <summary>Creates the service with its config and helper dependencies.</summary>
    public PostSetupConfigService(
        ConfigService config,
        InstallStateService installState,
        GamePathService gamePath,
        DisplayService display)
    {
        _config = config;
        _installState = installState;
        _gamePath = gamePath;
        _display = display;
    }

    /// <summary>Returns the effective display settings, seeding (and persisting) from the monitor on first use.</summary>
    public DisplaySettings EnsureDisplaySettings()
    {
        var d = _config.Current.Display;
        if (d.IsUnset)
        {
            var mode = _display.GetPrimaryMode();
            d.ResolutionX = mode.Width;
            d.ResolutionY = mode.Height;
            d.RefreshHz = mode.RefreshHz;
            d.UiScale = mode.RecommendedUiScale;
            _config.Save();
            Logger.Info($"Seeded display settings from monitor: " +
                        $"{d.ResolutionX}x{d.ResolutionY}@{d.RefreshHz}, UI {d.UiScale}.");
        }
        return d;
    }

    /// <summary>Applies the current display settings to the given edition's configs.</summary>
    public bool ApplyDisplay(Edition edition)
    {
        var d = EnsureDisplaySettings();
        var installDir = _installState.GetEditionInstallDir(edition);
        return edition == Edition.OpenMW
            ? ApplyOpenMw(installDir, d, _config.Current.Mo2Paths.OpenMwProfile)
            : ApplyMwse(installDir, d);
    }

    /// <summary>Writes resolution and UI scale into OpenMW's settings.cfg.</summary>
    private static bool ApplyOpenMw(string installDir, DisplaySettings d, string openMwProfile)
    {
        var cfg = AppPaths.OpenMwSettingsCfg(installDir, openMwProfile);
        if (!File.Exists(cfg))
        {
            Logger.Warn($"OpenMW settings.cfg not found at {cfg}.");
            return false;
        }

        var lines = File.ReadAllLines(cfg);
        var changed = false;
        (lines, var c1) = IniEditor.SetValue(lines, "Video", "resolution x",
            d.ResolutionX.ToString(CultureInfo.InvariantCulture));
        (lines, var c2) = IniEditor.SetValue(lines, "Video", "resolution y",
            d.ResolutionY.ToString(CultureInfo.InvariantCulture));
        (lines, var c3) = IniEditor.SetValue(lines, "GUI", "scaling factor",
            d.UiScale.ToString("0.0###", CultureInfo.InvariantCulture));
        changed = c1 || c2 || c3;

        if (changed)
        {
            AtomicFile.WriteLines(cfg, lines);
        }
        Logger.Info($"Applied OpenMW display config ({d.ResolutionX}x{d.ResolutionY}, " +
                    $"UI {d.UiScale}); changed={changed}.");
        return true;
    }

    /// <summary>Writes the registry screen mode plus refresh rate and UI scale into the MGE config ini.</summary>
    private bool ApplyMwse(string installDir, DisplaySettings d)
    {
        var regOk = _gamePath.WriteScreenSettings(d.ResolutionX, d.ResolutionY, d.RefreshHz);

        var mge = FindMgeIni(installDir, _config.Current.Mo2Paths.MgeConfigMod,
            _config.Current.Mo2Paths.MwseProfile);
        if (mge is null)
        {
            Logger.Warn("Active MGE config ini not found; skipped MGE display config.");
            return regOk;
        }

        var lines = File.ReadAllLines(mge);
        (lines, var c1) = IniEditor.SetValue(lines, "Render State", "UI Scaling",
            d.UiScale.ToString("0.0###", CultureInfo.InvariantCulture));
        (lines, var c2) = IniEditor.SetValue(lines, "Global Graphics", "Refresh Rate",
            d.RefreshHz.ToString(CultureInfo.InvariantCulture));
        if (c1 || c2)
        {
            AtomicFile.WriteLines(mge, lines);
        }
        Logger.Info($"Applied MGE display config (refresh {d.RefreshHz}, UI {d.UiScale}) " +
                    $"at \"{mge}\".");
        return regOk;
    }

    /// <summary>Turns distant land back on in the MGE config ini (MWSE only) after MGE XE disables it during generation.</summary>
    public bool EnableDistantLand(Edition edition)
    {
        if (edition != Edition.Mwse)
        {
            return true;
        }

        var installDir = _installState.GetEditionInstallDir(edition);
        var mge = FindMgeIni(installDir, _config.Current.Mo2Paths.MgeConfigMod,
            _config.Current.Mo2Paths.MwseProfile);
        if (mge is null)
        {
            Logger.Warn("Active MGE config ini not found; couldn't enable distant land.");
            return false;
        }

        var lines = File.ReadAllLines(mge);
        (lines, var changed) = IniEditor.SetValue(lines, "Distant Land", "Distant Land", "On");
        if (changed)
        {
            AtomicFile.WriteLines(mge, lines);
            Logger.Info($"Enabled distant land in \"{mge}\".");
        }
        else
        {
            Logger.Info("Distant land already enabled in MGE.ini.");
        }
        return true;
    }

    /// <summary>Locates the active MGE config ini by scanning the MWSE profile's enabled mods, since the mod name drifts.</summary>
    public static string? FindMgeIni(
        string installDir, string? preferredMod, string mwseProfileName)
    {
        var modsDir = AppPaths.Mo2ModsDir(installDir);

        if (!string.IsNullOrWhiteSpace(preferredMod) && Directory.Exists(modsDir))
        {
            foreach (var sub in new[] { "mge3", "MGE" })
            {
                var c = Path.Combine(modsDir, preferredMod, "Root", sub, "MGE.ini");
                if (File.Exists(c))
                {
                    return c;
                }
            }
        }

        var modlist = AppPaths.ModlistTxt(installDir, mwseProfileName);
        if (!File.Exists(modlist) || !Directory.Exists(modsDir))
        {
            return null;
        }

        var enabled = File.ReadAllLines(modlist)
            .Where(l => l.StartsWith('+'))
            .Select(l => l[1..].Trim())
            .Where(name => name.Contains("MGE", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name.Contains("Configuration", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var name in enabled)
        {
            foreach (var sub in new[] { "mge3", "MGE" })
            {
                var candidate = Path.Combine(modsDir, name, "Root", sub, "MGE.ini");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
