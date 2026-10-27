using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DusmpLauncher.Bridge;
using DusmpLauncher.Core;
using DusmpLauncher.Models;

namespace DusmpLauncher.Authentication;

/// <summary>Microsoft browser sign-in, Xbox Live, XSTS and Minecraft Java authentication.</summary>
public sealed class MicrosoftAuth : IMicrosoftAuth
{
    private const string ClientIdStoreKey = "microsoft.refreshTokenClientId";
    private readonly HttpClient _http; private readonly ISecureTokenStore _tokens; private readonly DeviceCodeFlow _flow;
    private readonly BrowserCodeFlow _browser;
    private readonly DesktopRedirectFlow _desktop;
    private readonly IEmbeddedLoginWindow _loginWindow;
    public MicrosoftAuth(HttpClient http, ISecureTokenStore tokens, LauncherPaths paths, IEmbeddedLoginWindow? loginWindow = null)
    {
        _http = http;
        _tokens = tokens;
        _flow = new DeviceCodeFlow(http);
        _browser = new BrowserCodeFlow(http);
        _desktop = new DesktopRedirectFlow(http);
        // Null means "this build has no window of its own", which is a supported
        // state: the other flows still run. It must never be left unassigned,
        // because SignInAsync reads IsSupported before choosing a flow.
        _loginWindow = loginWindow ?? new UnsupportedLoginWindow();
    }

    public async Task<GameSession> SignInAsync(Action<DeviceCodePrompt> onPrompt, CancellationToken ct = default, bool useDeviceCode = false)
    {
        // A configured desktop app uses PKCE. Legacy registrations use the
        // device link, which can open with the public user code already filled.
        // The built-in client id accepts neither a loopback redirect nor a device
        // code, so it signs in through a window the launcher owns and watches.
        if (!AuthConstants.HasBrowserClient && _loginWindow.IsSupported && !useDeviceCode)
        {
            var embedded = await SignInWithEmbeddedWindowAsync(ct).ConfigureAwait(false);
            if (embedded is not null) return embedded;
        }

        var browserResult = useDeviceCode
            ? BrowserCodeResult.Unavailable("auth.fallback.requested")
            : await _browser.AuthorizeAsync(onPrompt, ct).ConfigureAwait(false);
        // Only an unavailable app registration, loopback receiver, or browser
        // allows the device flow. Declines, bad state, and server failures do not.
        var result = browserResult.Result ?? await _flow.AuthorizeAsync(onPrompt, ct,
            browserResult.FallbackReasonKey, openSystemBrowser: true).ConfigureAwait(false);
        if (result.Outcome != DeviceCodeOutcome.Succeeded || result.Token is null)
            throw new LauncherException(result.ErrorKey ?? "error.auth.failed", result.Detail);
        if (string.IsNullOrWhiteSpace(result.Token.AccessToken))
            throw new LauncherException("error.auth.failed", "Microsoft returned no access token.");
        var session = await CompleteMinecraftLoginAsync(result.Token.AccessToken, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(result.Token.RawRefreshToken))
            await StoreRefreshAsync(result.Token.RawRefreshToken, session.Uuid, ct).ConfigureAwait(false);
        return session;
    }

    /// <param name="accountId">
    /// The account to resume, as its Minecraft UUID. Null resumes whichever one
    /// signed in last, which is what a plain start needs.
    /// </param>
    public async Task<GameSession?> SignInSilentlyAsync(string? accountId = null, CancellationToken ct = default)
    {
        var key = KeyFor(accountId);
        var refresh = await _tokens.GetAsync(key, ct).ConfigureAwait(false);
        // Accounts stored before the drawer existed only have the shared key.
        if (string.IsNullOrWhiteSpace(refresh) && key != AuthConstants.RefreshTokenStoreKey)
            refresh = await _tokens.GetAsync(AuthConstants.RefreshTokenStoreKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refresh)) return null;

        var storedClientId = await _tokens.GetAsync(ClientIdStoreKey, ct).ConfigureAwait(false);
        if (!string.Equals(storedClientId ?? AuthConstants.DefaultClientId, AuthConstants.ClientId, StringComparison.Ordinal)) return null;

        var result = await _flow.RefreshAsync(refresh, ct).ConfigureAwait(false);
        if (result is null || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            // A dead token is worth forgetting, but only this account's.
            await _tokens.RemoveAsync(key, ct).ConfigureAwait(false);
            return null;
        }

