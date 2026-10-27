using System.Text.Json.Serialization;

namespace DusmpLauncher.Models;

/// <summary>One entry on the "Optional mods" screen, from <c>GET /mods/optional</c>.</summary>
public sealed record OptionalMod
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>Display name. Not translated — mod names stay as their authors wrote them.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>
    /// Short description keyed by language tag, e.g. <c>{"en":"...","pt-BR":"..."}</c>.
    /// Falls back to <c>en</c>, then to the first entry present.
    /// </summary>
    [JsonPropertyName("description")] public IReadOnlyDictionary<string, string> Description { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Category id the UI groups by; resolved through the translation table.</summary>
    [JsonPropertyName("category")] public string Category { get; init; } = "misc";

    /// <summary>Expected impact id: <c>performance</c>, <c>visual</c>, <c>gameplay</c>, <c>heavy</c>…</summary>
    [JsonPropertyName("impact")] public string Impact { get; init; } = "none";

    /// <summary>Icon URL, or an <c>app://</c> URL for a built-in placeholder.</summary>
    [JsonPropertyName("icon")] public string? Icon { get; init; }

    /// <summary>Total bytes this mod adds to the download, for the UI to show.</summary>
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

    /// <summary>Default when the user has never made a choice. The API sets these to true.</summary>
    [JsonPropertyName("defaultEnabled")] public bool DefaultEnabled { get; init; } = true;

    /// <summary>Manifest version that introduced this mod, used to mark it "new".</summary>
    [JsonPropertyName("addedInVersion")] public string? AddedInVersion { get; init; }
}

/// <summary>An optional mod plus the state the UI needs to render it.</summary>
public sealed record OptionalModView(OptionalMod Mod, bool Enabled, bool IsNew);
