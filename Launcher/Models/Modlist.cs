using System.Text.Json.Serialization;

namespace MorrowindRemasteredLauncher.Models;

/// <summary>
/// Mirrors a single entry in modlists.json (fetched live from GitHub).
/// Only the fields the launcher needs are mapped.
/// </summary>
public sealed class Modlist
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("links")]
    public ModlistLinks Links { get; set; } = new();

    [JsonPropertyName("download_metadata")]
    public DownloadMetadata? DownloadMetadata { get; set; }

    [JsonPropertyName("dateUpdated")]
    public DateTimeOffset? DateUpdated { get; set; }
}

public sealed class ModlistLinks
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    [JsonPropertyName("readme")]
    public string Readme { get; set; } = "";

    /// <summary>Direct .wabbajack download URL (authored-files.wabbajack.org).</summary>
    [JsonPropertyName("download")]
    public string Download { get; set; } = "";

    [JsonPropertyName("machineURL")]
    public string MachineUrl { get; set; } = "";

    [JsonPropertyName("discordURL")]
    public string DiscordUrl { get; set; } = "";
}

/// <summary>
/// Size/hash metadata. modlists.json embeds this, but the authoritative copy is
/// the .wabbajack.meta.json fetched live. Same shape for both.
/// </summary>
public sealed class DownloadMetadata
{
    [JsonPropertyName("Hash")]
    public string Hash { get; set; } = "";

    /// <summary>Size of the .wabbajack file itself.</summary>
    [JsonPropertyName("Size")]
    public long Size { get; set; }

    [JsonPropertyName("NumberOfArchives")]
    public int NumberOfArchives { get; set; }

    /// <summary>Total bytes that must be downloaded from Nexus etc.</summary>
    [JsonPropertyName("SizeOfArchives")]
    public long SizeOfArchives { get; set; }

    [JsonPropertyName("NumberOfInstalledFiles")]
    public int NumberOfInstalledFiles { get; set; }

    /// <summary>Total bytes on disk once installed.</summary>
    [JsonPropertyName("SizeOfInstalledFiles")]
    public long SizeOfInstalledFiles { get; set; }

    /// <summary>Peak disk usage (archives + installed) during install.</summary>
    [JsonPropertyName("TotalSize")]
    public long TotalSize { get; set; }
}
