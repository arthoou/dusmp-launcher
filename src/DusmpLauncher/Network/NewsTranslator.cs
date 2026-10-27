using System.Collections.Concurrent;
using System.Text.Json;
using DusmpLauncher.Models;

namespace DusmpLauncher.Network;

/// <summary>
/// Translates news items the DUSMP team only wrote in one language.
///
/// News is authored per language, so a player whose language was never filled in
/// falls back to English and reads something they may not understand. This fills
/// that gap automatically, and the UI marks the result as machine-translated so
/// nobody mistakes it for the team's own words.
///
/// Best-effort by design: the translator is a free public endpoint with no key
/// and no uptime promise, so every failure leaves the original text in place
/// rather than blanking a news item or delaying the main screen.
/// </summary>
public sealed class NewsTranslator
{
    /// <summary>
    /// Chrome's dictionary-extension endpoint. Chosen over the plain
    /// <c>translate_a/single</c> one because that path answers bot-check HTML to
    /// datacentre addresses, while this returns a clean JSON array of strings.
    /// </summary>
    private const string Endpoint = "https://translate.googleapis.com/translate_a/t?client=dict-chrome-ex";

    /// <summary>Marks a value as produced here rather than written by a human.</summary>
    public const string MachineSuffixKey = "main.news.autoTranslated";

    private readonly HttpClient _http;

    /// <summary>
    /// Translations survive for the session, keyed by text and target language.
    /// News is re-fetched on every start and is short, so a plain dictionary is
    /// enough and keeps the endpoint from being hit for text already seen.
    /// </summary>
    private readonly ConcurrentDictionary<(string Text, string Target), string> _cache = new();

    public NewsTranslator(HttpClient http) => _http = http;

    /// <summary>
    /// Returns the news translated into <paramref name="languageTag"/>, leaving
    /// untouched any item that already has text for that language. Items that
    /// could not be translated come back exactly as they arrived.
    /// </summary>
    public async Task<IReadOnlyList<NewsItem>> TranslateAsync(
        IReadOnlyList<NewsItem> news,
        string languageTag,
        CancellationToken ct = default)
    {
        if (news.Count == 0 || string.IsNullOrWhiteSpace(languageTag))
            return news;

        var target = ShortTag(languageTag);
        if (target is "en")
            return news;

        // A slow translator must never hold up the main screen.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(12));

        var translated = new List<NewsItem>(news.Count);
        foreach (var item in news)
        {
            try
            {
                translated.Add(await TranslateItemAsync(item, languageTag, target, budget.Token).ConfigureAwait(false));
            }
            catch (Exception)
            {
                // Includes the budget running out: keep the original and move on.
                translated.Add(item);
            }
        }

        return translated;
    }

    private async Task<NewsItem> TranslateItemAsync(NewsItem item, string languageTag, string target, CancellationToken ct)
    {
        // Already written in this language: nothing to do.
        if (HasText(item.Title, languageTag) && HasText(item.Body, languageTag))
            return item;

        var sourceTitle = Pick(item.Title);
        var sourceBody = Pick(item.Body);
        if (sourceTitle is null && sourceBody is null)
            return item;

        var title = sourceTitle is null ? null : await TranslateTextAsync(sourceTitle, target, ct).ConfigureAwait(false);
        var body = sourceBody is null ? null : await TranslateTextAsync(sourceBody, target, ct).ConfigureAwait(false);
        if (title is null && body is null)
            return item;

        return new NewsItem
        {
            Id = item.Id,
            Url = item.Url,
            PublishedAt = item.PublishedAt,
            Title = With(item.Title, languageTag, title),
            Body = With(item.Body, languageTag, body),
            // Lets the UI say so instead of passing machine output off as written copy.
            AutoTranslated = true,
        };
    }

    private async Task<string?> TranslateTextAsync(string text, string target, CancellationToken ct)
    {
        var key = (text, target);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var url = $"{Endpoint}&sl=auto&tl={Uri.EscapeDataString(target)}&q={Uri.EscapeDataString(text)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // The endpoint answers bot-check HTML without a browser-shaped agent.
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64)");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        string? result = null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0)
            {
                var first = document.RootElement[0];
                if (first.ValueKind == JsonValueKind.String)
                    result = first.GetString();
            }
        }
        catch (JsonException)
        {
            // A bot-check page is HTML, not JSON: treat as "no translation".
            return null;
        }

        if (string.IsNullOrWhiteSpace(result))
            return null;

        _cache[key] = result;
        return result;
    }

    /// <summary>Copies the map with one language added, without disturbing the rest.</summary>
    private static IReadOnlyDictionary<string, string> With(
        IReadOnlyDictionary<string, string> source, string languageTag, string? value)
    {
        if (value is null) return source;
        var copy = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
        {
            [languageTag] = value,
        };
        return copy;
    }

    private static bool HasText(IReadOnlyDictionary<string, string> map, string languageTag) =>
        map.TryGetValue(languageTag, out var exact) && !string.IsNullOrWhiteSpace(exact);

    /// <summary>The text to translate from: English when present, else anything.</summary>
    private static string? Pick(IReadOnlyDictionary<string, string> map)
    {
        foreach (var candidate in new[] { "en", "en-US", "pt-BR" })
        {
            if (map.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return map.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    /// <summary>Google wants the bare language subtag, not the full BCP-47 tag.</summary>
    private static string ShortTag(string languageTag)
    {
        var bare = languageTag.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? languageTag;
        // Chinese is the exception: the script matters, so the region is kept.
        return bare.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? (languageTag.Contains("TW", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : "zh-CN")
            : bare.ToLowerInvariant();
    }
}
