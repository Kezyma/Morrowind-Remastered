using System.IO;
using System.Text.Json.Serialization;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.Models;

/// <summary>Persisted launcher configuration, stored portably in &lt;LauncherDir&gt;/Config/config.json.</summary>
/// <remarks>The Nexus API key is NOT stored here; it is held separately, DPAPI-encrypted.</remarks>
public sealed class LauncherConfig
{
    /// <summary>Path to a vanilla Morrowind.exe selected by the user.</summary>
    [JsonPropertyName("gamePath")]
    public string? GameExePath { get; set; }

    /// <summary>The currently selected edition/profile in the UI (chosen on the Play tab).</summary>
    [JsonPropertyName("selectedEdition")]
    public Edition SelectedEdition { get; set; } = Edition.OpenMW;

    /// <summary>The single combined-list install; one Wabbajack list / MO2 instance hosts both profiles, the edition only selects the profile.</summary>
    [JsonPropertyName("install")]
    public InstallRecord Install { get; set; } = new();

    /// <summary>Display settings applied to the game configs (shared, one monitor).</summary>
    [JsonPropertyName("display")]
    public DisplaySettings Display { get; set; } = new();

    /// <summary>Download URLs for the post-install binary scripts (updatable).</summary>
    [JsonPropertyName("downloads")]
    public DownloadUrls Downloads { get; set; } = new();

    /// <summary>Where to install the modlist from; config-only (no UI), for testing a list from a local file or machineURL before it's published.</summary>
    [JsonPropertyName("installSource")]
    public InstallSource InstallSource { get; set; } = new();

    /// <summary>MO2-relative target paths / mod-folder names used by post-setup.</summary>
    [JsonPropertyName("mo2Paths")]
    public Mo2Paths Mo2Paths { get; set; } = new();

    /// <summary>Launchable Tools-page entries keyed by edition display name; each launches an MO2 executable (or the MO2 GUI when blank) for that edition's profile, seeded from the bundled config.default.json.</summary>
    [JsonPropertyName("tools")]
    public Dictionary<string, List<ToolDefinition>> Tools { get; set; } = new();

    /// <summary>Steam integration settings (playtime tracking, app id, shortcut, artwork).</summary>
    [JsonPropertyName("steam")]
    public SteamSettings Steam { get; set; } = new();

    /// <summary>Per-edition identity strings (display name, machineURL, MO2/game executable names).</summary>
    [JsonPropertyName("editions")]
    public Dictionary<string, EditionProfile> Editions { get; set; } = new();

    /// <summary>Wabbajack repository, gallery, CLI-release and catalog settings.</summary>
    [JsonPropertyName("wabbajack")]
    public WabbajackSettings Wabbajack { get; set; } = new();

    /// <summary>Nexus Mods OAuth endpoints and client settings.</summary>
    [JsonPropertyName("nexus")]
    public NexusSettings Nexus { get; set; } = new();

    /// <summary>MO2 launch settings (instance/profile/exe names and the run monitor interval).</summary>
    [JsonPropertyName("mo2")]
    public Mo2LaunchSettings Mo2 { get; set; } = new();

    /// <summary>Bethesda Morrowind registry key/value names used for game detection and display.</summary>
    [JsonPropertyName("gameRegistry")]
    public GameRegistrySettings GameRegistry { get; set; } = new();

    /// <summary>Top-level portable folder/file names next to the launcher exe.</summary>
    [JsonPropertyName("paths")]
    public PathSettings Paths { get; set; } = new();

    /// <summary>Window titles, button labels and timing for the MCP/MGE GUI automation.</summary>
    [JsonPropertyName("toolAutomation")]
    public ToolAutomationSettings ToolAutomation { get; set; } = new();
}

/// <summary>Per-edition identity strings (display name, machineURL, MO2/game executable names).</summary>
/// <remarks><see cref="DisplayName"/> doubles as the key in the <c>tools</c> map, <c>install.setupComplete</c>, and the MO2 profile names — keep it in sync with those when editing.</remarks>
public sealed class EditionProfile
{
    /// <summary>Edition display name; doubles as the key in the tools map / install.setupComplete / MO2 profile names.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>Repository-qualified Wabbajack machineURL for this edition.</summary>
    [JsonPropertyName("machineUrl")]
    public string MachineUrl { get; set; } = "";

