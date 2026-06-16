using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Coarse progress for the install UI; <c>Line</c> is null for percent-only updates so the previous status text stays.</summary>
public sealed record InstallProgress(
    string Stage,
    string? Line,
    double? Percent,
    bool Indeterminate);

/// <summary>Outcome of an install run.</summary>
public sealed record InstallResult(bool Success, string? Error);

/// <summary>Drives a headless Wabbajack install, then records the installed version on success.</summary>
/// <remarks>
/// Acquires the cached Wabbajack CLI, then picks the install source as a cascade: a configured local
/// <c>.wabbajack</c> file when present (<c>install -w &lt;file&gt;</c>), else the online list named by the
/// configured machineURL resolved from the gallery with <c>-m &lt;repo/slug&gt;</c>. The <c>-m</c> form MUST
/// always be passed together with <c>-w &lt;cache path&gt;</c> (the CLI uses <c>-w</c> as the download
/// destination and crashes with "Value cannot be null. (Parameter 'array')" when <c>-m</c> is alone). The
/// gallery is the sole online source because authored-files.wabbajack.org 404s for plain-HTTP clients on every
/// list — only the CLI's internal resolver can fetch them. Auth comes from Wabbajack's encrypted OAuth token
/// store (no NEXUS_API_KEY in Wabbajack 4.x); the shared Downloads cache avoids re-downloading archives.
/// Post-setup (OpenMW/MWSE configuration) runs separately afterwards.
/// </remarks>
public sealed class InstallEngine
{
    private readonly WabbajackCliService _cli;
    private readonly NexusAuthService _nexus;
    private readonly InstallStateService _installState;
    private readonly ConfigService _config;

    /// <summary>Matches a percentage Wabbajack might print during install, e.g. "42.5%".</summary>
    private static readonly Regex PercentRegex =
        new(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    /// <summary>Matches the CLI's "00:01:23.456 [INFO] " line prefix, stripped from the progress text shown to the user.</summary>
    private static readonly Regex CliPrefixRegex =
        new(@"^\d{2}:\d{2}:\d{2}(?:\.\d+)?\s*\[\w+\]\s*", RegexOptions.Compiled);

    /// <summary>Creates the install engine.</summary>
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

    /// <summary>Installs or updates the edition's modlist, reporting progress; requires a usable Wabbajack Nexus token on disk.</summary>
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
        var wabbajack = _config.Current.Wabbajack;
        var maxResolveAttempts = wabbajack.MaxResolveAttempts;
        try
        {
            progress.Report("Preparing", "Preparing Wabbajack…", null, true);
            await _cli.EnsureAvailableAsync(
                new Progress<string>(s => progress.Report("Preparing", s, null, true)),
                ct).ConfigureAwait(false);

            var outputDir = _installState.GetEditionInstallDir(edition);
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(AppPaths.DownloadsDir);
            progress.Report("Installing", "Starting installation…", null, true);

            var totalArchives = modlist?.DownloadMetadata?.NumberOfArchives ?? 0;
            var installBytesTotal = modlist?.DownloadMetadata?.SizeOfInstalledFiles ?? 0;

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
                    if (r.ExitCode == 0 || !r.FailedResolvingList || attempt >= maxResolveAttempts)
                    {
                        return r;
                    }
                    Logger.Warn($"List resolution failed (attempt {attempt}/{maxResolveAttempts}); retrying…");
                    progress.Report("Preparing",
                        $"Couldn't reach the Wabbajack gallery; retrying ({attempt}/{maxResolveAttempts})…",
                        null, true);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                }
            }

            CliRunResult run;

            var recordedVersion = string.IsNullOrWhiteSpace(modlist?.Version) ? null : modlist!.Version;

