using System.Text.Json.Serialization;

namespace DusmpLauncher.Models;

public enum AccountType
{
    None = 0,
    Offline = 1,
    Microsoft = 2,
}

/// <summary>
/// What the UI is allowed to know about the signed-in player. Deliberately has
/// no token field: tokens never cross the bridge.
/// </summary>
public sealed record PlayerProfile
{
    [JsonPropertyName("type")] public AccountType Type { get; init; }
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("uuid")] public string? Uuid { get; init; }

    /// <summary>Avatar URL, or an <c>app://</c> URL for the offline placeholder head.</summary>
    [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; init; }

    /// <summary>Full skin render URL when available.</summary>
    [JsonPropertyName("skinUrl")] public string? SkinUrl { get; init; }

    [JsonPropertyName("isAuthenticated")] public bool IsAuthenticated => Type == AccountType.Microsoft;

    public static PlayerProfile Empty { get; } = new();
}
