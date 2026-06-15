using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Coarse progress for the install UI. Line is null for percent-only updates
/// (the UI keeps showing the previous status text).
/// </summary>
public sealed record InstallProgress(
    string Stage,
    string? Line,
    double? Percent,
    bool Indeterminate);

/// <summary>Outcome of an install run.</summary>
public sealed record InstallResult(bool Success, string? Error);

/// <summary>
/// Drives a headless Wabbajack install:
///   1. Acquire the Wabbajack CLI (cached in the portable Wabbajack folder).
///   2. Pick the install source as a cascade (no explicit mode):
///        - a local <c>.wabbajack</c> file when one is configured AND present on
///          disk (<c>wabbajack-cli install -w &lt;file&gt;</c>), otherwise
///        - the online list named by the configured machineURL, resolved from the
///          gallery with <c>-m &lt;repo/slug&gt;</c> — ALWAYS together with
///          <c>-w &lt;cache path&gt;</c>: the CLI uses -w as the destination for the
///          list it downloads, and crashes with "Value cannot be null. (Parameter
///          'array')" when -m is passed alone.
///      Either way the CLI downloads the archives itself, authenticating with the
///      OAuth token already written to Wabbajack's encrypted store, installing into
///      the edition's install dir and sharing the global Downloads cache so
///      editions/updates don't re-download archives. (The catalog's direct
///      <c>links.download</c> URL is not fetched: authored-files.wabbajack.org 404s
///      for plain-HTTP clients on every list — only the CLI's internal resolver can
///      fetch them — so the gallery <c>-m</c> path is the sole online source. The
///      catalog is still read elsewhere for version/size metadata.)
///   3. On success, record the installed version + timestamp in config.
///
/// Authentication is handled entirely by the Wabbajack token store (the launcher
/// signs the user in beforehand); the CLI reads it from
/// <c>%LOCALAPPDATA%\Wabbajack\encrypted\nexus-oauth-info</c>. There is no
/// NEXUS_API_KEY environment variable in Wabbajack 4.x.
///
/// Post-setup (OpenMW/MWSE configuration) is a separate step run afterwards.
/// </summary>
public sealed class InstallEngine
{
    /// <summary>
    /// The Wabbajack repository these lists are published under, as registered in
    /// the official <c>repositories.json</c>. The CLI's <c>-m</c> lookup keys
    /// featured lists as <c>&lt;RepositoryName&gt;/&lt;machineURL&gt;</c>, e.g.
    /// <c>Kezyma/MorrowindRemasteredMWSEEdition</c> — the bare machineURL alone
    /// resolves to "Couldn't find list".
    /// </summary>
    private const string RepositoryName = "Kezyma";

    /// <summary>
    /// How many times to retry when the CLI fails while resolving the list from
    /// the gallery (a transient failure that clears on retry).
    /// </summary>
    private const int MaxResolveAttempts = 4;

    private readonly WabbajackCliService _cli;
    private readonly NexusAuthService _nexus;
    private readonly InstallStateService _installState;
    private readonly ConfigService _config;

    // Matches a percentage Wabbajack prints during install, e.g. "42.5%".
    private static readonly Regex PercentRegex =
        new(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    // Matches the CLI's "00:01:23.456 [INFO] " line prefix, stripped from the
    // progress text shown to the user (the log keeps the raw line).
    private static readonly Regex CliPrefixRegex =
        new(@"^\d{2}:\d{2}:\d{2}(?:\.\d+)?\s*\[\w+\]\s*", RegexOptions.Compiled);

    public InstallEngine(
        WabbajackCliService cli,
        NexusAuthService nexus,
        InstallStateService installState,
        ConfigService config)
    {
        _cli = cli;
        _nexus = nexus;
        _installState = installState;
        _config = config;
    }

    /// <summary>
    /// Installs (or updates) the given edition's modlist. Reports progress and
    /// returns the result. Requires a usable Wabbajack Nexus token on disk.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        Edition edition,
        Modlist? modlist,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_nexus.HasUsableToken)
        {
            return new InstallResult(false, "Sign in to Nexus Mods before installing.");
        }