            if (source.ResolveExistingLocalFile() is { } listFile)
            {
                progress.Report("Installing", "Installing from local modlist file…", null, true);
                Logger.Info($"Installing from local modlist file: {listFile}");
                run = await RunCliInstallAsync(
                    machineUrl: null, listFile, outputDir, AppPaths.DownloadsDir,
                    totalArchives, installBytesTotal, progress, ct).ConfigureAwait(false);
            }
            else if (source.HasMachineUrl)
            {
                var machineUrl = source.MachineUrl!.Contains('/')
                    ? source.MachineUrl!
                    : $"{wabbajack.RepositoryName}/{source.MachineUrl}";
                run = await RunWithResolveRetry(
                    machineUrl, Path.Combine(AppPaths.ModlistCacheDir, wabbajack.CombinedListFileName))
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

            var record = _config.Current.Install;
            record.InstalledVersion = recordedVersion;
            record.InstalledAt = DateTimeOffset.UtcNow;
            record.SetupComplete.Clear();
            _config.Save();

            progress.Report("Done", "Installation complete.", 100, false);
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

    /// <summary>Derives a monotonic overall install percentage from the CLI's own log lines.</summary>
    /// <remarks>
    /// The CLI never prints a percentage (it computes progress internally but the verb doesn't subscribe), so
    /// this tracks the run's own events instead, which keeps reinstalls, updates and the shared Downloads cache
    /// honest: "Missing N archives" sets this run's download count (0 on a reinstall, few on an update —
    /// cached archives are excluded by Wabbajack's hash index); "Finished downloading" advances the 5–58% band;
    /// the "Installing files" step drives the 60–95% band from BYTES written (via
    /// <see cref="ReportInstallFilesFraction"/> — "Extracting" lines can't be counted because nested archives
    /// make their number unpredictable); "Building &lt;name&gt;" gives per-BSA bumps across 96–99; "Next Step:"
    /// sets fixed floor bumps per phase.
    /// </remarks>
    private sealed class CliProgressTracker
    {
        /// <summary>Matches the CLI's "Next Step: ..." phase markers.</summary>
        private static readonly Regex NextStepRegex =
            new(@"Next Step:\s*(.+?)\s*$", RegexOptions.Compiled);
        /// <summary>Matches the CLI's "Missing N archives" line.</summary>
        private static readonly Regex MissingArchivesRegex =
            new(@"Missing (\d+) archives", RegexOptions.Compiled);
        /// <summary>Matches the CLI's "Optimized X directives to Y required" line.</summary>
        private static readonly Regex OptimizedRegex =
            new(@"Optimized (\d+) directives to (\d+) required", RegexOptions.Compiled);
        /// <summary>Matches the CLI's "Building N bsa files" line.</summary>
        private static readonly Regex BsaCountRegex =
            new(@"Building (\d+) bsa files", RegexOptions.Compiled);

        private double _percent;
        private bool _started;
        private int _downloadTotal = -1;
        private int _downloadDone;
        private int _bsaTotal;
        private int _bsaDone;
        private bool _inBsaStep;

        /// <summary>Creates the tracker; <paramref name="totalArchives"/> is reserved (the download band sizes itself from this run's "Missing N archives" line).</summary>
        public CliProgressTracker(int totalArchives)
        {
            _ = totalArchives;
        }

        /// <summary>Fraction of this run's directives that need installing ("Optimized X to Y required"); scales the Installing-files phase's expected bytes (1.0 fresh, near 0 reinstalling over an identical copy).</summary>
        public double DirectiveRatio { get; private set; } = 1.0;

        /// <summary>True while the CLI is in the "Installing files" step.</summary>
        public bool InstallFilesPhaseActive { get; private set; }

        /// <summary>Maps a bytes-written fraction (0–1) of the Installing-files phase onto the 60–95 band and returns the monotonic percent; safe to race with line updates since Bump only ever raises the value.</summary>
        public double ReportInstallFilesFraction(double fraction)
        {
            Bump(60 + Math.Clamp(fraction, 0, 1) * 35);
            return _percent;
        }

        /// <summary>Consumes one raw CLI line and returns the updated monotonic percent, or null while the CLI is still starting up (game detection etc.).</summary>
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

            var pct = PercentRegex.Match(line);
            if (pct.Success && double.TryParse(pct.Groups[1].Value, out var p))
            {
                _started = true;
                Bump(Math.Clamp(p, 0, 100));
            }

            return _started ? _percent : null;
        }

        /// <summary>Raises the tracked percent toward <paramref name="value"/> (never lowers it, capped at 99).</summary>
        private void Bump(double value)
            => _percent = Math.Max(_percent, Math.Min(value, 99));
    }

    /// <summary>Runs one CLI install: local-file install from <paramref name="wabbajackFile"/> alone, or gallery resolve when <paramref name="machineUrl"/> is also given — in which case <c>-m</c> must be passed with <c>-w</c> (the CLI dereferences the <c>-w</c> path while saving the download and crashes when it is missing).</summary>
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

        await using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

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
            try { await poller.ConfigureAwait(false); } catch { }
        }
        return new CliRunResult(process.ExitCode, failedResolvingList);
    }

    /// <summary>Background companion to a CLI run that measures Installing-files progress by bytes written, because that phase logs no usable per-unit events (nested archives make "Extracting" counts unpredictable).</summary>
    /// <remarks>
    /// Once the tracker enters the "Installing files" phase it baselines the install dir's byte-count, then
    /// rescans every few seconds and maps bytes written since the baseline onto the 60–95 band. Expected bytes
    /// are the catalog's SizeOfInstalledFiles scaled by this run's directive ratio, so reinstalls (small delta)
    /// and pre-existing files (baseline) are handled honestly.
    /// </remarks>
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
                    catch { }
                }
            }
            catch { }
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
                progress.Report("Installing", null, percent, false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>True when a CLI output line indicates a transient failure to load the list from the gallery (so the caller retries) rather than a genuine install error.</summary>
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

    /// <summary>Logs one raw CLI line, updates the progress tracker, and reports the (prefix-stripped, non-TRACE) text to the UI.</summary>
    private static void HandleLine(
        string? line, CliProgressTracker tracker, IProgress<InstallProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Logger.Info($"[wabbajack] {line}");

        var percent = tracker.Update(line);

        var display = line.Contains("[TRACE]", StringComparison.Ordinal)
            ? null
            : CliPrefixRegex.Replace(line.Trim(), "");
        if (display is null && percent is null)
        {
            return;
        }
        progress.Report("Installing", display, percent, percent is null);
    }
}
