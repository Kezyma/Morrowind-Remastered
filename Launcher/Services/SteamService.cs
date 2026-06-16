using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Best-effort Steam integration: detect the install, add a non-Steam shortcut, and track Morrowind playtime.</summary>
/// <remarks>
/// All operations are fail-soft. Detects the Steam install (registry) and whether the client is running; adds
/// the launcher to Steam as a non-Steam shortcut (binary shortcuts.vdf); and, while the game runs, holds a
/// Steamworks session for Morrowind (appid 22320) so Steam logs the playtime. The steam_api64.dll ships
/// embedded in the exe and is extracted on demand, only used when Steam is running and tracking is enabled.
/// </remarks>
public sealed class SteamService
{
    /// <summary>Default Steam appid for Morrowind; the literal fallback for the presence helper's arg parse.</summary>
    public const uint MorrowindAppId = 22320;

    private readonly ConfigService _config;

    /// <summary>Creates the Steam service.</summary>
    public SteamService(ConfigService config)
    {
        _config = config;
    }

    /// <summary>The Steam install folder, or null when Steam isn't installed.</summary>
    public string? SteamPath
    {
        get
        {
            foreach (var reg in _config.Current.Steam.RegistryPaths)
            {
                try
                {
                    if (!Enum.TryParse<RegistryHive>(reg.Hive, out var hive) ||
                        !Enum.TryParse<RegistryView>(reg.View, out var view))
                    {
                        continue;
                    }
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(reg.SubKey);
                    if (key?.GetValue(reg.ValueName) is string p && !string.IsNullOrWhiteSpace(p))
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
                }
            }
            return null;
        }
    }

