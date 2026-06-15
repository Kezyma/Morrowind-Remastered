using System.IO;
using System.Text.Json.Serialization;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.Models;

/// <summary>
/// Persisted launcher configuration, stored portably in &lt;LauncherDir&gt;/Config/config.json.
/// The Nexus API key is NOT stored here; it is held separately, DPAPI-encrypted.
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>Path to a vanilla Morrowind.exe selected by the user.</summary>
    [JsonPropertyName("gamePath")]
    public string? GameExePath { get; set; }

    /// <summary>The currently selected edition/profile in the UI (chosen on the Play tab).</summary>
    [JsonPropertyName("selectedEdition")]
    public Edition SelectedEdition { get; set; } = Edition.OpenMW;

    /// <summary>
    /// The single combined-list install. One Wabbajack list / one MO2 instance hosts
    /// both the OpenMW and MWSE profiles; the edition only selects the profile.
    /// </summary>
    [JsonPropertyName("install")]
    public InstallRecord Install { get; set; } = new();

    /// <summary>Display settings applied to the game configs (shared, one monitor).</summary>
    [JsonPropertyName("display")]
    public DisplaySettings Display { get; set; } = new();

    /// <summary>Download URLs for the post-install binary scripts (updatable).</summary>
    [JsonPropertyName("downloads")]
    public DownloadUrls Downloads { get; set; } = new();

    /// <summary>
    /// Where to install the modlist from. Config-only (no UI) — lets us test the
    /// combined list from a local file or a specific machineURL before it's published.
    /// </summary>
    [JsonPropertyName("installSource")]
    public InstallSource InstallSource { get; set; } = new();

    /// <summary>MO2-relative target paths / mod-folder names used by post-setup.</summary>
    [JsonPropertyName("mo2Paths")]
    public Mo2Paths Mo2Paths { get; set; } = new();

    /// <summary>
    /// The launchable tools shown on the Tools page, keyed by edition display name
    /// ("OpenMW"/"MWSE"). Entirely config-driven: each tool launches an MO2
    /// executable (or the MO2 GUI itself when the executable is blank) for that
    /// edition's profile. Seeded from the bundled config.default.json.
    /// </summary>
    [JsonPropertyName("tools")]
    public Dictionary<string, List<ToolDefinition>> Tools { get; set; } = new();

    /// <summary>Steam integration settings (playtime tracking).</summary>
    [JsonPropertyName("steam")]
    public SteamSettings Steam { get; set; } = new();
}

/// <summary>Steam integration preferences.</summary>
public sealed class SteamSettings
{
    /// <summary>
    /// When true, while the game runs after Play it is counted as Steam playtime
    /// for The Elder Scrolls III: Morrowind (appid 22320) on the signed-in account.
    /// </summary>
    [JsonPropertyName("trackPlaytime")]
    public bool TrackPlaytime { get; set; }
}

/// <summary>
/// One launchable Tools-page entry. Launched through MO2 for the selected
/// edition's profile. A blank <see cref="Executable"/> opens the MO2 GUI itself
/// (no tool); otherwise it is the MO2 customExecutable title to launch.
/// </summary>
public sealed class ToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>MO2 customExecutable title to launch; blank = open the MO2 GUI.</summary>
    [JsonPropertyName("executable")]
    public string? Executable { get; set; }
}

/// <summary>
/// Resolution / refresh / UI-scale applied to whichever edition is configured.
/// Zero/unset values are seeded from the primary monitor on first run.
/// </summary>
public sealed class DisplaySettings
{
    [JsonPropertyName("resolutionX")]
    public int ResolutionX { get; set; }

    [JsonPropertyName("resolutionY")]
    public int ResolutionY { get; set; }

    [JsonPropertyName("refreshHz")]
    public int RefreshHz { get; set; }

    [JsonPropertyName("uiScale")]
    public double UiScale { get; set; }

    /// <summary>True until seeded from the monitor (all values still at zero).</summary>
    [JsonIgnore]
    public bool IsUnset => ResolutionX <= 0 || ResolutionY <= 0 || RefreshHz <= 0 || UiScale <= 0;
}

/// <summary>
/// Download links for the OpenMW / Delta Plugin / MWSE install scripts. Defaults
/// mirror the in-list batch scripts; stored in config so they can be updated
/// without a launcher rebuild.
/// </summary>
public sealed class DownloadUrls
{
    [JsonPropertyName("openMwInstaller")]
    public string OpenMwInstaller { get; set; } =
        "https://github.com/OpenMW/openmw/releases/download/openmw-0.50.0/OpenMW-0.50.0-win64.exe";

    [JsonPropertyName("deltaPlugin")]
    public string DeltaPlugin { get; set; } =
        "https://gitlab.com/portmod/delta-plugin/-/releases/0.25.2/downloads/delta-plugin-0.25.2-windows-amd64.zip";

    [JsonPropertyName("mwseNightly")]
    public string MwseNightly { get; set; } =
        "https://github.com/MWSE/MWSE/releases/download/build-automatic/mwse.zip";

    /// <summary>
    /// Valve's redistributable <c>steam_api64.dll</c>, downloaded on demand only
    /// when Steam playtime tracking is used (not shipped in the build).
    /// </summary>
    [JsonPropertyName("steamApi")]
    public string SteamApi { get; set; } =
        "https://github.com/Kezyma/Morrowind-Remastered/releases/latest/download/steam_api64.dll";
}

/// <summary>
/// What the launcher knows about the single combined-list install. The
/// <see cref="InstallDir"/> holds one MO2 instance with both profiles; post-setup
/// completion is tracked per profile.
/// </summary>
public sealed class InstallRecord
{
    /// <summary>
    /// Install directory chosen by the user. When null, the default portable
    /// location next to the launcher is used (&lt;Root&gt;/modorganizer).
    /// </summary>
    [JsonPropertyName("installDir")]
    public string? InstallDir { get; set; }

