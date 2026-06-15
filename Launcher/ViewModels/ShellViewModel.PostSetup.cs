using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MorrowindRemasteredLauncher.Models;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>Live status of a setup step on the Play tab.</summary>
public enum StepState { Pending, Running, Done, Failed }

/// <summary>One row of the per-edition setup checklist (Play tab); State ticks live.</summary>
public sealed partial class InstallStepVm : ObservableObject
{
    public PostSetupStep Step { get; init; }
    public string Label { get; init; } = "";

    [ObservableProperty]
    private StepState _state;
}

/// <summary>
/// A config-driven MO2 tool launchable from the Tools panel. A blank
/// <see cref="Executable"/> opens the MO2 GUI itself; otherwise it is the MO2
/// customExecutable title to launch for the selected edition's profile.
/// </summary>
public sealed record ToolLaunchVm(string Name, string Description, string? Executable)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

/// <summary>
/// Play launch (auto-running any incomplete setup first), the per-edition setup
/// checklist shown on the Play tab, and the Tools panel's launch-through-MO2
/// buttons. Reuses the shared busy / progress / result UI from the install flow.
/// </summary>
public partial class ShellViewModel
{
    /// <summary>The per-edition setup steps shown on the Play tab (with live state).</summary>
    public ObservableCollection<InstallStepVm> SetupSteps { get; } = new();

    /// <summary>The MO2 tools launchable from the Tools panel for the current edition.</summary>
    public ObservableCollection<ToolLaunchVm> ToolLaunches { get; } = new();