        var source = _config.Current.InstallSource;
        try
        {
            // ---- 1. Ensure the CLI is present ----
            Report(progress, "Preparing", "Preparing Wabbajack…", null, true);
            await _cli.EnsureAvailableAsync(
                new Progress<string>(s => Report(progress, "Preparing", s, null, true)),
                ct).ConfigureAwait(false);

            // ---- 2. Run the headless install into the single shared install dir ----
            var outputDir = _installState.GetEditionInstallDir(edition);
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(AppPaths.DownloadsDir);
            Report(progress, "Installing", "Starting installation…", null, true);

            // Progress-tracker inputs (see CliProgressTracker / PollInstallBytesAsync).
            var totalArchives = modlist?.DownloadMetadata?.NumberOfArchives ?? 0;
            var installBytesTotal = modlist?.DownloadMetadata?.SizeOfInstalledFiles ?? 0;

            // Gallery (-m) resolution fails transiently; retry a few times.
            async Task<CliRunResult> RunWithResolveRetry(string machineUrl, string fallbackTarget)
            {
                Directory.CreateDirectory(AppPaths.ModlistCacheDir);
                var attempt = 0;
                while (true)
                {
                    attempt++;
                    var r = await RunCliInstallAsync(
                        machineUrl, fallbackTarget, outputDir, AppPaths.DownloadsDir,
                        totalArchives, installBytesTotal, progress, ct).ConfigureAwait(false);
                    if (r.ExitCode == 0 || !r.FailedResolvingList || attempt >= MaxResolveAttempts)
                    {
                        return r;
                    }
                    Logger.Warn($"List resolution failed (attempt {attempt}/{MaxResolveAttempts}); retrying…");
                    Report(progress, "Preparing",
                        $"Couldn't reach the Wabbajack gallery; retrying ({attempt}/{MaxResolveAttempts})…",
                        null, true);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                }
            }

            CliRunResult run;

            // Source cascade: a present local .wabbajack file overrides the online list.
            // The version (when known) comes from the catalog metadata in `modlist`; a raw
            // local file usually has none, so the real version is read back from the
            // installed compiler_settings later (never record a blank).
            var recordedVersion = string.IsNullOrWhiteSpace(modlist?.Version) ? null : modlist!.Version;

            if (source.ResolveExistingLocalFile() is { } listFile)
            {
                // Tier 1: install straight from the local .wabbajack file.
                Report(progress, "Installing", "Installing from local modlist file…", null, true);
                Logger.Info($"Installing from local modlist file: {listFile}");
                run = await RunCliInstallAsync(
                    machineUrl: null, listFile, outputDir, AppPaths.DownloadsDir,
                    totalArchives, installBytesTotal, progress, ct).ConfigureAwait(false);
            }
            else if (source.HasMachineUrl)
            {
                // Tier 2: install the online list by resolving its machineURL from the gallery.
                var machineUrl = source.MachineUrl!.Contains('/')
                    ? source.MachineUrl!
                    : $"{RepositoryName}/{source.MachineUrl}";
                run = await RunWithResolveRetry(
                    machineUrl, Path.Combine(AppPaths.ModlistCacheDir, "combined.wabbajack"))
                    .ConfigureAwait(false);
            }
            else
            {
                return new InstallResult(false,
                    "No modlist source configured (set installSource.machineUrl or a local file).");
            }

            if (run.ExitCode != 0)
            {
                var detail = run.FailedResolvingList
                    ? "Couldn't load the modlist from the Wabbajack gallery. " +
                      "Please check your connection and try again."
                    : $"Wabbajack exited with code {run.ExitCode}. See the log for details.";
                return new InstallResult(false, detail);
            }

            // ---- 3. Record success (single combined install; setup runs per profile) ----
            var record = _config.Current.Install;
            record.InstalledVersion = recordedVersion;
            record.InstalledAt = DateTimeOffset.UtcNow;
            record.SetupComplete.Clear();
            _config.Save();

            Report(progress, "Done", "Installation complete.", 100, false);
            Logger.Info($"Install completed (v{recordedVersion}) at \"{outputDir}\".");
            return new InstallResult(true, null);
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("Install cancelled.");
            return new InstallResult(false, "Installation cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error("Install failed", ex);
            return new InstallResult(false, ex.Message);
        }
    }

