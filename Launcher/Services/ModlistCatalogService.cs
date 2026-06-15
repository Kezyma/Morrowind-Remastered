using System.Net.Http;
using System.Text.Json;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Fetches the live modlist catalog and per-edition metadata from GitHub, never trusting the meta files baked into the repo since download links/versions can change.</summary>
public sealed class ModlistCatalogService
{
    /// <summary>Case-insensitive JSON options for catalog deserialization.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Shared HTTP client used to fetch the catalog.</summary>
    private readonly HttpClient _http;
    /// <summary>Persisted launcher config (catalog URL).</summary>
    private readonly ConfigService _config;

    /// <summary>Cached catalog after the first successful fetch.</summary>
    private List<Modlist>? _cache;

    /// <summary>Creates the service over the shared HTTP client and config.</summary>
    public ModlistCatalogService(HttpClient http, ConfigService config)
    {
        _http = http;
        _config = config;
    }

    /// <summary>Fetches and caches the full catalog, throwing on network/parse failure so callers can show a clear "couldn't reach catalog" message.</summary>
    public async Task<IReadOnlyList<Modlist>> GetCatalogAsync(
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_cache is not null && !forceRefresh)
        {
            return _cache;
        }

        var json = await _http.GetStringAsync(_config.Current.Wabbajack.CatalogUrl, ct).ConfigureAwait(false);
        var lists = JsonSerializer.Deserialize<List<Modlist>>(json, JsonOptions)
                    ?? new List<Modlist>();
        _cache = lists;
        return lists;
    }

    /// <summary>Returns the catalog entry for the given edition, or null if absent.</summary>
    public async Task<Modlist?> GetModlistAsync(Edition edition, CancellationToken ct = default)
    {
        var catalog = await GetCatalogAsync(ct: ct).ConfigureAwait(false);
        var machineUrl = edition.MachineUrl();
        return catalog.FirstOrDefault(m =>
            string.Equals(m.Links.MachineUrl, machineUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the catalog entry whose machineURL matches the given value (also matching the trailing slug of repository-qualified values), to pin "latest version" to a configured machineURL.</summary>
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

    /// <summary>Returns the live download metadata for the edition, preferring the catalog's embedded metadata which is kept in sync with the published list.</summary>
    public async Task<DownloadMetadata?> GetLiveMetadataAsync(
        Modlist modlist, CancellationToken ct = default)
    {
        var download = modlist.Links.Download;
        if (string.IsNullOrWhiteSpace(download))
        {
            return modlist.DownloadMetadata;
        }

        return modlist.DownloadMetadata;
    }
}