    /// <summary>MO2 customExecutable title (used with moshortcut://) that launches the game.</summary>
    [JsonPropertyName("mo2PlayExecutableName")]
    public string Mo2PlayExecutableName { get; set; } = "";

    /// <summary>The actual game process name (no extension) used to detect when the game exits.</summary>
    [JsonPropertyName("gameProcessName")]
    public string GameProcessName { get; set; } = "";
}

/// <summary>Wabbajack repository/gallery, CLI-release and catalog settings.</summary>
public sealed class WabbajackSettings
{
    /// <summary>Repository these lists are published under; the CLI keys featured lists as <c>&lt;RepositoryName&gt;/&lt;machineURL&gt;</c>.</summary>
    [JsonPropertyName("repositoryName")]
    public string RepositoryName { get; set; } = "Kezyma";

    /// <summary>Retries when the CLI fails transiently while resolving the list from the gallery.</summary>
    [JsonPropertyName("maxResolveAttempts")]
    public int MaxResolveAttempts { get; set; } = 4;

    /// <summary>File name the online list is downloaded to in the modlist cache (the CLI's <c>-w</c> target).</summary>
    [JsonPropertyName("combinedListFileName")]
    public string CombinedListFileName { get; set; } = "combined.wabbajack";

    /// <summary>URL of the modlist catalog (modlists.json) fetched for version/metadata.</summary>
    [JsonPropertyName("catalogUrl")]
    public string CatalogUrl { get; set; } =
        "https://raw.githubusercontent.com/Kezyma/Morrowind-Remastered/main/modlists.json";

    /// <summary>GitHub API URL for the latest wabbajack-cli release.</summary>
    [JsonPropertyName("latestReleaseApi")]
    public string LatestReleaseApi { get; set; } =
        "https://api.github.com/repos/wabbajack-tools/wabbajack/releases/latest";
}

/// <summary>Nexus Mods OAuth endpoints and client settings (mirrors Wabbajack's own client).</summary>
public sealed class NexusSettings
{
    /// <summary>Base URL of the Nexus OAuth endpoints.</summary>
    [JsonPropertyName("oauthBase")]
    public string OAuthBase { get; set; } = "https://users.nexusmods.com/oauth";

    /// <summary>OAuth client id (reuses Wabbajack's registered client).</summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = "wabbajack";

    /// <summary>Loopback OAuth redirect URI watched by the login popup.</summary>
    [JsonPropertyName("redirectUri")]
    public string RedirectUri { get; set; } = "https://127.0.0.1:1234";

    /// <summary>Requested OAuth scopes.</summary>
    [JsonPropertyName("scopes")]
    public string Scopes { get; set; } = "public openid profile";

    /// <summary>Host of the redirect URI; the WebView2 popup intercepts navigations here.</summary>
    [JsonPropertyName("redirectHost")]
    public string RedirectHost { get; set; } = "127.0.0.1";
}

/// <summary>MO2 launch settings (instance/exe/ini/process names and the run-monitor interval).</summary>
public sealed class Mo2LaunchSettings
{
    /// <summary>Portable MO2 instance name (both editions ship portable.txt).</summary>
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; set; } = "Portable";

    /// <summary>MO2 executable file name.</summary>
    [JsonPropertyName("modOrganizerExe")]
    public string ModOrganizerExe { get; set; } = "ModOrganizer.exe";

    /// <summary>MO2 settings file name (paths rewritten by post-setup).</summary>
    [JsonPropertyName("modOrganizerIni")]
    public string ModOrganizerIni { get; set; } = "ModOrganizer.ini";

    /// <summary>ModOrganizer process name (no extension) polled to detect when MO2 has exited.</summary>
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "ModOrganizer";

    /// <summary>How often (seconds) to poll for MO2's exit after a launch.</summary>
    [JsonPropertyName("monitorPollSeconds")]
    public double MonitorPollSeconds { get; set; } = 1.5;
}

