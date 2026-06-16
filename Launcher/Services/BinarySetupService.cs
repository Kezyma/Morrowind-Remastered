using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Native C# reimplementation of the in-list <c>Install *.bat</c> scripts that each download a binary and place it in the right MO2 mod folder.</summary>
/// <remarks>
/// OpenMW runs its NSIS installer silently into <c>mods/OpenMW/OpenMW</c>; Delta
/// Plugin extracts into its mod; MWSE's nightly zip puts <c>Data Files\*</c> at
/// the mod root and loose files under <c>Root\</c>. Download URLs come from
/// config, and each step skips when its output is already present so it is safe
/// to re-run.
/// </remarks>
public sealed class BinarySetupService
{
    /// <summary>Shared HTTP client used to download the binaries.</summary>
    private readonly HttpClient _http;
    /// <summary>Persisted launcher config (download URLs, MO2 mod folder names).</summary>
    private readonly ConfigService _config;
    /// <summary>Resolves the install directory for an edition.</summary>
    private readonly InstallStateService _installState;

    /// <summary>Creates the service over the HTTP client, config and install-state service.</summary>
    public BinarySetupService(
        HttpClient http, ConfigService config, InstallStateService installState)
    {
        _http = http;
        _config = config;
        _installState = installState;
    }

    /// <summary>Downloads and silently installs OpenMW (NSIS <c>/S</c>, with the unquoted <c>/D=</c> target last) into its MO2 mod; skips if already present.</summary>
    public async Task<bool> InstallOpenMwAsync(
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var modDir = Path.Combine(
            _installState.GetEditionInstallDir(Edition.OpenMW), _config.Current.Mo2Paths.OpenMwModDir);
        var targetDir = Path.Combine(modDir, "OpenMW");
        var marker = Path.Combine(targetDir, "openmw.exe");
        if (File.Exists(marker))
        {
            progress.Report("Setup", "OpenMW already installed.", null, false);
            return true;
        }

        Directory.CreateDirectory(modDir);
        var installer = Path.Combine(modDir, "openmw.exe");
        await DownloadAsync(_config.Current.Downloads.OpenMwInstaller, installer,
            "Downloading OpenMW", progress, ct).ConfigureAwait(false);

        progress.Report("Setup", "Installing OpenMW…", null, true);
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

    /// <summary>Downloads and extracts Delta Plugin into its MO2 mod; skips if already present.</summary>
    public async Task<bool> InstallDeltaAsync(
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var modDir = Path.Combine(
            _installState.GetEditionInstallDir(Edition.OpenMW), _config.Current.Mo2Paths.DeltaModDir);
        var marker = Path.Combine(modDir, "delta_plugin.exe");
        if (File.Exists(marker))
        {
            progress.Report("Setup", "Delta Plugin already installed.", null, false);
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

    /// <summary>Downloads and extracts the MWSE nightly into its MO2 mod (Data Files to the mod root, loose files under <c>Root\</c>); skips if already present.</summary>
    public async Task<bool> InstallMwseAsync(
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var modDir = Path.Combine(
            _installState.GetEditionInstallDir(Edition.Mwse), _config.Current.Mo2Paths.MwseModDir);
        var marker = Path.Combine(modDir, "MWSE");
        if (Directory.Exists(marker))
        {
            progress.Report("Setup", "MWSE already installed.", null, false);
            return true;
        }

        Directory.CreateDirectory(modDir);
        await DownloadAndExtractAsync(
            _config.Current.Downloads.MwseNightly, "MWSE",
            extracted =>
            {
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

    /// <summary>Downloads a zip to a temp file, extracts it, runs <paramref name="place"/> to install it, then cleans up the temp files.</summary>
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

            progress.Report("Setup", $"Extracting {label}…", null, true);
            Directory.CreateDirectory(tmpDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(tmpZip, tmpDir, overwriteFiles: true), ct)
                .ConfigureAwait(false);

            progress.Report("Setup", $"Installing {label}…", null, true);
            await Task.Run(() => place(tmpDir), ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmpZip);
            TryDeleteDir(tmpDir);
        }
    }

    /// <summary>Streams a URL to a file, reporting download-percentage progress when the content length is known.</summary>
    private async Task DownloadAsync(
        string url, string dest, string stage,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress.Report("Setup", $"{stage}…", null, true);
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
                progress.Report("Setup", $"{stage}… {pct:0}%", pct, false);
            }
        }
    }

    /// <summary>Recursively copies a directory tree, overwriting existing files.</summary>
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

    /// <summary>Deletes a file if present, swallowing any error.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Recursively deletes a directory if present, swallowing any error.</summary>
    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
