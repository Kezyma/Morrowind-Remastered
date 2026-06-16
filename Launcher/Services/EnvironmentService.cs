using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Describes how the launcher is running: standalone, or embedded inside an MO2 install.</summary>
/// <remarks>
/// Standalone installs lists into sibling folders. Embedded means the executable
/// was shipped inside an MO2 install (as part of a Wabbajack list), so it acts as
/// a dedicated launcher for that list — the install button and (usually) the
/// edition selector are hidden.
/// </remarks>
public sealed class LauncherEnvironment
{
    /// <summary>True when the launcher is embedded inside an MO2 install.</summary>
    public bool IsEmbedded { get; init; }

    /// <summary>When embedded, the MO2 install directory the launcher lives inside.</summary>
    public string? EmbeddedMo2Dir { get; init; }

    /// <summary>Editions detected at the embedded location; usually one, but both can share a folder.</summary>
    public IReadOnlyList<Edition> EmbeddedEditions { get; init; } = Array.Empty<Edition>();

    /// <summary>The edition to default to when embedded (first detected).</summary>
    public Edition? PrimaryEmbeddedEdition =>
        EmbeddedEditions.Count > 0 ? EmbeddedEditions[0] : null;

    /// <summary>True when embedded and exactly one edition is present.</summary>
    public bool HideEditionSelector => IsEmbedded && EmbeddedEditions.Count <= 1;
}

/// <summary>Detects the launcher environment by checking the executable's folder and its parents for an enclosing MO2 install.</summary>
public sealed class EnvironmentService
{
    /// <summary>Persisted launcher config (MO2 profile names).</summary>
    private readonly ConfigService _config;

    /// <summary>Creates the service over the launcher config.</summary>
    public EnvironmentService(ConfigService config) => _config = config;

    /// <summary>Detects whether the launcher is standalone or embedded, and which editions are present.</summary>
    public LauncherEnvironment Detect()
    {
        var mo2Dir = FindEnclosingMo2Dir(AppPaths.Root);
        if (mo2Dir is null)
        {
            return new LauncherEnvironment { IsEmbedded = false };
        }

        var editions = DetectEditions(mo2Dir);
        Logger.Info($"Embedded mode: MO2 at '{mo2Dir}', editions=[{string.Join(", ", editions)}]");

        return new LauncherEnvironment
        {
            IsEmbedded = true,
            EmbeddedMo2Dir = mo2Dir,
            EmbeddedEditions = editions
        };
    }

    /// <summary>Walks up from <paramref name="start"/> for a folder containing ModOrganizer.exe, capping the climb to avoid false positives high up the tree.</summary>
    private static string? FindEnclosingMo2Dir(string start)
    {
        var dir = new DirectoryInfo(start);
        for (var depth = 0; dir is not null && depth < 4; depth++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ModOrganizer.exe")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Determines which edition(s) an MO2 install hosts by checking for each edition's MO2 profile.</summary>
    private List<Edition> DetectEditions(string mo2Dir)
    {
        var found = new List<Edition>();
        foreach (var edition in new[] { Edition.OpenMW, Edition.Mwse })
        {
            var profileDir = AppPaths.Mo2ProfileDir(
                mo2Dir, _config.Current.Mo2Paths.ProfileName(edition));
            if (Directory.Exists(profileDir))
            {
                found.Add(edition);
            }
        }
        return found;
    }
}
