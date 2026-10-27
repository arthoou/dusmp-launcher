namespace DusmpLauncher.Localization;

/// <summary>One selectable UI language.</summary>
/// <param name="Tag">BCP-47 tag, e.g. <c>pt-BR</c>.</param>
/// <param name="EnglishName">Name in English, for players searching in English.</param>
/// <param name="NativeName">Name written in the language itself.</param>
/// <param name="Flag">Embedded SVG flag URL, or null for languages with no country flag.</param>
/// <param name="Group">Grouping key for the list, e.g. <c>popular</c> or a script family.</param>
/// <param name="HasTranslation">True when the launcher ships strings for this tag.</param>
/// <param name="IsRightToLeft">True for RTL scripts, so the UI can flip direction.</param>
public sealed record LanguageInfo(
    string Tag,
    string EnglishName,
    string NativeName,
    string? Flag,
    string Group,
    bool HasTranslation,
    bool IsRightToLeft = false);

/// <summary>One selectable country.</summary>
public sealed record CountryInfo(
    string Code,
    string EnglishName,
    string NativeName,
    string Flag,
    string Continent);

/// <summary>
/// Resolves UI strings. Every visible string in the launcher comes from here —
/// no literal player-facing text lives in C# or in the HTML.
/// </summary>
public interface ILanguageManager
{
    /// <summary>Currently active tag.</summary>
    string CurrentTag { get; }

    bool IsRightToLeft { get; }

    /// <summary>Selectable languages with a translation packaged in the launcher.</summary>
    IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    IReadOnlyList<CountryInfo> Countries { get; }

    /// <summary>
    /// Switches language and reloads the string table. Falls back to the closest
    /// tag that has strings (e.g. <c>pt-PT</c> to <c>pt-BR</c>, then to English).
    /// </summary>
    Task SetLanguageAsync(string tag, CancellationToken ct = default);

    /// <summary>
    /// Looks up a key, substituting <c>{name}</c> placeholders. Returns the key
    /// itself when missing, so a gap is visible instead of silently blank.
    /// </summary>
    string Translate(string key, IReadOnlyDictionary<string, string>? args = null);

    /// <summary>The whole active table, handed to the UI in one go for rendering.</summary>
    IReadOnlyDictionary<string, string> CurrentTable { get; }

    /// <summary>Best value from an API-supplied per-language map, using the active tag.</summary>
    string PickLocalized(IReadOnlyDictionary<string, string> byLanguage);

    /// <summary>The tag to preselect on first run, guessed from the OS locale.</summary>
    string GuessSystemLanguage();
}