    /// <summary>True when Steam is installed.</summary>
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
            var exe = Path.Combine(p, _config.Current.Steam.SteamExeName);
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
        }
        return null;
    }

    /// <summary>Full path to the running launcher exe.</summary>
    private static string LauncherExe =>
        Environment.ProcessPath ?? Path.Combine(AppPaths.Root, "MorrowindRemastered.exe");

    /// <summary>The launcher exe path wrapped in quotes, as Steam stores it.</summary>
    private static string QuotedExe => $"\"{LauncherExe}\"";

    /// <summary>Per-user <c>shortcuts.vdf</c> paths: just <paramref name="activeUser"/>'s file when known, else every numeric <c>userdata/&lt;id&gt;</c> folder.</summary>
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

    /// <summary>Adds the launcher as a non-Steam shortcut for the signed-in user (or all users if unknown), preserving other shortcuts and keeping a one-time <c>.bak</c>; Steam must be restarted to show it. Returns true if added or already present.</summary>
    public bool AddLauncherShortcut() => WriteShortcut(ActiveUserId());

    /// <summary>Adds the shortcut and, when <paramref name="restartSteam"/> is set and Steam is running, restarts the client so the entry shows immediately. Returns true if added or already present.</summary>
    /// <remarks>Steam rewrites shortcuts.vdf from its in-memory list when it exits, so a write made while it runs is lost on the next shutdown; the write is therefore sandwiched between a graceful shutdown and a relaunch so it sticks. The signed-in user is captured up front because ActiveUser resets to 0 once Steam exits.</remarks>
    public async Task<bool> AddLauncherShortcutAsync(bool restartSteam, CancellationToken ct)
    {
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

    /// <summary>Writes/refreshes the launcher's shortcut entry (and its icon and artwork) into each target user's shortcuts.vdf. Returns true if any file was handled.</summary>
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

    /// <summary>Asks a running Steam client to shut down gracefully (<c>steam.exe -shutdown</c>) and waits for every steam process to exit up to <paramref name="timeout"/>; returns true once stopped (or not running), a false return meaning the caller should skip the relaunch.</summary>
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

        var limit = timeout ?? TimeSpan.FromSeconds(_config.Current.Steam.ShutdownTimeoutSeconds);
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

    /// <summary>Starts the Steam client via explorer.exe so it drops back to the user's normal integrity level (the launcher runs elevated and Steam misbehaves when elevated); best-effort, returns false if it couldn't be started.</summary>
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

    /// <summary>Folder under launcher/ where Steam artwork is kept/extracted.</summary>
    private static string SteamAssetsDir => AppPaths.SteamAssetsDir;

    /// <summary>Resolves a Steam asset on disk — a loose <c>Steam/&lt;fileName&gt;</c> beside the exe wins (dev override), else the embedded copy is extracted there (so artwork/icon work when the launcher ships embedded with no loose files); null when neither exists.</summary>
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

    /// <summary>The shortcut icon path — a bundled/extracted <c>icon.ico</c> (preferred), a loose <c>icon.png</c>, else the launcher exe; extracted to a persistent disk file (not temp) because Steam reads this path lazily after we exit.</summary>
    private string IconPath()
    {
        if (EnsureAsset(_config.Current.Steam.IconAssetName) is { } ico)
        {
            return ico;
        }
        var loosePng = Path.Combine(SteamAssetsDir, "icon.png");
        return File.Exists(loosePng) ? loosePng : LauncherExe;
    }

    /// <summary>Copies the wide-capsule, box-art, hero and logo artwork into the user's <c>config/grid</c> folder keyed by the shortcut appid, so the non-Steam entry shows custom library art.</summary>
    private void ApplyArtwork(string userConfigDir)
    {
        var grid = Path.Combine(userConfigDir, "grid");
        var appId = ShortcutAppIdUnsigned();
        var legacyId = ((ulong)appId << 32) | 0x02000000UL;
        Logger.Info($"Applying Steam artwork (appid {appId}) to \"{grid}\".");

        var art = _config.Current.Steam.Artwork;
        var placed = 0;
        placed += PlaceArtwork(grid, art.CapsuleWideStems,
            appId.ToString(CultureInfo.InvariantCulture),
            legacyId.ToString(CultureInfo.InvariantCulture));
        placed += PlaceArtwork(grid, art.CapsuleStems, $"{appId}p");
        placed += PlaceArtwork(grid, art.HeroStems, $"{appId}_hero");
        placed += PlaceArtwork(grid, art.LogoStems, $"{appId}_logo");
        Logger.Info($"Placed {placed}/4 Steam artwork slot(s) in the grid folder.");
    }

    /// <summary>Resolves an artwork source: a loose <c>Steam/&lt;stem&gt;.(png|jpg|jpeg)</c> for any of <paramref name="sourceStems"/> wins (dev override / fallback names), else the embedded copy under the first stem's <c>.png</c> name is extracted.</summary>
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

    /// <summary>An empty shortcuts.vdf root holding just an empty <c>shortcuts</c> map.</summary>
    private static VdfMap NewRoot()
    {
        var root = new VdfMap();
        root.Entries.Add(("shortcuts", Vdf.TypeMap, new VdfMap()));
        return root;
    }

    /// <summary>True if the shortcuts map already contains the launcher's entry.</summary>
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

    /// <summary>Sets the entry's <c>icon</c> field (adding it if absent); returns true only when the value changed, so the caller rewrites the vdf only when needed.</summary>
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

    /// <summary>The next free numeric index key for a new shortcut entry.</summary>
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

    /// <summary>Builds a fresh non-Steam shortcut entry for the launcher with the given icon.</summary>
    private VdfMap BuildEntry(string icon)
    {
        var e = new VdfMap();
        e.Entries.Add(("appid", Vdf.TypeInt, ComputeAppId()));
        e.Entries.Add(("AppName", Vdf.TypeString, _config.Current.Steam.ShortcutAppName));
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

    /// <summary>Steam's non-Steam shortcut id (unsigned): crc32(exe+name) with the high bit set — the key Steam uses for both shortcuts.vdf and the <c>config/grid</c> artwork filenames.</summary>
    private uint ShortcutAppIdUnsigned() =>
        Crc32(Encoding.UTF8.GetBytes(QuotedExe + _config.Current.Steam.ShortcutAppName)) | 0x80000000u;

    /// <summary>The same id as stored in shortcuts.vdf's int32 <c>appid</c> field.</summary>
    private int ComputeAppId() => unchecked((int)ShortcutAppIdUnsigned());

    /// <summary>Computes the standard CRC-32 of the given bytes.</summary>
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

    /// <summary>On-disk path of the Steamworks shim DLL next to the launcher.</summary>
    private static string SteamApiDllPath => AppPaths.SteamApiDll;

    /// <summary>Extracts the embedded Steamworks shim DLL next to the exe. False if absent.</summary>
    private bool TryExtractEmbeddedSteamApi()
    {
        try
        {
            using var stream = typeof(SteamService).Assembly
                .GetManifestResourceStream(_config.Current.Steam.SteamApiDllName);
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

    /// <summary>Finds an existing <c>steam_api64.dll</c> anywhere in the user's Steam library (any installed 64-bit game ships one), stopping at the first hit; null if none.</summary>
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

    /// <summary>True while a Steamworks playtime session is being held.</summary>
    public bool IsTracking => _tracking;

    /// <summary>Native signature of <c>SteamAPI_Init</c>.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiInitDelegate();

    /// <summary>Native signature of the void Steam API entry points (RunCallbacks, Shutdown).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiVoidDelegate();

    /// <summary>Ensures steam_api64.dll is present next to the launcher (the embedded copy, else an existing one in the Steam library); false if it can't be obtained.</summary>
    public bool EnsureSteamApi()
    {
        if (File.Exists(SteamApiDllPath))
        {
            return true;
        }

        if (TryExtractEmbeddedSteamApi())
        {
            return true;
        }

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

        Logger.Warn("steam_api64.dll unavailable (no embedded or local copy); can't track playtime.");
        return false;
    }

    /// <summary>Begins holding a Steamworks session for the given appid so Steam logs playtime; no-op returning false if Steam isn't running, the dll is missing, or init fails. Pair with <see cref="StopTracking"/>.</summary>
    /// <remarks>Steam only ends the session when the process that opened it EXITS, so this must run from a short-lived helper that exits when the game does, not the long-lived launcher. It writes steam_appid.txt, which the API reads from the process CWD (the presence helper sets its working directory to launcher/, see ShellViewModel.StartSteamPresence).</remarks>
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
            var id = appId.ToString(CultureInfo.InvariantCulture);
            Environment.SetEnvironmentVariable("SteamAppId", id);
            Environment.SetEnvironmentVariable("SteamGameId", id);
            try { File.WriteAllText(AppPaths.SteamAppIdFile, id); } catch { }

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
                _ => { try { _runCallbacks?.Invoke(); } catch { } }, null, 1000, 1000);
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

    /// <summary>Frees the native library and clears all tracking state.</summary>
    private void Cleanup()
    {
        _tracking = false;
        _callbackTimer = null;
        _runCallbacks = null;
        _shutdown = null;
        if (_steamApiLib != IntPtr.Zero)
        {
            try { NativeLibrary.Free(_steamApiLib); } catch { }
            _steamApiLib = IntPtr.Zero;
        }
    }

    /// <summary>An ordered binary-VDF map: each entry is (name, type byte, value).</summary>
    private sealed class VdfMap
    {
        /// <summary>The map's entries in file order.</summary>
        public List<(string Name, byte Type, object Value)> Entries { get; } = new();

        /// <summary>Returns the nested map under <paramref name="name"/>, or null.</summary>
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

        /// <summary>Returns the string value under <paramref name="name"/>, or null.</summary>
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
        /// <summary>VDF type byte for a nested map.</summary>
        public const byte TypeMap = 0x00;
        /// <summary>VDF type byte for a string value.</summary>
        public const byte TypeString = 0x01;
        /// <summary>VDF type byte for an int32 value.</summary>
        public const byte TypeInt = 0x02;
        /// <summary>VDF type byte that ends a map.</summary>
        private const byte EndMap = 0x08;

        /// <summary>Parses a binary-VDF byte array into a map.</summary>
        public static VdfMap Parse(byte[] data)
        {
            var pos = 0;
            return ReadMap(data, ref pos);
        }

        /// <summary>Reads one map (recursively for nested maps) from <paramref name="pos"/>.</summary>
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

        /// <summary>Reads a null-terminated UTF-8 string, advancing past the terminator.</summary>
        private static string ReadCString(byte[] data, ref int pos)
        {
            var start = pos;
            while (pos < data.Length && data[pos] != 0)
            {
                pos++;
            }
            var s = Encoding.UTF8.GetString(data, start, pos - start);
            pos++;
            return s;
        }

        /// <summary>Reads a little-endian int32, advancing four bytes.</summary>
        private static int ReadInt32(byte[] data, ref int pos)
        {
            var v = BitConverter.ToInt32(data, pos);
            pos += 4;
            return v;
        }

        /// <summary>Serializes a map back to a binary-VDF byte array.</summary>
        public static byte[] Serialize(VdfMap root)
        {
            using var ms = new MemoryStream();
            WriteMap(ms, root);
            return ms.ToArray();
        }

        /// <summary>Writes one map (recursively for nested maps) to the stream.</summary>
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

        /// <summary>Writes a string as null-terminated UTF-8.</summary>
        private static void WriteCString(MemoryStream ms, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0);
        }
    }
}
