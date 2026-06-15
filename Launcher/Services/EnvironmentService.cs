using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Describes how the launcher is running:
///  - Standalone: a normal portable launcher that installs lists into sibling
///    folders (OpenMW/, MWSE/).
///  - Embedded: the launcher executable was shipped inside an MO2 install (as
///    part of a Wabbajack list). In this mode it acts as a dedicated launcher
///    for that specific installed list - the install button and (usually) the
///    edition selector are hidden.
/// </summary>
public sealed class LauncherEnvironment
{
    public bool IsEmbedded { get; init; }

    /// <summary>
    /// When embedded, the MO2 install directory the launcher lives inside.
    /// </summary>
    public string? EmbeddedMo2Dir { get; init; }

    /// <summary>
    /// Editions detected as installed at the embedded location. Usually one, but
    /// both can share a folder, in which case the selector remains visible.
    /// </summary>
    public IReadOnlyList<Edition> EmbeddedEditions { get; init; } = Array.Empty<Edition>();

    /// <summary>The edition to default to when embedded (first detected).</summary>
    public Edition? PrimaryEmbeddedEdition =>
        EmbeddedEditions.Count > 0 ? EmbeddedEditions[0] : null;

    /// <summary>True when embedded and exactly one edition is present.</summary>
    public bool HideEditionSelector => IsEmbedded && EmbeddedEditions.Count <= 1;
}

/// <summary>
/// Detects the launcher environment by inspecting the folder the executable
/// resides in and its parents for an MO2 install.
/// </summary>
public sealed class EnvironmentService
{
    private readonly ConfigService _config;

    public EnvironmentService(ConfigService config) => _config = config;

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

    /// <summary>
    /// Walks up from <paramref name="start"/> looking for a folder containing
    /// ModOrganizer.exe. Returns it, or null if none found within a few levels.
    /// </summary>
    private static string? FindEnclosingMo2Dir(string start)
    {
        var dir = new DirectoryInfo(start);
        // Limit how far we climb to avoid false positives high up the tree.
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

    /// <summary>
    /// Determines which edition(s) an MO2 install hosts by checking for the
    /// edition-specific MO2 profiles.
    /// </summary>
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
