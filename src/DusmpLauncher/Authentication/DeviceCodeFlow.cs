using System.Net.Http;
using System.Text.Json;
using DusmpLauncher.Bridge;
using DusmpLauncher.Core;
using XboxAuthNet.OAuth;

namespace DusmpLauncher.Authentication;

/// <summary>
/// Outcome of one device-code sign-in attempt. A result object rather than an
/// exception because "the player declined" and "the code expired" are ordinary
/// flows the UI has screens for, not faults — and because the caller, not this
/// class, decides which translation key reaches the bridge.
/// </summary>
/// <param name="Outcome">Terminal state of the attempt.</param>
/// <param name="Token">
/// Microsoft token set, only when <paramref name="Outcome"/> is
/// <see cref="DeviceCodeOutcome.Succeeded"/>. Never logged, never serialised.
/// </param>
/// <param name="ErrorKey">Translation key, null on success.</param>
/// <param name="Detail">Untranslated technical detail for the log pane, secrets stripped.</param>
public sealed record DeviceCodeResult(
    DeviceCodeOutcome Outcome,
    MicrosoftOAuthResponse? Token,
    string? ErrorKey,
    string? Detail)
{
    public static DeviceCodeResult Success(MicrosoftOAuthResponse token) =>
        new(DeviceCodeOutcome.Succeeded, token, null, null);

    public static DeviceCodeResult Failure(DeviceCodeOutcome outcome, string errorKey, string? detail = null) =>
        new(outcome, null, errorKey, detail);
}

/// <summary>
/// OAuth device authorization for machines where browser callback sign-in is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// BrowserCodeFlow owns the primary PKCE loopback flow. This fallback also works
/// with legacy registrations that cannot register a localhost redirect URI.
/// </para>
/// <para>
/// XboxAuthNet's own code flow is not reused here for the same reason: it wants
/// a <c>IWebUI</c> that can navigate and read back a redirect URL. Only its
/// token <em>container</em> (<see cref="MicrosoftOAuthResponse"/>) is reused, so
/// the result can be handed straight to the Xbox Live / Minecraft pipeline.
/// </para>
/// </remarks>
public sealed class DeviceCodeFlow
{
    /// <summary>
    /// Fields that may appear in a technical detail string. An allowlist rather
    /// than a denylist: a new field Microsoft starts returning must not leak by
    /// default, and <c>device_code</c>, <c>access_token</c>, <c>refresh_token</c>
    /// and <c>id_token</c> can then never reach a log or the UI.
    /// </summary>
    private static readonly string[] ReportableFields =
    {
        "error",
        "error_description",
        "error_codes",
        "correlation_id",
        "trace_id",
        "timestamp",
    };

    /// <summary>Polling interval floor, so a bogus <c>interval</c> cannot spin the CPU.</summary>
    private const int MinimumIntervalSeconds = 1;

    /// <summary>Polling interval ceiling, so a bogus <c>interval</c> cannot stall the sign-in.</summary>
    private const int MaximumIntervalSeconds = 60;

    /// <summary>Extra seconds added to the interval on each <c>slow_down</c>, per RFC 8628.</summary>
    private const int SlowDownIncrementSeconds = 5;

    private const int MinimumLifetimeSeconds = 60;
    private const int MaximumLifetimeSeconds = 3600;
    private const int DefaultIntervalSeconds = 5;
    private const int DefaultLifetimeSeconds = 900;

    /// <summary>
    /// Consecutive transport failures tolerated while polling. The player is
    /// typing a code into a browser on the other side of this loop; a single
    /// dropped request must not throw away a sign-in that is about to succeed.
    /// </summary>
    private const int MaximumTransientFailures = 5;

    private const int MaximumDetailLength = 300;

    private readonly HttpClient _http;
    private readonly Func<Uri, CancellationToken, Task<bool>> _openBrowser;

    public DeviceCodeFlow(HttpClient http, Func<Uri, CancellationToken, Task<bool>>? openBrowser = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _openBrowser = openBrowser ?? BrowserCodeFlow.OpenSystemBrowserAsync;
    }

