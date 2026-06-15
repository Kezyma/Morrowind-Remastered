using System.IO;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Centralises all well-known filesystem locations. The launcher is fully
/// portable: EVERYTHING lives next to the executable, nothing is written
/// outside the launcher's own folder. The layout is:
///
///   &lt;LauncherDir&gt;/
///     MorrowindRemastered.exe
///     modorganizer/               (default MO2 install for the combined list)
///     morrowind/                  (optional copied vanilla game)
///     wabbajack/                  (wabbajack-cli install)
///     downloads/                  (Wabbajack archive download cache)
///     launcher/                   (everything else the launcher owns:)
///       config.json               (  live config + the optional default override)
///       logs/                     (  log files)
///       lists/                    (  cached .wabbajack modlist files)
///       steam/                    (  extracted Steam shortcut artwork + icon)
///       steam_api64.dll           (  extracted Steamworks shim)
///       webview2/                 (  Nexus OAuth login cache)
///
/// Because everything is relative to the executable, the whole folder can be
/// moved/copied and paths are recomputed on launch. ModOrganizer.ini paths are
/// repaired before every launch (see Mo2IniService).
/// </summary>
public static class AppPaths
{
    /// <summary>The folder the launcher executable resides in (the portable root).</summary>
    public static string Root =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    // ---- Top-level portable folders (siblings of the exe) ----

    /// <summary>Holds everything the launcher itself unpacks/keeps (config, logs, caches, etc.).</summary>
    public static string LauncherDataDir => Path.Combine(Root, "launcher");

    /// <summary>The wabbajack-cli install folder.</summary>
    public static string WabbajackDir => Path.Combine(Root, "wabbajack");

    /// <summary>The shared Wabbajack archive download cache ("Clear Downloads" wipes this).</summary>
    public static string DownloadsDir => Path.Combine(Root, "downloads");

    // ---- Inside launcher/ ----

    public static string LogsDir => Path.Combine(LauncherDataDir, "logs");

    public static string ConfigFile => Path.Combine(LauncherDataDir, "config.json");

    /// <summary>
    /// An optional loose default config under <c>launcher/</c>. The real fallback is the
    /// copy embedded in the exe; this only exists if a user drops one in to override it.
    /// </summary>
    public static string DefaultConfigFile => Path.Combine(LauncherDataDir, "config.default.json");

    /// <summary>DPAPI-encrypted Nexus token blob (portable but machine/user-bound).</summary>
    public static string NexusTokenFile => Path.Combine(LauncherDataDir, "nexus.token");

    /// <summary>Folder where Steam shortcut artwork + icon are extracted/kept.</summary>
    public static string SteamAssetsDir => Path.Combine(LauncherDataDir, "steam");

    /// <summary>The Steamworks shim DLL, extracted/downloaded on demand for playtime tracking.</summary>
    public static string SteamApiDll => Path.Combine(LauncherDataDir, "steam_api64.dll");

    /// <summary>The steam_appid.txt the Steamworks API reads from the presence helper's CWD.</summary>
    public static string SteamAppIdFile => Path.Combine(LauncherDataDir, "steam_appid.txt");

    /// <summary>WebView2 user-data folder for the Nexus OAuth login window.</summary>
    public static string WebView2Dir => Path.Combine(LauncherDataDir, "webview2");

    // ---- Wabbajack ----

    /// <summary>
    /// The folder the CLI is extracted into. Wabbajack ships the CLI inside its
    /// release zip in a <c>cli/</c> subfolder (with all its dependencies), so we
    /// extract that whole folder and run the exe from within it.
    /// </summary>
    public static string WabbajackCliDir => Path.Combine(WabbajackDir, "cli");

    public static string WabbajackCliExe =>
        Path.Combine(WabbajackCliDir, "wabbajack-cli.exe");

    /// <summary>
    /// Cache of downloaded .wabbajack modlist files (~5–22 MB each), installed
    /// via the CLI's <c>-w</c> flag. Kept under <c>launcher/</c> (not under
    /// <see cref="DownloadsDir"/>, the big archive cache wiped by "Clear Downloads").
    /// </summary>
    public static string ModlistCacheDir => Path.Combine(LauncherDataDir, "lists");

    /// <summary>
    /// Wabbajack's own per-user app data folder (%LOCALAPPDATA%\Wabbajack). The
    /// CLI reads its Nexus OAuth token from here; this is NOT inside our portable
    /// folder because the token is DPAPI-bound to the user and shared with any
    /// Wabbajack GUI install on the same machine.
    /// </summary>
    public static string WabbajackAppDataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wabbajack");

    /// <summary>The encrypted token store folder Wabbajack uses.</summary>
    public static string WabbajackEncryptedDir =>
        Path.Combine(WabbajackAppDataDir, "encrypted");

    /// <summary>The DPAPI-encrypted Nexus OAuth token file the CLI consumes.</summary>
    public static string WabbajackNexusTokenFile =>
        Path.Combine(WabbajackEncryptedDir, "nexus-oauth-info");

    // ---- Per-edition install dirs (default: a folder named per edition) ----

    /// <summary>
    /// Default install dir for an edition, next to the launcher
    /// (&lt;Root&gt;/OpenMW or &lt;Root&gt;/MWSE). Legacy: the combined list now uses a
    /// single shared install dir (<see cref="DefaultInstallDir"/>).
    /// </summary>
    public static string DefaultEditionInstallDir(Edition edition) =>
        Path.Combine(Root, edition.DisplayName());

    /// <summary>
    /// Default install dir for the single combined list (one MO2 instance with both
    /// the OpenMW and MWSE profiles), next to the launcher.
    /// </summary>
    public static string DefaultInstallDir => Path.Combine(Root, "modorganizer");

    /// <summary>
    /// The launcher-local clean vanilla game copy made by the "Copy game" feature
    /// (&lt;Root&gt;/morrowind), and its Morrowind.exe. Portable — lives next to the
    /// launcher — and distinct from <see cref="Mo2GameCopyDir"/> (a copy placed inside
    /// an MO2 install).
    /// </summary>
    public static string GameCopyDir => Path.Combine(Root, "morrowind");

    public static string GameCopyExe => Path.Combine(GameCopyDir, "Morrowind.exe");

    public static string Mo2Ini(string editionInstallDir) =>
        Path.Combine(editionInstallDir, "ModOrganizer.ini");

    public static string Mo2Exe(string editionInstallDir) =>
        Path.Combine(editionInstallDir, "ModOrganizer.exe");

    /// <summary>
    /// The profile folder for a given MO2 profile name. The profile name is
    /// config-driven (<see cref="Mo2Paths.ProfileName"/>) and must match the
    /// <c>-p</c> argument passed to ModOrganizer.exe.
    /// </summary>
    public static string Mo2ProfileDir(string editionInstallDir, string profileName) =>
        Path.Combine(editionInstallDir, "profiles", profileName);

    /// <summary>The MO2 mods folder for an edition install.</summary>
    public static string Mo2ModsDir(string editionInstallDir) =>
        Path.Combine(editionInstallDir, "mods");

    /// <summary>
    /// MO2's overwrite folder. Root Builder redirects files modified by tools
    /// (e.g. MCP's patched Morrowind.exe) here; the launcher harvests them into
    /// the matching "Generated Files" mod afterwards.
    /// </summary>
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

    public static void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(LauncherDataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
