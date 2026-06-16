using System.Text.Json.Serialization;

namespace MorrowindRemasteredLauncher.Models;

/// <summary>One entry in modlists.json (fetched live from GitHub); only the fields the launcher needs are mapped.</summary>
public sealed class Modlist
{
    /// <summary>Display title of the modlist.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Short description shown to the user.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Modlist author name.</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    /// <summary>Published modlist version (compared against the installed version).</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>Related links (image, readme, download, machineURL, Discord).</summary>
    [JsonPropertyName("links")]
    public ModlistLinks Links { get; set; } = new();

    /// <summary>Size/hash metadata for the list, when present.</summary>
    [JsonPropertyName("download_metadata")]
    public DownloadMetadata? DownloadMetadata { get; set; }

    /// <summary>When the list was last updated.</summary>
    [JsonPropertyName("dateUpdated")]
    public DateTimeOffset? DateUpdated { get; set; }
}

/// <summary>Related links for a <see cref="Modlist"/> entry.</summary>
public sealed class ModlistLinks
{
    /// <summary>Banner/preview image URL.</summary>
    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    /// <summary>Readme URL for the list.</summary>
    [JsonPropertyName("readme")]
    public string Readme { get; set; } = "";

    /// <summary>Direct .wabbajack download URL (authored-files.wabbajack.org).</summary>
    [JsonPropertyName("download")]
    public string Download { get; set; } = "";

    /// <summary>Wabbajack gallery machineURL for the list.</summary>
    [JsonPropertyName("machineURL")]
    public string MachineUrl { get; set; } = "";

    /// <summary>Support Discord invite URL.</summary>
    [JsonPropertyName("discordURL")]
    public string DiscordUrl { get; set; } = "";
}

/// <summary>Size/hash metadata; modlists.json embeds it, but the authoritative copy is the live .wabbajack.meta.json (same shape).</summary>
public sealed class DownloadMetadata
{
    /// <summary>Hash of the .wabbajack file.</summary>
    [JsonPropertyName("Hash")]
    public string Hash { get; set; } = "";

    /// <summary>Size of the .wabbajack file itself.</summary>
    [JsonPropertyName("Size")]
    public long Size { get; set; }

    /// <summary>Number of source archives the install pulls in.</summary>
    [JsonPropertyName("NumberOfArchives")]
    public int NumberOfArchives { get; set; }

    /// <summary>Total bytes that must be downloaded from Nexus etc.</summary>
    [JsonPropertyName("SizeOfArchives")]
    public long SizeOfArchives { get; set; }

    /// <summary>Number of files written once installed.</summary>
    [JsonPropertyName("NumberOfInstalledFiles")]
    public int NumberOfInstalledFiles { get; set; }

    /// <summary>Total bytes on disk once installed.</summary>
    [JsonPropertyName("SizeOfInstalledFiles")]
    public long SizeOfInstalledFiles { get; set; }

    /// <summary>Peak disk usage (archives + installed) during install.</summary>
    [JsonPropertyName("TotalSize")]
    public long TotalSize { get; set; }
}