/// <summary>Bethesda Morrowind registry key/value names used for game detection and display settings.</summary>
public sealed class GameRegistrySettings
{
    /// <summary>Registry subkey under HKLM for Bethesda Morrowind.</summary>
    [JsonPropertyName("subKey")]
    public string SubKey { get; set; } = @"SOFTWARE\Bethesda Softworks\Morrowind";

    /// <summary>Value name holding the game install path.</summary>
    [JsonPropertyName("installedPathValue")]
    public string InstalledPathValue { get; set; } = "Installed Path";

    /// <summary>Value name for screen width.</summary>
    [JsonPropertyName("screenWidthValue")]
    public string ScreenWidthValue { get; set; } = "Screen Width";

    /// <summary>Value name for screen height.</summary>
    [JsonPropertyName("screenHeightValue")]
    public string ScreenHeightValue { get; set; } = "Screen Height";

    /// <summary>Value name for refresh rate.</summary>
    [JsonPropertyName("refreshRateValue")]
    public string RefreshRateValue { get; set; } = "Refresh Rate";

    /// <summary>Vanilla game executable file name.</summary>
    [JsonPropertyName("gameExeName")]
    public string GameExeName { get; set; } = "Morrowind.exe";
}

/// <summary>Names of the top-level portable folders/files next to the launcher exe.</summary>
public sealed class PathSettings
{
    /// <summary>Default MO2 install folder name.</summary>
    [JsonPropertyName("installDirName")]
    public string InstallDirName { get; set; } = "modorganizer";

    /// <summary>Folder name for the launcher's copy of the base game.</summary>
    [JsonPropertyName("gameCopyDirName")]
    public string GameCopyDirName { get; set; } = "morrowind";

    /// <summary>Launcher data folder name (config, logs).</summary>
    [JsonPropertyName("launcherDirName")]
    public string LauncherDirName { get; set; } = "launcher";

    /// <summary>Folder name for the cached wabbajack-cli.</summary>
    [JsonPropertyName("wabbajackDirName")]
    public string WabbajackDirName { get; set; } = "wabbajack";

    /// <summary>Folder name for downloaded binaries/archives.</summary>
    [JsonPropertyName("downloadsDirName")]
    public string DownloadsDirName { get; set; } = "downloads";

    /// <summary>Logs subfolder name under the launcher dir.</summary>
    [JsonPropertyName("logsDirName")]
    public string LogsDirName { get; set; } = "logs";

    /// <summary>Runtime log file name.</summary>
    [JsonPropertyName("logFileName")]
    public string LogFileName { get; set; } = "morrowindremastered.log";
}

/// <summary>GUI-automation strings and timings for the off-screen MCP/MGE tool drivers.</summary>
public sealed class ToolAutomationSettings
{
    /// <summary>Morrowind Code Patch automation settings.</summary>
    [JsonPropertyName("mcp")]
    public McpAutomationSettings Mcp { get; set; } = new();

    /// <summary>MGE XE automation settings.</summary>
    [JsonPropertyName("mge")]
    public MgeAutomationSettings Mge { get; set; } = new();
}

/// <summary>Morrowind Code Patch automation: the Apply button, log markers and waits.</summary>
public sealed class McpAutomationSettings
{
    /// <summary>Label of the apply-patches button to click.</summary>
    [JsonPropertyName("applyButton")]
    public string ApplyButton { get; set; } = "Apply chosen patches";

    /// <summary>Log-pane phrases that indicate success.</summary>
    [JsonPropertyName("successPhrases")]
    public string[] SuccessPhrases { get; set; } = { "Patch succeeded", "succeeded" };

    /// <summary>Log-pane phrases that indicate failure.</summary>
    [JsonPropertyName("failurePhrases")]
    public string[] FailurePhrases { get; set; } = { "patch failed", "error", "cannot patch", "unable" };

    /// <summary>Seconds to wait for the MCP window to appear.</summary>
    [JsonPropertyName("windowWaitSeconds")]
    public int WindowWaitSeconds { get; set; } = 45;

    /// <summary>Seconds to wait for a success/failure log line.</summary>
    [JsonPropertyName("logWaitSeconds")]
    public int LogWaitSeconds { get; set; } = 45;

