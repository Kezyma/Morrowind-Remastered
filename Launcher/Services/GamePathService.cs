using System.IO;
using Microsoft.Win32;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Resolves, validates and manages the vanilla Morrowind game path (config, local copy, registry).</summary>
public sealed class GamePathService
{
    private readonly ConfigService _config;

    /// <summary>Creates the service over the shared config.</summary>
    public GamePathService(ConfigService config) => _config = config;

    /// <summary>True if the path points at an existing Morrowind game exe.</summary>
    public bool IsValidGameExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }
        return File.Exists(exePath) &&
               string.Equals(Path.GetFileName(exePath), _config.Current.GameRegistry.GameExeName,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The directory containing the game exe, or null.</summary>
    public string? GameDirectory(string? exePath)
        => IsValidGameExe(exePath) ? Path.GetDirectoryName(exePath) : null;

    /// <summary>Resolves the best available game exe (config, launcher-local copy, registry) without prompting.</summary>
    public string? ResolveExisting()
    {
        var saved = _config.Current.GameExePath;
        if (IsValidGameExe(saved))
        {
            return saved;
        }

        var localCopy = AppPaths.GameCopyExe;
        if (IsValidGameExe(localCopy))
        {
            return localCopy;
        }

        var fromRegistry = TryGetFromRegistry();
        if (IsValidGameExe(fromRegistry))
        {
            return fromRegistry;
        }

        return null;
    }

    /// <summary>Reads the Bethesda Morrowind "Installed Path" registry value to a game exe, or null.</summary>
    public string? TryGetFromRegistry()
    {
        var reg = _config.Current.GameRegistry;
        string? exe = null;
        ForFirstRegistryKey(writable: false, (key, _, _) =>
        {
            if (key.GetValue(reg.InstalledPathValue) is string installed &&
                !string.IsNullOrWhiteSpace(installed))
            {
                var candidate = Path.Combine(installed, reg.GameExeName);
                if (File.Exists(candidate))
                {
                    exe = candidate;
                    return true;
                }
            }
            return false;
        });
        return exe;
    }

    /// <summary>True if the given game folder lives inside the MO2 install folder.</summary>
    public bool IsInsideMo2(string? gameExePath)
    {
        var dir = GameDirectory(gameExePath);
        if (dir is null)
        {
            return false;
        }

        var record = _config.Current.Install;
        var installDir = string.IsNullOrWhiteSpace(record.InstallDir)
            ? AppPaths.DefaultInstallDir
            : record.InstallDir!;
        return IsSubPath(installDir, dir);
    }

    /// <summary>Persists the chosen game exe path to config.</summary>
    public void SaveGamePath(string exePath)
    {
        _config.Current.GameExePath = exePath;
        _config.Save();
    }

    /// <summary>
    /// Writes Morrowind's screen resolution + refresh rate to the registry — where vanilla
    /// Morrowind and MGE XE's borderless window read the display mode. Updates the hive/view
    /// already holding the key, else creates it under HKLM 32-bit. Requires elevation.
    /// </summary>
    public bool WriteScreenSettings(int width, int height, int refreshHz)
    {
        var reg = _config.Current.GameRegistry;

        var wrote = ForFirstRegistryKey(writable: true, (key, hive, view) =>
        {
            key.SetValue(reg.ScreenWidthValue, width, RegistryValueKind.DWord);
            key.SetValue(reg.ScreenHeightValue, height, RegistryValueKind.DWord);
            key.SetValue(reg.RefreshRateValue, refreshHz, RegistryValueKind.DWord);
            Logger.Info($"Wrote screen settings to registry ({hive}/{view}): {width}x{height}@{refreshHz}.");
            return true;
        });
        if (wrote)
        {
            return true;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.CreateSubKey(reg.SubKey, writable: true);
            key.SetValue(reg.ScreenWidthValue, width, RegistryValueKind.DWord);
            key.SetValue(reg.ScreenHeightValue, height, RegistryValueKind.DWord);
            key.SetValue(reg.RefreshRateValue, refreshHz, RegistryValueKind.DWord);
            Logger.Info($"Created registry screen settings: {width}x{height}@{refreshHz}.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't write Morrowind screen settings to registry", ex);
            return false;
        }
    }

    /// <summary>Reads the current registry screen settings, or null if absent.</summary>
    public (int Width, int Height, int RefreshHz)? ReadScreenSettings()
    {
        var reg = _config.Current.GameRegistry;
        (int Width, int Height, int RefreshHz)? result = null;
        ForFirstRegistryKey(writable: false, (key, _, _) =>
        {
            if (key.GetValue(reg.ScreenWidthValue) is int w &&
                key.GetValue(reg.ScreenHeightValue) is int h)
            {
                var hz = key.GetValue(reg.RefreshRateValue) as int? ?? 0;
                result = (w, h, hz);
                return true;
            }
            return false;
        });
        return result;
    }

    /// <summary>Opens the game registry key under each hive/view in order, stopping at the first action that succeeds.</summary>
    private bool ForFirstRegistryKey(bool writable, Func<RegistryKey, RegistryHive, RegistryView, bool> action)
    {
        foreach (var (hive, view) in EnumerateRegistryTargets())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(_config.Current.GameRegistry.SubKey, writable);
                if (key is not null && action(key, hive, view))
                {
                    return true;
                }
            }
            catch
            {
            }
        }
        return false;
    }

    /// <summary>The hive/view pairs probed for the Morrowind key, in a fixed precedence order.</summary>
    private static IEnumerable<(RegistryHive Hive, RegistryView View)> EnumerateRegistryTargets()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                yield return (hive, view);
            }
        }
    }

    /// <summary>True when <paramref name="child"/> is the same as or nested under <paramref name="parent"/>.</summary>
    private static bool IsSubPath(string parent, string child)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(p, c, StringComparison.OrdinalIgnoreCase);
    }
}
