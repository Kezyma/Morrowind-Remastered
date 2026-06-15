using System.IO;
using System.Text.Json;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

public enum InstallStatus
{
    /// <summary>No install root configured, or MO2 not present for this edition.</summary>
    NotInstalled,

    /// <summary>MO2 present but post-setup automation has not been run.</summary>
    InstalledNeedsSetup,

    /// <summary>Installed and post-setup complete; up to date with catalog.</summary>
    Ready,

    /// <summary>Installed and ready, but a newer catalog version is available.</summary>
    UpdateAvailable
}

public sealed record EditionState(
    Edition Edition,
    InstallStatus Status,
    string? InstalledVersion,
    string? LatestVersion)
{
    public bool IsInstalled => Status is not InstallStatus.NotInstalled;
    public bool IsPlayable => Status is InstallStatus.Ready or InstallStatus.UpdateAvailable
                                       or InstallStatus.InstalledNeedsSetup;
    public bool HasUpdate => Status is InstallStatus.UpdateAvailable;
}

/// <summary>
/// Determines per-edition install status by inspecting disk and the persisted
/// config, comparing against the live catalog version.
/// </summary>
public sealed class InstallStateService
{
    private readonly ConfigService _config;
    private readonly LauncherEnvironment _environment;
    private readonly GamePathService _gamePath;

    public InstallStateService(
        ConfigService config, LauncherEnvironment environment, GamePathService gamePath)
    {
        _config = config;
        _environment = environment;
        _gamePath = gamePath;
    }

    /// <summary>
    /// Resolves the single combined-list install directory (one MO2 instance with
    /// both profiles). The <paramref name="edition"/> is accepted for call-site
    /// convenience but no longer affects the directory — both profiles share it:
    ///  - Embedded: the MO2 folder the launcher lives in.
    ///  - User override if set.
    ///  - Otherwise the default portable location next to the launcher.
    /// </summary>
    public string GetEditionInstallDir(Edition edition)
    {
        if (_environment.IsEmbedded && _environment.EmbeddedMo2Dir is not null)
        {
            return _environment.EmbeddedMo2Dir;
        }

        var record = _config.Current.Install;
        if (!string.IsNullOrWhiteSpace(record.InstallDir))
        {
            return record.InstallDir!;
        }
        return AppPaths.DefaultInstallDir;
    }

    /// <summary>
    /// Returns true if MO2 is present for this edition (ModOrganizer.exe exists).
    /// </summary>
    public bool IsMo2Present(Edition edition)
    {
        var dir = GetEditionInstallDir(edition);
        return File.Exists(AppPaths.Mo2Exe(dir));
    }

    /// <summary>
    /// Reads the installed modlist version from the Wabbajack
    /// <c>*.compiler_settings</c> JSON in the install dir (its root <c>Version</c>
    /// field — NOT <c>ModlistVersion</c>). Tries the configured file name first,
    /// then any <c>*.compiler_settings</c> in the folder. Null when absent/unreadable.
    /// </summary>
    public string? ReadInstalledVersion(Edition edition)
    {
        try
        {
            var dir = GetEditionInstallDir(edition);
            if (!Directory.Exists(dir))
            {
                return null;
            }

            var configured = Path.Combine(dir, _config.Current.Mo2Paths.CompilerSettingsFile);
            var file = File.Exists(configured)
                ? configured
                : Directory.EnumerateFiles(dir, "*.compiler_settings").FirstOrDefault();
            if (file is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("Version", out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                var version = v.GetString();
                return string.IsNullOrWhiteSpace(version) ? null : version;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't read installed version from compiler_settings: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Computes the full state for an edition. <paramref name="latestVersion"/>
    /// is the catalog version (may be null if the catalog is unreachable).
    /// </summary>
    public EditionState GetState(Edition edition, string? latestVersion)
    {
        var record = _config.Current.Install;

        // Installed version comes from the modlist's own compiler_settings; fall
        // back to whatever we recorded at install time.
        var diskVersion = ReadInstalledVersion(edition);
        var installedVersion = diskVersion ?? record.InstalledVersion;

        // "Installed" needs MO2 on disk plus a positive completion signal — not
        // just files a failed/cancelled run can leave behind. Embedded installs
        // aren't run by the launcher (no InstalledAt), so a valid vanilla game
        // path is the signal there. Standalone: a launcher-recorded timestamp, OR
        // — for installs the launcher didn't perform (manual/debug copies) — a
        // modlist version readable from the compiler_settings on disk.
        var completed = _environment.IsEmbedded
            ? _gamePath.IsValidGameExe(_config.Current.GameExePath)
            : record.InstalledAt is not null || diskVersion is not null;

        if (!IsMo2Present(edition) || !completed)
        {
            return new EditionState(edition, InstallStatus.NotInstalled,
                installedVersion, latestVersion);
        }

        // Post-setup completion is tracked per profile (the install is shared).
        if (!record.GetSetupComplete(edition))
        {
            return new EditionState(edition, InstallStatus.InstalledNeedsSetup,
                installedVersion, latestVersion);
        }

        var status = InstallStatus.Ready;
        if (!string.IsNullOrWhiteSpace(latestVersion) &&
            !string.IsNullOrWhiteSpace(installedVersion) &&
            VersionCompare(installedVersion!, latestVersion!) < 0)
        {
            status = InstallStatus.UpdateAvailable;
        }

        return new EditionState(edition, status, installedVersion, latestVersion);
    }

    /// <summary>
    /// Compares dotted version strings (e.g. "3.0.5"). Returns &lt;0 if a precedes b.
    /// Falls back to ordinal comparison for non-numeric parts.
    /// </summary>
    public static int VersionCompare(string a, string b)
    {
        if (System.Version.TryParse(a, out var va) && System.Version.TryParse(b, out var vb))
        {
            return va.CompareTo(vb);
        }
        return string.CompareOrdinal(a, b);
    }
}