    /// <summary>Polling interval in milliseconds.</summary>
    [JsonPropertyName("pollMs")]
    public int PollMs { get; set; } = 500;
}

/// <summary>MGE XE distant-land automation: window title, wizard button labels and waits.</summary>
public sealed class MgeAutomationSettings
{
    /// <summary>Title of the MGE XE main window.</summary>
    [JsonPropertyName("mainWindowTitle")]
    public string MainWindowTitle { get; set; } = "Graphics Extender";

    /// <summary>MGE XE process name (no extension).</summary>
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "MGEXEgui";

    /// <summary>Win32 class name of the tab control.</summary>
    [JsonPropertyName("tabClass")]
    public string TabClass { get; set; } = "SysTabControl32";

    /// <summary>Index of the Distant Land tab.</summary>
    [JsonPropertyName("distantLandTabIndex")]
    public int DistantLandTabIndex { get; set; } = 1;

    /// <summary>Label of the generator-wizard button.</summary>
    [JsonPropertyName("generatorWizardButton")]
    public string GeneratorWizardButton { get; set; } = "generator wizard";

    /// <summary>Labels of dialog buttons to dismiss during the wizard.</summary>
    [JsonPropertyName("dismissLabels")]
    public string[] DismissLabels { get; set; } = { "Yes", "OK", "Continue" };

    /// <summary>Labels of startup popups to dismiss before the main window.</summary>
    [JsonPropertyName("startupDismissLabels")]
    public string[] StartupDismissLabels { get; set; } = { "OK" };

    /// <summary>Label of the Yes button.</summary>
    [JsonPropertyName("yesButton")]
    public string YesButton { get; set; } = "Yes";

    /// <summary>Label of the Continue button.</summary>
    [JsonPropertyName("continueButton")]
    public string ContinueButton { get; set; } = "Continue";

    /// <summary>Label of the run-steps button.</summary>
    [JsonPropertyName("runStepsButton")]
    public string RunStepsButton { get; set; } = "Run above steps";

    /// <summary>Label of the Finish button (enabled only when generation completes).</summary>
    [JsonPropertyName("finishButton")]
    public string FinishButton { get; set; } = "Finish";

    /// <summary>Seconds to wait for the main window.</summary>
    [JsonPropertyName("windowWaitSeconds")]
    public int WindowWaitSeconds { get; set; } = 45;

    /// <summary>Seconds to wait from clicking Yes to the Continue button.</summary>
    [JsonPropertyName("yesToContinueWaitSeconds")]
    public int YesToContinueWaitSeconds { get; set; } = 20;

    /// <summary>Seconds to wait from Continue to the run-steps button.</summary>
    [JsonPropertyName("continueToRunWaitSeconds")]
    public int ContinueToRunWaitSeconds { get; set; } = 40;

    /// <summary>Seconds to wait after pressing run-steps.</summary>
    [JsonPropertyName("runStepsWaitSeconds")]
    public int RunStepsWaitSeconds { get; set; } = 20;

    /// <summary>Maximum minutes to wait for distant-land generation.</summary>
    [JsonPropertyName("generationTimeoutMinutes")]
    public int GenerationTimeoutMinutes { get; set; } = 90;

    /// <summary>Main-loop polling interval in milliseconds.</summary>
    [JsonPropertyName("mainPollMs")]
    public int MainPollMs { get; set; } = 500;

    /// <summary>Pause after dismissing a dialog, in milliseconds.</summary>
    [JsonPropertyName("dialogDismissPauseMs")]
    public int DialogDismissPauseMs { get; set; } = 400;

    /// <summary>Pause after switching tabs, in milliseconds.</summary>
    [JsonPropertyName("tabSwitchPauseMs")]
    public int TabSwitchPauseMs { get; set; } = 800;

    /// <summary>Polling interval while waiting for the wizard button, in milliseconds.</summary>
    [JsonPropertyName("wizardClickPollMs")]
    public int WizardClickPollMs { get; set; } = 1200;

    /// <summary>Polling interval during generation, in milliseconds.</summary>
    [JsonPropertyName("generationPollMs")]
    public int GenerationPollMs { get; set; } = 3000;

