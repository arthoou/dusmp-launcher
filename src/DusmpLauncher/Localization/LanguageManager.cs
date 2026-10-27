using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DusmpLauncher.Localization;

/// <summary>Language and country picker data plus the active UI string table.</summary>
public sealed class LanguageManager : ILanguageManager
{
    private static readonly IReadOnlyDictionary<string, string> English = LoadEnglish();

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DusmpLauncher.Localization.Languages.en-US.json")
            ?? throw new InvalidOperationException("The English translation resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("The English translation resource is empty.");
    }

    private string _tag = LanguageCatalog.DefaultTag;
    private IReadOnlyDictionary<string, string> _current = English;
    public string CurrentTag => _tag;
    public bool IsRightToLeft => LanguageCatalog.Find(_tag)?.IsRightToLeft == true;
    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } = LanguageCatalog.All.Where(x => x.HasTranslation).ToArray();
    public IReadOnlyList<CountryInfo> Countries { get; } = BuildCountries();
    public IReadOnlyDictionary<string, string> CurrentTable => _current;

    public Task SetLanguageAsync(string tag, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var nextTag = ResolveTag(tag);
        var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{LanguageCatalog.TranslationResourcePrefix}{nextTag}.json");
        if (resource is null) { nextTag = LanguageCatalog.DefaultTag; _current = English; }
        else
        {
            using (resource)
            {
                var localized = JsonSerializer.Deserialize<Dictionary<string, string>>(resource) ?? new();
                var merged = English.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                foreach (var pair in localized) merged[pair.Key] = pair.Value;
                _current = merged;
            }
        }
        _tag = nextTag;
        return Task.CompletedTask;
    }

    private static string ResolveTag(string? tag)
    {
        var exact = LanguageCatalog.Find(tag);
        return exact?.HasTranslation == true ? exact.Tag
            : LanguageCatalog.FindByLanguagePart(tag, translatedOnly: true)?.Tag ?? LanguageCatalog.DefaultTag;
    }

    public string Translate(string key, IReadOnlyDictionary<string, string>? args = null)
    {
        var result = _current.TryGetValue(key, out var value) ? value : key;
        if (args is not null) foreach (var (name, replacement) in args) result = result.Replace("{" + name + "}", replacement, StringComparison.Ordinal);
        return result;
    }

    public string PickLocalized(IReadOnlyDictionary<string, string> byLanguage)
    {
        var entries = byLanguage.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToArray();
        var part = LanguageCatalog.LanguagePart(_tag);
        return entries.FirstOrDefault(x => x.Key.Equals(_tag, StringComparison.OrdinalIgnoreCase)).Value
            ?? entries.FirstOrDefault(x => x.Key.Equals(part, StringComparison.OrdinalIgnoreCase)).Value
            ?? entries.FirstOrDefault(x => LanguageCatalog.LanguagePart(x.Key) == part).Value
            ?? entries.FirstOrDefault(x => x.Key.Equals("en", StringComparison.OrdinalIgnoreCase)).Value
            ?? entries.FirstOrDefault(x => x.Key.Equals("en-US", StringComparison.OrdinalIgnoreCase)).Value
            ?? entries.FirstOrDefault().Value ?? "";
    }

    public string GuessSystemLanguage()
    {
        return ResolveTag(CultureInfo.CurrentUICulture.Name);
    }

    private static IReadOnlyList<CountryInfo> BuildCountries()
    {
        var rows = new List<CountryInfo>();
        var resources = typeof(LanguageManager).Assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        foreach (var region in CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => { try { return new RegionInfo(c.Name); } catch { return null; } }).Where(r => r is not null).DistinctBy(r => r!.TwoLetterISORegionName))
        {
            var r = region!;
            var code = r.TwoLetterISORegionName;
            // Numeric macroregions (such as 001) are not countries and have no flag.
            if (code.Length != 2 || code.Any(c => c is < 'A' or > 'Z')) continue;
            var flagName = code.ToLowerInvariant();
            var flag = resources.Contains($"DusmpLauncher.UI.assets.flags.{flagName}.svg")
                ? $"app://launcher/assets/flags/{flagName}.svg" : "app://launcher/assets/globe.svg";
            rows.Add(new(code, r.EnglishName, r.NativeName, flag, "world"));
        }
        return rows.OrderBy(c => c.EnglishName, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
