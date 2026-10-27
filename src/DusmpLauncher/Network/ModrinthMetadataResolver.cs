using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DusmpLauncher.Network;

/// <summary>What Modrinth knows about a mod the pack ships.</summary>
public sealed record ModrinthProject(string? IconUrl, string? Title, string? Description);

/// <summary>
/// Puts a real icon on the optional-mods screen.
///
/// The DUSMP API stores an icon per optional mod, but it is a field somebody has
/// to fill in by hand and in practice it is always null, so every row fell back
/// to the same placeholder and the list read as one mod repeated. Modrinth
/// already knows the answer: it indexes jars by SHA-1, and the manifest carries
/// that hash for the mirror lookup, so the icon can be recovered from data the
/// launcher already has.
///
/// Additive only. Anything the API actually set wins — this fills holes, it never
/// overrides what the team published.
///
/// Best-effort like every other third-party call here: a budget, a cache, and no
/// exception that can reach the install path. Modrinth being down must cost an
/// icon, never a launch.
/// </summary>
public sealed class ModrinthMetadataResolver
{
    private const string VersionFilesUrl = "https://api.modrinth.com/v2/version_files";
    private const string ProjectsUrl = "https://api.modrinth.com/v2/projects?ids=";

    /// <summary>Modrinth asks for a contactable agent; anonymous clients get throttled harder.</summary>
    private const string UserAgent = "DusmpLauncher/1.0 (+https://dusmp.online)";

    /// <summary>Hashes per request, matching <see cref="ExternalMirrorResolver"/>.</summary>
    private const int HashBatch = 300;

    /// <summary>Modrinth's documented ceiling for the bulk projects endpoint.</summary>
    private const int ProjectBatch = 100;

    /// <summary>
    /// How long an answer stays good. Icons change rarely, and a miss is cached
    /// for the same span on purpose: a jar Modrinth does not host would
    /// otherwise be re-queried on every single start.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(14);

    /// <summary>Runs while the player waits on the onboarding screen, so it stays short.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly TimeSpan _budget;

    /// <summary>Guards the cache file against two resolves overlapping.</summary>
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public ModrinthMetadataResolver(HttpClient http, string cacheFile, TimeSpan? budget = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheFile = cacheFile;
        _budget = budget is { } value && value > TimeSpan.Zero ? value : DefaultBudget;
    }

