using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MorrowindRemasteredLauncher.Services;
using MorrowindRemasteredLauncher.Views;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>Install-flow members of the shell view model: Nexus sign-in, the headless Wabbajack install run, shared-downloads clearing, and the busy/progress UI state these flows drive.</summary>
public partial class ShellViewModel
{
    /// <summary>Prompts for an install folder and persists the choice.</summary>
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

    /// <summary>True when a Nexus account is signed in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunInstall))]
    [NotifyCanExecuteChangedFor(nameof(RunInstallStepCommand))]
    private bool _isNexusLoggedIn;

    /// <summary>The Nexus sign-in status line shown on the Install page.</summary>
    [ObservableProperty]
    private string _nexusStatus = "Not signed in";

    /// <summary>Warning shown for a non-Premium account (null when none).</summary>
    [ObservableProperty]
    private string? _nexusWarning;

    /// <summary>Opens the embedded Nexus OAuth popup, persists the token to Wabbajack's store, and surfaces a non-Premium warning; a second click while signed in signs out.</summary>
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

            var challenge = _nexus.BeginLogin();

            var login = new NexusLoginWindow(challenge.AuthorizeUrl, _nexus.RedirectHost)
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

    /// <summary>The final install button is disabled until the user is signed in.</summary>
    public bool CanRunInstall => IsNexusLoggedIn && !IsBusy;

    private CancellationTokenSource? _installCts;

    /// <summary>The Install/Update button: installs the single combined Wabbajack list then repairs the Mod Organizer paths (the only post-install step here; per-engine setup happens on the Play tab).</summary>
    [RelayCommand]
    private Task RunInstall() =>
        RunBusyAsync($"{InstallOrUpdateLabel} Morrowind Remastered", async (p, ct) =>
        {
            var (ok, message) = await RunModlistInstallAsync(p, ct).ConfigureAwait(false);
            if (!ok)
            {
                return (false, message);
            }

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

    /// <summary>Installs the modlist from the configured source (catalog / machineURL / local file); the catalog modlist is optional since the test source modes don't need it.</summary>
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

    /// <summary>Cancels the in-flight install run.</summary>
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

    /// <summary>Whether the last install run succeeded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallFailed))]
    private bool _installResultSuccess;

    /// <summary>True when there is an install outcome message to show.</summary>
    public bool HasInstallResult => !string.IsNullOrEmpty(InstallResultMessage);

    /// <summary>True when the last install run ended unsuccessfully.</summary>
    public bool InstallFailed => HasInstallResult && !InstallResultSuccess;

    /// <summary>Clear Downloads is disabled while busy, since clearing the cache mid-install would pull files out from under the running Wabbajack process.</summary>
    public bool CanClearDownloads => !IsBusy;

    /// <summary>Deletes and recreates the shared downloads cache folder.</summary>
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

    /// <summary>Deletes the selected edition's installation folder and clears its install record, keeping the shared downloads cache (Clear Downloads handles that).</summary>
    /// <remarks>Failure is typically a file lock (e.g. MO2 still running); the error then surfaces in the banner via <c>Logger.ErrorLogged</c>.</remarks>
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

    /// <summary>True while any long-running operation is in progress (gates most commands).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunInstall))]
    [NotifyPropertyChangedFor(nameof(CanClearDownloads))]
    [NotifyPropertyChangedFor(nameof(CanLaunchMo2))]
    [NotifyCanExecuteChangedFor(nameof(ClearDownloadsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunInstallStepCommand))]
    private bool _isBusy;

    /// <summary>Title shown above the progress area for the current operation.</summary>
    [ObservableProperty]
    private string _busyTitle = "";

    /// <summary>Current progress percentage (0–100).</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>True when progress is indeterminate (no known percentage).</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>The current progress status line.</summary>
    [ObservableProperty]
    private string? _progressLine;
}