        var session = await CompleteMinecraftLoginAsync(result.AccessToken, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.RawRefreshToken))
            await StoreRefreshAsync(result.RawRefreshToken, session.Uuid, ct).ConfigureAwait(false);

        // Asking for an account and being handed a different one is a failure,
        // not a result. It can happen through the shared key above, which holds
        // whoever signed in last: resuming that in answer to a request for an
        // older account would switch the player to somebody else's profile and
        // report it as success.
        if (!string.IsNullOrWhiteSpace(accountId)
            && !string.Equals(session.Uuid?.Trim(), accountId.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        return session;
    }

    /// <summary>Forgets one account's token without touching the others.</summary>
    public Task ForgetAsync(string accountId, CancellationToken ct = default) =>
        _tokens.RemoveAsync(KeyFor(accountId), ct);

    /// <summary>
    /// Where an account's refresh token lives. Keyed by UUID so several accounts
    /// can stay signed in at once; the bare key is the one-account layout the
    /// launcher used before, kept so nobody is signed out by an update.
    /// </summary>
    private static string KeyFor(string? accountId) => string.IsNullOrWhiteSpace(accountId)
        ? AuthConstants.RefreshTokenStoreKey
        : $"{AuthConstants.RefreshTokenStoreKey}.{accountId.Trim().ToLowerInvariant()}";

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _tokens.RemoveAsync(AuthConstants.RefreshTokenStoreKey, ct).ConfigureAwait(false);
        await _tokens.RemoveAsync(ClientIdStoreKey, ct).ConfigureAwait(false);
    }

    private async Task StoreRefreshAsync(string token, string? accountId, CancellationToken ct)
    {
        // Both keys: the shared one keeps a plain restart working, the per-account
        // one is what lets the drawer switch without a fresh sign-in.
        await _tokens.SetAsync(AuthConstants.RefreshTokenStoreKey, token, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(accountId))
            await _tokens.SetAsync(KeyFor(accountId), token, ct).ConfigureAwait(false);
        await _tokens.SetAsync(ClientIdStoreKey, AuthConstants.ClientId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in through the launcher's own window. Returns null when the player
    /// closed it or the platform could not deliver a code, which sends the
    /// caller on to the other flows rather than failing the sign-in outright.
    /// </summary>
    private async Task<GameSession?> SignInWithEmbeddedWindowAsync(CancellationToken ct)
    {
        string? code;
        try
        {
            code = await _loginWindow.CaptureCodeAsync(
                DesktopRedirectFlow.BuildAuthorizeUri(),
                DesktopRedirectFlow.DesktopRedirectUri,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"embedded sign-in window failed: {ex.Message}");
            return null;
        }

        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var token = await _desktop.ExchangeAsync(code, ct).ConfigureAwait(false);
        var session = await CompleteMinecraftLoginAsync(token.AccessToken, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            await StoreRefreshAsync(token.RefreshToken, session.Uuid, ct).ConfigureAwait(false);
        return session;
    }

    private async Task<GameSession> CompleteMinecraftLoginAsync(string microsoftToken, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { return await ExchangeMinecraftSessionAsync(microsoftToken, timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new LauncherException("error.auth.connection", "Could not reach the Microsoft or Minecraft sign-in service.", ex); }
        catch (JsonException ex)
        { throw new LauncherException("error.auth.failed", "A sign-in service returned an invalid response.", ex); }
    }

    /// <summary>
    /// Trades the Microsoft token for an Xbox Live user token.
    ///
    /// The RpsTicket prefix depends on where the token came from — <c>t=</c> for
    /// the legacy MSA flow, <c>d=</c> for an Entra app, and some issuers want it
    /// bare — and getting it wrong yields HTTP 400 (malformed) rather than 401
    /// (refused). Since the two are told apart only by trying, the configured
    /// form goes first and the others are attempted on a 400. A 401 is a real
    /// refusal and stops immediately.
    /// </summary>
    private async Task<XboxToken> AuthenticateXboxLiveAsync(string microsoftToken, CancellationToken ct)
    {
        var configured = AuthConstants.XboxTicketPrefix;
        var candidates = new[] { configured }
            .Concat(new[] { "t=", "d=", "" }.Where(x => x != configured))
            .ToArray();

        LauncherException? lastMalformed = null;
        var attempts = new List<string>(candidates.Length);
        foreach (var prefix in candidates)
        {
            try
            {
                using var xbl = await PostAsync("https://user.auth.xboxlive.com/user/authenticate", new
                {
                    Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = prefix + microsoftToken },
                    RelyingParty = "http://auth.xboxlive.com",
                    TokenType = "JWT"
                }, ct).ConfigureAwait(false);

                var token = await xbl.Content.ReadFromJsonAsync<XboxToken>(cancellationToken: ct).ConfigureAwait(false)
                    ?? throw new LauncherException("error.auth.failed", "Xbox Live returned an empty response.");
                if (string.IsNullOrWhiteSpace(token.Token))
                    throw new LauncherException("error.auth.failed", "Xbox Live returned no token.");
                return token;
            }
            catch (LauncherException ex) when (ex.Detail?.Contains("HTTP 400", StringComparison.Ordinal) == true)
            {
                // Only a malformed-request answer is worth another shape. Every
                // prefix tried is listed, not just the last one: naming a single
                // prefix reads as "we only sent that one" and sends whoever is
                // debugging this after a ticket format that was already ruled out.
                attempts.Add(prefix.Length == 0 ? "(none)" : prefix);
                lastMalformed = new LauncherException(ex.ErrorKey, $"{ex.Detail} Ticket prefixes tried: {string.Join(", ", attempts)}.", ex);
            }
        }

        if (lastMalformed is null)
            throw new LauncherException("error.auth.failed", "Xbox Live refused every ticket format.");

        // Every ticket shape was refused, so the shape is not the problem — the
        // token is. Say what it actually contains: an access token issued for the
        // wrong audience, or without the XboxLive.signin scope, is rejected here
        // exactly like a malformed ticket, and the two are indistinguishable from
        // the status code alone.
        throw new LauncherException(
            lastMalformed.ErrorKey,
            $"{lastMalformed.Detail} Token: {DescribeToken(microsoftToken)}",
            lastMalformed);
    }

    /// <summary>
    /// The audience and scopes of an access token, for diagnostics. Never returns
    /// the token or anything that could stand in for it — only the two claims
    /// that decide whether Xbox Live will accept it at all.
    /// </summary>
    private static string DescribeToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return "opaque, not a JWT";

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));

            string Claim(string name) => document.RootElement.TryGetProperty(name, out var value)
                ? value.ToString()
                : "(absent)";

            return $"aud={Claim("aud")}, scp={Claim("scp")}, appid={Claim("appid")}";
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return "unreadable";
        }
    }

    private async Task<GameSession> ExchangeMinecraftSessionAsync(string microsoftToken, CancellationToken ct)
    {
        var xblToken = await AuthenticateXboxLiveAsync(microsoftToken, ct).ConfigureAwait(false);

        using var xsts = await PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken.Token } },
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
        }, ct).ConfigureAwait(false);
        var xstsToken = await xsts.Content.ReadFromJsonAsync<XboxToken>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new LauncherException("error.auth.failed", "Xbox XSTS returned an empty response.");
        var hash = xstsToken.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs;
        if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(xstsToken.Token)) throw new LauncherException("error.auth.failed", "Xbox XSTS response is incomplete.");

        using var minecraft = await PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={hash};{xstsToken.Token}" }, ct).ConfigureAwait(false);
        var minecraftToken = await minecraft.Content.ReadFromJsonAsync<MinecraftToken>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new LauncherException("error.auth.failed", "Minecraft authentication returned an empty response.");
        if (string.IsNullOrWhiteSpace(minecraftToken.AccessToken)) throw new LauncherException("error.auth.failed", "Minecraft returned no access token.");

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", minecraftToken.AccessToken);
        using var profileResponse = await _http.SendAsync(profileRequest, ct).ConfigureAwait(false);
        if (profileResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new LauncherException("error.auth.noMinecraft", "This Microsoft account has no Minecraft Java profile.");
        if (!profileResponse.IsSuccessStatusCode)
            throw new LauncherException("error.auth.profile", $"Minecraft profile returned HTTP {(int)profileResponse.StatusCode}.");
        var profileData = await profileResponse.Content.ReadFromJsonAsync<MinecraftProfile>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new LauncherException("error.auth.failed", "Minecraft profile response was empty.");
        if (string.IsNullOrWhiteSpace(profileData.Id) || string.IsNullOrWhiteSpace(profileData.Name))
            throw new LauncherException("error.auth.profile", "Minecraft returned an incomplete profile.");
        var profile = new PlayerProfile
        {
            Type = AccountType.Microsoft,
            Username = profileData.Name,
            Uuid = profileData.Id,
            AvatarUrl = AuthConstants.BuildAvatarUrl(profileData.Id),
            // The chip crops the head out of this texture, and the page only
            // loads https, so an http texture would show nothing at all.
            SkinUrl = profileData.Skins?.FirstOrDefault()?.Url is { Length: > 0 } skin
                ? Network.MinecraftAppearance.Https(skin)
                : AuthConstants.BuildSkinUrl(profileData.Id)
        };
        return new(profile, minecraftToken.AccessToken, profileData.Id, profileData.Name, xstsToken.DisplayClaims?.Xui?.FirstOrDefault()?.Xid);
    }

    /// <summary>
    /// Property names exactly as written in the request objects below.
    ///
    /// <see cref="JsonContent.Create{T}(T, System.Net.Http.Headers.MediaTypeHeaderValue, JsonSerializerOptions)"/>
    /// defaults to the Web options, which camel-case every name. Xbox Live reads
    /// <c>RpsTicket</c> and nothing else: given <c>rpsTicket</c> it sees a request
    /// with no ticket at all and answers HTTP 400 with an empty body — which
    /// looks exactly like a rejected application, and is not one. Verified
    /// against the live endpoint: the same token gets 401 (read, refused) in
    /// PascalCase and 400 (never read) in camelCase.
    ///
    /// Each body below is already written in the casing its own service wants,
    /// so the fix is simply to stop rewriting them.
    /// </summary>
    private static readonly JsonSerializerOptions VerbatimNames = new() { PropertyNamingPolicy = null };

    private async Task<HttpResponseMessage> PostAsync(string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: VerbatimNames) };
        request.Headers.Accept.ParseAdd("application/json");
        if (new Uri(url).Host.EndsWith(".xboxlive.com", StringComparison.Ordinal))
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var key = "error.auth.failed";
                long? xboxError = null;

                // Read the body once, for every service. Without it a 400 from
                // Xbox Live is indistinguishable from a 401, and the two mean
                // very different things (malformed request vs refused ticket).
                var rawBody = "";
                try { rawBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
                if (url.Contains("xsts.auth.xboxlive.com", StringComparison.Ordinal))
                {
                    try
                    {
                        using var errorBody = JsonDocument.Parse(rawBody);
                        if (errorBody.RootElement.TryGetProperty("XErr", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var code)) xboxError = code;
                    }
                    catch (JsonException) { }
                    key = xboxError switch
                    {
                        2148916233 => "error.auth.xboxProfile",
                        2148916238 => "error.auth.family",
                        2148916235 => "error.auth.region",
                        2148916236 or 2148916237 => "error.auth.ageVerification",
                        _ => key
                    };
                }
                if (url.Contains("api.minecraftservices.com", StringComparison.Ordinal) && response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    key = "error.auth.appAccess";
                if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429) key = "error.auth.connection";

                // If Xbox rejects this legacy registration, explain which app
                // configuration needs attention instead of offering endless retries.
                if (url.Contains("user.auth.xboxlive.com", StringComparison.Ordinal)
                    && (int)response.StatusCode is 400 or 401
                    && !AuthConstants.HasBrowserClient)
                    key = "error.auth.legacyClient";
                // Three different services sit in this chain, so "HTTP 401" on its
                // own is not diagnosable. Name the step that actually refused.
                var step = url switch
                {
                    _ when url.Contains("user.auth.xboxlive.com", StringComparison.Ordinal) => "Xbox Live (user authenticate)",
                    _ when url.Contains("xsts.auth.xboxlive.com", StringComparison.Ordinal) => "Xbox XSTS (authorize)",
                    _ when url.Contains("api.minecraftservices.com", StringComparison.Ordinal) => "Minecraft (login_with_xbox)",
                    _ => new Uri(url).Host,
                };
                // The body from the Xbox and Minecraft services is an error
                // document, never a token, and it is usually the only thing that
                // distinguishes one 400 from another — an empty body is itself a
                // finding. Trimmed so a long HTML error page cannot flood the
                // screen, and only for those hosts, so no token endpoint response
                // can be echoed by accident.
                var detail = $"{step} returned HTTP {(int)response.StatusCode}"
                    + (xboxError is null ? "." : $" (Xbox error {xboxError}).")
                    + $" Client id: {AuthConstants.ClientId}. Scope: {AuthConstants.Scope}.";
                if (url.Contains(".xboxlive.com", StringComparison.Ordinal)
                    || url.Contains("api.minecraftservices.com", StringComparison.Ordinal))
                {
                    var trimmed = rawBody.Trim();
                    detail += trimmed.Length == 0
                        ? " Response body: empty."
                        : $" Response body: {trimmed[..Math.Min(300, trimmed.Length)]}";
                }

                throw new LauncherException(key, detail);
            }
        }
        return response;
    }

    private sealed class XboxToken { [JsonPropertyName("Token")] public string Token { get; set; } = ""; [JsonPropertyName("DisplayClaims")] public DisplayClaims? DisplayClaims { get; set; } }
    private sealed class DisplayClaims { [JsonPropertyName("xui")] public List<Xui>? Xui { get; set; } }
    private sealed class Xui { [JsonPropertyName("uhs")] public string? Uhs { get; set; } [JsonPropertyName("xid")] public string? Xid { get; set; } }
    private sealed class MinecraftToken { [JsonPropertyName("access_token")] public string AccessToken { get; set; } = ""; }
    private sealed class MinecraftProfile { [JsonPropertyName("id")] public string Id { get; set; } = ""; [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("skins")] public List<Skin>? Skins { get; set; } }
    private sealed class Skin { [JsonPropertyName("url")] public string? Url { get; set; } }
}
