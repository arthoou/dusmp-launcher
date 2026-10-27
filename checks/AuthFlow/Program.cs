using DusmpLauncher.Models;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DusmpLauncher.Authentication;
using DusmpLauncher.Bridge;
using DusmpLauncher.Core;

// Headless protocol checks: a simulated browser uses only a temporary loopback
// receiver; HTTP token exchanges are mocked and no real browser/account is used.
var originalClient = Environment.GetEnvironmentVariable(AuthConstants.ClientIdVariable);
Environment.SetEnvironmentVariable(AuthConstants.ClientIdVariable, "706daabd-9385-41db-a3e3-e2c8f1a8fae1");
try
{
    await BrowserSuccessAsync();
    await RejectedCallbackAsync("state=wrong&code=untrusted", "error.auth.state");
    await RejectedCallbackAsync("error=access_denied", "error.auth.declined");
    await RejectedCallbackAsync("error=unauthorized_client", "error.auth.configuration");
    await CancelAndTimeoutAsync();
    await UnavailableAsync();
    await RefreshErrorsAsync();
    // Offline accounts must keep working without any Microsoft registration:
    // this is the only way in for a player whose account DUSMP cannot sign in.
    var offline = new OfflineAuth();
    Check(offline.ValidateUsername("Steve") is null, "A normal name must be accepted offline");
    Check(offline.ValidateUsername("ab") == "error.auth.offlineNameLength", "Names under 3 characters must be refused");
    Check(offline.ValidateUsername(new string('a', 17)) == "error.auth.offlineNameLength", "Names over 16 characters must be refused");
    Check(offline.ValidateUsername("pl ayer") == "error.auth.offlineNameCharacters", "A space must be refused");
    Check(offline.ValidateUsername("ação") == "error.auth.offlineNameCharacters", "Non-ASCII must be refused");
    Check(offline.ValidateUsername("a_1") is null, "Letters, digits and underscore must be accepted");

    var offlineSession = offline.CreateSession("Steve");
    // The UUID has to be the one a server in offline mode derives for that name,
    // or the player loses their inventory the first time anything changes here.
    var expectedBytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:Steve"));
    expectedBytes[6] = (byte)((expectedBytes[6] & 0x0f) | 0x30);
    expectedBytes[8] = (byte)((expectedBytes[8] & 0x3f) | 0x80);
    Check(offlineSession.Uuid == new Guid(expectedBytes).ToString("N"), "The offline UUID must match what a server computes");
    Check(offlineSession.Profile.Type == AccountType.Offline && offlineSession.Profile.Username == "Steve", "The offline profile is wrong");
    // CmlLib rejects an empty access token even for an offline session.
    Check(!string.IsNullOrEmpty(offlineSession.AccessToken), "An offline session still needs a non-empty token");

    // O corpo enviado ao Xbox Live precisa sair EXATAMENTE com os nomes que cada
    // servico le. Os padroes Web do JsonContent.Create convertem tudo para
    // camelCase; com "rpsTicket" o Xbox nao ve ticket nenhum e responde HTTP 400
    // com corpo vazio, que e indistinguivel de um aplicativo recusado.
    // Conferido contra o endpoint real: mesmo token da 401 em PascalCase
    // (lido e recusado) e 400 em camelCase (nunca lido).
    var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
    var capturing = new HttpClient(new MockHandler(async request =>
    {
        var url = request.RequestUri!.ToString();
        if (request.Content is not null) bodies[url] = await request.Content.ReadAsStringAsync();
        if (url.Contains("oauth20_token") || url.Contains("/token"))
            return Json(HttpStatusCode.OK, """{"token_type":"Bearer","expires_in":3600,"access_token":"ACCESS","refresh_token":"M.C1_BAY.0.U.r"}""");
        // Devolve 401 para parar a cadeia logo apos o POST que interessa.
        return Json(HttpStatusCode.Unauthorized, "");
    }));
    var auth = new MicrosoftAuth(capturing, new StubTokenStore("M.C1_BAY.0.U.refresh"), LauncherPaths.Resolve());
    try { await auth.SignInSilentlyAsync(); } catch (LauncherException) { }

    var xboxUrl = bodies.Keys.FirstOrDefault(u => u.Contains("user.auth.xboxlive.com"));
    Check(xboxUrl is not null, "O passo do Xbox Live nao chegou a ser chamado");
    var xboxBody = bodies[xboxUrl!];
    foreach (var field in new[] { "\"Properties\"", "\"AuthMethod\"", "\"SiteName\"", "\"RpsTicket\"", "\"RelyingParty\"", "\"TokenType\"" })
        Check(xboxBody.Contains(field, StringComparison.Ordinal), $"O Xbox Live precisa receber {field}; corpo: {xboxBody}");
    Check(!xboxBody.Contains("\"rpsTicket\"", StringComparison.Ordinal), "camelCase faz o Xbox ignorar o ticket: " + xboxBody);
    Check(xboxBody.Contains("\"RpsTicket\":\"d=ACCESS\"", StringComparison.Ordinal), "O ticket precisa levar o token de verdade: " + xboxBody);


    // O padrao compilado e a combinacao que o msmc usa e que funciona hoje: o id
    // embutido do Minecraft nos endpoints login.live.com, mas pedindo
    // XboxLive.signin em vez do antigo escopo de servico MBI_SSL. Juntar esse id
    // com MBI_SSL produz um ticket que o Xbox Live nao aproveita, que foi o que
    // este launcher enviou durante muito tempo.
    Environment.SetEnvironmentVariable(AuthConstants.ClientIdVariable, null);
    Check(AuthConstants.ClientId == "00000000402b5328", "O client id embutido mudou: " + AuthConstants.ClientId);
    Check(AuthConstants.Scope == "XboxLive.signin offline_access", "Escopo errado: " + AuthConstants.Scope);
    Check(AuthConstants.XboxTicketPrefix == "d=", "O Xbox Live precisa do prefixo d=");
    Check(AuthConstants.AuthorizeUrl.StartsWith("https://login.live.com/", StringComparison.Ordinal),
        "O id embutido so funciona em login.live.com: " + AuthConstants.AuthorizeUrl);
    Check(!AuthConstants.HasBrowserClient, "O id embutido usa o redirect de desktop, nao loopback");

    // O app proprio do DUSMP, quando a Mojang aprovar: mesmo escopo e prefixo,
    // mas na autoridade /consumers, porque um GUID e um aplicativo do Entra.
    Environment.SetEnvironmentVariable(AuthConstants.ClientIdVariable, "ebf296cf-8547-427e-afb9-a7b12962ed76");
    Check(AuthConstants.HasBrowserClient, "Um GUID precisa habilitar o fluxo de navegador");
    Check(AuthConstants.Scope == "XboxLive.signin offline_access", "O escopo nao pode mudar com o id");
    Check(AuthConstants.XboxTicketPrefix == "d=", "O prefixo nao pode mudar com o id");
    foreach (var url in new[] { AuthConstants.AuthorizeUrl, AuthConstants.TokenUrl, AuthConstants.DeviceCodeUrl })
        Check(url.StartsWith("https://login.microsoftonline.com/consumers/", StringComparison.Ordinal),
            "Um id do Entra precisa da autoridade /consumers: " + url);
    Environment.SetEnvironmentVariable(AuthConstants.ClientIdVariable, "706daabd-9385-41db-a3e3-e2c8f1a8fae1");

    // Resuming by account id answers for that account or not at all.
    //
    // An account saved before tokens were kept per account has nothing under its
    // own key and falls back to the shared one, which holds whoever signed in
    // last. Handing that back would switch the player to someone else's profile
    // and report the switch as a success — the drawer would show the change as
    // done while the launcher was signed in as a different person.
    const string resumedUuid = "11111111111111111111111111111111";
    var chain = new HttpClient(new MockHandler(request =>
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("oauth20_token") || url.Contains("/token"))
            return Task.FromResult(Json(HttpStatusCode.OK, """{"token_type":"Bearer","expires_in":3600,"access_token":"ACCESS","refresh_token":"M.R"}"""));
        if (url.Contains("user.auth.xboxlive.com"))
            return Task.FromResult(Json(HttpStatusCode.OK, """{"Token":"XBL","DisplayClaims":{"xui":[{"uhs":"HASH"}]}}"""));
        if (url.Contains("xsts.auth.xboxlive.com"))
            return Task.FromResult(Json(HttpStatusCode.OK, """{"Token":"XSTS","DisplayClaims":{"xui":[{"uhs":"HASH","xid":"XID"}]}}"""));
        if (url.Contains("login_with_xbox"))
            return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"MC","expires_in":86400}"""));
        if (url.Contains("minecraft/profile"))
            return Task.FromResult(Json(HttpStatusCode.OK, $$"""{"id":"{{resumedUuid}}","name":"Arthur","skins":[]}"""));
        return Task.FromResult(Json(HttpStatusCode.NotFound, ""));
    }));
    // Only the shared key is stored, exactly as an account from before the
    // drawer would have it.
    var shared = new StubTokenStore("M.C1_BAY.0.U.refresh");
    var resumeAuth = new MicrosoftAuth(chain, shared, LauncherPaths.Resolve());

    var mine = await resumeAuth.SignInSilentlyAsync(resumedUuid);
    Check(mine is not null && mine.Uuid == resumedUuid, "A conta certa tem de voltar pelo token compartilhado");

    var someoneElse = await resumeAuth.SignInSilentlyAsync("22222222222222222222222222222222");
    Check(someoneElse is null, "Pedir uma conta e receber outra tem de contar como falha, nao como troca");

    var any = await resumeAuth.SignInSilentlyAsync();
    Check(any is not null && any.Uuid == resumedUuid, "Sem pedir conta nenhuma o token compartilhado continua valendo");

    Console.WriteLine("PASS: browser PKCE callback, state/decline/configuration, cancellation/timeout, fallback, refresh errors; offline accounts; identidade compilada igual a do msmc; Xbox Live request field names; retomada silenciosa so responde pela conta pedida.");
}
finally { Environment.SetEnvironmentVariable(AuthConstants.ClientIdVariable, originalClient); }
static async Task BrowserSuccessAsync()
{
    Dictionary<string, string>? authQuery = null;
    Task<HttpResponseMessage>? callback = null;
    DeviceCodePrompt? prompt = null;
    using var local = new HttpClient();
    using var http = new HttpClient(new MockHandler(async request =>
    {
        var form = Parse(await request.Content!.ReadAsStringAsync());
        Check(form["grant_type"] == "authorization_code", "Authorization-code grant");
        Check(form["code"] == "mock-code+with/slash", "Code survives URL encoding");
        Check(form["redirect_uri"] == authQuery!["redirect_uri"], "Redirect URI matches");
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Check(challenge == authQuery["code_challenge"], "PKCE S256 verifies");
        Check(authQuery["code_challenge_method"] == "S256" && authQuery["state"].Length >= 43, "Strong challenge/state");
        return Json(HttpStatusCode.OK, "{\"access_token\":\"mock-access\",\"refresh_token\":\"mock-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
    }));
    var flow = new BrowserCodeFlow(http, (uri, ct) =>
    {
        authQuery = Parse(uri.Query.TrimStart('?'));
        callback = local.GetAsync(authQuery["redirect_uri"] + "?state=" + authQuery["state"] + "&code=mock-code%2Bwith%2Fslash", ct);
        return Task.FromResult(true);
    });
    var result = await flow.AuthorizeAsync(value => prompt = value, default);
    Check(result.Result?.Outcome == DeviceCodeOutcome.Succeeded && result.Result.Token?.AccessToken == "mock-access", "Browser succeeds");
    Check(prompt?.Mode == "browser" && prompt.UserCode == "" && prompt.BrowserUri is not null, "Browser-only UI prompt");
    using var response = await callback!;
    var body = await response.Content.ReadAsStringAsync();
    Check(!body.Contains("mock-code") && !body.Contains(authQuery!["state"]), "Callback HTML contains no OAuth data");
}

static async Task RejectedCallbackAsync(string suffix, string errorKey)
{
    var tokenCalls = 0;
    using var http = new HttpClient(new MockHandler(_ => { tokenCalls++; throw new Exception("Must not exchange rejected callback"); }));
    using var local = new HttpClient();
    Task<HttpResponseMessage>? callback = null;
    var flow = new BrowserCodeFlow(http, (uri, ct) =>
    {
        var query = Parse(uri.Query.TrimStart('?'));
        var callbackQuery = suffix.StartsWith("state=") ? suffix : "state=" + query["state"] + "&" + suffix;
        callback = local.GetAsync(query["redirect_uri"] + "?" + callbackQuery, ct);
        return Task.FromResult(true);
    });
    var result = await flow.AuthorizeAsync(_ => { }, default);
    Check(result.Result?.ErrorKey == errorKey && result.FallbackReasonKey is null && tokenCalls == 0, "Rejected callback never falls back");
    using var response = await callback!;
}

static async Task CancelAndTimeoutAsync()
{
    using var http = new HttpClient(new MockHandler(_ => throw new Exception("Unexpected HTTP request")));
    using var cancel = new CancellationTokenSource();
    var flow = new BrowserCodeFlow(http, (_, _) => Task.FromResult(true));
    var cancelled = await flow.AuthorizeAsync(_ => cancel.Cancel(), cancel.Token);
    Check(cancelled.Result?.Outcome == DeviceCodeOutcome.Cancelled && cancelled.FallbackReasonKey is null, "Cancellation closes receiver");
    var timeout = new BrowserCodeFlow(http, (_, _) => Task.FromResult(true), TimeSpan.FromMilliseconds(70));
    var expired = await timeout.AuthorizeAsync(_ => { }, default);
    Check(expired.Result?.Outcome == DeviceCodeOutcome.Expired && expired.FallbackReasonKey is null, "Timeout never falls back");
}

static async Task UnavailableAsync()
{
    using var http = new HttpClient(new MockHandler(_ => throw new Exception("Unexpected HTTP request")));
    var flow = new BrowserCodeFlow(http, (_, _) => Task.FromResult(false));
    var missingBrowser = await flow.AuthorizeAsync(_ => throw new Exception("No browser prompt expected"), default);
    Check(missingBrowser.Result is null && missingBrowser.FallbackReasonKey == "auth.fallback.browserUnavailable", "Browser launch failure enables fallback");
    var configured = Environment.GetEnvironmentVariable(AuthConstants.ClientIdVariable);
    // A non-GUID id is what "no application of our own" looks like: clearing the
    // variable no longer produces that state, because the shipped default is now
    // DUSMP's own registration.
    Environment.SetEnvironmentVariable(AuthConstants.ClientIdVariable, "00000000402b5328");
    try
    {
        Check(!AuthConstants.HasBrowserClient, "A non-GUID id must not count as a browser client");
        var missingClient = await flow.AuthorizeAsync(_ => { }, default);
        Check(missingClient.Result is null && missingClient.FallbackReasonKey == "auth.fallback.notConfigured", "Legacy registration cannot use loopback");
    }
    finally { Environment.SetEnvironmentVariable(AuthConstants.ClientIdVariable, configured); }

}

static async Task RefreshErrorsAsync()
{
    using var rejected = new HttpClient(new MockHandler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}"))));
    Check(await new DeviceCodeFlow(rejected).RefreshAsync("mock-refresh") is null, "Revoked refresh token requires sign-in");
    using var badConfig = new HttpClient(new MockHandler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\"}"))));
    try
    {
        await new DeviceCodeFlow(badConfig).RefreshAsync("mock-refresh");
        throw new Exception("Configuration failure must not erase a valid session");
    }
    catch (LauncherException ex) { Check(ex.ErrorKey == "error.auth.configuration", "Configuration failure preserves session"); }
}

static Dictionary<string, string> Parse(string query) => query.Split('&').Select(x => x.Split('=', 2))
    .ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
}


sealed class StubTokenStore(string refreshToken) : ISecureTokenStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal)
    {
        [AuthConstants.RefreshTokenStoreKey] = refreshToken,
        ["microsoft.refreshTokenClientId"] = AuthConstants.ClientId,
    };
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value, CancellationToken ct = default) { _values[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken ct = default) { _values.Remove(key); return Task.CompletedTask; }
}
