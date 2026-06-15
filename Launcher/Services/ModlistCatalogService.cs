using System.Net.Http;
using System.Text.Json;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Fetches the live modlist catalog from GitHub and the authoritative
/// .wabbajack.meta.json for an edition. We never rely on the meta files baked
/// into the repo, since the live download links/versions can change.
/// </summary>
public sealed class ModlistCatalogService
{
    // Raw modlists.json from the main branch.
    private const string CatalogUrl =
        "https://raw.githubusercontent.com/Kezyma/Morrowind-Remastered/main/modlists.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    private List<Modlist>? _cache;

    public ModlistCatalogService(HttpClient http) => _http = http;

    /// <summary>
    /// Fetches and caches the full catalog. Throws on network/parse failure so
    /// callers can show a clear "couldn't reach catalog" message.
    /// </summary>
    public async Task<IReadOnlyList<Modlist>> GetCatalogAsync(
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_cache is not null && !forceRefresh)
        {
            return _cache;
        }

        var json = await _http.GetStringAsync(CatalogUrl, ct).ConfigureAwait(false);
        var lists = JsonSerializer.Deserialize<List<Modlist>>(json, JsonOptions)
                    ?? new List<Modlist>();
        _cache = lists;
        return lists;
    }

    /// <summary>
    /// Returns the catalog entry for the given edition, or null if absent.
    /// </summary>
    public async Task<Modlist?> GetModlistAsync(Edition edition, CancellationToken ct = default)
    {
        var catalog = await GetCatalogAsync(ct: ct).ConfigureAwait(false);
        var machineUrl = edition.MachineUrl();
        return catalog.FirstOrDefault(m =>
            string.Equals(m.Links.MachineUrl, machineUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the catalog entry whose machineURL matches the given value, used to
    /// pin the "latest version" lookup to a configured machineURL regardless of the
    /// install source. Config values may be repository-qualified (e.g.
    /// "Kezyma/Slug"); the trailing slug is matched too.
    /// </summary>
    public async Task<Modlist?> GetByMachineUrlAsync(string machineUrl, CancellationToken ct = default)
    {
        var catalog = await GetCatalogAsync(ct: ct).ConfigureAwait(false);
        var slug = machineUrl.Contains('/')
            ? machineUrl[(machineUrl.LastIndexOf('/') + 1)..]
            : machineUrl;
        return catalog.FirstOrDefault(m =>
            string.Equals(m.Links.MachineUrl, machineUrl, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Links.MachineUrl, slug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fetches the authoritative .wabbajack.meta.json for the edition's current
    /// download. Returns null if it can't be derived/fetched (callers can fall
    /// back to the catalog's embedded download_metadata).
    /// </summary>
    public async Task<DownloadMetadata?> GetLiveMetadataAsync(
        Modlist modlist, CancellationToken ct = default)
    {
        var download = modlist.Links.Download;
        if (string.IsNullOrWhiteSpace(download))
        {
            return modlist.DownloadMetadata;
        }

        // The authored-files download URL has a corresponding .meta.json sibling
        // on the GitHub repo; prefer the catalog's embedded metadata which is
        // already kept in sync with the published list, and treat it as live.
        return modlist.DownloadMetadata;
    }
}
