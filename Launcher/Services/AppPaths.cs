using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Centralises all well-known filesystem locations; the launcher is fully portable, so everything is relative to the executable and nothing is written outside its own folder.</summary>
/// <remarks>
/// Layout under the launcher's folder: the exe, <c>modorganizer/</c> (default MO2
/// install for the combined list), <c>morrowind/</c> (optional copied game),
/// <c>wabbajack/</c> (CLI), <c>downloads/</c> (archive cache), and
/// <c>launcher/</c> (config.json, logs/, lists/, steam/, the Steamworks shim,
/// webview2/). Because all paths are recomputed from the exe on launch, the whole
/// folder can be moved/copied; ModOrganizer.ini paths are repaired before every
/// launch (see <see cref="Mo2IniService"/>).
/// </remarks>
public static class AppPaths
{
    /// <summary>Defaults used before config loads, or if it never does.</summary>
    private static readonly LauncherConfig Fallback = new();

    /// <summary>Live config when loaded, else defaults (the first calls run before config loads).</summary>
    private static LauncherConfig Cfg => ConfigService.Instance?.Current ?? Fallback;

    /// <summary>The folder the launcher executable resides in (the portable root).</summary>
    public static string Root =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>Holds everything the launcher itself unpacks/keeps (config, logs, caches, etc.).</summary>
    public static string LauncherDataDir => Path.Combine(Root, Cfg.Paths.LauncherDirName);

    /// <summary>The wabbajack-cli install folder.</summary>
    public static string WabbajackDir => Path.Combine(Root, Cfg.Paths.WabbajackDirName);

    /// <summary>The shared Wabbajack archive download cache ("Clear Downloads" wipes this).</summary>
    public static string DownloadsDir => Path.Combine(Root, Cfg.Paths.DownloadsDirName);

    /// <summary>The folder log files are written to.</summary>
    public static string LogsDir => Path.Combine(LauncherDataDir, Cfg.Paths.LogsDirName);

    /// <summary>The live launcher config file.</summary>
    public static string ConfigFile => Path.Combine(LauncherDataDir, "config.json");

    /// <summary>An optional loose default-config override under <c>launcher/</c>; the real fallback is the copy embedded in the exe.</summary>
    public static string DefaultConfigFile => Path.Combine(LauncherDataDir, "config.default.json");

    /// <summary>DPAPI-encrypted Nexus token blob (portable but machine/user-bound).</summary>
    public static string NexusTokenFile => Path.Combine(LauncherDataDir, "nexus.token");

    /// <summary>Folder where Steam shortcut artwork + icon are extracted/kept.</summary>
    public static string SteamAssetsDir => Path.Combine(LauncherDataDir, "steam");

    /// <summary>The Steamworks shim DLL, extracted/downloaded on demand for playtime tracking.</summary>
    public static string SteamApiDll => Path.Combine(LauncherDataDir, Cfg.Steam.SteamApiDllName);

    /// <summary>The steam_appid.txt the Steamworks API reads from the presence helper's CWD.</summary>
    public static string SteamAppIdFile => Path.Combine(LauncherDataDir, "steam_appid.txt");

    /// <summary>WebView2 user-data folder for the Nexus OAuth login window.</summary>
    public static string WebView2Dir => Path.Combine(LauncherDataDir, "webview2");

    /// <summary>The folder the CLI is extracted into; Wabbajack ships it (with all dependencies) inside its release zip's <c>cli/</c> subfolder, so the exe runs from there.</summary>
    public static string WabbajackCliDir => Path.Combine(WabbajackDir, "cli");

    /// <summary>The wabbajack-cli.exe path.</summary>
    public static string WabbajackCliExe =>
        Path.Combine(WabbajackCliDir, "wabbajack-cli.exe");

    /// <summary>Cache of downloaded .wabbajack modlist files (installed via the CLI's <c>-w</c>), kept under <c>launcher/</c> — not in <see cref="DownloadsDir"/>, which "Clear Downloads" wipes.</summary>
    public static string ModlistCacheDir => Path.Combine(LauncherDataDir, "lists");

