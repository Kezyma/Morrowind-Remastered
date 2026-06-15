using System.Globalization;
using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Reads and writes the curated game settings (see <see cref="SettingsCatalog"/>)
/// for the selected edition, routing each value to its backing store: OpenMW's
/// settings.cfg, MGE.ini (a safe subset only), Morrowind.ini, or the registry
/// screen mode. Reuses <see cref="PostSetupConfigService.FindMgeIni"/>,
/// <see cref="AppPaths"/> and <see cref="GamePathService"/> so path/registry logic
/// stays single-sourced; resolution/refresh/UI-scale changes are mirrored into
/// <c>config.Display</c> to keep the seed/Play path consistent. Fail-soft: a
/// missing install or file logs and returns without throwing.
/// </summary>
public sealed class GameSettingsService
{
    private readonly ConfigService _config;
    private readonly InstallStateService _installState;
    private readonly GamePathService _gamePath;
    private readonly DisplayService _display;

    public GameSettingsService(
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
    /// The descriptors for an edition, with resolution/refresh dropdown options
    /// filled in from the current monitor's available modes.
    /// </summary>
    public IReadOnlyList<SettingDescriptor> GetDescriptors(Edition edition)
    {
        var modes = _display.EnumerateModes();

        var resOptions = modes
            .Select(m => (m.Width, m.Height))
            .Distinct()
            .OrderByDescending(p => (long)p.Width * p.Height)
            .Select(p => new SettingOption($"{p.Width} x {p.Height}", $"{p.Width}x{p.Height}"))
            .ToList();

        var hzOptions = modes
            .Select(m => m.RefreshHz)
            .Where(hz => hz > 0)
            .Distinct()
            .OrderByDescending(hz => hz)
            .Select(hz => new SettingOption($"{hz} Hz", hz.ToString(CultureInfo.InvariantCulture)))
            .ToList();

        var list = new List<SettingDescriptor>();
        foreach (var d in SettingsCatalog.For(edition))
        {
            list.Add(d.Id switch
            {
                SettingsCatalog.ResolutionIdValue => d with { Options = resOptions },
                SettingsCatalog.RefreshIdValue => d with { Options = hzOptions },
                _ => d
            });
        }
        return list;
    }

    /// <summary>
    /// Reads each setting's current stored token for the edition. Values map to the
    /// raw token in the file (or the "WIDTHxHEIGHT"/Hz synthesised from the
    /// registry); the row view-model decodes them. Missing files/keys yield null.
    /// </summary>
    public IReadOnlyDictionary<string, string?> LoadCurrent(Edition edition)
    {
        var result = new Dictionary<string, string?>();
        if (!_installState.IsMo2Present(edition))
        {
            Logger.Info($"Settings load skipped: {edition.DisplayName()} is not installed.");
            return result;
        }

        var installDir = _installState.GetEditionInstallDir(edition);
        var mo2 = _config.Current.Mo2Paths;

        var cfgLines = ReadLinesOrNull(AppPaths.OpenMwSettingsCfg(installDir, mo2.OpenMwProfile));
        string[]? mgeLines = null;
        string[]? mwLines = null;
        if (edition == Edition.Mwse)
        {
            var mge = PostSetupConfigService.FindMgeIni(installDir, mo2.MgeConfigMod, mo2.MwseProfile);
            mgeLines = mge is null ? null : ReadLinesOrNull(mge);
            mwLines = ReadLinesOrNull(AppPaths.MorrowindIni(installDir, mo2.MwseProfile));
        }
        var screen = _gamePath.ReadScreenSettings();

        foreach (var d in SettingsCatalog.For(edition))
        {
            result[d.Id] = ReadOne(d, edition, cfgLines, mgeLines, mwLines, screen);
        }
        return result;
    }

    /// <summary>
    /// Applies a single setting's new logical value to its backing store. Returns
    /// false (and logs) when the install/file is missing or the write fails.
    /// </summary>
    public bool Apply(Edition edition, SettingDescriptor descriptor, string value)
    {
        try
        {
            if (!_installState.IsMo2Present(edition))
            {
                Logger.Warn($"Settings apply skipped: {edition.DisplayName()} is not installed.");
                return false;
            }

            var installDir = _installState.GetEditionInstallDir(edition);
            var mo2 = _config.Current.Mo2Paths;

            // Display specials: registry-backed, with mirrors to config / MGE.
            if (descriptor.Id == SettingsCatalog.ResolutionIdValue)
            {
                return ApplyResolution(edition, installDir, mo2, value);
            }
            if (descriptor.Id == SettingsCatalog.RefreshIdValue)
            {
                return ApplyRefresh(installDir, mo2, value);
            }

            if (descriptor.Target.Store == SettingStore.RegistryScreen)
            {
                Logger.Warn($"Unexpected registry target for setting {descriptor.Id}.");
                return false;
            }

            var path = ResolveIniPath(descriptor.Target.File, installDir, mo2);
            if (path is null || !File.Exists(path))
            {
                Logger.Warn($"Config file for {descriptor.Id} not found ({descriptor.Target.File}); skipped.");
                return false;
            }

            var token = SettingValueCodec.ToStored(descriptor.Target.Format, value);
            var lines = File.ReadAllLines(path);
            (lines, var changed) = IniEditor.SetValue(
                lines, descriptor.Target.Section!, descriptor.Target.Key!, token);
            if (changed)
            {
                AtomicFile.WriteLines(path, lines);
                Logger.Info($"Set [{descriptor.Target.Section}] {descriptor.Target.Key} = " +
                            $"{token} in \"{path}\".");
            }

            MirrorToConfig(descriptor, value);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't apply setting {descriptor.Id}: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------- read helpers

    private static string? ReadOne(
        SettingDescriptor d, Edition edition,
        string[]? cfgLines, string[]? mgeLines, string[]? mwLines,
        (int Width, int Height, int RefreshHz)? screen)
    {
        if (d.Id == SettingsCatalog.ResolutionIdValue)
        {
            if (edition == Edition.OpenMW)
            {
                if (cfgLines is null)
                {
                    return null;
                }
                var x = IniEditor.GetValue(cfgLines, "Video", "resolution x")?.Trim();
                var y = IniEditor.GetValue(cfgLines, "Video", "resolution y")?.Trim();
                return string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y) ? null : $"{x}x{y}";
            }
            return screen is { } s ? $"{s.Width}x{s.Height}" : null;
        }
        if (d.Id == SettingsCatalog.RefreshIdValue)
        {
            return screen is { } s ? s.RefreshHz.ToString(CultureInfo.InvariantCulture) : null;
        }

        if (d.Target.Store == SettingStore.RegistryScreen)
        {
            return null; // only resolution/refresh use the registry; handled above
        }

        var lines = LinesFor(d.Target.File, cfgLines, mgeLines, mwLines);
        return lines is null ? null : IniEditor.GetValue(lines, d.Target.Section!, d.Target.Key!);
    }

    private static string[]? LinesFor(
        SettingFile file, string[]? cfgLines, string[]? mgeLines, string[]? mwLines) =>
        file switch
        {
            SettingFile.SettingsCfg => cfgLines,
            SettingFile.MgeIni => mgeLines,
            SettingFile.MorrowindIni => mwLines,
            _ => null
        };

    private static string[]? ReadLinesOrNull(string path)
    {
        if (!File.Exists(path))
        {
            Logger.Warn($"Settings file not found: \"{path}\".");
            return null;
        }
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't read \"{path}\": {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------ apply helpers

    private bool ApplyResolution(Edition edition, string installDir, Mo2Paths mo2, string value)
    {
        var (w, h) = ParseWxH(value);
        if (w <= 0 || h <= 0)
        {
            return false;
        }

        if (edition == Edition.OpenMW)
        {
            var cfg = AppPaths.OpenMwSettingsCfg(installDir, mo2.OpenMwProfile);
            if (!File.Exists(cfg))
            {
                Logger.Warn($"OpenMW settings.cfg not found at {cfg}.");
                return false;
            }
            var lines = File.ReadAllLines(cfg);
            (lines, var c1) = IniEditor.SetValue(lines, "Video", "resolution x",
                w.ToString(CultureInfo.InvariantCulture));
            (lines, var c2) = IniEditor.SetValue(lines, "Video", "resolution y",
                h.ToString(CultureInfo.InvariantCulture));
            if (c1 || c2)
            {
                AtomicFile.WriteLines(cfg, lines);
            }
        }
        else
        {
            var current = _gamePath.ReadScreenSettings();
            var hz = current?.RefreshHz ?? _config.Current.Display.RefreshHz;
            if (hz <= 0)
            {
                // Never write a 0 Hz refresh into the registry (breaks the game's
                // display mode); fall back to the primary monitor's rate.
                hz = _display.GetPrimaryMode().RefreshHz;
                Logger.Info($"No known refresh rate; using primary monitor's {hz} Hz for the resolution write.");
            }
            _gamePath.WriteScreenSettings(w, h, hz);
        }

        _config.Current.Display.ResolutionX = w;
        _config.Current.Display.ResolutionY = h;
        _config.Save();
        return true;
    }

    private bool ApplyRefresh(string installDir, Mo2Paths mo2, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hz) || hz <= 0)
        {
            return false;
        }

        var current = _gamePath.ReadScreenSettings();
        var w = current?.Width ?? _config.Current.Display.ResolutionX;
        var h = current?.Height ?? _config.Current.Display.ResolutionY;
        if (w <= 0 || h <= 0)
        {
            // Never write a 0x0 resolution into the registry; fall back to the
            // primary monitor's current resolution.
            var primary = _display.GetPrimaryMode();
            w = primary.Width;
            h = primary.Height;
            Logger.Info($"No known resolution; using primary monitor's {w}x{h} for the refresh write.");
        }
        _gamePath.WriteScreenSettings(w, h, hz);

        // Mirror into MGE's [Global Graphics] Refresh Rate (matches PostSetupConfigService).
        var mge = PostSetupConfigService.FindMgeIni(installDir, mo2.MgeConfigMod, mo2.MwseProfile);
        if (mge is not null && File.Exists(mge))
        {
            var lines = File.ReadAllLines(mge);
            (lines, var changed) = IniEditor.SetValue(lines, "Global Graphics", "Refresh Rate",
                hz.ToString(CultureInfo.InvariantCulture));
            if (changed)
            {
                AtomicFile.WriteLines(mge, lines);
            }
        }

        _config.Current.Display.RefreshHz = hz;
        _config.Save();
        return true;
    }

    private void MirrorToConfig(SettingDescriptor descriptor, string value)
    {
        if (descriptor.Id == SettingsCatalog.UiScaleIdValue &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) &&
            scale > 0)
        {
            _config.Current.Display.UiScale = scale;
            _config.Save();
        }
    }

    private static string? ResolveIniPath(SettingFile file, string installDir, Mo2Paths mo2) =>
        file switch
        {
            SettingFile.SettingsCfg => AppPaths.OpenMwSettingsCfg(installDir, mo2.OpenMwProfile),
            SettingFile.MorrowindIni => AppPaths.MorrowindIni(installDir, mo2.MwseProfile),
            SettingFile.MgeIni => PostSetupConfigService.FindMgeIni(
                installDir, mo2.MgeConfigMod, mo2.MwseProfile),
            _ => null
        };

    private static (int Width, int Height) ParseWxH(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (0, 0);
        }
        var parts = text.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
        {
            return (w, h);
        }
        return (0, 0);
    }
}
