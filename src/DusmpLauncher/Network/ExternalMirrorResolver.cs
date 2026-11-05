using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DusmpLauncher.Models;

namespace DusmpLauncher.Network;

/// <summary>
/// Finds the public homes of the files a modpack is about to download.
/// </summary>
/// <remarks>
/// Almost every jar in the pack is a public mod that Modrinth or CurseForge
/// already serves from their own CDN. Asking them first means DUSMP only pays
/// bandwidth for what is actually ours — private mods, configs, branding — and
/// the player usually gets a closer, faster edge for everything else.
/// <para>
/// The lookup is by content hash, never by name: Modrinth indexes files by
/// SHA-1, CurseForge by a murmur2 fingerprint, so a hit means "this exact
/// artifact", not "a file with the same name". Even so, nothing here is a
/// security boundary — the downloader still verifies SHA-256 against the
/// manifest, which is the only hash this launcher trusts. That is why a URL
/// from here can be tried first without taking on any new trust.
/// </para>
/// <para>
/// Every failure is survivable by design. If Modrinth is down, rate-limiting us
/// or answering nonsense, the player must still be able to install from DUSMP,
/// so this class returns what it managed to resolve and never throws.
/// </para>
/// </remarks>
public sealed class ExternalMirrorResolver
{
    /// <summary>
    /// Overrides the bundled CurseForge API key. An explicitly blank key
    /// disables CurseForge lookups.
    /// </summary>
    public const string CurseForgeApiKeyVariable = "CURSEFORGE_API_KEY";

    /// <summary>The name this was read from before; still honoured so an existing setup keeps working.</summary>
    public const string LegacyCurseForgeApiKeyVariable = "DUSMP_CURSEFORGE_API_KEY";

    /// <summary>
    /// Optional Modrinth token. The endpoints used here are public and work
    /// without one, so this exists only for an operator who wants their traffic
    /// attributed to their account. It is read from the environment and never
    /// written anywhere.
    /// </summary>
    public const string ModrinthTokenVariable = "MODRINTH_TOKEN";

    private const string ModrinthVersionFilesUrl = "https://api.modrinth.com/v2/version_files";
    private const string CurseForgeFingerprintsUrl = "https://api.curseforge.com/v1/fingerprints";
    private const string DefaultCurseForgeApiKey = "$2a$10$/i2EevvGr8iKzwBykzM5ne4HAZOsKNMG4puk/a4uFBRskxUVDAL3.";

    /// <summary>Modrinth's algorithm selector. It publishes sha1 and sha512 only.</summary>
    private const string Sha1Algorithm = "sha1";

    /// <summary>
    /// Hashes per request. A thousand-file pack must not turn into one enormous
    /// body that a proxy truncates or the API rejects outright, and batches this
    /// size still keep the whole pack to a couple of round trips.
    /// </summary>
    private const int BatchSize = 300;

    /// <summary>
    /// Whole-operation budget. This runs while the player is staring at a
    /// progress bar, so a slow third party must cost seconds, not the install:
    /// when it expires we simply download from DUSMP as before.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyResult =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly TimeSpan _budget;
    private readonly string? _curseForgeApiKey;
    private readonly string? _modrinthToken;

    /// <summary>
    /// Set when a host asks us to back off, which stops this run from asking it
    /// anything else. Hammering an API that just returned 429 is how a launcher
    /// earns a longer ban than the one it was given.
    /// </summary>
    private bool _modrinthThrottled;
    private bool _curseForgeThrottled;