    /// <summary>Result of a single CLI install attempt.</summary>
    private sealed record CliRunResult(int ExitCode, bool FailedResolvingList);

    /// <summary>
    /// Derives a monotonic overall percentage for one CLI install run from the
    /// CLI's own log lines. The CLI never prints a percentage (it computes
    /// progress internally but the CLI verb doesn't subscribe to it), so we
    /// track this run's work instead:
    ///   "Missing N archives"          → N downloads happen this run (0 on a
    ///                                    reinstall; few on an update — shared/
    ///                                    cached archives already excluded by
    ///                                    Wabbajack's hash index),
    ///   "Finished downloading …"      → one download done (5–58% band),
    ///   "Installing files" step       → 60–95% band driven by BYTES written to
    ///                                    the install dir (fed by the engine's
    ///                                    folder poller via
    ///                                    <see cref="ReportInstallFilesFraction"/>;
    ///                                    "Extracting" lines can't be counted —
    ///                                    nested archives make their number
    ///                                    unpredictable),
    ///   "Building <name>"             → per-BSA bumps across 96–99,
    ///   "Next Step: …"                → fixed floor bumps per phase.
    /// Tracking the run's own events keeps reinstalls, updates and the shared
    /// Downloads cache honest.
    /// </summary>
    private sealed class CliProgressTracker
    {
        private static readonly Regex NextStepRegex =
            new(@"Next Step:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex MissingArchivesRegex =
            new(@"Missing (\d+) archives", RegexOptions.Compiled);
        private static readonly Regex OptimizedRegex =
            new(@"Optimized (\d+) directives to (\d+) required", RegexOptions.Compiled);
        private static readonly Regex BsaCountRegex =
            new(@"Building (\d+) bsa files", RegexOptions.Compiled);

        private double _percent;
        private bool _started;
        private int _downloadTotal = -1;
        private int _downloadDone;
        private int _bsaTotal;
        private int _bsaDone;
        private bool _inBsaStep;

        public CliProgressTracker(int totalArchives)
        {
            // totalArchives reserved for future use; the download band sizes
            // itself from this run's "Missing N archives" line instead.
            _ = totalArchives;
        }

        /// <summary>
        /// Fraction of this run's directives that actually need installing
        /// ("Optimized X directives to Y required"). Scales the expected bytes
        /// of the Installing-files phase: 1.0 on a fresh install, near 0 when
        /// reinstalling over an identical copy.
        /// </summary>
        public double DirectiveRatio { get; private set; } = 1.0;

        /// <summary>True while the CLI is in the "Installing files" step.</summary>
        public bool InstallFilesPhaseActive { get; private set; }

        /// <summary>
        /// Maps a bytes-written fraction (0–1) of the Installing-files phase
        /// onto the 60–95 band and returns the monotonic overall percent.
        /// Called from the engine's folder poller thread; racing with line
        /// updates is benign (Bump only ever raises the value).
        /// </summary>
        public double ReportInstallFilesFraction(double fraction)
        {
            Bump(60 + Math.Clamp(fraction, 0, 1) * 35);
            return _percent;
        }

        /// <summary>
        /// Consumes one raw CLI line and returns the updated monotonic percent,
        /// or null while the CLI is still starting up (game detection etc.).
        /// </summary>
        public double? Update(string line)
        {
            var step = NextStepRegex.Match(line);
            if (step.Success)
            {
                _started = true;
                var name = step.Groups[1].Value;
                InstallFilesPhaseActive = name.StartsWith("Installing files",
                    StringComparison.OrdinalIgnoreCase);
                _inBsaStep = name.StartsWith("Building BSAs",
                    StringComparison.OrdinalIgnoreCase);
                Bump(name switch
                {
                    "Configuring Installer" => 1,
                    "Looking for files to delete" => 2,
                    "Deleting outdated files" => 2,
                    "Cleaning empty folders" => 2,
                    "Looking for unmodified files" => 3,
                    "Updating ModList" => 3,
                    "Hashing Archives" => 4,
                    "Downloading files" => 5,
                    "Extracting Modlist" => 58,
                    "Priming VFS" => 59,
                    "Building Folder Structure" => 60,
                    "Installing files" => 60,
                    "Installing Included Files" => 95,
                    "Building BSAs" => 96,
                    "Generating ZEdit Merges" => 99,
                    "Finished" => 99,
                    _ => 0
                });
            }

            var missing = MissingArchivesRegex.Match(line);
            if (missing.Success && int.TryParse(missing.Groups[1].Value, out var n))
            {
                _downloadTotal = n;
            }

            if (_downloadTotal > 0 &&
                line.Contains("Finished downloading", StringComparison.OrdinalIgnoreCase))
            {
                _downloadDone++;
                Bump(5 + Math.Min(1.0, (double)_downloadDone / _downloadTotal) * 53);
            }

            var optimized = OptimizedRegex.Match(line);
            if (optimized.Success &&
                long.TryParse(optimized.Groups[1].Value, out var total) && total > 0 &&
                long.TryParse(optimized.Groups[2].Value, out var required))
            {
                DirectiveRatio = Math.Clamp((double)required / total, 0, 1);
            }

            var bsaCount = BsaCountRegex.Match(line);
            if (bsaCount.Success && int.TryParse(bsaCount.Groups[1].Value, out var bsas))
            {
                _bsaTotal = bsas;
            }
            else if (_inBsaStep && _bsaTotal > 0 &&
                     line.Contains("] Building ", StringComparison.Ordinal))
            {
                _bsaDone++;
                Bump(96 + Math.Min(1.0, (double)_bsaDone / _bsaTotal) * 3);
            }

            // Opportunistic: honour any literal "NN%" a future CLI might print.
            var pct = PercentRegex.Match(line);
            if (pct.Success && double.TryParse(pct.Groups[1].Value, out var p))
            {
                _started = true;
                Bump(Math.Clamp(p, 0, 100));
            }

            return _started ? _percent : null;
        }

        private void Bump(double value)
            => _percent = Math.Max(_percent, Math.Min(value, 99));
    }