    /// <summary>Pause after clicking Finish, in milliseconds.</summary>
    [JsonPropertyName("finishPauseMs")]
    public int FinishPauseMs { get; set; } = 1500;
}

/// <summary>Steam integration preferences (playtime tracking, app id, shortcut, artwork, detection).</summary>
public sealed class SteamSettings
{
    /// <summary>When true, the game's run time after Play is counted as Steam playtime for the app id below.</summary>
    [JsonPropertyName("trackPlaytime")]
    public bool TrackPlaytime { get; set; }

    /// <summary>Steam app id whose session is held for playtime tracking (22320 = Morrowind).</summary>
    [JsonPropertyName("morrowindAppId")]
    public uint MorrowindAppId { get; set; } = 22320;

    /// <summary>Name shown for the non-Steam launcher shortcut.</summary>
    [JsonPropertyName("shortcutAppName")]
    public string ShortcutAppName { get; set; } = "The Elder Scrolls III: Morrowind Remastered";

    /// <summary>Seconds to wait for the Steam client to exit when restarting it.</summary>
    [JsonPropertyName("shutdownTimeoutSeconds")]
    public int ShutdownTimeoutSeconds { get; set; } = 20;

    /// <summary>The Steam client executable file name.</summary>
    [JsonPropertyName("steamExeName")]
    public string SteamExeName { get; set; } = "steam.exe";

    /// <summary>Embedded/loose resource name of the shortcut icon.</summary>
    [JsonPropertyName("iconAssetName")]
    public string IconAssetName { get; set; } = "icon.ico";

    /// <summary>Embedded/loose resource name of the Steamworks shim DLL.</summary>
    [JsonPropertyName("steamApiDllName")]
    public string SteamApiDllName { get; set; } = "steam_api64.dll";

    /// <summary>Registry locations probed (in order) to find the Steam install folder.</summary>
    [JsonPropertyName("registryPaths")]
    public List<SteamRegistryPath> RegistryPaths { get; set; } = new()
    {
        new() { Hive = "CurrentUser", View = "Registry64", SubKey = @"Software\Valve\Steam", ValueName = "SteamPath" },
        new() { Hive = "CurrentUser", View = "Registry32", SubKey = @"Software\Valve\Steam", ValueName = "SteamPath" },
        new() { Hive = "LocalMachine", View = "Registry32", SubKey = @"SOFTWARE\WOW6432Node\Valve\Steam", ValueName = "InstallPath" },
        new() { Hive = "LocalMachine", View = "Registry64", SubKey = @"SOFTWARE\Valve\Steam", ValueName = "InstallPath" },
    };

    /// <summary>Source-name stems for each library-artwork slot placed in Steam's grid folder.</summary>
    [JsonPropertyName("artwork")]
    public SteamArtworkSettings Artwork { get; set; } = new();
}

/// <summary>One registry location (hive/view/key/value) probed to locate the Steam install.</summary>
public sealed class SteamRegistryPath
{
    /// <summary>Registry hive name, parsed as <c>RegistryHive</c> (e.g. "CurrentUser", "LocalMachine").</summary>
    [JsonPropertyName("hive")]
    public string Hive { get; set; } = "";

    /// <summary>Registry view name, parsed as <c>RegistryView</c> (e.g. "Registry32", "Registry64").</summary>
    [JsonPropertyName("view")]
    public string View { get; set; } = "";

    /// <summary>Registry subkey to read.</summary>
    [JsonPropertyName("subKey")]
    public string SubKey { get; set; } = "";

    /// <summary>Value name holding the Steam install path.</summary>
    [JsonPropertyName("valueName")]
    public string ValueName { get; set; } = "";
}

/// <summary>Source-name stems for each Steam library-artwork slot (first match wins, .png/.jpg/.jpeg).</summary>
public sealed class SteamArtworkSettings
{
    /// <summary>Source-name stems for the wide capsule artwork.</summary>
    [JsonPropertyName("capsuleWide")]
    public string[] CapsuleWideStems { get; set; } = { "Steam Capsule Wide", "header" };

    /// <summary>Source-name stems for the capsule (box-art) artwork.</summary>
    [JsonPropertyName("capsule")]
    public string[] CapsuleStems { get; set; } = { "Steam Capsule", "boxart" };

