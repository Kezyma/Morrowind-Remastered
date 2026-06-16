using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Acquires and locates the Wabbajack CLI used to perform headless installs.</summary>
/// <remarks>
/// Wabbajack ships the CLI inside its release zip (the only versioned asset)
/// under a <c>cli/</c> subfolder with the exe plus its dependencies; this
/// downloads that zip once into the portable <c>Wabbajack/</c> folder and
/// extracts it whole, preserving the <c>cli/</c> layout so
/// <see cref="AppPaths.WabbajackCliExe"/> resolves. Subsequent installs reuse the
/// cached copy.
/// </remarks>
public sealed class WabbajackCliService
{
    /// <summary>Shared HTTP client used to fetch the release and download the zip.</summary>
    private readonly HttpClient _http;
    /// <summary>Persisted launcher config (GitHub release API).</summary>
    private readonly ConfigService _config;

    /// <summary>Creates the service over the shared HTTP client and config.</summary>
    public WabbajackCliService(HttpClient http, ConfigService config)
    {
        _http = http;
        _config = config;
    }

    /// <summary>True when the CLI executable is already present locally.</summary>
    public bool IsInstalled => File.Exists(AppPaths.WabbajackCliExe);

    /// <summary>The resolved path to wabbajack-cli.exe (may not yet exist).</summary>
    public string CliPath => AppPaths.WabbajackCliExe;

    /// <summary>Ensures the CLI is available, downloading and extracting the latest release if necessary, reporting coarse progress.</summary>
    public async Task EnsureAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (IsInstalled)
        {
            return;
        }

        Directory.CreateDirectory(AppPaths.WabbajackDir);

        progress?.Report("Locating Wabbajack CLI release…");
        var assetUrl = await ResolveCliAssetUrlAsync(ct).ConfigureAwait(false);
        if (assetUrl is null)
        {
            throw new InvalidOperationException(
                "Couldn't find a Wabbajack CLI download in the latest release.");
        }

        var fileName = Path.GetFileName(new Uri(assetUrl).LocalPath);
        var targetPath = Path.Combine(AppPaths.WabbajackDir, fileName);

        progress?.Report("Downloading Wabbajack CLI…");
        await DownloadToFileAsync(assetUrl, targetPath, ct).ConfigureAwait(false);

        progress?.Report("Extracting Wabbajack CLI…");
        ExtractCli(targetPath);
        TryDelete(targetPath);

        if (!IsInstalled)
        {
            throw new InvalidOperationException(
                "Wabbajack CLI was downloaded but the executable could not be located.");
        }

        Logger.Info($"Wabbajack CLI ready at {AppPaths.WabbajackCliExe}");
    }

    /// <summary>Resolves the download URL of the release zip that contains the CLI, or null if not found.</summary>
    private async Task<string?> ResolveCliAssetUrlAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, _config.Current.Wabbajack.LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var release = await response.Content
            .ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
            .ConfigureAwait(false);

        var assets = release?.Assets ?? new List<GitHubAsset>();

        var zip = assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        return zip?.DownloadUrl;
    }

    /// <summary>Streams a URL to a local file.</summary>
    private async Task DownloadToFileAsync(string url, string targetPath, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(targetPath);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    /// <summary>Extracts every zip entry under the Wabbajack folder, preserving the archive layout so the <c>cli/</c> folder lands intact, and guarding against zip-slip.</summary>
    private static void ExtractCli(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var destPath = Path.GetFullPath(
                Path.Combine(AppPaths.WabbajackDir, entry.FullName));

            var root = Path.GetFullPath(AppPaths.WabbajackDir)
                + Path.DirectorySeparatorChar;
            if (!destPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn($"Skipping suspicious zip entry: {entry.FullName}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    /// <summary>Deletes a file, logging (not throwing) on failure.</summary>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Logger.Warn($"Could not delete {path}: {ex.Message}"); }
    }

    /// <summary>A GitHub release response (subset).</summary>
    private sealed class GitHubRelease
    {
        /// <summary>The release tag (version).</summary>
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        /// <summary>The release's downloadable assets.</summary>
        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    /// <summary>A single GitHub release asset (subset).</summary>
    private sealed class GitHubAsset
    {
        /// <summary>The asset file name.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>The browser download URL.</summary>
        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";
    }
}