    /// <summary>The modlist version recorded at install time (compared to catalog).</summary>
    [JsonPropertyName("installedVersion")]
    public string? InstalledVersion { get; set; }

    /// <summary>UTC timestamp of last successful install/update.</summary>
    [JsonPropertyName("installedAt")]
    public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Per-profile post-setup completion (keyed by edition display name).</summary>
    [JsonPropertyName("setupComplete")]
    public Dictionary<string, bool> SetupComplete { get; set; } = new();

    public bool GetSetupComplete(Edition edition) =>
        SetupComplete.TryGetValue(edition.DisplayName(), out var v) && v;

    public void SetSetupComplete(Edition edition, bool value) =>
        SetupComplete[edition.DisplayName()] = value;
}

/// <summary>
/// Source of the Wabbajack list to install. Resolved as a cascade, not an explicit
/// mode: a local <c>.wabbajack</c> file (when present on disk) overrides the online
/// list named by <see cref="MachineUrl"/>. The local file lets us test the combined
/// list before it's published; end users without it install from the gallery.
/// </summary>
public sealed class InstallSource
{
    /// <summary>Repository-qualified machineURL (e.g. "Kezyma/MorrowindRemastered") of the
    /// online Wabbajack list. The CLI resolves this from the gallery (<c>-m</c>).</summary>
    [JsonPropertyName("machineUrl")]
    public string? MachineUrl { get; set; }

    /// <summary>Path to a local .wabbajack file (absolute, or relative to the launcher exe).
    /// When the file exists it takes priority over <see cref="MachineUrl"/>.</summary>
    [JsonPropertyName("localFile")]
    public string? LocalFile { get; set; }

    /// <summary>True when an online list is configured.</summary>
    [JsonIgnore]
    public bool HasMachineUrl => !string.IsNullOrWhiteSpace(MachineUrl);

    /// <summary>
    /// Resolves <see cref="LocalFile"/> to an absolute path (kept as-is when rooted,
    /// else relative to the launcher exe) and returns it only if the file exists on
    /// disk; otherwise null. This is the first cascade tier — a missing/unset local
    /// file simply isn't selected (the online list is used instead).
    /// </summary>
    public string? ResolveExistingLocalFile()
    {
        if (string.IsNullOrWhiteSpace(LocalFile))
        {
            return null;
        }
        var path = Path.IsPathRooted(LocalFile)
            ? LocalFile
            : Path.Combine(AppPaths.Root, LocalFile);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>
/// Post-setup target paths/mod-folder names, relative to the MO2 install root.
/// Configurable so the post-setup steps don't hard-code list-specific names.
/// </summary>
public sealed class Mo2Paths
{
    /// <summary>The MO2 profile name (the <c>-p</c> argument) for the OpenMW edition.
    /// Must match the on-disk <c>profiles/&lt;name&gt;</c> folder.</summary>
    [JsonPropertyName("openMwProfile")]
    public string OpenMwProfile { get; set; } = "OpenMW";

    /// <summary>The MO2 profile name (the <c>-p</c> argument) for the MWSE edition.
    /// Must match the on-disk <c>profiles/&lt;name&gt;</c> folder.</summary>
    [JsonPropertyName("mwseProfile")]
    public string MwseProfile { get; set; } = "MWSE";

    /// <summary>
    /// Name of the Wabbajack <c>.compiler_settings</c> JSON the modlist writes into
    /// its install root; the launcher reads the installed version from its root
    /// <c>Version</c> field. Configurable because published lists use different names.
    /// </summary>
    [JsonPropertyName("compilerSettingsFile")]
    public string CompilerSettingsFile { get; set; } = "Morrowind Remastered.compiler_settings";

    /// <summary>The configured MO2 profile name (the <c>-p</c> argument) for an edition.</summary>
    public string ProfileName(Edition edition) =>
        edition == Edition.OpenMW ? OpenMwProfile : MwseProfile;

    [JsonPropertyName("mwseModDir")]
    public string MwseModDir { get; set; } = @"mods\MWSE";

    [JsonPropertyName("openMwModDir")]
    public string OpenMwModDir { get; set; } = @"mods\OpenMW";

    [JsonPropertyName("deltaModDir")]
    public string DeltaModDir { get; set; } = @"mods\Delta Plugin";

    [JsonPropertyName("mcpGeneratedFilesMod")]
    public string McpGeneratedFilesMod { get; set; } = "Morrowind Code Patch - Generated Files";

    /// <summary>
    /// Fallback name tokens for locating the MCP "Generated Files" mod when the exact
    /// <see cref="McpGeneratedFilesMod"/> folder isn't found — a mod whose folder name
    /// contains ALL tokens (case-insensitive) matches. Tolerates list renames.
    /// </summary>
    [JsonPropertyName("mcpModTokens")]
    public string[] McpModTokens { get; set; } = { "Morrowind Code Patch", "Generated Files" };

    [JsonPropertyName("mgeGeneratedFilesMod")]
    public string MgeGeneratedFilesMod { get; set; } = "MGE XE - Generated Files (MWSE)";

    /// <summary>Fallback name tokens for locating the MGE "Generated Files" mod (see <see cref="McpModTokens"/>).</summary>
    [JsonPropertyName("mgeModTokens")]
    public string[] MgeModTokens { get; set; } = { "MGE XE", "Generated Files" };

    [JsonPropertyName("mgeConfigMod")]
    public string MgeConfigMod { get; set; } = "MGE XE Distant Land Configuration (MWSE)";

    [JsonPropertyName("distantLandSubdir")]
    public string DistantLandSubdir { get; set; } = "distantland";
}
