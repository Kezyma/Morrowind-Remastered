using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MorrowindRemasteredLauncher.Services;
using MorrowindRemasteredLauncher.Views;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>
/// Install-flow members of the shell view model: Nexus sign-in, the headless
/// Wabbajack install run, shared-downloads clearing, and the busy/progress UI
/// state these flows drive.
/// </summary>
public partial class ShellViewModel
{
    // -------------------------------------------------------- Install options

    [RelayCommand]
    private void BrowseInstallLocation()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select where to install this modlist",
            InitialDirectory = Directory.Exists(CurrentInstallDir)
                ? CurrentInstallDir
                : AppPaths.Root
        };

        if (dialog.ShowDialog() == true)
        {
            _config.Current.Install.InstallDir = dialog.FolderName;
            _config.Save();
            OnPropertyChanged(nameof(CurrentInstallDir));
            RefreshState();
        }
    }

    // ------------------------------------------------------------ Nexus state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunInstall))]
    [NotifyCanExecuteChangedFor(nameof(RunInstallStepCommand))]
    private bool _isNexusLoggedIn;

    [ObservableProperty]
    private string _nexusStatus = "Not signed in";

    [ObservableProperty]
    private string? _nexusWarning;

    /// <summary>
    /// Opens the embedded Nexus OAuth login popup (WebView2) and waits for
    /// approval. On success the account is shown, the token is persisted into
    /// Wabbajack's store, and a non-Premium warning is surfaced. A second click
    /// while signed in signs out.
    /// </summary>
    [RelayCommand]
    private async Task NexusLogin()
    {
        if (IsNexusLoggedIn)
        {
            _nexus.SignOut();
            ApplyNexusAccount(null);
            return;
        }

        try
        {
            NexusStatus = "Opening Nexus sign-in…";

            // Build the authorize URL + PKCE secrets, then show the popup.
            var challenge = _nexus.BeginLogin();

            var login = new NexusLoginWindow(challenge.AuthorizeUrl, NexusAuthService.RedirectHost)
            {
                Owner = Application.Current?.MainWindow
            };

            var approved = login.ShowDialog() == true && login.RedirectUri is not null;
            if (!approved)
            {
                NexusStatus = "Sign-in was cancelled.";
                return;
            }

            IsBusy = true;
            BusyTitle = "Signing in to Nexus Mods";
            IsProgressIndeterminate = true;
            ProgressLine = "Completing sign-in…";

            var account = await _nexus
                .CompleteAsync(login.RedirectUri!, challenge)
                .ConfigureAwait(true);

            ApplyNexusAccount(account);

            if (account is null)
            {
                NexusStatus = "Sign-in failed. Please try again.";
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Nexus sign-in error", ex);
            NexusStatus = "Sign-in failed. Please try again.";
        }
        finally
        {
            IsBusy = false;
            ProgressLine = null;
        }
    }

    /// <summary>Reflects a (possibly null) Nexus account into the UI state.</summary>
    private void ApplyNexusAccount(NexusAccount? account)
    {
        IsNexusLoggedIn = account is not null;

        if (account is null)
        {
            NexusStatus = "Not signed in";
            NexusWarning = null;
            return;
        }

        NexusStatus = account.IsPremium
            ? $"Signed in as {account.Name} (Premium)"
            : $"Signed in as {account.Name}";

        NexusWarning = account.IsPremium
            ? null
            : "Your Nexus account is not Premium. Automated downloads require a "
              + "Premium membership; without it, installation cannot proceed "
              + "unattended.";
    }

    // --------------------------------------------------------- Install action

    /// <summary>The final install button is disabled until the user is signed in.</summary>
    public bool CanRunInstall => IsNexusLoggedIn && !IsBusy;

    private CancellationTokenSource? _installCts;

    /// <summary>
    /// The Install/Update button: installs the single combined Wabbajack list, then
    /// updates the Mod Organizer paths. Per-engine setup happens on the Play tab.
    /// </summary>
    [RelayCommand]
    private Task RunInstall() =>
        RunBusyAsync($"{InstallOrUpdateLabel} Morrowind Remastered", async (p, ct) =>
        {
            var (ok, message) = await RunModlistInstallAsync(p, ct).ConfigureAwait(false);
            if (!ok)
            {
                return (false, message);
            }

            // The only post-install step the Install tab runs: repair MO2 paths.
            BusyTitle = "Updating Mod Organizer paths";
            var r = await _postSetup
                .RunStepAsync(SelectedEdition, PostSetupStep.RepairPaths, force: true, p, ct)
                .ConfigureAwait(false);
            if (!r.Success)
            {
                return (false, r.Error ?? "Couldn't update Mod Organizer paths.");
            }

            return (true, "Installed. Pick a version on the Play tab to finish setup.");
        });

    /// <summary>
    /// Installs the modlist from the configured source (catalog / machineURL / local
    /// file). The catalog modlist is optional — the test source modes don't need it.
    /// </summary>
    private async Task<(bool Ok, string Message)> RunModlistInstallAsync(
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (!IsNexusLoggedIn)
        {
            return (false, "Sign in to Nexus Mods first.");
        }

        var result = await _installEngine
            .InstallAsync(SelectedEdition, CurrentModlist, progress, ct)
            .ConfigureAwait(false);
        return result.Success
            ? (true, "Morrowind Remastered installed.")
            : (false, result.Error ?? "Installation failed.");
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    /// <summary>True while an install run is in flight (drives the Cancel button).</summary>
    [ObservableProperty]
    private bool _isInstallRunning;

    /// <summary>Persistent outcome line shown once an install run ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallResult))]
    [NotifyPropertyChangedFor(nameof(InstallFailed))]
    private string? _installResultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallFailed))]
    private bool _installResultSuccess;

    public bool HasInstallResult => !string.IsNullOrEmpty(InstallResultMessage);

    /// <summary>True when the last install run ended unsuccessfully.</summary>
    public bool InstallFailed => HasInstallResult && !InstallResultSuccess;

    /// <summary>Clearing the cache mid-install would pull files out from under
    /// the running Wabbajack process.</summary>
    public bool CanClearDownloads => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClearDownloads))]
    private void ClearDownloads()
    {
        var dir = DownloadsDir;
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                Logger.Info($"Cleared downloads cache at \"{dir}\".");
            }
            Directory.CreateDirectory(dir);
            ProgressLine = "Downloads cache cleared.";
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to clear downloads", ex);
            ProgressLine = $"Couldn't clear downloads: {ex.Message}";
        }
        finally
        {
            RefreshDownloadsInfo();
        }
    }

    /// <summary>
    /// Deletes the selected edition's installation folder and clears its install
    /// record. The shared downloads cache is kept (Clear Downloads handles it).
    /// </summary>
    [RelayCommand]
    private async Task Uninstall()
    {
        if (IsBusy)
        {
            return;
        }

        var dir = CurrentInstallDir;
        var edition = SelectedEdition;
        var confirm = MessageBox.Show(
            $"This will delete the {SelectedEditionName} Edition installation at:\n\n{dir}\n\n" +
            "Downloaded archives are kept and can be removed with Clear Downloads. Continue?",
            "Uninstall Morrowind Remastered",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            IsBusy = true;
            InstallResultMessage = null;
            BusyTitle = $"Uninstalling {SelectedEditionName} Edition";
            IsProgressIndeterminate = true;
            ProgressLine = "Removing files…";
            Logger.Info($"Uninstalling {edition} from \"{dir}\"");

            await Task.Run(() =>
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }).ConfigureAwait(true);

            var record = _config.Current.Install;
            record.InstalledVersion = null;
            record.InstalledAt = null;
            record.SetupComplete.Clear();
            _config.Save();

            InstallResultSuccess = true;
            InstallResultMessage = $"{SelectedEditionName} Edition uninstalled.";
            Logger.Info($"Uninstall complete for {edition}");
        }
        catch (Exception ex)
        {
            // Typically a file lock (e.g. MO2 still running); the error banner
            // appears via Logger.ErrorLogged.
            Logger.Error("Uninstall failed", ex);
            InstallResultSuccess = false;
            InstallResultMessage = $"Uninstall failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshState();
        }
    }

    // ----------------------------------------------------------- Busy / progress

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunInstall))]
    [NotifyPropertyChangedFor(nameof(CanClearDownloads))]
    [NotifyPropertyChangedFor(nameof(CanLaunchMo2))]
    [NotifyCanExecuteChangedFor(nameof(ClearDownloadsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunInstallStepCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyTitle = "";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string? _progressLine;
}
