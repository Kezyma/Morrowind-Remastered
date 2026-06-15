using System.Globalization;
using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Applies the launcher's display settings (resolution, refresh, UI scale) to an
/// edition's game configuration:
///   - OpenMW: <c>profiles/OpenMW/settings.cfg</c> (<c>[Video]</c> resolution,
///     <c>[GUI]</c> scaling factor).
///   - MWSE: the registry screen mode (where Morrowind/MGE read resolution +
///     refresh) and the active MGE config ini (<c>[Render State] UI Scaling</c>,
///     <c>[Global Graphics] Refresh Rate</c>).
/// Values come from <see cref="LauncherConfig.Display"/>, which is seeded from
/// the primary monitor on first use.
/// </summary>
public sealed class PostSetupConfigService
{
    private readonly ConfigService _config;
    private readonly InstallStateService _installState;
    private readonly GamePathService _gamePath;
    private readonly DisplayService _display;

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

    /// <summary>
    /// Returns the effective display settings, seeding config from the monitor
    /// the first time (and persisting the seed).
    /// </summary>
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

    private bool ApplyMwse(string installDir, DisplaySettings d)
    {
        // 1. Registry: where Morrowind + MGE's borderless window read the mode.
        var regOk = _gamePath.WriteScreenSettings(d.ResolutionX, d.ResolutionY, d.RefreshHz);

        // 2. MGE config ini: refresh rate + UI scaling.
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

    /// <summary>
    /// Ensures distant land is turned on in the active MGE config ini
    /// (<c>[Distant Land] Distant Land=On</c>). MGE XE flips this to Off while we
    /// drive it to (re)generate distant land — e.g. it disables distant land at
    /// startup when it finds the files missing/old — so we set it back on after
    /// generation. MWSE only (OpenMW has no MGE).
    /// </summary>
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

    /// <summary>
    /// Locates the active MGE config ini by scanning the MWSE profile's
    /// modlist.txt for an enabled MGE-configuration mod containing
    /// <c>Root/mge*/MGE.ini</c>. Names drift (e.g. the "(Legacy)" suffix), so
    /// this resolves it dynamically rather than via a fixed path.
    /// </summary>
    public static string? FindMgeIni(
        string installDir, string? preferredMod, string mwseProfileName)
    {
        var modsDir = AppPaths.Mo2ModsDir(installDir);

        // Prefer the configured MGE config mod, if present.
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

        // Enabled mods ('+' prefix), MGE-config first.
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
