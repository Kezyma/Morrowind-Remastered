using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Outcome of a single post-setup step.</summary>
public sealed record StepResult(PostSetupStep Step, bool Success, bool Skipped, string? Error);

/// <summary>
/// Runs the post-install steps for an edition, in order. Each step is idempotent
/// — it is skipped when <see cref="PostSetupVerifier"/> already reports it done
/// (unless forced) — so this both drives the automatic post-install tail and
/// backs the Tools panel's per-step / run-all buttons. Reports through the same
/// <see cref="InstallProgress"/> pipe the install uses.
/// </summary>
public sealed class PostSetupService
{
    private readonly ConfigService _config;
    private readonly InstallStateService _installState;
    private readonly PostSetupVerifier _verifier;
    private readonly Mo2IniService _mo2Ini;
    private readonly PostSetupConfigService _displayConfig;
    private readonly BinarySetupService _binaries;
    private readonly Mo2ToolAutomation _tools;
    private readonly SteamService _steam;

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

    /// <summary>
    /// Runs every applicable step for an edition, skipping ones already done.
    /// On completion records <c>PostSetupComplete</c> = fully configured.
    /// </summary>
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

    /// <summary>
    /// Runs one step. When not forced, skips it if already done. Used by both
    /// the run-all loop and the Tools panel's individual buttons (forced).
    /// </summary>
    public async Task<StepResult> RunStepAsync(
        Edition edition, PostSetupStep step, bool force,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var label = PostSetupVerifier.Label(step);
        if (!force && _verifier.IsDone(edition, step))
        {
            Report(progress, $"{label}: already done.", null, false);
            return new StepResult(step, Success: true, Skipped: true, null);
        }

        Report(progress, $"{label}…", null, true);
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
                    // MGE flips [Distant Land] Distant Land=Off while generating; turn it back on.
                    _displayConfig.EnableDistantLand(edition);
                    break;
                case PostSetupStep.DeltaMerge:
                    await _tools.DeltaMergeAsync(edition, progress, ct).ConfigureAwait(false);
                    break;
                case PostSetupStep.AddToSteam:
                    // Restart Steam (when running) so the shortcut + artwork load now;
                    // this also makes the write stick, since Steam rewrites shortcuts.vdf
                    // from memory on exit.
                    if (!await _steam.AddLauncherShortcutAsync(restartSteam: true, ct)
                            .ConfigureAwait(false))
                    {
                        return Fail(step, "Couldn't add the launcher to Steam.");
                    }
                    break;
            }

            Report(progress, $"{label}: done.", null, false);
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

    private static StepResult Fail(PostSetupStep step, string? error)
        => new(step, Success: false, Skipped: false, error ?? "Failed.");

    private static void Report(
        IProgress<InstallProgress>? progress, string line, double? percent, bool indeterminate)
        => progress?.Report(new InstallProgress("Setup", line, percent, indeterminate));
}
