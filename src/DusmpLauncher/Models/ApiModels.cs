using System.Text.Json.Serialization;

namespace DusmpLauncher.Models;

/// <summary><c>GET /launcher/version</c></summary>
public sealed class LauncherVersionInfo
{
    [JsonPropertyName("version")] public string Version { get; init; } = "0.0.0";
    [JsonPropertyName("minimumVersion")] public string? MinimumVersion { get; init; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; init; }
    [JsonPropertyName("notesUrl")] public string? NotesUrl { get; init; }

    /// <summary>True when the launcher must refuse to run until updated.</summary>
    [JsonPropertyName("mandatory")] public bool Mandatory { get; init; }

    /// <summary>
    /// Azure application id used for Microsoft sign-in. Served by the API so a
    /// new registration reaches every player without shipping a new build; an
    /// environment override still wins for local testing.
    /// </summary>
    [JsonPropertyName("microsoftClientId")] public string? MicrosoftClientId { get; init; }
}

/// <summary><c>GET /modpack/latest</c></summary>
public sealed class ModpackLatestInfo
{
    [JsonPropertyName("version")] public string Version { get; init; } = "0.0.0";
    [JsonPropertyName("manifestUrl")] public string? ManifestUrl { get; init; }
    [JsonPropertyName("releasedAt")] public DateTimeOffset? ReleasedAt { get; init; }
    [JsonPropertyName("changelogUrl")] public string? ChangelogUrl { get; init; }
}

/// <summary>
/// <c>GET /modpack/editions</c> — the packs the player can choose between.
/// Each one carries its own Minecraft and loader version, so the launcher pins
/// nothing itself: whatever the panel published is what gets installed.
/// </summary>
public sealed class ModpackEdition
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>Keyed by language tag, like the other API prose.</summary>
    [JsonPropertyName("description")] public IReadOnlyDictionary<string, string> Description { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Newest published version of this edition, or null when it has none yet.</summary>
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("releasedAt")] public DateTimeOffset? ReleasedAt { get; init; }
    [JsonPropertyName("minecraft")] public string? Minecraft { get; init; }
    [JsonPropertyName("loader")] public string? Loader { get; init; }
    [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; init; }
    [JsonPropertyName("files")] public int Files { get; init; }
}

/// <summary><c>GET /status</c> — drives the maintenance banner.</summary>
public sealed class ServiceStatus
{
    [JsonPropertyName("maintenance")] public bool Maintenance { get; init; }

    /// <summary>Message keyed by language tag; falls back to <c>en</c>.</summary>
    [JsonPropertyName("message")] public IReadOnlyDictionary<string, string> Message { get; init; }
        = new Dictionary<string, string>();

    /// <summary>When true the launcher blocks Play entirely.</summary>
    [JsonPropertyName("blockPlay")] public bool BlockPlay { get; init; }
}

/// <summary>One news item for the main screen.</summary>
public sealed class NewsItem
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public IReadOnlyDictionary<string, string> Title { get; init; }
        = new Dictionary<string, string>();
    [JsonPropertyName("body")] public IReadOnlyDictionary<string, string> Body { get; init; }
        = new Dictionary<string, string>();
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>
    /// True when the text shown was machine-translated because the DUSMP team
    /// never wrote this item in the player's language. Set by the launcher, not
    /// by the API, and surfaced so machine output is never passed off as the
    /// team's own words.
    /// </summary>
    [JsonPropertyName("autoTranslated")] public bool AutoTranslated { get; init; }
}

/// <summary>Body of <c>POST /client/region</c>. Deliberately minimal — see README.</summary>
public sealed class RegionHintRequest
{
    /// <summary>ISO 3166-1 alpha-2, as chosen by the player.</summary>
    [JsonPropertyName("country")] public string Country { get; init; } = "";

    /// <summary>BCP-47 tag of the UI language, so the API can pick a mirror with local docs.</summary>
    [JsonPropertyName("language")] public string Language { get; init; } = "";

    /// <summary>Measured latencies by region id. Empty when probing was skipped.</summary>
    [JsonPropertyName("latencies")] public IReadOnlyDictionary<string, int> Latencies { get; init; }
        = new Dictionary<string, int>();
}

/// <summary>Response of <c>POST /client/region</c>: what the API wants us to use.</summary>
public sealed class RegionHintResponse
{
    [JsonPropertyName("regionId")] public string? RegionId { get; init; }
    [JsonPropertyName("cdnBases")] public IReadOnlyList<string> CdnBases { get; init; } = Array.Empty<string>();
}