    /// <summary>
    /// Why a lookup was refused, reported once per kind so a misconfigured key
    /// is visible to whoever set it instead of looking like "no mirrors found"
    /// forever. Never carries the key itself.
    /// </summary>
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <param name="http">Shared client, never disposed here — the launcher owns its lifetime.</param>
    /// <param name="budget">Overrides <see cref="DefaultBudget"/>, mainly for tests.</param>
    /// <param name="curseForgeApiKey">
    /// Explicit key, or null to read the environment before using the bundled
    /// key. An explicitly blank value disables CurseForge lookups.
    /// </param>
    public ExternalMirrorResolver(HttpClient http, TimeSpan? budget = null, string? curseForgeApiKey = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _budget = budget is { } value && value > TimeSpan.Zero ? value : DefaultBudget;

        var key = curseForgeApiKey
            ?? Environment.GetEnvironmentVariable(CurseForgeApiKeyVariable)
            ?? Environment.GetEnvironmentVariable(LegacyCurseForgeApiKeyVariable)
            ?? DefaultCurseForgeApiKey;
        _curseForgeApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();

        var token = Environment.GetEnvironmentVariable(ModrinthTokenVariable);
        _modrinthToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    /// <summary>True unless CurseForge lookups were explicitly disabled with a blank key.</summary>
    public bool CurseForgeEnabled => _curseForgeApiKey is not null;

    /// <summary>
    /// Maps <see cref="ModFile.Path"/> to the public URLs that serve that exact
    /// file, best first. Paths with no public home are simply absent, and the
    /// caller keeps downloading those from DUSMP.
    /// </summary>
    /// <remarks>
    /// Never throws, including on cancellation: the caller is on the install path
    /// and checks its own token immediately afterwards, so turning a dead CDN —
    /// or a shutdown — into an exception would only give it a failure it has no
    /// way to act on.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ResolveAsync(
        IReadOnlyList<ModFile> files, CancellationToken ct = default)
    {
        if (files is null || files.Count == 0)
            return EmptyResult;

        var resolved = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        try
        {
            // One budget for both providers: the point is to bound the delay the
            // player sees, not to give each API its own ten seconds.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_budget);

            await ResolveFromModrinthAsync(files, resolved, budget.Token).ConfigureAwait(false);
            await ResolveFromCurseForgeAsync(files, resolved, budget.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Defence in depth: the per-provider paths already swallow their own
            // failures, so anything arriving here is unexpected. It still must
            // not cost the player an install.
        }

        return Freeze(resolved);
    }

    /// <summary>
    /// Bulk SHA-1 lookup. One POST per batch rather than one GET per file: a
    /// four-hundred-mod pack would otherwise mean four hundred round trips, which
    /// is both slower than the download it is trying to save and a good way to
    /// get rate-limited.
    /// </summary>
    private async Task ResolveFromModrinthAsync(
        IReadOnlyList<ModFile> files, Dictionary<string, List<string>> into, CancellationToken ct)
    {
        if (_modrinthThrottled || ct.IsCancellationRequested)
            return;

        // Several manifest entries can legitimately share a hash (the same jar
        // installed under two paths), so a hash maps to a list of owners.
        var byHash = new Dictionary<string, List<ModFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                continue;

            var sha1 = NormaliseSha1(file.Sha1);
            if (sha1 is null)
                continue;

            if (!byHash.TryGetValue(sha1, out var owners))
                byHash[sha1] = owners = new List<ModFile>(1);

            owners.Add(file);
        }

        if (byHash.Count == 0)
            return;

        foreach (var batch in byHash.Keys.Chunk(BatchSize))
        {
            if (ct.IsCancellationRequested)
                return;

            var payload = new ModrinthLookupRequest { Hashes = batch, Algorithm = Sha1Algorithm };
            var response = await PostJsonAsync<Dictionary<string, ModrinthVersion>>(
                ModrinthVersionFilesUrl, payload, addHeaders: null, ct).ConfigureAwait(false);

            // A failed batch means the API is unhappy with us as a whole — down,
            // rate-limiting, or answering something we cannot parse — so the
            // remaining batches would only burn the budget for the same answer.
            if (response is null)
                return;

            foreach (var (hash, version) in response)
            {
                if (version?.Files is not { Count: > 0 } candidates)
                    continue;

                if (!byHash.TryGetValue(hash, out var owners))
                    continue;

                foreach (var owner in owners)
                {
                    var match = candidates.FirstOrDefault(c => IsSameArtifact(c, hash, owner.Size));
                    if (match?.Url is { } url)
                        Add(into, owner.Path, url);
                }
            }
        }
    }

