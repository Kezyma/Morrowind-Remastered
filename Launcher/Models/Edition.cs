namespace MorrowindRemasteredLauncher.Models;

/// <summary>
/// The two supported modlist editions. The Legacy edition exists in the
/// catalog but is intentionally not surfaced by this launcher.
/// </summary>
public enum Edition
{
    OpenMW,
    Mwse
}

public static class EditionExtensions
{
    /// <summary>
    /// The "machineURL" used in modlists.json / Wabbajack to identify the list.
    /// </summary>
    public static string MachineUrl(this Edition edition) => edition switch
    {
        Edition.OpenMW => "MorrowindRemasteredOpenMWEdition",
        Edition.Mwse => "MorrowindRemasteredMWSEEdition",
        _ => throw new ArgumentOutOfRangeException(nameof(edition))
    };

    /// <summary>
    /// The title used as the modlist key in modlists.json.
    /// </summary>
    public static string CatalogTitle(this Edition edition) => edition switch
    {
        Edition.OpenMW => "Morrowind Remastered - OpenMW Edition",
        Edition.Mwse => "Morrowind Remastered - MWSE Edition",
        _ => throw new ArgumentOutOfRangeException(nameof(edition))
    };

    /// <summary>
    /// Friendly display name shown in the UI.
    /// </summary>
    public static string DisplayName(this Edition edition) => edition switch
    {
        Edition.OpenMW => "OpenMW",
        Edition.Mwse => "MWSE",
        _ => throw new ArgumentOutOfRangeException(nameof(edition))
    };

    /// <summary>
    /// The MO2 executable name (used with moshortcut://) that launches the game.
    /// </summary>
    public static string Mo2PlayExecutableName(this Edition edition) => edition switch
    {
        Edition.OpenMW => "OpenMW",
        Edition.Mwse => "Morrowind",
        _ => throw new ArgumentOutOfRangeException(nameof(edition))
    };

    /// <summary>
    /// The actual game process name (no extension) launched inside MO2, used to
    /// detect when the game exits — MO2 stays open afterwards, so we can't rely on
    /// it. OpenMW runs <c>openmw.exe</c>; MWSE runs <c>Morrowind.exe</c>.
    /// </summary>
    public static string GameProcessName(this Edition edition) => edition switch
    {
        Edition.OpenMW => "openmw",
        Edition.Mwse => "Morrowind",
        _ => throw new ArgumentOutOfRangeException(nameof(edition))
    };
}