    /// <summary>Wabbajack's own per-user app data folder (%LOCALAPPDATA%\Wabbajack), where the CLI's encrypted Nexus token lives — outside our portable folder because it is user-bound and shared with any Wabbajack GUI install.</summary>
    public static string WabbajackAppDataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wabbajack");

    /// <summary>The encrypted token store folder Wabbajack uses.</summary>
    public static string WabbajackEncryptedDir =>
        Path.Combine(WabbajackAppDataDir, "encrypted");

    /// <summary>The encrypted Nexus OAuth token file the CLI consumes (in Wabbajack's store, user-bound).</summary>
    public static string WabbajackNexusTokenFile =>
        Path.Combine(WabbajackEncryptedDir, "nexus-oauth-info");

    /// <summary>Default per-edition install dir next to the launcher; legacy, since the combined list now uses one shared dir (<see cref="DefaultInstallDir"/>).</summary>
    public static string DefaultEditionInstallDir(Edition edition) =>
        Path.Combine(Root, edition.DisplayName());

    /// <summary>Default install dir for the single combined list (one MO2 instance with both profiles), next to the launcher.</summary>
    public static string DefaultInstallDir => Path.Combine(Root, Cfg.Paths.InstallDirName);

    /// <summary>The portable clean vanilla game copy made by "Copy game", distinct from <see cref="Mo2GameCopyDir"/> (a copy placed inside an MO2 install).</summary>
    public static string GameCopyDir => Path.Combine(Root, Cfg.Paths.GameCopyDirName);

    /// <summary>Morrowind.exe inside the portable game copy.</summary>
    public static string GameCopyExe => Path.Combine(GameCopyDir, Cfg.GameRegistry.GameExeName);

    /// <summary>ModOrganizer.ini for an edition install.</summary>
    public static string Mo2Ini(string editionInstallDir) =>
        Path.Combine(editionInstallDir, Cfg.Mo2.ModOrganizerIni);

    /// <summary>ModOrganizer.exe for an edition install.</summary>
    public static string Mo2Exe(string editionInstallDir) =>
        Path.Combine(editionInstallDir, Cfg.Mo2.ModOrganizerExe);

    /// <summary>The profile folder for a config-driven MO2 profile name, which must match the <c>-p</c> argument passed to ModOrganizer.exe.</summary>
    public static string Mo2ProfileDir(string editionInstallDir, string profileName) =>
        Path.Combine(editionInstallDir, "profiles", profileName);

    /// <summary>The MO2 mods folder for an edition install.</summary>
    public static string Mo2ModsDir(string editionInstallDir) =>
        Path.Combine(editionInstallDir, "mods");

    /// <summary>MO2's overwrite folder, where Root Builder redirects tool-modified files (e.g. MCP's patched Morrowind.exe) that the launcher then harvests into the matching "Generated Files" mod.</summary>
    public static string OverwriteDir(string editionInstallDir) =>
        Path.Combine(editionInstallDir, "overwrite");

    /// <summary>The profile's modlist.txt (mod enablement + priority order).</summary>
    public static string ModlistTxt(string editionInstallDir, string profileName) =>
        Path.Combine(Mo2ProfileDir(editionInstallDir, profileName), "modlist.txt");

    /// <summary>OpenMW user settings written by our editor.</summary>
    public static string OpenMwSettingsCfg(string editionInstallDir, string openMwProfileName) =>
        Path.Combine(Mo2ProfileDir(editionInstallDir, openMwProfileName), "settings.cfg");

    /// <summary>Morrowind.ini for the MWSE profile.</summary>
    public static string MorrowindIni(string editionInstallDir, string mwseProfileName) =>
        Path.Combine(Mo2ProfileDir(editionInstallDir, mwseProfileName), "Morrowind.ini");

    /// <summary>The copied vanilla game folder, when the user opts to copy into MO2.</summary>
    public static string Mo2GameCopyDir(string editionInstallDir) =>
        Path.Combine(editionInstallDir, "game");

    /// <summary>Creates the launcher data and logs folders if they don't exist.</summary>
    public static void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(LauncherDataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
