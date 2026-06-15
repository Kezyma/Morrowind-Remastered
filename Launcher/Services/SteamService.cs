using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Steam integration, all best-effort / fail-soft:
///   - detect the Steam install (registry) and whether the client is running;
///   - add the launcher to Steam as a non-Steam shortcut (binary shortcuts.vdf);
///   - while the game runs, hold a Steamworks session for Morrowind (appid 22320)
///     so Steam logs the playtime. The steam_api64.dll is downloaded on demand
///     (not shipped) and only used when Steam is running and tracking is enabled.
/// </summary>
public sealed class SteamService
{
    /// <summary>Steam appid for The Elder Scrolls III: Morrowind — used for playtime tracking.</summary>
    public const uint MorrowindAppId = 22320;

    private const string ShortcutAppName = "The Elder Scrolls III: Morrowind Remastered";

    private readonly HttpClient _http;
    private readonly ConfigService _config;

    public SteamService(HttpClient http, ConfigService config)
    {
        _http = http;
        _config = config;
    }

    // -------------------------------------------------------------- Detection

    /// <summary>The Steam install folder, or null when Steam isn't installed.</summary>
    public string? SteamPath
    {
        get
        {
            foreach (var (hive, view, sub, name) in new[]
            {
                (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamPath"),
                (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Valve\Steam", "SteamPath"),
                (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
            })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(sub);
                    if (key?.GetValue(name) is string p && !string.IsNullOrWhiteSpace(p))
                    {
                        var full = Path.GetFullPath(p);
                        if (Directory.Exists(full))
                        {
                            return full;
                        }
                    }
                }
                catch
                {
                    // Ignore and try the next hive/view.
                }
            }
            return null;
        }
    }

    /// <summary>True when Steam is installed on this machine.</summary>
    public bool IsInstalled => SteamPath is not null;

    /// <summary>Full path to <c>steam.exe</c>, or null when Steam isn't installed.</summary>
    public string? SteamExe
    {
        get
        {
            var p = SteamPath;
            if (p is null)
            {
                return null;
            }
            var exe = Path.Combine(p, "steam.exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    /// <summary>True when the Steam client is currently running.</summary>
    public bool IsRunning
    {
        get
        {
            try { return Process.GetProcessesByName("steam").Length > 0; }
            catch { return false; }
        }
    }

    /// <summary>The Steam3 account id of the signed-in user, or null if not running.</summary>
    private static string? ActiveUserId()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is int active && active != 0)
            {
                return active.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    // ------------------------------------------------- Shortcut (Add to Steam)

    private static string LauncherExe =>
        Environment.ProcessPath ?? Path.Combine(AppPaths.Root, "MorrowindRemastered.exe");

    private static string QuotedExe => $"\"{LauncherExe}\"";

    /// <summary>
    /// Per-user <c>shortcuts.vdf</c> paths. When <paramref name="activeUser"/> is a
    /// known account id, only that user's file; when null, every numeric
    /// <c>userdata/&lt;id&gt;</c> folder.
    /// </summary>
    private IEnumerable<string> ShortcutsVdfPaths(string? activeUser)
    {
        var steam = SteamPath;
        if (steam is null)
        {
            yield break;
        }
        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata))
        {
            yield break;
        }

        foreach (var dir in Directory.GetDirectories(userdata))
        {
            var id = Path.GetFileName(dir);
            if (!long.TryParse(id, out var n) || n <= 0)
            {
                continue;
            }
            if (activeUser is not null && id != activeUser)
            {
                continue;
            }
            yield return Path.Combine(dir, "config", "shortcuts.vdf");
        }
    }

    /// <summary>True if the launcher already appears in any user's shortcuts.vdf.</summary>
    public bool IsLauncherShortcutPresent()
    {
        try
        {
            foreach (var file in ShortcutsVdfPaths(activeUser: null))
            {
                if (!File.Exists(file))
                {
                    continue;
                }
                var shortcuts = Vdf.Parse(File.ReadAllBytes(file)).GetMap("shortcuts");
                if (shortcuts is not null && HasLauncherEntry(shortcuts))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't read Steam shortcuts: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Adds the launcher as a non-Steam shortcut for the signed-in user (or all
    /// users if unknown). Round-trips the existing shortcuts.vdf so other shortcuts
    /// are preserved; a one-time <c>.bak</c> is kept. Steam must be restarted to
    /// show the shortcut. Returns true if added or already present.
    /// </summary>
    public bool AddLauncherShortcut() => WriteShortcut(ActiveUserId());

    /// <summary>
    /// Adds the shortcut and, when <paramref name="restartSteam"/> is set and Steam is
    /// running, restarts the client so the new entry shows up immediately. Steam
    /// rewrites shortcuts.vdf from its in-memory list when it exits, so a write made
    /// while it runs is lost on the next shutdown; we therefore sandwich the write
    /// between a graceful shutdown and a relaunch so it sticks. Returns true if the
    /// shortcut was added or already present.
    /// </summary>
    public async Task<bool> AddLauncherShortcutAsync(bool restartSteam, CancellationToken ct)
    {
        // Capture the signed-in user up front — ActiveUser resets to 0 once Steam exits.
        var activeUser = ActiveUserId();
        var restart = restartSteam && IsRunning;

        if (restart)
        {
            await ShutdownSteamAsync(ct).ConfigureAwait(false);
        }

        var ok = WriteShortcut(activeUser);

        if (restart && ok)
        {
            StartSteam();
        }
        return ok;
    }

    private bool WriteShortcut(string? activeUser)
    {
        var any = false;
        foreach (var file in ShortcutsVdfPaths(activeUser))
        {
            try
            {
                var root = File.Exists(file)
                    ? Vdf.Parse(File.ReadAllBytes(file))
                    : NewRoot();

                var shortcuts = root.GetMap("shortcuts");
                if (shortcuts is null)
                {
                    shortcuts = new VdfMap();
                    root.Entries.Add(("shortcuts", Vdf.TypeMap, shortcuts));
                }

                // Always resolve the icon — this re-extracts icon.ico to disk whenever it's
                // missing (e.g. after a rebuild wiped the output), the same way artwork is
                // re-applied every run. Then ensure the entry exists and points at it.
                var icon = IconPath();
                var entry = FindLauncherEntry(shortcuts);
                bool changed;
                if (entry is null)
                {
                    shortcuts.Entries.Add((NextIndex(shortcuts).ToString(CultureInfo.InvariantCulture),
                        Vdf.TypeMap, BuildEntry(icon)));
                    changed = true;
                    Logger.Info($"Added launcher to Steam shortcuts: {file}");
                }
                else
                {
                    changed = SetEntryIcon(entry, icon);
                    if (changed)
                    {
                        Logger.Info($"Refreshed Steam shortcut icon: {file}");
                    }
                }

                if (changed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    if (File.Exists(file) && !File.Exists(file + ".bak"))
                    {
                        File.Copy(file, file + ".bak");
                    }
                    var tmp = file + ".tmp";
                    File.WriteAllBytes(tmp, Vdf.Serialize(root));
                    File.Move(tmp, file, overwrite: true);
                }

                // (Re)apply any custom artwork for this user, whether the shortcut was
                // just added or already present.
                ApplyArtwork(Path.GetDirectoryName(file)!);
                any = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Couldn't write Steam shortcut to {file}: {ex.Message}");
            }
        }
        return any;
    }

    // ----------------------------------------------------- Restart the client

    /// <summary>
    /// Asks a running Steam client to shut down gracefully (<c>steam.exe -shutdown</c>)
    /// and waits for every steam process to exit, up to <paramref name="timeout"/>
    /// (default 20s). Returns true once Steam has stopped (or wasn't running). No-op
    /// fail-soft: a false return just means the caller should skip the relaunch.
    /// </summary>
    public async Task<bool> ShutdownSteamAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        if (!IsRunning)
        {
            return true;
        }
        var exe = SteamExe;
        if (exe is null)
        {
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, "-shutdown")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            })?.Dispose();
            Logger.Info("Asked Steam to shut down (-shutdown).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't signal Steam to shut down: {ex.Message}");
            return false;
        }

        var limit = timeout ?? TimeSpan.FromSeconds(20);
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(250);
        while (IsRunning && waited < limit)
        {
            await Task.Delay(step, ct).ConfigureAwait(false);
            waited += step;
        }
        if (IsRunning)
        {
            Logger.Warn($"Steam still running after waiting {limit.TotalSeconds:0}s for shutdown.");
            return false;
        }
        Logger.Info("Steam has shut down.");
        return true;
    }

    /// <summary>
    /// Starts the Steam client. The launcher runs elevated, so Steam is launched via
    /// explorer.exe to drop back to the user's normal integrity level (Steam launched
    /// elevated misbehaves). Best-effort; returns false if it couldn't be started.
    /// </summary>
    public bool StartSteam()
    {
        var exe = SteamExe;
        if (exe is null)
        {
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"")
            {
                UseShellExecute = true
            })?.Dispose();
            Logger.Info("Started Steam (via explorer, non-elevated).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't start Steam: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------- Custom artwork / icon

    /// <summary>Folder under launcher/ where Steam artwork is kept/extracted.</summary>
    private static string SteamAssetsDir => AppPaths.SteamAssetsDir;

    /// <summary>
    /// Resolves a Steam asset to an on-disk path: a loose <c>Steam/&lt;fileName&gt;</c>
    /// beside the exe wins (dev override); otherwise the copy embedded in the exe
    /// (logical resource name == <paramref name="fileName"/>) is extracted there.
    /// Returns null when neither exists. Extraction is what lets the artwork/icon work
    /// when the launcher ships embedded inside a modlist with no loose files beside it.
    /// </summary>
    private static string? EnsureAsset(string fileName)
    {
        var path = Path.Combine(SteamAssetsDir, fileName);
        if (File.Exists(path))
        {
            return path;
        }
        try
        {
            using var stream = typeof(SteamService).Assembly.GetManifestResourceStream(fileName);
            if (stream is null)
            {
                return null;
            }
            Directory.CreateDirectory(SteamAssetsDir);
            var tmp = path + ".tmp";
            using (var dst = File.Create(tmp))
            {
                stream.CopyTo(dst);
            }
            File.Move(tmp, path, overwrite: true);
            Logger.Info($"Extracted embedded Steam asset \"{fileName}\".");
            return path;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't extract embedded Steam asset '{fileName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The shortcut icon path: a bundled/extracted <c>icon.ico</c> (preferred), a loose
    /// <c>icon.png</c>, else the launcher exe (which carries the same icon via
    /// <c>&lt;ApplicationIcon&gt;</c>). Steam reads this path lazily after we exit, so it
    /// must point at a file that persists — hence we extract to disk, not a temp file.
    /// </summary>
    private static string IconPath()
    {
        if (EnsureAsset("icon.ico") is { } ico)
        {
            return ico;
        }
        var loosePng = Path.Combine(SteamAssetsDir, "icon.png");
        return File.Exists(loosePng) ? loosePng : LauncherExe;
    }

    /// <summary>
    /// Copies user-supplied artwork from the "Steam" folder beside the launcher into
    /// the user's <c>config/grid</c> folder, keyed by the shortcut appid, so the
    /// non-Steam entry shows a custom wide capsule, box art, hero and logo. Each slot
    /// accepts the descriptive shipped name first, then an older generic stem as a
    /// fallback (.png/.jpg): "Steam Capsule Wide"/<c>header</c>,
    /// "Steam Capsule"/<c>boxart</c>, "Steam Hero"/<c>hero</c>, "Steam Logo"/<c>logo</c>.
    /// </summary>
    private void ApplyArtwork(string userConfigDir)
    {
        var grid = Path.Combine(userConfigDir, "grid");
        var appId = ShortcutAppIdUnsigned();
        var legacyId = ((ulong)appId << 32) | 0x02000000UL;
        Logger.Info($"Applying Steam artwork (appid {appId}) to \"{grid}\".");

        var placed = 0;
        // Wide capsule → horizontal capsule (new UI) + legacy grid id for the old UI.
        placed += PlaceArtwork(grid, new[] { "Steam Capsule Wide", "header" },
            appId.ToString(CultureInfo.InvariantCulture),
            legacyId.ToString(CultureInfo.InvariantCulture));
        // Capsule → vertical/portrait box art.
        placed += PlaceArtwork(grid, new[] { "Steam Capsule", "boxart" }, $"{appId}p");
        // Hero banner + transparent logo.
        placed += PlaceArtwork(grid, new[] { "Steam Hero", "hero" }, $"{appId}_hero");
        placed += PlaceArtwork(grid, new[] { "Steam Logo", "logo" }, $"{appId}_logo");
        Logger.Info($"Placed {placed}/4 Steam artwork slot(s) in the grid folder.");
    }

    /// <summary>
    /// Resolves an artwork source: a loose <c>Steam/&lt;stem&gt;.(png|jpg|jpeg)</c> for any of
    /// <paramref name="sourceStems"/> wins (dev override / generic fallback names); otherwise
    /// the embedded copy shipped under the first stem's <c>.png</c> name is extracted.
    /// </summary>
    private static string? ResolveArtworkSource(string[] sourceStems)
    {
        var loose = sourceStems
            .SelectMany(stem => new[] { ".png", ".jpg", ".jpeg" }
                .Select(ext => Path.Combine(SteamAssetsDir, stem + ext)))
            .FirstOrDefault(File.Exists);
        return loose ?? EnsureAsset(sourceStems[0] + ".png");
    }

    /// <summary>Copies one resolved source into the grid folder under each target stem. Returns 1 if placed, else 0.</summary>
    private static int PlaceArtwork(string gridDir, string[] sourceStems, params string[] targetStems)
    {
        var src = ResolveArtworkSource(sourceStems);
        if (src is null)
        {
            Logger.Warn($"Steam artwork '{sourceStems[0]}' not found (loose or embedded); skipped.");
            return 0;
        }
        var ext2 = Path.GetExtension(src);
        try
        {
            Directory.CreateDirectory(gridDir);
            foreach (var stem in targetStems)
            {
                File.Copy(src, Path.Combine(gridDir, stem + ext2), overwrite: true);
            }
            return 1;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't place Steam artwork '{sourceStems[0]}': {ex.Message}");
            return 0;
        }
    }

    private static VdfMap NewRoot()
    {
        var root = new VdfMap();
        root.Entries.Add(("shortcuts", Vdf.TypeMap, new VdfMap()));
        return root;
    }

    private static bool HasLauncherEntry(VdfMap shortcuts) => FindLauncherEntry(shortcuts) is not null;

    /// <summary>The launcher's shortcut entry (matched by Exe == the launcher exe), or null.</summary>
    private static VdfMap? FindLauncherEntry(VdfMap shortcuts)
    {
        foreach (var (_, type, value) in shortcuts.Entries)
        {
            if (type == Vdf.TypeMap && value is VdfMap entry)
            {
                var exe = entry.GetString("Exe");
                if (exe is not null &&
                    exe.Replace("\"", "").Trim().Equals(LauncherExe, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Sets the entry's <c>icon</c> string field to <paramref name="icon"/> (adding it if
    /// absent). Returns true if the value changed, so the caller only rewrites the vdf when
    /// needed.
    /// </summary>
    private static bool SetEntryIcon(VdfMap entry, string icon)
    {
        for (var i = 0; i < entry.Entries.Count; i++)
        {
            var (name, type, value) = entry.Entries[i];
            if (type == Vdf.TypeString && string.Equals(name, "icon", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(value as string, icon, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                entry.Entries[i] = (name, type, icon);
                return true;
            }
        }
        entry.Entries.Add(("icon", Vdf.TypeString, icon));
        return true;
    }

    private static int NextIndex(VdfMap shortcuts)
    {
        var max = -1;
        foreach (var (name, type, _) in shortcuts.Entries)
        {
            if (type == Vdf.TypeMap && int.TryParse(name, out var n) && n > max)
            {
                max = n;
            }
        }
        return max + 1;
    }

    private static VdfMap BuildEntry(string icon)
    {
        var e = new VdfMap();
        e.Entries.Add(("appid", Vdf.TypeInt, ComputeAppId()));
        e.Entries.Add(("AppName", Vdf.TypeString, ShortcutAppName));
        e.Entries.Add(("Exe", Vdf.TypeString, QuotedExe));
        e.Entries.Add(("StartDir", Vdf.TypeString, $"\"{AppPaths.Root}\""));
        e.Entries.Add(("icon", Vdf.TypeString, icon));
        e.Entries.Add(("ShortcutPath", Vdf.TypeString, ""));
        e.Entries.Add(("LaunchOptions", Vdf.TypeString, ""));
        e.Entries.Add(("IsHidden", Vdf.TypeInt, 0));
        e.Entries.Add(("AllowDesktopConfig", Vdf.TypeInt, 1));
        e.Entries.Add(("AllowOverlay", Vdf.TypeInt, 1));
        e.Entries.Add(("OpenVR", Vdf.TypeInt, 0));
        e.Entries.Add(("Devkit", Vdf.TypeInt, 0));
        e.Entries.Add(("DevkitGameID", Vdf.TypeString, ""));
        e.Entries.Add(("DevkitOverrideAppID", Vdf.TypeInt, 0));
        e.Entries.Add(("LastPlayTime", Vdf.TypeInt, 0));
        e.Entries.Add(("tags", Vdf.TypeMap, new VdfMap()));
        return e;
    }

    /// <summary>
    /// Steam's non-Steam shortcut id (unsigned): crc32(exe+name) with the high bit
    /// set. This is the key Steam uses for both shortcuts.vdf and the library
    /// artwork filenames in <c>config/grid</c>.
    /// </summary>
    private static uint ShortcutAppIdUnsigned() =>
        Crc32(Encoding.UTF8.GetBytes(QuotedExe + ShortcutAppName)) | 0x80000000u;

    /// <summary>The same id as stored in shortcuts.vdf's int32 <c>appid</c> field.</summary>
    private static int ComputeAppId() => unchecked((int)ShortcutAppIdUnsigned());

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    // --------------------------------------------- Playtime tracking (22320)

    private static string SteamApiDllPath => AppPaths.SteamApiDll;

    /// <summary>Extracts the embedded steam_api64.dll next to the exe. False if absent.</summary>
    private static bool TryExtractEmbeddedSteamApi()
    {
        try
        {
            using var stream = typeof(SteamService).Assembly
                .GetManifestResourceStream("steam_api64.dll");
            if (stream is null)
            {
                return false;
            }
            var tmp = SteamApiDllPath + ".tmp";
            using (var dst = File.Create(tmp))
            {
                stream.CopyTo(dst);
            }
            File.Move(tmp, SteamApiDllPath, overwrite: true);
            Logger.Info("Extracted embedded steam_api64.dll.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't extract embedded steam_api64.dll: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Finds an existing <c>steam_api64.dll</c> anywhere in the user's Steam library
    /// (any installed 64-bit game ships one), or null. Lazy enumeration stops at the
    /// first hit.
    /// </summary>
    private string? TryFindLocalSteamApi()
    {
        var steam = SteamPath;
        if (steam is null)
        {
            return null;
        }
        foreach (var lib in SteamLibraryFolders(steam))
        {
            var common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common))
            {
                continue;
            }
            try
            {
                var hit = Directory
                    .EnumerateFiles(common, "steam_api64.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null)
                {
                    return hit;
                }
            }
            catch
            {
                // Permission/IO issue scanning this library; try the next.
            }
        }
        return null;
    }

    /// <summary>The Steam library roots: the default install plus those in libraryfolders.vdf.</summary>
    private static IEnumerable<string> SteamLibraryFolders(string steam)
    {
        yield return steam;

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }
        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var p = m.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.Equals(p, steam, StringComparison.OrdinalIgnoreCase) && Directory.Exists(p))
            {
                yield return p;
            }
        }
    }

    private IntPtr _steamApiLib = IntPtr.Zero;
    private SteamApiVoidDelegate? _runCallbacks;
    private SteamApiVoidDelegate? _shutdown;
    private System.Threading.Timer? _callbackTimer;
    private bool _tracking;

    public bool IsTracking => _tracking;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiVoidDelegate();

    /// <summary>
    /// Ensures steam_api64.dll is present next to the launcher, downloading it from
    /// the configured URL on demand. Returns false if it can't be obtained.
    /// </summary>
    public async Task<bool> EnsureSteamApiAsync(IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(SteamApiDllPath))
        {
            return true;
        }

        // Primary source: the copy embedded in the launcher (extract next to the exe).
        if (TryExtractEmbeddedSteamApi())
        {
            return true;
        }

        // Fallback: an existing steam_api64.dll already on disk in the user's Steam
        // library — it's the generic shim and works for any appid.
        var local = TryFindLocalSteamApi();
        if (local is not null)
        {
            try
            {
                File.Copy(local, SteamApiDllPath, overwrite: true);
                Logger.Info($"Using steam_api64.dll from \"{local}\".");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Couldn't copy steam_api64.dll from \"{local}\": {ex.Message}");
            }
        }

        var url = _config.Current.Downloads.SteamApi;
        if (string.IsNullOrWhiteSpace(url))
        {
            Logger.Warn("No local steam_api64.dll found and no download URL configured; " +
                        "can't track playtime.");
            return false;
        }
        try
        {
            progress?.Report(new InstallProgress("Steam", "Downloading Steam API…", null, true));
            using var resp = await _http
                .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var tmp = SteamApiDllPath + ".tmp";
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            File.Move(tmp, SteamApiDllPath, overwrite: true);
            Logger.Info($"Downloaded steam_api64.dll to {SteamApiDllPath}.");
            return true;
        }
        catch (Exception ex)
        {
            // Not fatal: the game still launches, tracking just won't start. Logged
            // as a warning (no error banner) since the dll may simply not be hosted
            // yet — host it at the configured URL, or drop steam_api64.dll next to
            // the launcher to use it directly.
            Logger.Warn($"Couldn't download steam_api64.dll from '{url}' ({ex.Message}); " +
                        "Steam playtime tracking is unavailable.");
            return false;
        }
    }

    /// <summary>
    /// Begins holding a Steamworks session for the given appid so Steam logs the
    /// playtime while it's open. No-op returning false if Steam isn't running, the dll
    /// is missing, or init fails (e.g. the account doesn't own the game). IMPORTANT:
    /// Steam only ends the session when the process that opened it EXITS, so this must
    /// be used from a short-lived helper that exits when the game does — not the
    /// long-lived launcher. Pair with <see cref="StopTracking"/>.
    /// </summary>
    public bool StartTracking(uint appId)
    {
        if (_tracking)
        {
            return true;
        }
        if (!IsRunning || !File.Exists(SteamApiDllPath))
        {
            return false;
        }

        try
        {
            // Tell the Steam API which app this process represents before init. The
            // steam_appid.txt is read from the process CWD; the presence helper sets its
            // working directory to launcher/ (see ShellViewModel.StartSteamPresence).
            var id = appId.ToString(CultureInfo.InvariantCulture);
            Environment.SetEnvironmentVariable("SteamAppId", id);
            Environment.SetEnvironmentVariable("SteamGameId", id);
            try { File.WriteAllText(AppPaths.SteamAppIdFile, id); } catch { /* best effort */ }

            _steamApiLib = NativeLibrary.Load(SteamApiDllPath);
            var init = Marshal.GetDelegateForFunctionPointer<SteamApiInitDelegate>(
                NativeLibrary.GetExport(_steamApiLib, "SteamAPI_Init"));
            _runCallbacks = Marshal.GetDelegateForFunctionPointer<SteamApiVoidDelegate>(
                NativeLibrary.GetExport(_steamApiLib, "SteamAPI_RunCallbacks"));
            _shutdown = Marshal.GetDelegateForFunctionPointer<SteamApiVoidDelegate>(
                NativeLibrary.GetExport(_steamApiLib, "SteamAPI_Shutdown"));

            if (!init())
            {
                Logger.Warn("SteamAPI_Init failed — Steam not running, the launcher is elevated, " +
                            "or the account doesn't own the game. Playtime won't be tracked.");
                Cleanup();
                return false;
            }

            _tracking = true;
            _callbackTimer = new System.Threading.Timer(
                _ => { try { _runCallbacks?.Invoke(); } catch { /* ignore */ } }, null, 1000, 1000);
            Logger.Info($"Steam playtime tracking started (appid {appId}).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't start Steam playtime tracking", ex);
            Cleanup();
            return false;
        }
    }

    /// <summary>Ends the Morrowind Steam session started by <see cref="StartTracking"/>.</summary>
    public void StopTracking()
    {
        if (!_tracking)
        {
            return;
        }
        try
        {
            _callbackTimer?.Dispose();
            _shutdown?.Invoke();
            Logger.Info("Steam playtime tracking stopped.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"SteamAPI_Shutdown failed: {ex.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _tracking = false;
        _callbackTimer = null;
        _runCallbacks = null;
        _shutdown = null;
        if (_steamApiLib != IntPtr.Zero)
        {
            try { NativeLibrary.Free(_steamApiLib); } catch { /* ignore */ }
            _steamApiLib = IntPtr.Zero;
        }
    }

    // ------------------------------------------- Binary VDF (shortcuts.vdf)

    /// <summary>An ordered binary-VDF map: each entry is (name, type byte, value).</summary>
    private sealed class VdfMap
    {
        public List<(string Name, byte Type, object Value)> Entries { get; } = new();

        public VdfMap? GetMap(string name)
        {
            foreach (var e in Entries)
            {
                if (e.Type == Vdf.TypeMap && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return (VdfMap)e.Value;
                }
            }
            return null;
        }

        public string? GetString(string name)
        {
            foreach (var e in Entries)
            {
                if (e.Type == Vdf.TypeString && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return (string)e.Value;
                }
            }
            return null;
        }
    }

    /// <summary>Minimal binary-VDF (Valve KeyValues) reader/writer for shortcuts.vdf.</summary>
    private static class Vdf
    {
        public const byte TypeMap = 0x00;
        public const byte TypeString = 0x01;
        public const byte TypeInt = 0x02;
        private const byte EndMap = 0x08;

        public static VdfMap Parse(byte[] data)
        {
            var pos = 0;
            return ReadMap(data, ref pos);
        }

        private static VdfMap ReadMap(byte[] data, ref int pos)
        {
            var map = new VdfMap();
            while (pos < data.Length)
            {
                var type = data[pos++];
                if (type == EndMap)
                {
                    break;
                }
                var name = ReadCString(data, ref pos);
                object value = type switch
                {
                    TypeMap => ReadMap(data, ref pos),
                    TypeString => ReadCString(data, ref pos),
                    TypeInt => ReadInt32(data, ref pos),
                    _ => throw new InvalidDataException($"Unknown VDF type 0x{type:X2}")
                };
                map.Entries.Add((name, type, value));
            }
            return map;
        }

        private static string ReadCString(byte[] data, ref int pos)
        {
            var start = pos;
            while (pos < data.Length && data[pos] != 0)
            {
                pos++;
            }
            var s = Encoding.UTF8.GetString(data, start, pos - start);
            pos++; // skip the null terminator
            return s;
        }

        private static int ReadInt32(byte[] data, ref int pos)
        {
            var v = BitConverter.ToInt32(data, pos);
            pos += 4;
            return v;
        }

        public static byte[] Serialize(VdfMap root)
        {
            using var ms = new MemoryStream();
            WriteMap(ms, root);
            return ms.ToArray();
        }

        private static void WriteMap(MemoryStream ms, VdfMap map)
        {
            foreach (var (name, type, value) in map.Entries)
            {
                ms.WriteByte(type);
                WriteCString(ms, name);
                switch (type)
                {
                    case TypeMap:
                        WriteMap(ms, (VdfMap)value);
                        break;
                    case TypeString:
                        WriteCString(ms, (string)value);
                        break;
                    case TypeInt:
                        ms.Write(BitConverter.GetBytes((int)value));
                        break;
                }
            }
            ms.WriteByte(EndMap);
        }

        private static void WriteCString(MemoryStream ms, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0);
        }
    }
}
