using System.IO;
using System.Text.Json;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>The install lifecycle state of an edition.</summary>
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

/// <summary>An edition's resolved install state plus its installed and latest versions.</summary>
public sealed record EditionState(
    Edition Edition,
    InstallStatus Status,
    string? InstalledVersion,
    string? LatestVersion)
{
    /// <summary>True when the edition is installed in any state.</summary>
    public bool IsInstalled => Status is not InstallStatus.NotInstalled;
    /// <summary>True when the edition can be launched (ready, updatable, or needs setup).</summary>
    public bool IsPlayable => Status is InstallStatus.Ready or InstallStatus.UpdateAvailable
                                       or InstallStatus.InstalledNeedsSetup;
    /// <summary>True when a newer catalog version is available.</summary>
    public bool HasUpdate => Status is InstallStatus.UpdateAvailable;
}

/// <summary>Determines per-edition install status from disk and config, compared against the live catalog version.</summary>
public sealed class InstallStateService
{
    /// <summary>Persisted launcher config (install record, MO2 paths).</summary>
    private readonly ConfigService _config;
    /// <summary>Standalone vs embedded-in-MO2 environment.</summary>
    private readonly LauncherEnvironment _environment;
    /// <summary>Validates the vanilla game path (the "installed" signal when embedded).</summary>
    private readonly GamePathService _gamePath;

    /// <summary>Creates the service with its config and environment dependencies.</summary>
    public InstallStateService(
        ConfigService config, LauncherEnvironment environment, GamePathService gamePath)
    {
        _config = config;
        _environment = environment;
        _gamePath = gamePath;
    }

    /// <summary>Resolves the single combined-list install directory shared by both profiles (<paramref name="edition"/> no longer affects it).</summary>
    /// <remarks>Embedded: the enclosing MO2 folder; else the user override if set; else the default portable location next to the launcher.</remarks>
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

    /// <summary>True if MO2 (ModOrganizer.exe) is present for this edition.</summary>
    public bool IsMo2Present(Edition edition)
    {
        var dir = GetEditionInstallDir(edition);
        return File.Exists(AppPaths.Mo2Exe(dir));
    }

    /// <summary>Reads the installed modlist version from the Wabbajack <c>*.compiler_settings</c> JSON's root <c>Version</c> field (not <c>ModlistVersion</c>); null when absent/unreadable.</summary>
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

    /// <summary>Computes the full state for an edition, comparing against the (possibly null) catalog <paramref name="latestVersion"/>.</summary>
    /// <remarks>
    /// "Installed" needs MO2 on disk plus a positive completion signal (not just
    /// files a failed/cancelled run leaves behind): embedded installs use a valid
    /// vanilla game path; standalone uses a launcher-recorded timestamp or a
    /// version readable from the compiler_settings (for manual/debug copies).
    /// </remarks>
    public EditionState GetState(Edition edition, string? latestVersion)
    {
        var record = _config.Current.Install;

        var diskVersion = ReadInstalledVersion(edition);
        var installedVersion = diskVersion ?? record.InstalledVersion;

        var completed = _environment.IsEmbedded
            ? _gamePath.IsValidGameExe(_config.Current.GameExePath)
            : record.InstalledAt is not null || diskVersion is not null;

        if (!IsMo2Present(edition) || !completed)
        {
            return new EditionState(edition, InstallStatus.NotInstalled,
                installedVersion, latestVersion);
        }

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

    /// <summary>Compares dotted version strings (&lt;0 if a precedes b), falling back to ordinal comparison for non-numeric parts.</summary>
    public static int VersionCompare(string a, string b)
    {
        if (System.Version.TryParse(a, out var va) && System.Version.TryParse(b, out var vb))
        {
            return va.CompareTo(vb);
        }
        return string.CompareOrdinal(a, b);
    }
}