    /// <summary>Source-name stems for the hero artwork.</summary>
    [JsonPropertyName("hero")]
    public string[] HeroStems { get; set; } = { "Steam Hero", "hero" };

    /// <summary>Source-name stems for the logo artwork.</summary>
    [JsonPropertyName("logo")]
    public string[] LogoStems { get; set; } = { "Steam Logo", "logo" };
}

/// <summary>One launchable Tools-page entry, launched through MO2 for the selected edition's profile.</summary>
public sealed class ToolDefinition
{
    /// <summary>Display name of the tool.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Short description shown to the user.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>MO2 customExecutable title to launch; blank = open the MO2 GUI.</summary>
    [JsonPropertyName("executable")]
    public string? Executable { get; set; }
}

/// <summary>Resolution / refresh / UI-scale applied to whichever edition is configured; zero/unset values are seeded from the primary monitor on first run.</summary>
public sealed class DisplaySettings
{
    /// <summary>Horizontal resolution in pixels.</summary>
    [JsonPropertyName("resolutionX")]
    public int ResolutionX { get; set; }

    /// <summary>Vertical resolution in pixels.</summary>
    [JsonPropertyName("resolutionY")]
    public int ResolutionY { get; set; }

    /// <summary>Refresh rate in Hz.</summary>
    [JsonPropertyName("refreshHz")]
    public int RefreshHz { get; set; }

    /// <summary>Interface scale factor.</summary>
    [JsonPropertyName("uiScale")]
    public double UiScale { get; set; }

    /// <summary>True until seeded from the monitor (all values still at zero).</summary>
    [JsonIgnore]
    public bool IsUnset => ResolutionX <= 0 || ResolutionY <= 0 || RefreshHz <= 0 || UiScale <= 0;
}

/// <summary>Download links for the OpenMW / Delta Plugin / MWSE binaries; defaults mirror the in-list batch scripts, stored in config so they can be updated without a launcher rebuild.</summary>
public sealed class DownloadUrls
{
    /// <summary>OpenMW installer download URL.</summary>
    [JsonPropertyName("openMwInstaller")]
    public string OpenMwInstaller { get; set; } =
        "https://github.com/OpenMW/openmw/releases/download/openmw-0.50.0/OpenMW-0.50.0-win64.exe";

    /// <summary>Delta Plugin archive download URL.</summary>
    [JsonPropertyName("deltaPlugin")]
    public string DeltaPlugin { get; set; } =
        "https://gitlab.com/portmod/delta-plugin/-/releases/0.25.2/downloads/delta-plugin-0.25.2-windows-amd64.zip";

    /// <summary>MWSE nightly build archive download URL.</summary>
    [JsonPropertyName("mwseNightly")]
    public string MwseNightly { get; set; } =
        "https://github.com/MWSE/MWSE/releases/download/build-automatic/mwse.zip";
}

/// <summary>What the launcher knows about the single combined-list install; <see cref="InstallDir"/> holds one MO2 instance with both profiles, post-setup completion tracked per profile.</summary>
public sealed class InstallRecord
{
    /// <summary>Install directory chosen by the user; null = the default portable location next to the launcher (&lt;Root&gt;/modorganizer).</summary>
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

    /// <summary>Whether post-setup has completed for the given edition's profile.</summary>
    public bool GetSetupComplete(Edition edition) =>
        SetupComplete.TryGetValue(edition.DisplayName(), out var v) && v;

    /// <summary>Records post-setup completion for the given edition's profile.</summary>
    public void SetSetupComplete(Edition edition, bool value) =>
        SetupComplete[edition.DisplayName()] = value;
}

/// <summary>Source of the Wabbajack list to install, resolved as a cascade: a present local <c>.wabbajack</c> file overrides the online list named by <see cref="MachineUrl"/>.</summary>
/// <remarks>The local file lets us test the combined list before it's published; end users without it install from the gallery.</remarks>
public sealed class InstallSource
{
    /// <summary>Repository-qualified machineURL of the online list; the CLI resolves it from the gallery (<c>-m</c>).</summary>
    [JsonPropertyName("machineUrl")]
    public string? MachineUrl { get; set; }

