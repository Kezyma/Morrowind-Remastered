using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>
/// Prune Mods: finds and deletes loose mod files that neither profile loads, to reclaim disk space.
/// The (heavy) scan runs in the background when the Install tab opens and after an install completes;
/// the button shows the freed-space total and enables once the scan finishes.
/// </summary>
public partial class ShellViewModel
{
    /// <summary>Total size in bytes the prune scan found to be redundant.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PruneLabel))]
    [NotifyCanExecuteChangedFor(nameof(PruneCommand))]
    private long _pruneSize;

    /// <summary>Number of redundant files the last prune scan found (shown in the confirmation).</summary>
    [ObservableProperty]
    private int _pruneCount;

    /// <summary>True while the background prune scan is running (button shows "calculating…" and stays disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PruneLabel))]
    [NotifyCanExecuteChangedFor(nameof(PruneCommand))]
    private bool _isPruneScanning;

    /// <summary>The Prune button is shown once an install is present; it then scans and shows the freed-space total, enabling when there is something to free.</summary>
    public bool ShowPruneButton => CurrentState?.IsInstalled == true;

    /// <summary>The Prune Mods button label: a calculating note while scanning, the freed-space total when there is something to prune, or just "Prune Mods" (no size) when there is nothing.</summary>
    public string PruneLabel => IsPruneScanning
        ? "Prune Mods (calculating…)"
        : PruneSize > 0
            ? $"Prune Mods ({Converters.ByteSizeConverter.Format(PruneSize)})"
            : "Prune Mods";

    /// <summary>Prune is available once installed, when nothing else is busy, MO2 is closed, the scan is done, and there is something to free.</summary>
    public bool CanPrune => !IsBusy && !IsMo2Running && !IsPruneScanning
                            && CurrentState?.IsInstalled == true && PruneSize > 0;

    /// <summary>True once a successful scan's result is cached; cleared when the mods folder changes (install/uninstall/prune) so the heavy scan runs once per change, not on every tab visit.</summary>
    private bool _pruneScanValid;

    /// <summary>Recomputes the prunable size off the UI thread, then shows it and enables the button.</summary>
    /// <remarks>Far heavier than <see cref="RefreshDownloadsInfo"/> (it walks every mod folder and both load orders), so it is cached and only re-run when <paramref name="force"/> is set (after install/prune) or no valid result exists — never from <see cref="RefreshState"/>.</remarks>
    public void RefreshPruneInfo(bool force = false)
    {
        if (IsPruneScanning || CurrentState?.IsInstalled != true)
        {
            return;
        }
        if (!force && _pruneScanValid)
        {
            return; // cached result is still good — skip the expensive re-scan.
        }

        IsPruneScanning = true;
        var edition = SelectedEdition;
        Logger.Info("Prune scan starting…");
        _ = Task.Run(() => _modPrune.Analyze(edition)).ContinueWith(t =>
        {
            ModPruneAnalysis analysis;
            bool ok;
            if (t.Status == TaskStatus.RanToCompletion)
            {
                analysis = t.Result;
                ok = true;
                Logger.Info($"Prune scan found {analysis.TotalCount} file(s), " +
                            $"{Converters.ByteSizeConverter.Format(analysis.TotalSize)}.");
            }
            else
            {
                analysis = ModPruneAnalysis.Empty;
                ok = false;
                Logger.Warn($"Prune scan failed: {t.Exception?.GetBaseException().Message}");
            }

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                PruneSize = analysis.TotalSize;
                PruneCount = analysis.TotalCount;
                _pruneScanValid = ok; // a failed scan stays invalid so it retries next time.
                IsPruneScanning = false;
            });
        });
    }

    /// <summary>Invalidates the cached scan (and clears the shown total) after the mods folder changes.</summary>
    public void InvalidatePruneScan()
    {
        _pruneScanValid = false;
        PruneSize = 0;
        PruneCount = 0;
    }

    /// <summary>Confirms, then deletes the redundant mod files, reporting progress and the bytes freed.</summary>
    [RelayCommand(CanExecute = nameof(CanPrune))]
    private Task Prune() =>
        RunBusyAsync("Prune Mods", async (p, ct) =>
        {
            var confirm = MessageBox.Show(
                $"Permanently delete {PruneCount} redundant file(s) " +
                $"({Converters.ByteSizeConverter.Format(PruneSize)}) that neither profile loads?\n\n" +
                "This cannot be undone.",
                "Prune Mods", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return (false, "Prune cancelled.");
            }

            var edition = SelectedEdition;
            var result = await Task.Run(() => _modPrune.Prune(edition, p, ct), ct).ConfigureAwait(true);
            RefreshPruneInfo(force: true); // files were deleted — recompute from disk.
            return (true, $"Pruned {result.DeletedCount} file(s), freed " +
                          $"{Converters.ByteSizeConverter.Format(result.DeletedSize)}." +
                          (result.FailedCount > 0 ? $" {result.FailedCount} could not be deleted." : ""));
        });

    /// <summary>Triggers the background prune scan when the user opens the Install/Manage tab.</summary>
    partial void OnCurrentPageChanged(NavPage value)
    {
        if (value == NavPage.Install)
        {
            RefreshPruneInfo();
        }
    }
}
