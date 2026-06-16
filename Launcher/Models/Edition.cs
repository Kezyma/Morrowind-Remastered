using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.Models;

/// <summary>The two supported modlist editions (the catalog's Legacy edition is not surfaced).</summary>
public enum Edition
{
    OpenMW,
    Mwse
}

/// <summary>
/// Edition string projections, read from the <c>editions</c> config map with an in-code
/// fallback so they resolve before config loads or when the section is absent. The enum
/// itself stays the control-flow source of truth; only these strings are config-driven.
/// </summary>
public static class EditionExtensions
{
    private static readonly IReadOnlyDictionary<Edition, EditionProfile> Fallback =
        new Dictionary<Edition, EditionProfile>
        {
            [Edition.OpenMW] = new()
            {
                DisplayName = "OpenMW",
                MachineUrl = "MorrowindRemasteredOpenMWEdition",
                Mo2PlayExecutableName = "OpenMW",
                GameProcessName = "openmw"
            },
            [Edition.Mwse] = new()
            {
                DisplayName = "MWSE",
                MachineUrl = "MorrowindRemasteredMWSEEdition",
                Mo2PlayExecutableName = "Morrowind",
                GameProcessName = "Morrowind"
            },
        };

    /// <summary>The configured profile for an edition, or the in-code fallback when unset.</summary>
    private static EditionProfile Profile(this Edition edition)
    {
        var editions = ConfigService.Instance?.Current.Editions;
        if (editions is not null &&
            editions.TryGetValue(edition.ToString(), out var p) &&
            !string.IsNullOrWhiteSpace(p.DisplayName))
        {
            return p;
        }
        return Fallback[edition];
    }

    /// <summary>The "machineURL" used in modlists.json / Wabbajack to identify the list.</summary>
    public static string MachineUrl(this Edition edition) => edition.Profile().MachineUrl;

    /// <summary>Friendly display name shown in the UI (also the tools/setup/profile key).</summary>
    public static string DisplayName(this Edition edition) => edition.Profile().DisplayName;

    /// <summary>The MO2 executable name (used with moshortcut://) that launches the game.</summary>
    public static string Mo2PlayExecutableName(this Edition edition) => edition.Profile().Mo2PlayExecutableName;

    /// <summary>The game process name (no extension) used to detect when the game exits.</summary>
    public static string GameProcessName(this Edition edition) => edition.Profile().GameProcessName;
}
