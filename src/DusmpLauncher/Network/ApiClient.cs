using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DusmpLauncher.Models;

namespace DusmpLauncher.Network;

/// <summary>
/// Translation keys every failure in this namespace can carry. They live in one
/// place so the translation tables have a single list to satisfy, and so a
/// feature can never invent a key the tables have never heard of.
/// </summary>
public static class ApiErrorKeys
{
    /// <summary>The configured base URL is not a usable absolute HTTP(S) URL.</summary>
    public const string BadBaseUrl = "error.api.badBaseUrl";

    /// <summary>DNS, TLS or socket failure: we never reached the API at all.</summary>
    public const string Unreachable = "error.api.unreachable";

    /// <summary>The API accepted the connection but did not answer in time.</summary>
    public const string Timeout = "error.api.timeout";

    /// <summary>A non-success status we cannot interpret any further.</summary>
    public const string HttpStatus = "error.api.httpStatus";

    /// <summary>The body was not the JSON shape this endpoint promises.</summary>
    public const string BadResponse = "error.api.badResponse";

    /// <summary>A 200 with a body that deserialised to nothing, e.g. literal <c>null</c>.</summary>
    public const string EmptyResponse = "error.api.emptyResponse";

    public const string LauncherVersionMissing = "error.api.launcherVersionMissing";
    public const string ModpackLatestMissing = "error.api.modpackLatestMissing";

    /// <summary>No manifest for the requested version: nothing can be installed.</summary>
    public const string ManifestMissing = "error.api.manifestMissing";

    /// <summary>A bundled fixture resource is not in the assembly — a packaging bug.</summary>
    public const string FixtureMissing = "error.fixture.missing";

    /// <summary>A bundled fixture exists but does not parse.</summary>
    public const string FixtureUnreadable = "error.fixture.unreadable";
}

/// <summary>
/// A launcher-API failure the UI can localise. Carries the translation key
/// separately from the technical detail precisely so the two never get mixed:
/// <see cref="ErrorKey"/> goes on screen, <see cref="Detail"/> goes in the log.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(string errorKey, string detail)
        : base($"{errorKey} ({detail})")
    {
        ErrorKey = errorKey;
        Detail = detail;
    }

    /// <summary>Translation key for the message shown to the player.</summary>
    public string ErrorKey { get; }

    /// <summary>Untranslated technical detail. Never contains a credential.</summary>
    public string Detail { get; }
}

/// <summary>
/// <see cref="IApiClient"/> over HTTP. Deliberately thin: it owns URL building,
/// timeouts, one retry and JSON shape, and nothing else — no caching, no state,
/// no policy about what a missing value means beyond "optional or required".
/// </summary>
/// <remarks>
/// The <see cref="HttpClient"/> is injected and never disposed here, because the
/// launcher shares one handler for its whole lifetime; disposing per client is
/// what exhausts sockets. The per-request timeout is enforced with a linked
/// token instead of <see cref="HttpClient.Timeout"/> so the shared client can
/// still be used for long modpack downloads by another module.
/// </remarks>
public sealed class ApiClient : IApiClient
{
    private const string JsonMediaType = "application/json";

    /// <summary>Query keys. Paths come from <see cref="ApiEndpoints"/>; these are the only other URL fragments.</summary>
    private const string QueryVersion = "version";
    private const string QueryEdition = "edition";
    private const string QueryLanguage = "language";

    /// <summary>
    /// One retry only. The launcher is on a loading screen with a human watching
    /// it, so a long exponential backoff would read as a hang; a second chance
    /// covers the common case of a single dropped packet or a restarting node.
    /// </summary>
    private const int MaxRetries = 1;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Liveness probes get a tighter budget: a slow answer is as bad as none.</summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;

