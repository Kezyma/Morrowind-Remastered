using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Outcome of a single post-setup step.</summary>
public sealed record StepResult(PostSetupStep Step, bool Success, bool Skipped, string? Error);

/// <summary>Runs the ordered, idempotent post-install steps for an edition, backing both the automatic post-install tail and the Tools panel's per-step / run-all buttons.</summary>
/// <remarks>Each step is skipped when <see cref="PostSetupVerifier"/> already reports it done (unless forced), and progress flows through the same <see cref="InstallProgress"/> pipe the install uses.</remarks>
public sealed class PostSetupService
{
    /// <summary>Persisted launcher config (the install record + per-profile setup flags).</summary>
    private readonly ConfigService _config;
    /// <summary>Resolves install state for an edition.</summary>
    private readonly InstallStateService _installState;
    /// <summary>Derives which steps are needed and which are already done.</summary>
    private readonly PostSetupVerifier _verifier;
    /// <summary>Repairs ModOrganizer.ini paths.</summary>
    private readonly Mo2IniService _mo2Ini;
    /// <summary>Applies display config and toggles distant land.</summary>
    private readonly PostSetupConfigService _displayConfig;
    /// <summary>Downloads and places the OpenMW/Delta/MWSE binaries.</summary>
    private readonly BinarySetupService _binaries;
    /// <summary>Drives MCP and MGE through MO2.</summary>
    private readonly Mo2ToolAutomation _tools;
    /// <summary>Adds the launcher shortcut to Steam.</summary>
    private readonly SteamService _steam;

    /// <summary>Creates the service with all the step dependencies it orchestrates.</summary>
    public PostSetupService(
        ConfigService config,
        InstallStateService installState,
        PostSetupVerifier verifier,
        Mo2IniService mo2Ini,
        PostSetupConfigService displayConfig,
        BinarySetupService binaries,
        Mo2ToolAutomation tools,
        SteamService steam)
    {
        _config = config;
        _installState = installState;
        _verifier = verifier;
        _mo2Ini = mo2Ini;
        _displayConfig = displayConfig;
        _binaries = binaries;
        _tools = tools;
        _steam = steam;
    }

    /// <summary>Runs every applicable step for an edition, skipping ones already done, then records whether it is fully configured.</summary>
    public async Task<IReadOnlyList<StepResult>> RunAllAsync(
        Edition edition, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var results = new List<StepResult>();
        foreach (var step in PostSetupVerifier.SetupStepsFor(edition))
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunStepAsync(edition, step, force: false, progress, ct)
                .ConfigureAwait(false));
        }

        var record = _config.Current.Install;
        var done = _verifier.IsFullyConfigured(edition);
        record.SetSetupComplete(edition, done);
        _config.Save();
        Logger.Info($"Post-setup for {edition} finished; fully configured = {done}.");
        return results;
    }

    /// <summary>Runs one step, skipping it when not forced and already done; used by both the run-all loop and the Tools panel's (forced) buttons.</summary>
    public async Task<StepResult> RunStepAsync(
        Edition edition, PostSetupStep step, bool force,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var label = PostSetupVerifier.Label(step);
        if (!force && _verifier.IsDone(edition, step))
        {
            progress.Report("Setup", $"{label}: already done.", null, false);
            return new StepResult(step, Success: true, Skipped: true, null);
        }

        progress.Report("Setup", $"{label}…", null, true);
        try
        {
            switch (step)
            {
                case PostSetupStep.RepairPaths:
                {
                    var r = _mo2Ini.RepairPaths(edition);
                    if (!r.Success)
                    {
                        return Fail(step, r.Error);
                    }
                    break;
                }
                case PostSetupStep.ApplyDisplay:
                    _displayConfig.ApplyDisplay(edition);
                    break;
                case PostSetupStep.InstallOpenMw:
                    await _binaries.InstallOpenMwAsync(progress, ct).ConfigureAwait(false);
                    break;
                case PostSetupStep.InstallDelta:
                    await _binaries.InstallDeltaAsync(progress, ct).ConfigureAwait(false);
                    break;
                case PostSetupStep.InstallMwse:
                    await _binaries.InstallMwseAsync(progress, ct).ConfigureAwait(false);
                    break;
                case PostSetupStep.ApplyMcp:
                    await _tools.ApplyMcpAsync(edition, progress, ct).ConfigureAwait(false);
                    break;
                case PostSetupStep.GenerateDistantLand:
                    await _tools.GenerateDistantLandAsync(edition, progress, ct).ConfigureAwait(false);
                    _displayConfig.EnableDistantLand(edition);
                    break;
                case PostSetupStep.DeltaMerge:
                    await _tools.DeltaMergeAsync(edition, progress, ct).ConfigureAwait(false);
                    break;
                case PostSetupStep.AddToSteam:
                    if (!await _steam.AddLauncherShortcutAsync(restartSteam: true, ct)
                            .ConfigureAwait(false))
                    {
                        return Fail(step, "Couldn't add the launcher to Steam.");
                    }
                    break;
            }

            progress.Report("Setup", $"{label}: done.", null, false);
            return new StepResult(step, Success: true, Skipped: false, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Post-setup step {step} failed for {edition}", ex);
            return Fail(step, ex.Message);
        }
    }

    /// <summary>Builds a failed <see cref="StepResult"/> for a step.</summary>
    private static StepResult Fail(PostSetupStep step, string? error)
        => new(step, Success: false, Skipped: false, error ?? "Failed.");
}
