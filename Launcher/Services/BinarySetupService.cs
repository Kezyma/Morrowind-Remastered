using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Native C# reimplementation of the in-list install scripts (the
/// <c>Install *.bat</c> files), which each download a binary and place it:
///   - OpenMW (OpenMW ed.): NSIS installer run silently into <c>mods/OpenMW/OpenMW</c>.
///   - Delta Plugin (OpenMW ed.): zip extracted into <c>mods/Delta Plugin</c>.
///   - MWSE (MWSE ed.): nightly zip — <c>Data Files\*</c> to the mod root,
///     loose files to <c>Root\</c>.
/// Download URLs come from <see cref="DownloadUrls"/> in config (updatable).
/// Each step skips when its output is already present, so it is safe to re-run.
/// </summary>
public sealed class BinarySetupService
{
    private readonly HttpClient _http;
    private readonly ConfigService _config;
    private readonly InstallStateService _installState;

    public BinarySetupService(
        HttpClient http, ConfigService config, InstallStateService installState)
    {
        _http = http;
        _config = config;
        _installState = installState;
    }

    // ----------------------------------------------------------------- OpenMW

    public async Task<bool> InstallOpenMwAsync(
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var modDir = Path.Combine(
            _installState.GetEditionInstallDir(Edition.OpenMW), _config.Current.Mo2Paths.OpenMwModDir);
        var targetDir = Path.Combine(modDir, "OpenMW");
        var marker = Path.Combine(targetDir, "openmw.exe");
        if (File.Exists(marker))
        {
            Report(progress, "OpenMW already installed.", null, false);
            return true;
        }

        Directory.CreateDirectory(modDir);
        var installer = Path.Combine(modDir, "openmw.exe");
        await DownloadAsync(_config.Current.Downloads.OpenMwInstaller, installer,
            "Downloading OpenMW", progress, ct).ConfigureAwait(false);

        // NSIS: /S silent, /D=<dir> must be last and unquoted (raw args).
        Report(progress, "Installing OpenMW…", null, true);
        var psi = new ProcessStartInfo
        {
            FileName = installer,
            Arguments = $"/S /D={targetDir}",
            WorkingDirectory = modDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using (var proc = Process.Start(psi)!)
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                throw new IOException($"OpenMW installer exited with {proc.ExitCode}.");
            }
        }

        TryDelete(installer);
        if (!File.Exists(marker))
        {
            throw new IOException("OpenMW installer finished but openmw.exe is missing.");
        }
        Logger.Info("OpenMW installed.");
        return true;
    }

    // ------------------------------------------------------------ Delta Plugin

    public async Task<bool> InstallDeltaAsync(
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var modDir = Path.Combine(
            _installState.GetEditionInstallDir(Edition.OpenMW), _config.Current.Mo2Paths.DeltaModDir);
        var marker = Path.Combine(modDir, "delta_plugin.exe");
        if (File.Exists(marker))
        {
            Report(progress, "Delta Plugin already installed.", null, false);
            return true;
        }

        Directory.CreateDirectory(modDir);
        await DownloadAndExtractAsync(
            _config.Current.Downloads.DeltaPlugin, "Delta Plugin",
            extracted => CopyDirectory(extracted, modDir), progress, ct).ConfigureAwait(false);

        if (!File.Exists(marker))
        {
            throw new IOException("Delta Plugin extracted but delta_plugin.exe is missing.");
        }
        Logger.Info("Delta Plugin installed.");
        return true;
    }

    // -------------------------------------------------------------------- MWSE

    public async Task<bool> InstallMwseAsync(
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var modDir = Path.Combine(
            _installState.GetEditionInstallDir(Edition.Mwse), _config.Current.Mo2Paths.MwseModDir);
        var marker = Path.Combine(modDir, "MWSE"); // Data Files/MWSE → mod root
        if (Directory.Exists(marker))
        {
            Report(progress, "MWSE already installed.", null, false);
            return true;
        }

        Directory.CreateDirectory(modDir);
        await DownloadAndExtractAsync(
            _config.Current.Downloads.MwseNightly, "MWSE",
            extracted =>
            {
                // Data Files\* → mod root; everything else → Root\.
                var dataFiles = Path.Combine(extracted, "Data Files");
                if (Directory.Exists(dataFiles))
                {
                    CopyDirectory(dataFiles, modDir);
                }
                var rootDir = Path.Combine(modDir, "Root");
                Directory.CreateDirectory(rootDir);
                foreach (var entry in Directory.GetFileSystemEntries(extracted))
                {
                    if (string.Equals(Path.GetFileName(entry), "Data Files",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var dest = Path.Combine(rootDir, Path.GetFileName(entry));
                    if (Directory.Exists(entry))
                    {
                        CopyDirectory(entry, dest);
                    }
                    else
                    {
                        File.Copy(entry, dest, overwrite: true);
                    }
                }
            }, progress, ct).ConfigureAwait(false);

        if (!Directory.Exists(marker))
        {
            throw new IOException("MWSE extracted but the MWSE Data Files are missing.");
        }
        Logger.Info("MWSE installed.");
        return true;
    }

    // --------------------------------------------------------------- helpers

    private async Task DownloadAndExtractAsync(
        string url, string label, Action<string> place,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var tmpZip = Path.Combine(Path.GetTempPath(),
            $"mr_{label.Replace(' ', '_')}_{Guid.NewGuid():N}.zip");
        var tmpDir = tmpZip + "_x";
        try
        {
            await DownloadAsync(url, tmpZip, $"Downloading {label}", progress, ct)
                .ConfigureAwait(false);

            Report(progress, $"Extracting {label}…", null, true);
            Directory.CreateDirectory(tmpDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(tmpZip, tmpDir, overwriteFiles: true), ct)
                .ConfigureAwait(false);

            Report(progress, $"Installing {label}…", null, true);
            await Task.Run(() => place(tmpDir), ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmpZip);
            TryDeleteDir(tmpDir);
        }
    }

    private async Task DownloadAsync(
        string url, string dest, string stage,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Report(progress, $"{stage}…", null, true);
        Logger.Info($"{stage}: {url}");

        using var response = await _http
            .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (total > 0)
            {
                var pct = Math.Clamp(written * 100.0 / total, 0, 100);
                Report(progress, $"{stage}… {pct:0}%", pct, false);
            }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* ignore */ }
    }

    private static void Report(
        IProgress<InstallProgress>? progress, string line, double? percent, bool indeterminate)
        => progress?.Report(new InstallProgress("Setup", line, percent, indeterminate));
}