    /// <param name="http">Shared client. Its own <see cref="HttpClient.Timeout"/> is not relied on.</param>
    /// <param name="baseUrl">Base URL, or null to take <see cref="ApiEndpoints.ResolveBaseUrl"/>.</param>
    /// <param name="requestTimeout">Per-request budget; the default keeps startup under a human's patience.</param>
    public ApiClient(HttpClient http, string? baseUrl = null, TimeSpan? requestTimeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _requestTimeout = requestTimeout is { } t && t > TimeSpan.Zero ? t : DefaultRequestTimeout;

        var resolved = (string.IsNullOrWhiteSpace(baseUrl) ? ApiEndpoints.ResolveBaseUrl() : baseUrl!).TrimEnd('/');

        // Validate here rather than at the first request: a typo in DUSMP_API_BASE
        // should fail with one clear message, not once per endpoint.
        if (!Uri.TryCreate(resolved + "/", UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ApiException(ApiErrorKeys.BadBaseUrl, resolved);
        }

        BaseUrl = resolved;
    }

    /// <inheritdoc />
    public string BaseUrl { get; }

    /// <inheritdoc />
    public bool IsOffline => false;

    /// <inheritdoc />
    public async Task<LauncherVersionInfo> GetLauncherVersionAsync(CancellationToken ct = default)
        => await GetRequiredAsync<LauncherVersionInfo>(
            ApiEndpoints.LauncherVersion, null, ApiErrorKeys.LauncherVersionMissing, ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// An empty list is a valid answer and means this API has no editions, which
    /// is how every deployment before them behaves. The caller then installs the
    /// single pack the other endpoints serve.
    /// </remarks>
    public async Task<IReadOnlyList<ModpackEdition>> GetEditionsAsync(CancellationToken ct = default)
        => await GetOptionalListAsync<ModpackEdition>(ApiEndpoints.ModpackEditions, null, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ModpackLatestInfo> GetModpackLatestAsync(string? edition = null, CancellationToken ct = default)
        => await GetRequiredAsync<ModpackLatestInfo>(
            ApiEndpoints.ModpackLatest, Query((QueryEdition, edition)), ApiErrorKeys.ModpackLatestMissing, ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// The only required endpoint whose absence is fatal: without a manifest
    /// there is nothing to verify against and nothing to install, so a 404 here
    /// becomes an error rather than an empty value.
    /// </remarks>
    public async Task<ModpackManifest> GetModpackManifestAsync(string? version = null, CancellationToken ct = default)
        => await GetRequiredAsync<ModpackManifest>(
            ApiEndpoints.ModpackManifest,
            Query((QueryVersion, version)),
            ApiErrorKeys.ManifestMissing,
            ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>A 404 means "this pack has no optional mods", which is a valid pack.</remarks>
    public async Task<IReadOnlyList<OptionalMod>> GetOptionalModsAsync(
        string? modpackVersion = null, CancellationToken ct = default)
        => await GetOptionalListAsync<OptionalMod>(
            ApiEndpoints.OptionalMods, Query((QueryVersion, modpackVersion)), ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// A 404 means "no CDN steering", not a failure: the launcher then downloads
    /// straight from the URLs the manifest already carries.
    /// </remarks>
    public async Task<IReadOnlyList<ServerRegion>> GetRegionsAsync(CancellationToken ct = default)
        => await GetOptionalListAsync<ServerRegion>(ApiEndpoints.Regions, null, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameServer>> GetServersAsync(CancellationToken ct = default)
        => await GetOptionalListAsync<GameServer>(ApiEndpoints.Servers, null, ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// No status endpoint is treated as "nothing wrong". Defaulting to
    /// maintenance mode would lock players out every time the banner service
    /// itself is the thing that broke.
    /// </remarks>
    public async Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default)
        => await GetOptionalAsync<ServiceStatus>(ApiEndpoints.Status, null, ct).ConfigureAwait(false)
           ?? new ServiceStatus();

    /// <inheritdoc />
    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(string? language = null, CancellationToken ct = default)
        => await GetOptionalListAsync<NewsItem>(
            ApiEndpoints.News, Query((QueryLanguage, language)), ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Every failure is swallowed on purpose. The hint only lets the API steer us
    /// to a better mirror; if it cannot answer we still have local measurements,
    /// so turning this into an error would break startup for no gain. Caller
    /// cancellation still propagates — that is a decision, not a failure.
    /// </remarks>
    public async Task<RegionHintResponse?> ReportRegionAsync(
        RegionHintRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = BuildUri(ApiEndpoints.ClientRegion, null);
        try
        {
            using var response = await SendWithRetryAsync(
                HttpMethod.Post,
                uri,
                () => JsonContent.Create(request, mediaType: null, JsonOptions),
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content
                .ReadFromJsonAsync<RegionHintResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Not retried: this is the probe that decides whether to bother with the
    /// API at all, and a retry would double the very stall it exists to detect.
    /// </remarks>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var uri = BuildUri(ApiEndpoints.Health, null);
        try
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(PingTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static (string Key, string? Value)[]? Query(params (string Key, string? Value)[] pairs)
        => pairs.Length == 0 ? null : pairs;

    /// <summary>
    /// The only place a launcher API URL is assembled. Endpoint names come from
    /// <see cref="ApiEndpoints"/>; null query values are dropped so callers do
    /// not have to branch on "was a version supplied".
    /// </summary>
    private Uri BuildUri(string endpoint, (string Key, string? Value)[]? query)
    {
        var builder = new StringBuilder(BaseUrl).Append('/').Append(endpoint.TrimStart('/'));

        var first = true;
        if (query is not null)
        {
            foreach (var (key, value) in query)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                builder.Append(first ? '?' : '&');
                first = false;
                builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private async Task<T> GetRequiredAsync<T>(
        string endpoint, (string Key, string? Value)[]? query, string notFoundKey, CancellationToken ct)
        where T : class
    {
        var uri = BuildUri(endpoint, query);
        using var response = await SendWithRetryAsync(HttpMethod.Get, uri, null, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ApiException(notFoundKey, Describe(HttpMethod.Get, uri, "HTTP 404"));

        EnsureSuccess(HttpMethod.Get, uri, response);
        return await ReadJsonAsync<T>(HttpMethod.Get, uri, response, ct).ConfigureAwait(false);
    }

    private async Task<T?> GetOptionalAsync<T>(
        string endpoint, (string Key, string? Value)[]? query, CancellationToken ct)
        where T : class
    {
        var uri = BuildUri(endpoint, query);
        using var response = await SendWithRetryAsync(HttpMethod.Get, uri, null, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            return null;

        EnsureSuccess(HttpMethod.Get, uri, response);
        return await ReadJsonAsync<T>(HttpMethod.Get, uri, response, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> GetOptionalListAsync<T>(
        string endpoint, (string Key, string? Value)[]? query, CancellationToken ct)
        => await GetOptionalAsync<List<T>>(endpoint, query, ct).ConfigureAwait(false)
           ?? (IReadOnlyList<T>)Array.Empty<T>();

    /// <summary>
    /// Sends one request, retrying once on a failure that a second attempt could
    /// plausibly fix. A 4xx is never retried: the request itself is what is wrong.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method, Uri uri, Func<HttpContent>? contentFactory, CancellationToken ct)
    {
        // Remembered across attempts so the thrown error describes the last real
        // failure rather than whichever kind happened to occur first.
        var failureKey = ApiErrorKeys.Unreachable;
        var failureDetail = "no attempt completed";

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(_requestTimeout);

            try
            {
                // ResponseContentRead, not ResponseHeadersRead: these bodies are a
                // few kilobytes, and buffering them inside the attempt keeps the
                // timeout covering the whole request instead of only its headers.
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));
                if (contentFactory is not null)
                    request.Content = contentFactory();

                var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, attemptCts.Token)
                    .ConfigureAwait(false);

                if (attempt < MaxRetries && IsTransient(response.StatusCode))
                {
                    failureKey = ApiErrorKeys.HttpStatus;
                    failureDetail = FormattableString.Invariant($"HTTP {(int)response.StatusCode}");
                    response.Dispose();
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The linked token fired, not the caller's: this is our timeout.
                failureKey = ApiErrorKeys.Timeout;
                failureDetail = string.Format(
                    CultureInfo.InvariantCulture, "no response within {0:0.#}s", _requestTimeout.TotalSeconds);
            }
            catch (HttpRequestException ex)
            {
                failureKey = ApiErrorKeys.Unreachable;
                failureDetail = ex.Message;
            }

            if (attempt < MaxRetries)
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
        }

        throw new ApiException(failureKey, Describe(method, uri, failureDetail));
    }

    /// <summary>
    /// Statuses where the same request sent again might succeed: the server is
    /// overloaded, restarting, or asked us to slow down.
    /// </summary>
    private static bool IsTransient(HttpStatusCode code)
        => (int)code >= 500
           || code == HttpStatusCode.RequestTimeout
           || code == HttpStatusCode.TooManyRequests;

    private static void EnsureSuccess(HttpMethod method, Uri uri, HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw new ApiException(
            ApiErrorKeys.HttpStatus,
            Describe(method, uri, FormattableString.Invariant($"HTTP {(int)response.StatusCode}")));
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpMethod method, Uri uri, HttpResponseMessage response, CancellationToken ct)
        where T : class
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value is null)
                throw new ApiException(ApiErrorKeys.EmptyResponse, Describe(method, uri, "body was null"));

            return value;
        }
        catch (JsonException ex)
        {
            throw new ApiException(ApiErrorKeys.BadResponse, Describe(method, uri, ex.Message));
        }
        catch (NotSupportedException ex)
        {
            // Wrong Content-Type, e.g. a proxy's HTML error page with a 200.
            throw new ApiException(ApiErrorKeys.BadResponse, Describe(method, uri, ex.Message));
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(ApiErrorKeys.Unreachable, Describe(method, uri, ex.Message));
        }
        catch (IOException ex)
        {
            throw new ApiException(ApiErrorKeys.Unreachable, Describe(method, uri, ex.Message));
        }
    }

    /// <summary>
    /// Builds the technical detail line. Uses path and query only — never the
    /// full absolute URL with credentials, and never a header value.
    /// </summary>
    private static string Describe(HttpMethod method, Uri uri, string detail)
        => FormattableString.Invariant($"{method.Method} {uri.PathAndQuery}: {detail}");
}