    /// <summary>
    /// Maps each caller key to what Modrinth knows about the jar with that SHA-1.
    /// Keys with no match are simply absent; the result is never null and this
    /// never throws.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModrinthProject>> ResolveAsync(
        IReadOnlyDictionary<string, string> sha1ByKey,
        CancellationToken ct = default)
    {
        var empty = (IReadOnlyDictionary<string, ModrinthProject>)new Dictionary<string, ModrinthProject>(StringComparer.Ordinal);
        if (sha1ByKey.Count == 0) return empty;

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, hash) in sha1ByKey)
        {
            if (Normalise(hash) is { } clean) wanted[key] = clean;
        }
        if (wanted.Count == 0) return empty;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_budget);

        try
        {
            return await ResolveCoreAsync(wanted, budget.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Including the budget expiring. An icon is never worth a failure.
            return empty;
        }
    }

    private async Task<IReadOnlyDictionary<string, ModrinthProject>> ResolveCoreAsync(
        IReadOnlyDictionary<string, string> wanted, CancellationToken ct)
    {
        var cache = await ReadCacheAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, ModrinthProject>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var (key, hash) in wanted)
        {
            if (cache.TryGetValue(hash, out var entry) && entry.IsFresh)
            {
                if (entry.Project is { } known) result[key] = known;
            }
            else if (!missing.Contains(hash, StringComparer.Ordinal))
            {
                missing.Add(hash);
            }
        }

        if (missing.Count == 0) return result;

        // sha1 -> Modrinth project id, then project id -> the project itself.
        var projectIdByHash = await LookupProjectIdsAsync(missing, ct).ConfigureAwait(false);
        var projects = await LookupProjectsAsync(projectIdByHash.Values.Distinct(StringComparer.Ordinal).ToArray(), ct)
            .ConfigureAwait(false);

        var fetched = new Dictionary<string, ModrinthProject?>(StringComparer.Ordinal);
        foreach (var hash in missing)
        {
            fetched[hash] = projectIdByHash.TryGetValue(hash, out var id) && projects.TryGetValue(id, out var project)
                ? project
                // A negative answer is an answer, and caching it is the whole
                // point: private DUSMP jars must not be asked about every start.
                : null;
        }

        foreach (var (key, hash) in wanted)
        {
            if (!result.ContainsKey(key) && fetched.TryGetValue(hash, out var project) && project is not null)
                result[key] = project;
        }

        await WriteCacheAsync(cache, fetched, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<Dictionary<string, string>> LookupProjectIdsAsync(IReadOnlyList<string> hashes, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var offset = 0; offset < hashes.Count; offset += HashBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = hashes.Skip(offset).Take(HashBatch).ToArray();
            var payload = JsonSerializer.Serialize(new { hashes = batch, algorithm = "sha1" });

            using var request = new HttpRequestMessage(HttpMethod.Post, VersionFilesUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var found = JsonSerializer.Deserialize<Dictionary<string, VersionDto>>(body, Json);
            if (found is null) continue;

            foreach (var (hash, version) in found)
            {
                if (Normalise(hash) is { } clean && !string.IsNullOrWhiteSpace(version?.ProjectId))
                    map[clean] = version!.ProjectId!;
            }
        }
        return map;
    }

    private async Task<Dictionary<string, ModrinthProject>> LookupProjectsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var map = new Dictionary<string, ModrinthProject>(StringComparer.Ordinal);
        for (var offset = 0; offset < ids.Count; offset += ProjectBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(offset).Take(ProjectBatch).ToArray();
            var url = ProjectsUrl + Uri.EscapeDataString(JsonSerializer.Serialize(batch));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var projects = JsonSerializer.Deserialize<List<ProjectDto>>(body, Json);
            if (projects is null) continue;

            foreach (var project in projects)
            {
                if (string.IsNullOrWhiteSpace(project.Id)) continue;
                // The UI only accepts https, so an http icon would silently turn
                // back into the placeholder — drop it here instead.
                var icon = project.IconUrl is { Length: > 0 } candidate
                    && candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? candidate : null;
                map[project.Id] = new ModrinthProject(icon, Trim(project.Title), Trim(project.Description));
            }
        }
        return map;
    }

    private async Task<Dictionary<string, CacheEntry>> ReadCacheAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_cacheFile)) return new(StringComparer.Ordinal);
            var body = await File.ReadAllTextAsync(_cacheFile, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(body, Json) is { } parsed
                ? new Dictionary<string, CacheEntry>(parsed, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt cache is not worth reporting; it rebuilds itself.
            return new(StringComparer.Ordinal);
        }
        finally { _cacheGate.Release(); }
    }

    private async Task WriteCacheAsync(
        Dictionary<string, CacheEntry> existing,
        IReadOnlyDictionary<string, ModrinthProject?> fetched,
        CancellationToken ct)
    {
        await _cacheGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (hash, project) in fetched)
                existing[hash] = new CacheEntry { FetchedAt = now, Project = project };

            // Drop anything long stale so the file cannot grow without bound.
            var live = existing
                .Where(pair => now - pair.Value.FetchedAt < CacheLifetime * 2)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var temporary = _cacheFile + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(live), ct).ConfigureAwait(false);
            File.Move(temporary, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Losing the cache only costs one more round trip next time.
        }
        finally { _cacheGate.Release(); }
    }

    /// <summary>Lowercase 40-hex, or null when the value is not a SHA-1 at all.</summary>
    private static string? Normalise(string? hash)
    {
        if (hash is null) return null;
        var trimmed = hash.Trim();
        if (trimmed.Length != 40) return null;
        foreach (var c in trimmed)
        {
            if (!Uri.IsHexDigit(c)) return null;
        }
        return trimmed.ToLowerInvariant();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class VersionDto
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    }

    private sealed class ProjectDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    }

    private sealed class CacheEntry
    {
        [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }

        /// <summary>Null means "Modrinth does not have this jar", which is cached too.</summary>
        [JsonPropertyName("project")] public ModrinthProject? Project { get; set; }

        [JsonIgnore] public bool IsFresh => DateTimeOffset.UtcNow - FetchedAt < CacheLifetime;
    }
}
