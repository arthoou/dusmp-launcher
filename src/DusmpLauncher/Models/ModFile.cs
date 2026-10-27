using System.Text.Json.Serialization;

namespace DusmpLauncher.Models;

/// <summary>
/// A single launcher-managed file. <see cref="Path"/> is relative to the
/// instance root and always uses forward slashes, whatever the host OS.
/// </summary>
public sealed class ModFile
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }

    /// <summary>Lowercase hex SHA-256. The only thing we trust for integrity.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";

    /// <summary>Primary download URL. May be absolute, or relative to the CDN base.</summary>
    [JsonPropertyName("url")] public string Url { get; init; } = "";

    /// <summary>Extra URLs to try, in order, when <see cref="Url"/> fails.</summary>
    [JsonPropertyName("mirrors")] public IReadOnlyList<string> Mirrors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Lowercase hex SHA-1, when the API knows it. Not used for integrity —
    /// <see cref="Sha256"/> stays the only thing trusted for that — but it is
    /// the key Modrinth indexes files by, so it is what lets the launcher pull a
    /// public mod straight from itsown CDN instead of through DUSMP's bandwidth.
    /// </summary>
    [JsonPropertyName("sha1")] public string? Sha1 { get; init; }

    /// <summary>
    /// CurseForge murmur2 fingerprint, when known. Optional: CurseForge lookups
    /// need an API key, so this is only consulted when one is configured.
    /// </summary>
    [JsonPropertyName("fingerprint")] public long? Fingerprint { get; init; }

    /// <summary>
    /// Id of the optional mod this file belongs to, or null when the file is
    /// mandatory. Files whose optional mod is disabled are never downloaded.
    /// </summary>
    [JsonPropertyName("optionalId")] public string? OptionalId { get; init; }

    /// <summary>
    /// When true the launcher writes the file once and then leaves it alone, so
    /// player edits survive updates (configs, keybinds, options).
    /// </summary>
    [JsonPropertyName("writeOnce")] public bool WriteOnce { get; init; }

    /// <summary>True when this file is required for the game to start at all.</summary>
    [JsonIgnore] public bool IsOptional => OptionalId is not null;
}
