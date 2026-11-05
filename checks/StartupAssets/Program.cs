using System.Text.Json;
using System.Text.RegularExpressions;
using DusmpLauncher.Bridge;
using DusmpLauncher.Core;
using DusmpLauncher.Localization;

// Exercises embedded resources without starting Photino or touching user data.
Environment.SetEnvironmentVariable("DUSMP_UI_DIR", null);
var assets = new EmbeddedAssets();
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
string ReadAsset(string url, string mime)
{
    using var stream = assets.Resolve(assets, EmbeddedAssets.Scheme, url, out var contentType);
    Require(stream is not null, $"Missing asset: {url}");
    Require(contentType.StartsWith(mime), $"Wrong MIME for {url}: {contentType}");
    using var reader = new StreamReader(stream!);
    return reader.ReadToEnd();
}

var html = ReadAsset(EmbeddedAssets.EntryPoint.AbsoluteUri, "text/html");
string AssetUrl(string path) => new Uri(EmbeddedAssets.EntryPoint, path).AbsoluteUri;
Require(html.Contains("screen-loading is-active"), "Loading screen is hidden before JavaScript starts.");
Require(!html.Contains("type=\"module\""), "Bootstrap should not depend on module CORS over a custom scheme.");
Require(ReadAsset(AssetUrl("/"), "text/html") == html, "Root and entry point resolve differently.");
Require(EmbeddedAssets.Scheme == (OperatingSystem.IsWindows() ? "http" : "app"), "The entry point uses an unsupported platform scheme.");
var flagUrl = EmbeddedAssets.UsePlatformOrigin("app://launcher/assets/flags/us.svg");
Require(flagUrl == AssetUrl("assets/flags/us.svg"), "Backend image URLs use a different origin from the document.");
ReadAsset(flagUrl, "image/svg+xml");
if (OperatingSystem.IsWindows())
{
    Require(html.Contains("http://localhost/js/app.js"), "The Windows entry point lost its intercepted asset origin.");
    foreach (var path in new[] { "index.html", "profile.html", "css/app.css", "js/app.js", "js/profile.js" })
    {
        using var stream = assets.Resolve(assets, EmbeddedAssets.Scheme, AssetUrl(path), out _);
        using var reader = new StreamReader(stream!);
        var content = reader.ReadToEnd();
        Require(!content.Contains("app://launcher/"), $"Windows asset still uses the unsupported custom scheme: {path}");
    }
}
var assetLinks = Regex.Matches(html, "(?:src|href)=\"((?:app|http)://[^\"]+)\"");
Require(assetLinks.Count > 0, "No asset links were checked.");
foreach (Match match in assetLinks)
{
    var url = match.Groups[1].Value;
    Require(new Uri(url).Host == EmbeddedAssets.Host, $"Asset is on a different origin: {url}");
    using var stream = assets.Resolve(assets, EmbeddedAssets.Scheme, url, out _);
    Require(stream is not null && stream.Length > 0, $"Unresolved embedded asset: {url}");
}
ReadAsset(new Uri(EmbeddedAssets.EntryPoint, "css/app.css").AbsoluteUri, "text/css");
ReadAsset(new Uri(EmbeddedAssets.EntryPoint, "js/app.js").AbsoluteUri, "text/javascript");
ReadAsset(AssetUrl("index.html?cache=1"), "text/html");
foreach (var invalid in new[] { "file:///etc/passwd", $"{EmbeddedAssets.Scheme}://foreign/index.html", AssetUrl("%2e%2e%2fsecret"), AssetUrl("%5csecret") })
{
    using var stream = assets.Resolve(assets, EmbeddedAssets.Scheme, invalid, out _);
    Require(stream is null, $"Unexpected asset for {invalid}");
}

// Every shipped language has to resolve a real sentence, not the key it was
// looked up by. Asserted against the embedded resources rather than against
// pinned prose, so rewording a message is not a test failure — only losing a
// translation is.
// The skin editor is a second window with its own page, so nothing in
// index.html references it — a missing file there would only surface when the
// player opens the window.
var skinPage = ReadAsset(new Uri(EmbeddedAssets.EntryPoint, "profile.html").AbsoluteUri, "text/html");
Require(skinPage.Contains("player-model", StringComparison.Ordinal), "The skin window page lost its model container.");
Require(skinPage.Contains("js/profile.js", StringComparison.Ordinal), "The skin window page does not load its script.");
foreach (Match match in Regex.Matches(skinPage, "(?:src|href)=\"((?:app|http)://[^\"]+)\""))
{
    using var stream = assets.Resolve(assets, EmbeddedAssets.Scheme, match.Groups[1].Value, out _);
    Require(stream is not null && stream.Length > 0, $"Unresolved skin window asset: {match.Groups[1].Value}");
}

var language = new LanguageManager();
const string probeKey = "error.api.manifestMissing";
var seen = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var tag in LanguageCatalog.TranslatedTags)
{
    await language.SetLanguageAsync(tag);
    Require(string.Equals(language.CurrentTag, tag, StringComparison.OrdinalIgnoreCase),
        $"Language did not switch to {tag}; it stayed on {language.CurrentTag}.");
    var translated = language.Translate(probeKey);
    Require(!string.IsNullOrWhiteSpace(translated) && translated != probeKey,
        $"{tag} has no translation for {probeKey}.");
    seen[tag] = translated;
}
Require(seen.ContainsKey("en-US") && seen.ContainsKey("pt-BR"), "The base languages are not embedded.");
Require(seen["en-US"] != seen["pt-BR"], "Switching language did not change the text.");
Require(seen.Count >= 9, $"Only {seen.Count} translations are embedded.");

// Every language carries every key. A key present only in English falls back to
// printing itself, so a player on another language sees "error.external.launchFailed"
// where a sentence belongs. Probing one key never caught that: 37 of them had
// been missing from fourteen languages for weeks.
var english = LoadKeys("en-US");
foreach (var tag in LanguageCatalog.TranslatedTags)
{
    var missing = english.Except(LoadKeys(tag), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    Require(missing.Length == 0,
        $"{tag} is missing {missing.Length} keys, starting with {string.Join(", ", missing.Take(5))}.");
}

static HashSet<string> LoadKeys(string tag)
{
    var name = $"DusmpLauncher.Localization.Languages.{tag}.json";
    using var stream = typeof(LanguageManager).Assembly.GetManifestResourceStream(name)
        ?? throw new Exception($"FAIL: {tag} is not embedded as {name}.");
    using var document = JsonDocument.Parse(stream);
    return document.RootElement.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
}
var state = JsonSerializer.Serialize(new LauncherState(), BridgeProtocol.Json);
Require(state.Contains("\"gameState\":\"idle\""), "The UI's string-enum contract has changed.");
Console.WriteLine("PASS: embedded entry point, all HTML assets, same-origin paths, pre-bootstrap loading screen, translations and bridge enum contract.");