    /// <summary>Path to a local .wabbajack file (absolute or relative to the launcher exe); takes priority over <see cref="MachineUrl"/> when it exists.</summary>
    [JsonPropertyName("localFile")]
    public string? LocalFile { get; set; }

    /// <summary>True when an online list is configured.</summary>
    [JsonIgnore]
    public bool HasMachineUrl => !string.IsNullOrWhiteSpace(MachineUrl);

    /// <summary>Returns <see cref="LocalFile"/> as an absolute path if it exists on disk, else null (the first cascade tier; a missing/unset file falls through to the online list).</summary>
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

/// <summary>Post-setup target paths/mod-folder names relative to the MO2 install root, configurable so post-setup steps don't hard-code list-specific names.</summary>
public sealed class Mo2Paths
{
    /// <summary>OpenMW MO2 profile name (the <c>-p</c> argument); must match the on-disk <c>profiles/&lt;name&gt;</c> folder.</summary>
    [JsonPropertyName("openMwProfile")]
    public string OpenMwProfile { get; set; } = "OpenMW";

    /// <summary>MWSE MO2 profile name (the <c>-p</c> argument); must match the on-disk <c>profiles/&lt;name&gt;</c> folder.</summary>
    [JsonPropertyName("mwseProfile")]
    public string MwseProfile { get; set; } = "MWSE";

    /// <summary>Name of the Wabbajack <c>.compiler_settings</c> JSON the launcher reads the installed version from; configurable because published lists use different names.</summary>
    [JsonPropertyName("compilerSettingsFile")]
    public string CompilerSettingsFile { get; set; } = "Morrowind Remastered.compiler_settings";

    /// <summary>The configured MO2 profile name (the <c>-p</c> argument) for an edition.</summary>
    public string ProfileName(Edition edition) =>
        edition == Edition.OpenMW ? OpenMwProfile : MwseProfile;

    /// <summary>MO2-relative folder where MWSE binaries are placed.</summary>
    [JsonPropertyName("mwseModDir")]
    public string MwseModDir { get; set; } = @"mods\MWSE";

    /// <summary>MO2-relative folder where OpenMW binaries are placed.</summary>
    [JsonPropertyName("openMwModDir")]
    public string OpenMwModDir { get; set; } = @"mods\OpenMW";

    /// <summary>MO2-relative folder where the Delta Plugin binary is placed.</summary>
    [JsonPropertyName("deltaModDir")]
    public string DeltaModDir { get; set; } = @"mods\Delta Plugin";

    /// <summary>Mod folder that harvests MCP's overwrite output.</summary>
    [JsonPropertyName("mcpGeneratedFilesMod")]
    public string McpGeneratedFilesMod { get; set; } = "Morrowind Code Patch - Generated Files";

    /// <summary>Fallback name tokens (all must match, case-insensitive) for locating the MCP "Generated Files" mod when the exact folder isn't found; tolerates list renames.</summary>
    [JsonPropertyName("mcpModTokens")]
    public string[] McpModTokens { get; set; } = { "Morrowind Code Patch", "Generated Files" };

    /// <summary>Mod folder that harvests MGE XE's overwrite output.</summary>
    [JsonPropertyName("mgeGeneratedFilesMod")]
    public string MgeGeneratedFilesMod { get; set; } = "MGE XE - Generated Files (MWSE)";

    /// <summary>Fallback name tokens for locating the MGE "Generated Files" mod (see <see cref="McpModTokens"/>).</summary>
    [JsonPropertyName("mgeModTokens")]
    public string[] MgeModTokens { get; set; } = { "MGE XE", "Generated Files" };

    /// <summary>Mod folder holding the MGE XE distant-land configuration.</summary>
    [JsonPropertyName("mgeConfigMod")]
    public string MgeConfigMod { get; set; } = "MGE XE Distant Land Configuration (MWSE)";

    /// <summary>Subfolder under the config mod containing generated distant-land data.</summary>
    [JsonPropertyName("distantLandSubdir")]
    public string DistantLandSubdir { get; set; } = "distantland";
}
