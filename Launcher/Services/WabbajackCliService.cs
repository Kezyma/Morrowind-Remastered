using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Acquires and locates the Wabbajack CLI used to perform headless installs.
///
/// Wabbajack ships the CLI <em>inside</em> its release zip (the only versioned
/// asset, e.g. <c>4.2.1.4.zip</c>) under a <c>cli/</c> subfolder containing
/// <c>cli/wabbajack-cli.exe</c> plus hundreds of dependency files. We download
/// that zip once into the portable <c>Wabbajack/</c> folder and extract it whole,
/// preserving the <c>cli/</c> layout so <see cref="AppPaths.WabbajackCliExe"/>
/// resolves. Subsequent installs reuse the cached copy.
/// </summary>
public sealed class WabbajackCliService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/wabbajack-tools/wabbajack/releases/latest";

    private readonly HttpClient _http;

    public WabbajackCliService(HttpClient http) => _http = http;

    /// <summary>True when the CLI executable is already present locally.</summary>
    public bool IsInstalled => File.Exists(AppPaths.WabbajackCliExe);

    /// <summary>The resolved path to wabbajack-cli.exe (may not yet exist).</summary>
    public string CliPath => AppPaths.WabbajackCliExe;

    /// <summary>
    /// Ensures the CLI is available, downloading + extracting the latest release
    /// if necessary. Reports coarse progress via <paramref name="progress"/>.
    /// </summary>
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

    private async Task<string?> ResolveCliAssetUrlAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var release = await response.Content
            .ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
            .ConfigureAwait(false);

        var assets = release?.Assets ?? new List<GitHubAsset>();

        // Wabbajack's release has exactly two assets: the installer
        // (Wabbajack.exe) and the full package zip (e.g. 4.2.1.4.zip) which
        // contains cli/wabbajack-cli.exe. We want the zip.
        var zip = assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        return zip?.DownloadUrl;
    }

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

    private static void ExtractCli(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        // The zip ships the CLI as cli/wabbajack-cli.exe alongside its many
        // dependency files. Extract every entry preserving the archive layout so
        // the cli/ folder (and its DLLs) lands intact under Wabbajack/.
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // directory marker
            }

            var destPath = Path.GetFullPath(
                Path.Combine(AppPaths.WabbajackDir, entry.FullName));

            // Guard against zip-slip: ensure the resolved path stays under the
            // Wabbajack folder.
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Logger.Warn($"Could not delete {path}: {ex.Message}"); }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";
    }
}
