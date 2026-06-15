using System.IO;
using Microsoft.Win32;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Resolves, validates and manages the vanilla Morrowind game path.
/// Resolution order: saved config -> launcher-local copy -> registry ->
/// (prompt handled by UI).
/// </summary>
public sealed class GamePathService
{
    private readonly ConfigService _config;

    public GamePathService(ConfigService config) => _config = config;

    /// <summary>True if the path points at an existing Morrowind.exe.</summary>
    public bool IsValidGameExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }
        return File.Exists(exePath) &&
               string.Equals(Path.GetFileName(exePath), "Morrowind.exe",
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The directory containing Morrowind.exe, or null.</summary>
    public string? GameDirectory(string? exePath)
        => IsValidGameExe(exePath) ? Path.GetDirectoryName(exePath) : null;

    /// <summary>
    /// Resolve the best available game exe path without prompting. Returns null if no
    /// saved, launcher-local-copy, or registry path is valid.
    /// </summary>
    public string? ResolveExisting()
    {
        var saved = _config.Current.GameExePath;
        if (IsValidGameExe(saved))
        {
            return saved;
        }

        // A clean copy already placed next to the launcher by "Copy game" — portable
        // and launcher-managed, so preferred over the registry's original install.
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

    /// <summary>
    /// Reads the Bethesda Softworks Morrowind "Installed Path" registry value.
    /// Uses the same hive/view order as the screen-settings read/write
    /// (<see cref="EnumerateRegistryTargets"/>) so detection and writes always
    /// agree on which key wins when the value exists in more than one view.
    /// </summary>
    public string? TryGetFromRegistry()
    {
        foreach (var (hive, view) in EnumerateRegistryTargets())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(
                    @"SOFTWARE\Bethesda Softworks\Morrowind");
                var installed = key?.GetValue("Installed Path") as string;
                if (!string.IsNullOrWhiteSpace(installed))
                {
                    var exe = Path.Combine(installed, "Morrowind.exe");
                    if (File.Exists(exe))
                    {
                        return exe;
                    }
                }
            }
            catch
            {
                // Ignore registry access issues and try the next target.
            }
        }
        return null;
    }

    /// <summary>True if the given game folder lives inside an MO2 install folder.</summary>
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

    public void SaveGamePath(string exePath)
    {
        _config.Current.GameExePath = exePath;
        _config.Save();
    }

    /// <summary>
    /// Writes Morrowind's screen resolution + refresh rate to the registry — the
    /// location vanilla Morrowind (and MGE XE's borderless window) reads the
    /// display mode from. Writes to whichever hive/view already holds the
    /// Bethesda Softworks\Morrowind key, falling back to HKLM 32-bit
    /// (WOW6432Node). Requires elevation (the launcher runs elevated).
    /// </summary>
    public bool WriteScreenSettings(int width, int height, int refreshHz)
    {
        const string subKey = @"SOFTWARE\Bethesda Softworks\Morrowind";

        // Prefer the hive/view that already has the key, so we update in place.
        foreach (var (hive, view) in EnumerateRegistryTargets())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey, writable: true);
                if (key is null)
                {
                    continue;
                }
                key.SetValue("Screen Width", width, RegistryValueKind.DWord);
                key.SetValue("Screen Height", height, RegistryValueKind.DWord);
                key.SetValue("Refresh Rate", refreshHz, RegistryValueKind.DWord);
                Logger.Info($"Wrote screen settings to registry ({hive}/{view}): " +
                            $"{width}x{height}@{refreshHz}.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Couldn't write screen settings to {hive}/{view}: {ex.Message}");
            }
        }

        // Not present anywhere: create under HKLM 32-bit (where Morrowind lives).
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.CreateSubKey(subKey, writable: true);
            key.SetValue("Screen Width", width, RegistryValueKind.DWord);
            key.SetValue("Screen Height", height, RegistryValueKind.DWord);
            key.SetValue("Refresh Rate", refreshHz, RegistryValueKind.DWord);
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
        const string subKey = @"SOFTWARE\Bethesda Softworks\Morrowind";
        foreach (var (hive, view) in EnumerateRegistryTargets())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue("Screen Width") is int w &&
                    key.GetValue("Screen Height") is int h)
                {
                    var hz = key.GetValue("Refresh Rate") as int? ?? 0;
                    return (w, h, hz);
                }
            }
            catch
            {
                // Try the next target.
            }
        }
        return null;
    }

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

    private static bool IsSubPath(string parent, string child)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(p, c, StringComparison.OrdinalIgnoreCase);
    }
}