    /// <summary>
    /// Requests a device code, hands it to <paramref name="onPrompt"/> for the
    /// player to type into their browser, then polls until the grant resolves.
    /// </summary>
    /// <param name="onPrompt">
    /// Called exactly once, before the first poll, with the user code and the
    /// verification URI. The device code is not passed: it is the credential
    /// that redeems the token and never leaves this class.
    /// </param>
    public async Task<DeviceCodeResult> AuthorizeAsync(
        Action<DeviceCodePrompt> onPrompt,
        CancellationToken ct = default,
        string? fallbackReasonKey = null,
        bool openSystemBrowser = false)
    {
        ArgumentNullException.ThrowIfNull(onPrompt);
        ct.ThrowIfCancellationRequested();

        HttpOutcome start;
        try
        {
            start = await PostAsync(
                AuthConstants.DeviceCodeUrl,
                new Dictionary<string, string>
                {
                    ["client_id"] = AuthConstants.ClientId,
                    ["scope"] = AuthConstants.Scope,
                    ["response_type"] = AuthConstants.DeviceCodeResponseType,
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DeviceCodeResult.Failure(DeviceCodeOutcome.Cancelled, "error.auth.cancelled");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return DeviceCodeResult.Failure(
                DeviceCodeOutcome.Failed, "error.auth.connection", "Could not reach Microsoft to start sign-in.");
        }

        if (TryReadError(start, out var startError, out var oauthDetail))
            return DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, BrowserCodeFlow.OAuthErrorKey(startError), oauthDetail);

        if (!TryReadStartResponse(start, out var deviceCode, out var prompt, out var interval, out var lifetime, out var startDetail))
            return DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, "error.auth.failed", startDetail);

        // Only open the public user-code URL. The private device_code stays in this process.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(lifetime));
        try
        {
            var opened = openSystemBrowser && await _openBrowser(
                new Uri(prompt.VerificationUriComplete ?? prompt.VerificationUri), deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            onPrompt(prompt with { FallbackReasonKey = fallbackReasonKey, BrowserOpened = opened });
            var result = await PollAsync(deviceCode, interval, lifetime, deadline.Token).ConfigureAwait(false);
            return deadline.IsCancellationRequested && !ct.IsCancellationRequested
                ? DeviceCodeResult.Failure(DeviceCodeOutcome.Expired, "error.auth.expired") : result;
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested
                ? DeviceCodeResult.Failure(DeviceCodeOutcome.Cancelled, "error.auth.cancelled")
                : DeviceCodeResult.Failure(DeviceCodeOutcome.Expired, "error.auth.expired");
        }
    }

    /// <summary>
    /// Exchanges a stored refresh token for a fresh token set. Returns null when
    /// the server rejects the grant — revoked, superseded or simply too old —
    /// which is the signal to ask the player to sign in again. Transport and
    /// protocol failures throw instead, because those are not the player's fault
    /// and must not be mistaken for "your account is gone".
    /// </summary>
    public async Task<MicrosoftOAuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return null;

        ct.ThrowIfCancellationRequested();

        HttpOutcome outcome;
        try
        {
            outcome = await PostAsync(
                AuthConstants.TokenUrl,
                new Dictionary<string, string>
                {
                    ["client_id"] = AuthConstants.ClientId,
                    ["scope"] = AuthConstants.Scope,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = AuthConstants.RefreshTokenGrantType,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new LauncherException("error.auth.failed", $"token refresh failed: {ex.Message}", ex);
        }

        if (TryReadError(outcome, out var error, out _))
        {
            if (error is "invalid_grant" or "interaction_required" or "consent_required") return null;
            throw new LauncherException(BrowserCodeFlow.OAuthErrorKey(error), "Microsoft could not refresh this session.");
        }

        var token = ParseToken(outcome);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new LauncherException("error.auth.failed", $"token refresh returned no access token ({Describe(outcome)})");

        // Microsoft usually rotates the refresh token, but is not required to.
        // Carrying the old one forward keeps the account signed in either way.
        if (string.IsNullOrEmpty(token.RawRefreshToken))
            token.RawRefreshToken = refreshToken;

        return token;
    }

    private async Task<DeviceCodeResult> PollAsync(
        string deviceCode,
        int intervalSeconds,
        int lifetimeSeconds,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds);
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var transientFailures = 0;
        string? lastDetail = null;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = AuthConstants.ClientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = AuthConstants.DeviceCodeGrantType,
        };

        while (true)
        {
            // Wait first: the code cannot possibly be redeemed before the player
            // has had time to open the page, and polling immediately only earns
            // a slow_down. Task.Delay is also where cancellation lands fastest.
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return DeviceCodeResult.Failure(DeviceCodeOutcome.Cancelled, "error.auth.cancelled");
            }

            if (ct.IsCancellationRequested)
                return DeviceCodeResult.Failure(DeviceCodeOutcome.Cancelled, "error.auth.cancelled");

            if (DateTimeOffset.UtcNow >= deadline)
                return DeviceCodeResult.Failure(DeviceCodeOutcome.Expired, "error.auth.expired", lastDetail ?? "device code lifetime elapsed");

            HttpOutcome outcome;
            try
            {
                outcome = await PostAsync(AuthConstants.TokenUrl, form, ct).ConfigureAwait(false);
                transientFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return DeviceCodeResult.Failure(DeviceCodeOutcome.Cancelled, "error.auth.cancelled");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                lastDetail = "The Microsoft sign-in request timed out or lost its connection.";
                interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * 2, MaximumIntervalSeconds));
                if (++transientFailures >= MaximumTransientFailures)
                    return DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, "error.auth.connection", lastDetail);

                continue;
            }

            if (!TryReadError(outcome, out var error, out var detail))
            {
                var token = ParseToken(outcome);
                if (token is null || string.IsNullOrEmpty(token.AccessToken))
                    return DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, "error.auth.failed", $"no access token in token response ({Describe(outcome)})");

                if (string.IsNullOrEmpty(token.RawRefreshToken))
                {
                    // Without a refresh token the account would silently stop
                    // working on the next start, so treat it as a failure now
                    // rather than as a sign-in that quietly does not persist.
                    return DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, "error.auth.failed", "token response carried no refresh token");
                }

                return DeviceCodeResult.Success(token);
            }

            lastDetail = detail;
            switch (error)
            {
                case "authorization_pending":
                    continue;

                case "slow_down":
                    interval = TimeSpan.FromSeconds(Math.Min(
                        interval.TotalSeconds + SlowDownIncrementSeconds,
                        MaximumIntervalSeconds));
                    continue;

                case "authorization_declined":
                case "access_denied":
                    return DeviceCodeResult.Failure(DeviceCodeOutcome.Declined, "error.auth.declined", detail);

                case "expired_token":
                case "code_expired":
                    return DeviceCodeResult.Failure(DeviceCodeOutcome.Expired, "error.auth.expired", detail);

                default:
                    return DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, BrowserCodeFlow.OAuthErrorKey(error), detail);
            }
        }
    }

    private bool TryReadStartResponse(
        HttpOutcome outcome,
        out string deviceCode,
        out DeviceCodePrompt prompt,
        out int intervalSeconds,
        out int lifetimeSeconds,
        out string? detail)
    {
        deviceCode = "";
        prompt = new DeviceCodePrompt("", "", 0);
        intervalSeconds = DefaultIntervalSeconds;
        lifetimeSeconds = DefaultLifetimeSeconds;
        detail = null;

        if (TryReadError(outcome, out _, out var errorDetail))
        {
            detail = errorDetail;
            return false;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(outcome.Body);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            detail = $"device code response was not JSON: {ex.Message} ({Describe(outcome)})";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            detail = $"device code response was not an object ({Describe(outcome)})";
            return false;
        }

        var userCode = ReadString(root, "user_code");
        var rawDeviceCode = ReadString(root, "device_code");
        var verificationUri = ReadString(root, "verification_uri") ?? ReadString(root, "verification_url");
        var verificationUriComplete = ReadString(root, "verification_uri_complete");

        if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(rawDeviceCode) || string.IsNullOrEmpty(verificationUri))
        {
            detail = $"device code response was missing required fields ({Describe(outcome)})";
            return false;
        }

        if (!IsMicrosoftVerificationUri(verificationUri) ||
            (verificationUriComplete is not null && !IsMicrosoftVerificationUri(verificationUriComplete)))
        {
            detail = "Microsoft returned an unexpected verification link.";
            return false;
        }

        intervalSeconds = Math.Clamp(
            ReadInt(root, "interval") ?? DefaultIntervalSeconds,
            MinimumIntervalSeconds,
            MaximumIntervalSeconds);

        lifetimeSeconds = Math.Clamp(
            ReadInt(root, "expires_in") ?? DefaultLifetimeSeconds,
            MinimumLifetimeSeconds,
            MaximumLifetimeSeconds);

        deviceCode = rawDeviceCode;
        verificationUriComplete ??= BuildPrefilledVerificationUri(verificationUri, userCode);
        prompt = new DeviceCodePrompt(userCode, verificationUri, lifetimeSeconds, verificationUriComplete);
        return true;
    }

    /// <summary>The consumer Microsoft Link page accepts the public user code as `otc`.
    /// Entra's /devicelogin page does not promise this shortcut; leave it unchanged.</summary>
    public static string? BuildPrefilledVerificationUri(string verificationUri, string userCode)
    {
        if (string.IsNullOrWhiteSpace(userCode) || !IsMicrosoftVerificationUri(verificationUri)) return null;
        var uri = new Uri(verificationUri);
        var path = uri.AbsolutePath.TrimEnd('/');
        var linkPage = (uri.Host is "microsoft.com" or "www.microsoft.com") && path.Equals("/link", StringComparison.OrdinalIgnoreCase);
        var consumerPage = uri.Host == "login.live.com" && path.Equals("/oauth20_remoteconnect.srf", StringComparison.OrdinalIgnoreCase);
        if (!linkPage && !consumerPage) return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0]).Equals("otc", StringComparison.OrdinalIgnoreCase));
        return new UriBuilder(uri) { Query = string.Join("&", query.Append("otc=" + Uri.EscapeDataString(userCode))) }.Uri.AbsoluteUri;
    }

    private static bool IsMicrosoftVerificationUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && uri.Host is "microsoft.com" or "www.microsoft.com" or "login.microsoftonline.com" or "login.live.com" or "account.live.com";

    /// <summary>
    /// Builds the library's token container from a success body. The raw
    /// <c>refresh_token</c> goes into <c>RawRefreshToken</c>, not
    /// <c>RefreshToken</c>: the latter's setter prepends XboxAuthNet's
    /// <c>M.R3_BAY.</c> marker, which would corrupt a token that already has it.
    /// </summary>
    private static MicrosoftOAuthResponse? ParseToken(HttpOutcome outcome)
    {
        try
        {
            return MicrosoftOAuthResponse.FromHttpResponse(outcome.Body, (int)outcome.StatusCode, outcome.ReasonPhrase);
        }
        catch (Exception ex) when (ex is MicrosoftOAuthException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the body carries an OAuth <c>error</c>. Both the error code and
    /// the detail come from the allowlist, so no branch of the caller can end up
    /// holding a token value.
    /// </summary>
    private static bool TryReadError(HttpOutcome outcome, out string error, out string? detail)
    {
        error = "";
        detail = null;

        if (string.IsNullOrWhiteSpace(outcome.Body))
        {
            if (outcome.IsSuccess)
                return false;

            error = "http_error";
            detail = Describe(outcome);
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(outcome.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                if (outcome.IsSuccess)
                    return false;

                error = "http_error";
                detail = Describe(outcome);
                return true;
            }

            var code = ReadString(document.RootElement, "error");
            if (string.IsNullOrEmpty(code))
            {
                if (outcome.IsSuccess)
                    return false;

                error = "http_error";
                detail = Summarize(document.RootElement, outcome);
                return true;
            }

            error = code;
            detail = Summarize(document.RootElement, outcome);
            return true;
        }
        catch (JsonException)
        {
            if (outcome.IsSuccess)
                return false;

            error = "http_error";
            detail = Describe(outcome);
            return true;
        }
    }

    /// <summary>
    /// Renders only the allowlisted fields of an error body, prefixed with the
    /// status. Everything else is dropped rather than redacted, so there is no
    /// pattern to get wrong.
    /// </summary>
    private static string Summarize(JsonElement root, HttpOutcome outcome)
    {
        var parts = new List<string> { Describe(outcome) };

        foreach (var field in ReportableFields)
        {
            if (!root.TryGetProperty(field, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
                _ => value.ToString(),
            };

            if (!string.IsNullOrWhiteSpace(text))
                parts.Add($"{field}={text}");
        }

        var detail = string.Join(", ", parts);
        return detail.Length <= MaximumDetailLength ? detail : detail[..MaximumDetailLength];
    }

    /// <summary>Status line only. Safe for bodies we could not parse at all.</summary>
    private static string Describe(HttpOutcome outcome) =>
        $"HTTP {(int)outcome.StatusCode} {outcome.ReasonPhrase}, {outcome.Body.Length} bytes";

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private async Task<HttpOutcome> PostAsync(
        string url,
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        // The device endpoints answer 400 for authorization_pending and friends,
        // so the body is read on every status and never gated on success.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return new HttpOutcome(response.StatusCode, response.ReasonPhrase ?? "", body, response.IsSuccessStatusCode);
    }

    private readonly record struct HttpOutcome(
        System.Net.HttpStatusCode StatusCode,
        string ReasonPhrase,
        string Body,
        bool IsSuccess);
}