    /// <summary>
    /// Fingerprint lookup for files not found on Modrinth. Uses the bundled key
    /// unless overridden, and skips requests when a blank key disables it.
    /// </summary>
    private async Task ResolveFromCurseForgeAsync(
        IReadOnlyList<ModFile> files, Dictionary<string, List<string>> into, CancellationToken ct)
    {
        if (_curseForgeApiKey is null || _curseForgeThrottled || ct.IsCancellationRequested)
            return;

        var byFingerprint = new Dictionary<long, List<ModFile>>();

        foreach (var file in files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                continue;

            // A zero fingerprint is the "unset" value a serialiser produces, not
            // a real murmur2 result worth a round trip.
            if (file.Fingerprint is not { } fingerprint || fingerprint <= 0)
                continue;

            // Modrinth already covered this one; a second URL for the same file
            // buys nothing and CurseForge would rather not be asked.
            if (into.ContainsKey(file.Path))
                continue;

            if (!byFingerprint.TryGetValue(fingerprint, out var owners))
                byFingerprint[fingerprint] = owners = new List<ModFile>(1);

            owners.Add(file);
        }

        if (byFingerprint.Count == 0)
            return;

        foreach (var batch in byFingerprint.Keys.Chunk(BatchSize))
        {
            if (ct.IsCancellationRequested)
                return;

            var payload = new CurseForgeFingerprintRequest { Fingerprints = batch };
            var response = await PostJsonAsync<CurseForgeFingerprintResponse>(
                CurseForgeFingerprintsUrl,
                payload,
                headers => headers.TryAddWithoutValidation("x-api-key", _curseForgeApiKey),
                ct).ConfigureAwait(false);

            if (response is null)
                return;

            foreach (var match in response.Data?.ExactMatches ?? new List<CurseForgeMatch>())
            {
                if (match?.File is not { } candidate)
                    continue;

                if (!byFingerprint.TryGetValue(candidate.FileFingerprint, out var owners))
                    continue;

                foreach (var owner in owners)
                {
                    // downloadUrl is null for projects whose author disabled
                    // third-party downloads. That is a normal answer, not an
                    // error: those files stay on the DUSMP mirror.
                    if (candidate.DownloadUrl is not { } url || !IsHttpUrl(url))
                        continue;

                    if (candidate.FileLength is { } length && length != owner.Size)
                        continue;

                    Add(into, owner.Path, url);
                }
            }
        }
    }

