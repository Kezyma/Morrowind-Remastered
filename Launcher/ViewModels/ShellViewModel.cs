using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MorrowindRemasteredLauncher.Models;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.ViewModels;

public enum NavPage
{
    Install,
    Play,
    Settings,
    Tools,
    Mods,
    About
}

/// <summary>
/// Top-level view model: owns the edition switch, the game-path bar, navigation,
/// and the computed per-edition install state that drives menu enablement.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly ModlistCatalogService _catalog;
    private readonly InstallStateService _installState;
    private readonly GamePathService _gamePath;
    private readonly NexusAuthService _nexus;
    private readonly InstallEngine _installEngine;
    private readonly PostSetupService _postSetup;
    private readonly PostSetupVerifier _verifier;
    private readonly PostSetupConfigService _displayConfig;
    private readonly DisplayService _display;
    private readonly Mo2LaunchService _mo2Launch;
    private readonly GameSettingsService _gameSettings;
    private readonly SteamService _steam;
    private readonly LauncherEnvironment _environment;

    public ShellViewModel(
        ConfigService config,
        ModlistCatalogService catalog,
        InstallStateService installState,
        GamePathService gamePath,
        NexusAuthService nexus,
        InstallEngine installEngine,
        PostSetupService postSetup,
        PostSetupVerifier verifier,
        PostSetupConfigService displayConfig,
        DisplayService display,
        Mo2LaunchService mo2Launch,
        GameSettingsService gameSettings,
        SteamService steam,
        LauncherEnvironment environment)
    {
        _config = config;
        _catalog = catalog;
        _installState = installState;
        _gamePath = gamePath;
        _nexus = nexus;
        _installEngine = installEngine;
        _postSetup = postSetup;
        _verifier = verifier;
        _displayConfig = displayConfig;
        _display = display;
        _mo2Launch = mo2Launch;
        _gameSettings = gameSettings;
        _steam = steam;
        _environment = environment;

        // When embedded inside an MO2 list, lock to the detected edition.
        _selectedEdition = environment.PrimaryEmbeddedEdition ?? config.Current.SelectedEdition;

        _gameExePath = _gamePath.ResolveExisting();

        // Persist any path discovered via registry so subsequent launches are fast.
        if (_gameExePath is not null &&
            !string.Equals(_gameExePath, config.Current.GameExePath, StringComparison.OrdinalIgnoreCase))
        {
            _gamePath.SaveGamePath(_gameExePath);
        }

        // Surface logged errors as a dismissable banner with an Open Log action.
        Logger.ErrorLogged += msg =>
            Application.Current?.Dispatcher.BeginInvoke(() => LastError = msg);

        UpdateCurrentModlist();   // seeds details from a local .wabbajack before the catalog loads
        RefreshState();

        // Open straight on Play when this edition is already installed/playable;
        // otherwise start on the Install page. Covers embedded + standalone.
        _currentPage = CurrentState?.IsPlayable == true ? NavPage.Play : NavPage.Install;

        StartMo2Monitor();
    }

    // ---------------------------------------------------- MO2-running monitor

    /// <summary>
    /// True while any ModOrganizer.exe is running. Buttons that launch MO2 are
    /// disabled until every instance closes (MO2 single-instances per machine).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunchMo2))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunInstallStepCommand))]
    private bool _isMo2Running;

    /// <summary>True while the Steam client is running (gates the playtime checkbox).</summary>
    [ObservableProperty]
    private bool _isSteamRunning;

    private DispatcherTimer? _mo2Timer;

    private void StartMo2Monitor()
    {
        _mo2Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _mo2Timer.Tick += (_, _) => RefreshMo2Running();
        _mo2Timer.Start();
        RefreshMo2Running();
    }

    private void RefreshMo2Running()
    {
        try
        {
            IsMo2Running = Process.GetProcessesByName("ModOrganizer").Length > 0;
            IsSteamRunning = _steam.IsRunning;
        }
        catch
        {
            // Process enumeration can briefly fail; keep the last value.
        }
    }

    /// <summary>
    /// Spawns a headless copy of the launcher (<c>--steam-presence</c>) that holds a
    /// Morrowind Steam session while the game runs and exits when it does. This can't
    /// be done in-process: Steam only ends a session when the owning process exits, and
    /// the launcher stays open.
    /// </summary>
    private void StartSteamPresence(Edition edition)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                Logger.Warn("Can't start Steam presence helper: no process path.");
                return;
            }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                // The Steamworks API reads steam_appid.txt from the CWD; point it at
                // launcher/ where the helper writes that file.
                WorkingDirectory = AppPaths.LauncherDataDir
            };
            psi.ArgumentList.Add("--steam-presence");
            psi.ArgumentList.Add(SteamService.MorrowindAppId.ToString());
            psi.ArgumentList.Add(edition.GameProcessName());
            Process.Start(psi);
            Logger.Info("Started Steam playtime presence helper.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't start Steam presence helper: {ex.Message}");
        }
    }

    // ---------------------------------------------------------- Error banner

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _lastError;

    public bool HasError => !string.IsNullOrEmpty(LastError);

    /// <summary>Shows the error banner (also fed by Logger.ErrorLogged).</summary>
    public void ReportError(string message) => LastError = message;

    [RelayCommand]
    private void DismissError() => LastError = null;

    [RelayCommand]
    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Logger.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't open the log file: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores a saved Nexus session (if any) so the user doesn't have to sign
    /// in every launch. Safe to call at startup.
    /// </summary>
    public async Task RestoreNexusSessionAsync(CancellationToken ct = default)
    {
        var account = await _nexus.TryRestoreAsync(ct).ConfigureAwait(true);
        ApplyNexusAccount(account);
    }

    // ----------------------------------------------------------- Embedded mode

    /// <summary>True when the launcher is shipped inside an MO2 install.</summary>
    public bool IsEmbedded => _environment.IsEmbedded;

    /// <summary>
    /// The edition selector is shown unless embedded with only one edition
    /// present at this location.
    /// </summary>
    public bool ShowEditionSelector => !_environment.HideEditionSelector;

    /// <summary>
    /// The Install/Manage nav item is always available. In embedded mode the page
    /// only offers the game-path selector (see <see cref="ShowFullInstall"/>).
    /// </summary>
    public bool ShowInstallNav => true;

    /// <summary>
    /// Whether the Install page shows the full install flow (sizes, install
    /// location, Nexus sign-in, Install/Uninstall). Hidden in embedded mode, where
    /// the list is already installed and only the game path needs selecting.
    /// </summary>
    public bool ShowFullInstall => !_environment.IsEmbedded;

    /// <summary>
    /// Whether the running game is counted as Steam playtime for Morrowind. Backed
    /// by config; the checkbox that drives it is only shown while Steam is running.
    /// </summary>
    public bool TrackSteamPlaytime
    {
        get => _config.Current.Steam.TrackPlaytime;
        set
        {
            if (_config.Current.Steam.TrackPlaytime == value)
            {
                return;
            }
            _config.Current.Steam.TrackPlaytime = value;
            _config.Save();
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- Edition

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenMwSelected))]
    [NotifyPropertyChangedFor(nameof(IsMwseSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedEditionName))]
    [NotifyPropertyChangedFor(nameof(LatestVersionDisplay))]
    [NotifyPropertyChangedFor(nameof(IsModlistVersionKnown))]
    private Edition _selectedEdition;

    public bool IsOpenMwSelected => SelectedEdition == Edition.OpenMW;
    public bool IsMwseSelected => SelectedEdition == Edition.Mwse;

    /// <summary>UI display name ("OpenMW"/"MWSE" — never the raw enum "Mwse").</summary>
    public string SelectedEditionName => SelectedEdition.DisplayName();

    partial void OnSelectedEditionChanged(Edition value)
    {
        _config.Current.SelectedEdition = value;
        _config.Save();
        UpdateCurrentModlist();
        RefreshState();
        OnPropertyChanged(nameof(CurrentInstallDir));
    }

    /// <summary>
    /// Flips between the two editions. The whole selector invokes this, so a click
    /// anywhere on it (text, "/", or the gaps) simply toggles the version. Gated
    /// to installed lists so the selector stays inert while greyed out.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectEdition))]
    private void ToggleEdition() =>
        SelectedEdition = SelectedEdition == Edition.OpenMW ? Edition.Mwse : Edition.OpenMW;

    // -------------------------------------------------------------- Game path

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameDirectory))]
    [NotifyPropertyChangedFor(nameof(HasValidGamePath))]
    [NotifyPropertyChangedFor(nameof(GamePathDisplay))]
    [NotifyPropertyChangedFor(nameof(CanCopyGame))]
    private string? _gameExePath;

    public string? GameDirectory => _gamePath.GameDirectory(GameExePath);

    public bool HasValidGamePath => _gamePath.IsValidGameExe(GameExePath);

    public string GamePathDisplay =>
        HasValidGamePath ? GameDirectory! : "No Morrowind install selected";

    /// <summary>The clean game copy lives in &lt;LauncherDir&gt;/Morrowind.</summary>
    private static string GameCopyDir => AppPaths.GameCopyDir;

    /// <summary>
    /// Show the copy button only for a valid vanilla path that is neither inside
    /// an MO2 install nor already the launcher's own game copy.
    /// </summary>
    public bool CanCopyGame =>
        HasValidGamePath &&
        !_gamePath.IsInsideMo2(GameExePath) &&
        !string.Equals(GameDirectory, GameCopyDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the vanilla game into the launcher-local "Morrowind" folder and
    /// records that copy as the active game path.
    /// </summary>
    [RelayCommand]
    private async Task CopyGame()
    {
        var source = GameDirectory;
        if (source is null || IsBusy)
        {
            return;
        }

        var target = GameCopyDir;
        try
        {
            IsBusy = true;
            BusyTitle = "Copying game files";
            IsProgressIndeterminate = false;
            ProgressPercent = 0;
            ProgressLine = "Preparing to copy…";
            Logger.Info($"Copying game from \"{source}\" to \"{target}\"");

            await Task.Run(() =>
            {
                var files = new DirectoryInfo(source).GetFiles("*", SearchOption.AllDirectories);
                var totalBytes = files.Sum(f => f.Length);
                long copied = 0, lastReport = 0;

                // Mirror the directory tree once up front.
                Directory.CreateDirectory(target);
                foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                {
                    Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
                }

                Parallel.ForEach(files,
                    new ParallelOptions { MaxDegreeOfParallelism = 4 },
                    file =>
                    {
                        var dest = Path.Combine(target,
                            Path.GetRelativePath(source, file.FullName));

                        // Skip files an earlier (possibly interrupted) copy already
                        // brought over, so re-runs resume instead of restarting.
                        var existing = new FileInfo(dest);
                        if (!existing.Exists ||
                            existing.Length != file.Length ||
                            existing.LastWriteTimeUtc != file.LastWriteTimeUtc)
                        {
                            file.CopyTo(dest, overwrite: true);
                        }

                        var done = Interlocked.Add(ref copied, file.Length);
                        if (done - Interlocked.Read(ref lastReport) > 50_000_000 ||
                            done == totalBytes)
                        {
                            Interlocked.Exchange(ref lastReport, done);
                            var pct = totalBytes > 0 ? done * 100.0 / totalBytes : 100;
                            Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                ProgressPercent = pct;
                                ProgressLine = $"Copying game files… {pct:0}%";
                            });
                        }
                    });
            }).ConfigureAwait(true);

            var newExe = Path.Combine(target, "Morrowind.exe");
            _gamePath.SaveGamePath(newExe);
            GameExePath = newExe;
            RefreshState();
            ProgressLine = "Game copy complete.";
            Logger.Info($"Game copy complete; game path is now \"{newExe}\"");
        }
        catch (Exception ex)
        {
            Logger.Error("Game copy failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseGamePath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select your vanilla Morrowind.exe",
            Filter = "Morrowind executable|Morrowind.exe",
            FileName = "Morrowind.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            if (_gamePath.IsValidGameExe(dialog.FileName))
            {
                GameExePath = dialog.FileName;
                _gamePath.SaveGamePath(dialog.FileName);
                RefreshState();
            }
        }
    }

    // -------------------------------------------------------------- Navigation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallActive))]
    [NotifyPropertyChangedFor(nameof(IsPlayActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    [NotifyPropertyChangedFor(nameof(IsToolsActive))]
    [NotifyPropertyChangedFor(nameof(IsModsActive))]
    [NotifyPropertyChangedFor(nameof(IsAboutActive))]
    private NavPage _currentPage = NavPage.Install;

    public bool IsInstallActive => CurrentPage == NavPage.Install;
    public bool IsPlayActive => CurrentPage == NavPage.Play;
    public bool IsSettingsActive => CurrentPage == NavPage.Settings;
    public bool IsToolsActive => CurrentPage == NavPage.Tools;
    public bool IsModsActive => CurrentPage == NavPage.Mods;
    public bool IsAboutActive => CurrentPage == NavPage.About;

    [RelayCommand]
    private void Navigate(NavPage page) => CurrentPage = page;

    // ------------------------------------------------------- Per-edition state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallOrUpdateLabel))]
    [NotifyPropertyChangedFor(nameof(InstallNavLabel))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanOpenSettings))]
    [NotifyPropertyChangedFor(nameof(CanOpenTools))]
    [NotifyPropertyChangedFor(nameof(CanOpenMods))]
    [NotifyPropertyChangedFor(nameof(ModsMarkdownPath))]
    [NotifyPropertyChangedFor(nameof(CanSelectEdition))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEditionCommand))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(InstalledVersionDisplay))]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    private EditionState? _currentState;

    /// <summary>Short human description of the current edition's state.</summary>
    public string StatusSummary => CurrentState?.Status switch
    {
        InstallStatus.NotInstalled => "Not installed",
        InstallStatus.InstalledNeedsSetup => "Installed — setup required",
        InstallStatus.Ready => "Installed and ready",
        InstallStatus.UpdateAvailable => "Update available",
        _ => ""
    };

    /// <summary>
    /// The main action button's label: Install when this edition isn't installed,
    /// Update when an installed copy is outdated, Reinstall when it's current.
    /// </summary>
    public string InstallOrUpdateLabel => CurrentState switch
    {
        { IsInstalled: true, HasUpdate: true } => "Update",
        { IsInstalled: true } => "Reinstall",
        _ => "Install"
    };

    /// <summary>
    /// The nav-menu label for the Install page: "Manage" once installed (the page
    /// then manages an existing install), "Install" otherwise.
    /// </summary>
    public string InstallNavLabel => CurrentState?.IsInstalled == true ? "Manage" : "Install";

    // Menu enablement (spec: each disabled unless this edition is installed).
    public bool CanPlay => CurrentState?.IsPlayable == true;
    public bool CanOpenSettings => CurrentState?.IsInstalled == true;
    public bool CanOpenTools => CurrentState?.IsInstalled == true;
    public bool HasUpdate => CurrentState?.HasUpdate == true;

    /// <summary>
    /// Path to the installed list's mod-list markdown (<c>modlist.md</c>, the name the
    /// list actually ships; <c>modlists.md</c> accepted as a fallback) in the single
    /// shared MO2 folder, or null when the list doesn't ship one. Read fresh each time
    /// so it picks up the file once an install lays it down.
    /// </summary>
    public string? ModsMarkdownPath
    {
        get
        {
            try
            {
                var dir = CurrentInstallDir;
                foreach (var name in new[] { "modlist.md", "modlists.md" })
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch
            {
                // Best effort; the nav item simply stays disabled.
            }
            return null;
        }
    }

    /// <summary>The optional Mods page is available only when a modlists.md exists.</summary>
    public bool CanOpenMods => ModsMarkdownPath is not null;

    /// <summary>The version selector is greyed out until the list is installed.</summary>
    public bool CanSelectEdition => CurrentState?.IsInstalled == true;

    /// <summary>Uninstall is shown only when this edition is installed.</summary>
    public bool CanUninstall => CurrentState?.IsInstalled == true;

    /// <summary>
    /// Recompute the selected edition's state from disk/config. The latest
    /// catalog version is filled asynchronously by <see cref="RefreshCatalogAsync"/>.
    /// </summary>
    public void RefreshState()
    {
        var latest = _latestVersions.TryGetValue(SelectedEdition, out var v) ? v : null;
        CurrentState = _installState.GetState(SelectedEdition, latest);
        RefreshDownloadsInfo();
        RebuildSetupSteps();
        RebuildToolsLists();
        RebuildGameSettings();
    }

    // ------------------------------------------------------ Downloads cache

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearDownloadsLabel))]
    private long _downloadsSize;

    /// <summary>True when the shared Wabbajack downloads cache has any files.</summary>
    [ObservableProperty]
    private bool _hasDownloads;

    public string ClearDownloadsLabel =>
        $"Clear Downloads ({Converters.ByteSizeConverter.Format(DownloadsSize)})";

    /// <summary>
    /// The downloads cache the Install/Manage tab sizes and clears. Standalone:
    /// our portable Downloads folder. Embedded: MO2's own download_directory (from
    /// ModOrganizer.ini), so Clear Downloads works against the adjacent install.
    /// </summary>
    private string DownloadsDir =>
        _environment.IsEmbedded && _environment.EmbeddedMo2Dir is not null
            ? Mo2IniService.ResolveDownloadDirectory(_environment.EmbeddedMo2Dir)
            : AppPaths.DownloadsDir;

    /// <summary>
    /// Recomputes the shared downloads cache size off the UI thread (it can hold
    /// tens of GB across hundreds of archives).
    /// </summary>
    public void RefreshDownloadsInfo()
    {
        var downloadsDir = DownloadsDir;
        _ = Task.Run(() =>
        {
            long size = 0;
            var hasFiles = false;
            try
            {
                if (Directory.Exists(downloadsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(
                        downloadsDir, "*", SearchOption.AllDirectories))
                    {
                        size += new FileInfo(file).Length;
                        hasFiles = true;
                    }
                }
            }
            catch
            {
                // Best effort; sizes refresh again on the next state change.
            }

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                DownloadsSize = size;
                HasDownloads = hasFiles;
            });
        });
    }

    private readonly Dictionary<Edition, string?> _latestVersions = new();
    private readonly Dictionary<Edition, Modlist> _modlists = new();

    [ObservableProperty]
    private string? _catalogError;

    [ObservableProperty]
    private bool _isCatalogLoading;

    /// <summary>The catalog entry for the currently selected edition (if loaded).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionDisplay))]
    [NotifyPropertyChangedFor(nameof(DownloadSize))]
    [NotifyPropertyChangedFor(nameof(InstallSize))]
    [NotifyPropertyChangedFor(nameof(TotalSize))]
    [NotifyPropertyChangedFor(nameof(InstalledVersionDisplay))]
    [NotifyPropertyChangedFor(nameof(IsModlistVersionKnown))]
    private Modlist? _currentModlist;

    /// <summary>
    /// The catalog entry for the selected edition (always keyed by the edition's
    /// machineURL), independent of the install source. Drives the "latest" version
    /// so it shows even when installing from a local file or a custom machineURL.
    /// </summary>
    private Modlist? CatalogModlist =>
        _modlists.TryGetValue(SelectedEdition, out var m) ? m : null;

    /// <summary>Whether the catalog reports a version for the selected edition.</summary>
    public bool IsModlistVersionKnown => !string.IsNullOrWhiteSpace(CatalogModlist?.Version);

    public string LatestVersionDisplay =>
        CatalogModlist is { Version.Length: > 0 } m ? $"v{m.Version}" : "—";

    public string InstalledVersionDisplay =>
        CurrentState?.InstalledVersion is { Length: > 0 } v ? $"v{v}" : "—";

    public long DownloadSize => CurrentModlist?.DownloadMetadata?.SizeOfArchives ?? 0;
    public long InstallSize => CurrentModlist?.DownloadMetadata?.SizeOfInstalledFiles ?? 0;
    public long TotalSize => CurrentModlist?.DownloadMetadata?.TotalSize ?? 0;

    /// <summary>The resolved install directory for the selected edition.</summary>
    public string CurrentInstallDir => _installState.GetEditionInstallDir(SelectedEdition);

    /// <summary>
    /// Fetches the live catalog to learn each edition's version + sizes, then
    /// refreshes state so update indicators appear.
    /// </summary>
    public async Task RefreshCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            IsCatalogLoading = true;
            CatalogError = null;
            // A configured machineURL pins the "latest version" lookup regardless of
            // the install source/mode; otherwise each edition uses its own machineURL.
            var configMachineUrl = _config.Current.InstallSource.MachineUrl;
            foreach (var edition in new[] { Edition.OpenMW, Edition.Mwse })
            {
                var modlist = string.IsNullOrWhiteSpace(configMachineUrl)
                    ? await _catalog.GetModlistAsync(edition, ct).ConfigureAwait(true)
                    : await _catalog.GetByMachineUrlAsync(configMachineUrl, ct).ConfigureAwait(true);
                _latestVersions[edition] = modlist?.Version;
                if (modlist is not null)
                {
                    _modlists[edition] = modlist;
                }
            }
            UpdateCurrentModlist();
            RefreshState();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to refresh catalog", ex);
            CatalogError = "Couldn't reach the modlist catalog. Check your connection.";
        }
        finally
        {
            IsCatalogLoading = false;
        }
    }

    private void UpdateCurrentModlist()
    {
        // Mirror the install-source cascade: when a local .wabbajack file is present
        // it overrides the online list, so take the modlist details (sizes) from its
        // sibling .meta.json rather than the catalog.
        var source = _config.Current.InstallSource;
        if (source.ResolveExistingLocalFile() is { } path && LoadLocalModlist(path) is { } local)
        {
            CurrentModlist = local;
            return;
        }
        CurrentModlist = _modlists.TryGetValue(SelectedEdition, out var m) ? m : null;
    }

    /// <summary>
    /// Builds a <see cref="Modlist"/> for a local .wabbajack file (an already-resolved
    /// absolute path) from its sibling "&lt;file&gt;.meta.json" (sizes/archive counts).
    /// The version isn't in the meta, so it stays blank and the Install page hides the
    /// version sections.
    /// </summary>
    private static Modlist? LoadLocalModlist(string path)
    {
        try
        {
            var modlist = new Modlist { Title = "Morrowind Remastered", Version = "" };
            var metaPath = path + ".meta.json";
            if (File.Exists(metaPath))
            {
                modlist.DownloadMetadata =
                    JsonSerializer.Deserialize<DownloadMetadata>(File.ReadAllText(metaPath));
            }
            return modlist;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't read local modlist meta for \"{path}\": {ex.Message}");
            return null;
        }
    }
}