    /// <summary>Rebuilds the Play-tab setup checklist for the selected edition.</summary>
    private void RebuildSetupSteps()
    {
        SetupSteps.Clear();
        foreach (var s in _verifier.Verify(SelectedEdition))
        {
            SetupSteps.Add(new InstallStepVm
            {
                Step = s.Step,
                Label = s.Label,
                State = s.Done ? StepState.Done : StepState.Pending
            });
        }

        // Optional, non-required final step (only when Steam is installed): it's kept
        // out of the required/auto-run set, so Play never waits on or runs it.
        if (_steam.IsInstalled)
        {
            SetupSteps.Add(new InstallStepVm
            {
                Step = PostSetupStep.AddToSteam,
                Label = PostSetupVerifier.Label(PostSetupStep.AddToSteam),
                State = _verifier.IsDone(SelectedEdition, PostSetupStep.AddToSteam)
                    ? StepState.Done : StepState.Pending
            });
        }
        RunInstallStepCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Rebuilds the manual tool-launch list for the selected edition from config
    /// (keyed by edition display name). Called from RefreshState.
    /// </summary>
    private void RebuildToolsLists()
    {
        ToolLaunches.Clear();
        if (_config.Current.Tools.TryGetValue(SelectedEdition.DisplayName(), out var tools))
        {
            foreach (var t in tools)
            {
                ToolLaunches.Add(new ToolLaunchVm(t.Name, t.Description, t.Executable));
            }
        }
    }

    // ----------------------------------------------------- command gating

    /// <summary>True when an MO2 launch is allowed (not busy, no MO2 running).</summary>
    public bool CanLaunchMo2 => !IsBusy && !IsMo2Running;

    /// <summary>
    /// A setup step can run when not busy, every earlier step is Done (forward
    /// gating), and — for steps that launch MO2 — no ModOrganizer.exe is open.
    /// </summary>
    private bool CanRunInstallStep(InstallStepVm? item)
    {
        if (IsBusy || item is null)
        {
            return false;
        }
        // The optional "Add to Steam" step is independent of modlist setup — runnable
        // any time (not forward-gated behind the required steps).
        if (item.Step == PostSetupStep.AddToSteam)
        {
            return true;
        }
        foreach (var s in SetupSteps)
        {
            if (ReferenceEquals(s, item))
            {
                break;
            }
            if (s.State != StepState.Done)
            {
                return false;
            }
        }
        return !(PostSetupVerifier.LaunchesMo2(item.Step) && IsMo2Running);
    }

    // ------------------------------------------------------------------- Play

    [RelayCommand(CanExecute = nameof(CanLaunchMo2))]
    private async Task Play()
    {
        if (IsBusy)
        {
            return;
        }

        // Auto-run any incomplete setup steps for the selected edition first.
        if (!_verifier.IsFullyConfigured(SelectedEdition))
        {
            await RunBusyAsync($"Setting up {SelectedEditionName}…", async (p, ct) =>
            {
                var steps = await _postSetup.RunAllAsync(SelectedEdition, p, ct).ConfigureAwait(false);
                var failed = steps.Where(s => !s.Success).ToList();
                return failed.Count == 0
                    ? (true, "Setup complete.")
                    : (false, "Setup didn't finish: " +
                        string.Join(", ", failed.Select(f => PostSetupVerifier.Label(f.Step))) +
                        ". See the log, then try again.");
            }).ConfigureAwait(true);

            // RunBusyAsync re-runs the verifier via RefreshState; bail if still incomplete.
            if (!_verifier.IsFullyConfigured(SelectedEdition))
            {
                return;
            }
        }

        try
        {
            // Make sure the latest display settings are on disk before launch.
            _displayConfig.ApplyDisplay(SelectedEdition);
            await _mo2Launch
                .LaunchAsync(SelectedEdition, SelectedEdition.Mo2PlayExecutableName(),
                    waitForExit: false, CancellationToken.None)
                .ConfigureAwait(true);
            Logger.Info($"Launched {SelectedEdition} via MO2.");

            // Optionally hold a Steam session so the run counts as Morrowind playtime;
            // a headless helper process owns the session and ends it when the game exits.
            if (TrackSteamPlaytime && IsSteamRunning)
            {
                StartSteamPresence(SelectedEdition);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't launch the game", ex);
            ReportError($"Couldn't launch the game: {ex.Message}");
        }
    }

    // ------------------------------------------------------- Setup checklist

    [RelayCommand(CanExecute = nameof(CanRunInstallStep))]
    private Task RunInstallStep(InstallStepVm? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }
        return RunBusyAsync(item.Label, async (p, ct) =>
        {
            item.State = StepState.Running;
            var r = await _postSetup.RunStepAsync(SelectedEdition, item.Step, force: true, p, ct)
                .ConfigureAwait(false);
            // Report against the real marker — a GUI tool the user didn't finish
            // leaves the step incomplete.
            var done = r.Success && _verifier.IsDone(SelectedEdition, item.Step);
            item.State = done ? StepState.Done : StepState.Failed;
            var label = PostSetupVerifier.Label(item.Step);
            if (!r.Success)
            {
                return (false, r.Error ?? $"{label} failed.");
            }
            if (done && item.Step == PostSetupStep.AddToSteam)
            {
                return (true, "Added to Steam — the shortcut appears once Steam restarts.");
            }
            return done
                ? (true, $"{label} complete.")
                : (false, $"{label} isn't finished yet — complete it and run again.");
        });
    }

    // ------------------------------------------------------------- Tools panel

    [RelayCommand(CanExecute = nameof(CanLaunchMo2))]
    private async Task LaunchTool(ToolLaunchVm? tool)
    {
        if (tool is null)
        {
            return;
        }
        try
        {
            // A blank executable opens the MO2 GUI itself (instance+profile, no tool).
            var appName = string.IsNullOrWhiteSpace(tool.Executable)
                ? string.Empty
                : tool.Executable;
            await _mo2Launch
                .LaunchAsync(SelectedEdition, appName, waitForExit: false, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.Error($"Couldn't launch {tool.Name}", ex);
            ReportError($"Couldn't launch {tool.Name}: {ex.Message}");
        }
    }

    // --------------------------------------------------- shared busy runner

    /// <summary>
    /// Runs a cancellable, progress-reporting operation using the same busy /
    /// progress / result UI as the install flow.
    /// </summary>
    private async Task RunBusyAsync(
        string title,
        Func<IProgress<InstallProgress>, CancellationToken, Task<(bool Ok, string Message)>> work)
    {
        if (IsBusy)
        {
            return;
        }

        _installCts = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            IsInstallRunning = true;
            InstallResultMessage = null;
            LastError = null;
            BusyTitle = title;
            IsProgressIndeterminate = true;
            ProgressPercent = 0;
            ProgressLine = "Starting…";

            var progress = new Progress<InstallProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Line))
                {
                    ProgressLine = p.Line;
                }
                IsProgressIndeterminate = p.Indeterminate;
                if (p.Percent is { } pct)
                {
                    ProgressPercent = pct;
                }
            });

            var (ok, message) = await work(progress, _installCts.Token).ConfigureAwait(true);
            InstallResultSuccess = ok;
            InstallResultMessage = message;
        }
        catch (OperationCanceledException)
        {
            InstallResultSuccess = false;
            InstallResultMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            Logger.Error($"{title} failed", ex);
            InstallResultSuccess = false;
            InstallResultMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsInstallRunning = false;
            _installCts?.Dispose();
            _installCts = null;
            SyncPostSetupComplete();
            RefreshState();
        }
    }

    /// <summary>
    /// Keeps the persisted <c>PostSetupComplete</c> flag in step with the real
    /// verifier result. The per-step checklist run bypasses
    /// <see cref="PostSetupService.RunAllAsync"/> (which used to set it), so we sync
    /// it here — otherwise the edition would stay "setup required" after the
    /// checklist is finished.
    /// </summary>
    private void SyncPostSetupComplete()
    {
        try
        {
            var done = _verifier.IsFullyConfigured(SelectedEdition);
            if (_config.Current.Install.GetSetupComplete(SelectedEdition) != done)
            {
                _config.Current.Install.SetSetupComplete(SelectedEdition, done);
                _config.Save();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't sync post-setup flag: {ex.Message}");
        }
    }
}