    /// <summary>
    /// Runs one CLI install. With only <paramref name="wabbajackFile"/>, the
    /// CLI installs that local file directly. With <paramref name="machineUrl"/>
    /// too, the CLI resolves the list from the gallery and downloads it to
    /// <paramref name="wabbajackFile"/> first (-m must always be accompanied by
    /// -w: the CLI dereferences the -w path while saving the download and
    /// crashes when it is missing).
    /// </summary>
    private async Task<CliRunResult> RunCliInstallAsync(
        string? machineUrl,
        string wabbajackFile,
        string outputDir,
        string downloadsDir,
        int totalArchives,
        long installBytesTotal,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var tracker = new CliProgressTracker(totalArchives);
        var psi = new ProcessStartInfo
        {
            FileName = _cli.CliPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppPaths.WabbajackDir
        };

        // Auth is taken from Wabbajack's encrypted OAuth token store.
        psi.ArgumentList.Add("install");
        if (machineUrl is not null)
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(machineUrl);
        }
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add(wabbajackFile);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDir);
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(downloadsDir);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Track whether this run died while resolving the list (vs. a real install
        // failure), so the caller can decide to retry.
        var failedResolvingList = false;

        void OnLine(string? line)
        {
            if (LooksLikeListResolutionFailure(line))
            {
                failedResolvingList = true;
            }
            HandleLine(line, tracker, progress);
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);

        var listArg = machineUrl is not null
            ? $"-m \"{machineUrl}\" -w \"{wabbajackFile}\""
            : $"-w \"{wabbajackFile}\"";
        Logger.Info($"Launching: {_cli.CliPath} install {listArg} " +
                    $"-o \"{outputDir}\" -d \"{downloadsDir}\"");

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Ensure the process is killed if the user cancels.
        await using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* ignore */ }
        });

        // The "Installing files" phase logs no usable per-unit events (nested
        // archives make "Extracting" counts unpredictable), so progress there
        // is measured as bytes written to the install dir since the phase
        // began, against this run's expected workload.
        using var pollerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poller = PollInstallBytesAsync(
            tracker, outputDir, installBytesTotal, progress, pollerCts.Token);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            pollerCts.Cancel();
            try { await poller.ConfigureAwait(false); } catch { /* cancelled */ }
        }
        return new CliRunResult(process.ExitCode, failedResolvingList);
    }

    /// <summary>
    /// Background companion to a CLI run: once the tracker enters the
    /// "Installing files" phase, takes a baseline byte-count of the install
    /// dir, then rescans every few seconds and maps the bytes written since
    /// the baseline onto the 60–95 band. Expected bytes are the catalog's
    /// SizeOfInstalledFiles scaled by this run's directive ratio, so
    /// reinstalls (delta ≈ small) and pre-existing files (baseline) are
    /// handled honestly.
    /// </summary>
    private static async Task PollInstallBytesAsync(
        CliProgressTracker tracker,
        string outputDir,
        long installBytesTotal,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        if (installBytesTotal <= 0)
        {
            return;
        }

        static long ScanBytes(string dir)
        {
            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(
                    dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { /* file vanished mid-scan */ }
                }
            }
            catch { /* dir busy/missing; treat as current total */ }
            return total;
        }

        try
        {
            while (!tracker.InstallFilesPhaseActive)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }

            var baseline = await Task.Run(() => ScanBytes(outputDir), ct).ConfigureAwait(false);
            var expected = Math.Max(1, (long)(installBytesTotal * tracker.DirectiveRatio));

            while (tracker.InstallFilesPhaseActive)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                var written = await Task.Run(() => ScanBytes(outputDir), ct)
                    .ConfigureAwait(false) - baseline;
                var percent = tracker.ReportInstallFilesFraction((double)written / expected);
                Report(progress, "Installing", null, percent, false);
            }
        }
        catch (OperationCanceledException)
        {
            // CLI run ended; nothing to clean up.
        }
    }

    /// <summary>
    /// True when a CLI output line indicates the (transient) failure to load the
    /// list from the gallery, rather than a genuine install error.
    /// </summary>
    private static bool LooksLikeListResolutionFailure(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return line.Contains("Couldn't find list", StringComparison.OrdinalIgnoreCase)
            || line.Contains("DownloadMachineUrl", StringComparison.OrdinalIgnoreCase)
            || line.Contains("LoadFeaturedLists", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Value cannot be null. (Parameter 'array')",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void HandleLine(
        string? line, CliProgressTracker tracker, IProgress<InstallProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Logger.Info($"[wabbajack] {line}");

        // Null until the install steps begin (CLI startup/game detection):
        // the bar stays indeterminate, then turns determinate and counts up.
        var percent = tracker.Update(line);

        // TRACE lines (raw paths etc.) stay in the log but aren't shown in
        // the UI; the previous status line remains visible.
        var display = line.Contains("[TRACE]", StringComparison.Ordinal)
            ? null
            : CliPrefixRegex.Replace(line.Trim(), "");
        if (display is null && percent is null)
        {
            return;
        }
        Report(progress, "Installing", display, percent, percent is null);
    }

    private static void Report(
        IProgress<InstallProgress>? progress,
        string stage, string? line, double? percent, bool indeterminate)
        => progress?.Report(new InstallProgress(stage, line, percent, indeterminate));
}