    /// <summary>
    /// Sends one JSON POST and reads one JSON body, returning null for every
    /// failure. Errors are deliberately indistinguishable to the caller: a 503, a
    /// 429 and a truncated body all mean the same thing here — carry on without
    /// this mirror.
    /// </summary>
    private async Task<T?> PostJsonAsync<T>(
        string url, object payload, Action<System.Net.Http.Headers.HttpRequestHeaders>? addHeaders, CancellationToken ct)
        where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload, payload.GetType(), mediaType: null, JsonOptions),
            };

            // Set explicitly rather than relying on the shared client's default:
            // Modrinth asks third-party tools to identify themselves, and an
            // anonymous launcher is the kind that gets blocked as a scraper.
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            if (_modrinthToken is not null && url.StartsWith(ModrinthVersionFilesUrl, StringComparison.Ordinal))
                request.Headers.TryAddWithoutValidation("Authorization", _modrinthToken);
            addHeaders?.Invoke(request.Headers);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Diagnose(url, response);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Includes the budget expiring, a DNS or TLS failure, a proxy's HTML
            // error page and a malformed body. None of them are worth a log line
            // on the player's screen, and none of them may stop the install.
            return null;
        }
    }

    /// <summary>
    /// Says what a refusal actually was.
    ///
    /// Every failure used to collapse into "no mirror for this file", so a key
    /// that was wrong, expired or missing the right permission looked exactly
    /// like a pack whose mods are not on CurseForge — and nobody could tell.
    /// These go to the launcher's own error stream, once per kind per run, and
    /// never contain the key.
    /// </summary>
    private void Diagnose(string url, HttpResponseMessage response)
    {
        var host = new Uri(url).Host;
        var status = (int)response.StatusCode;

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // Honour the brake rather than keep pulling: the rest of this run
            // falls back to the DUSMP mirror, which always has every file.
            if (host.Contains("curseforge", StringComparison.OrdinalIgnoreCase)) _curseForgeThrottled = true;
            else _modrinthThrottled = true;
        }

        var reason = status switch
        {
            401 => "the API key was not accepted",
            403 => "the API key is valid but not allowed to do this",
            404 => "the endpoint does not exist",
            429 => $"rate limited; backing off for the rest of this run (retry-after: {response.Headers.RetryAfter?.ToString() ?? "unset"})",
            >= 500 => "the service is having trouble",
            _ => "unexpected status",
        };

        if (_reported.Add($"{host}:{status}"))
            Console.Error.WriteLine($"mirror lookup refused by {host}: HTTP {status} — {reason}.");
    }

    /// <summary>
    /// Identifies the launcher to the public APIs, with a contact URL so an
    /// operator who sees this traffic knows whose it is.
    /// </summary>
    private static string UserAgent =>
        $"DusmpLauncher/{typeof(ExternalMirrorResolver).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"} (+https://dusmp.online)";

    /// <summary>
    /// Cheap early guard that the remote file really is the manifest's file. The
    /// hash already says so, so a size mismatch means one side is describing a
    /// different artifact and the entry is not worth a download attempt. The real
    /// integrity check is still the SHA-256 verified after the bytes arrive.
    /// </summary>
    private static bool IsSameArtifact(ModrinthFile? candidate, string sha1, long expectedSize)
        => candidate?.Url is { } url
           && IsHttpUrl(url)
           && candidate.Size == expectedSize
           && string.Equals(NormaliseSha1(candidate.Hashes?.Sha1), sha1, StringComparison.OrdinalIgnoreCase);

    private static void Add(Dictionary<string, List<string>> into, string path, string url)
    {
        if (!into.TryGetValue(path, out var urls))
            into[path] = urls = new List<string>(1);

        if (!urls.Contains(url, StringComparer.Ordinal))
            urls.Add(url);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Freeze(Dictionary<string, List<string>> source)
    {
        if (source.Count == 0)
            return EmptyResult;

        var frozen = new Dictionary<string, IReadOnlyList<string>>(source.Count, StringComparer.Ordinal);
        foreach (var (path, urls) in source)
            frozen[path] = urls.ToArray();

        return frozen;
    }

    /// <summary>
    /// Accepts only a well-formed 40-character hex SHA-1, lowercased. Anything
    /// else in the manifest — a truncated hash, a SHA-256 pasted in the wrong
    /// field, a placeholder — is dropped here rather than sent to Modrinth, where
    /// it could only ever miss.
    /// </summary>
    private static string? NormaliseSha1(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length != 40)
            return null;

        foreach (var c in trimmed)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
                return null;
        }

        return trimmed.ToLowerInvariant();
    }

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private sealed class ModrinthLookupRequest
    {
        [JsonPropertyName("hashes")] public IReadOnlyList<string> Hashes { get; init; } = Array.Empty<string>();
        [JsonPropertyName("algorithm")] public string Algorithm { get; init; } = Sha1Algorithm;
    }

    /// <summary>The version object Modrinth returns per matched hash.</summary>
    private sealed class ModrinthVersion
    {
        [JsonPropertyName("files")] public List<ModrinthFile>? Files { get; init; }
    }

    private sealed class ModrinthFile
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("filename")] public string? FileName { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("hashes")] public ModrinthHashes? Hashes { get; init; }
    }

    /// <summary>Modrinth publishes sha1 and sha512; there is no sha256 to compare.</summary>
    private sealed class ModrinthHashes
    {
        [JsonPropertyName("sha1")] public string? Sha1 { get; init; }
    }

    private sealed class CurseForgeFingerprintRequest
    {
        [JsonPropertyName("fingerprints")] public IReadOnlyList<long> Fingerprints { get; init; } = Array.Empty<long>();
    }

    private sealed class CurseForgeFingerprintResponse
    {
        [JsonPropertyName("data")] public CurseForgeFingerprintData? Data { get; init; }
    }

    private sealed class CurseForgeFingerprintData
    {
        [JsonPropertyName("exactMatches")] public List<CurseForgeMatch>? ExactMatches { get; init; }
    }

    private sealed class CurseForgeMatch
    {
        [JsonPropertyName("file")] public CurseForgeFile? File { get; init; }
    }

    private sealed class CurseForgeFile
    {
        [JsonPropertyName("fileFingerprint")] public long FileFingerprint { get; init; }
        [JsonPropertyName("fileLength")] public long? FileLength { get; init; }
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; init; }
    }
}
