using System.Text.Json.Serialization;

namespace DusmpLauncher.Models;

/// <summary>An infrastructure region from <c>GET /regions</c>.</summary>
public sealed class ServerRegion
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>ISO 3166-1 alpha-2 codes this region is the natural home for.</summary>
    [JsonPropertyName("countries")] public IReadOnlyList<string> Countries { get; init; } = Array.Empty<string>();

    /// <summary>CDN base URLs for this region, best first.</summary>
    [JsonPropertyName("cdnBases")] public IReadOnlyList<string> CdnBases { get; init; } = Array.Empty<string>();

    /// <summary>URL to probe for latency. Should be small and cacheable.</summary>
    [JsonPropertyName("pingUrl")] public string? PingUrl { get; init; }

    /// <summary>False when the API has taken the region out of rotation.</summary>
    [JsonPropertyName("available")] public bool Available { get; init; } = true;

    /// <summary>Tie-breaker when country and ping do not decide. Lower wins.</summary>
    [JsonPropertyName("priority")] public int Priority { get; init; }
}

/// <summary>Result of scoring one region: what the selector decided and why.</summary>
public sealed record RegionMeasurement(
    ServerRegion Region,
    int? LatencyMs,
    bool CountryMatch,
    double Score,
    string? FailureReason);

/// <summary>A game server shown on the main screen, from <c>GET /servers</c>.</summary>
public sealed class GameServer
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("port")] public int Port { get; init; } = 25565;
    [JsonPropertyName("regionId")] public string? RegionId { get; init; }
}

/// <summary>Live status of one game server, as the UI shows it.</summary>
public sealed record GameServerStatus(
    string Id,
    string Name,
    bool Online,
    int? PlayersOnline,
    int? PlayersMax,
    int? LatencyMs);
