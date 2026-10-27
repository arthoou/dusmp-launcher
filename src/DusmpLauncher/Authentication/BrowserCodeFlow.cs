using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DusmpLauncher.Bridge;
using DusmpLauncher.Core;
using XboxAuthNet.OAuth;

namespace DusmpLauncher.Authentication;

/// <summary>Desktop OAuth authorization code flow, with PKCE and a temporary loopback receiver.</summary>
public sealed class BrowserCodeFlow
{
    private readonly HttpClient _http;
    private readonly Func<Uri, CancellationToken, Task<bool>> _openBrowser;
    private readonly TimeSpan _timeout;

    public BrowserCodeFlow(HttpClient http,
        Func<Uri, CancellationToken, Task<bool>>? openBrowser = null, TimeSpan? timeout = null)
    {
        _http = http;
        _openBrowser = openBrowser ?? OpenSystemBrowserAsync;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<BrowserCodeResult> AuthorizeAsync(Action<DeviceCodePrompt> onPrompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!AuthConstants.HasBrowserClient)
            return BrowserCodeResult.Unavailable("auth.fallback.notConfigured");

        // Bind before opening the browser. TcpListener avoids HTTP.sys URL ACL
        // requirements on Windows and never binds to a network-facing address.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try { listener.Start(8); }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        { return BrowserCodeResult.Unavailable("auth.fallback.callbackUnavailable"); }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://localhost:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var clientId = AuthConstants.ClientId;
        var scope = AuthConstants.Scope;
        var tokenUrl = AuthConstants.TokenUrl;
        var authorizeUri = new Uri(AuthConstants.AuthorizeUrl + "?" + EncodeForm(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["response_type"] = "code",
            ["redirect_uri"] = redirectUri, ["response_mode"] = "query",
            ["scope"] = scope, ["state"] = state, ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256", ["prompt"] = "select_account"
        }));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            if (!await _openBrowser(authorizeUri, deadline.Token).ConfigureAwait(false))
                return BrowserCodeResult.Unavailable("auth.fallback.browserUnavailable");

            onPrompt(new DeviceCodePrompt("", "", (int)_timeout.TotalSeconds,
                Mode: "browser", BrowserUri: authorizeUri.AbsoluteUri, BrowserOpened: true));
            var code = await ReceiveCodeAsync(listener, port, state, deadline.Token).ConfigureAwait(false);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["scope"] = scope, ["code"] = code,
                ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier
            });
            using var response = await _http.PostAsync(tokenUrl, content, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            var error = ReadString(json.RootElement, "error");
            if (!response.IsSuccessStatusCode || error is not null)
                return BrowserCodeResult.Finished(DeviceCodeResult.Failure(DeviceCodeOutcome.Failed,
                    OAuthErrorKey(error), $"Microsoft token exchange returned HTTP {(int)response.StatusCode}."));

            var token = MicrosoftOAuthResponse.FromHttpResponse(body, (int)response.StatusCode, response.ReasonPhrase);
            if (string.IsNullOrWhiteSpace(token.AccessToken))
                return BrowserCodeResult.Finished(DeviceCodeResult.Failure(DeviceCodeOutcome.Failed,
                    "error.auth.failed", "Microsoft token exchange returned no access token."));
            return BrowserCodeResult.Finished(DeviceCodeResult.Success(token));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return BrowserCodeResult.Finished(DeviceCodeResult.Failure(DeviceCodeOutcome.Cancelled, "error.auth.cancelled")); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { return BrowserCodeResult.Finished(DeviceCodeResult.Failure(DeviceCodeOutcome.Expired, "error.auth.expired")); }
        catch (LauncherException ex)
        { return BrowserCodeResult.Finished(DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, ex.ErrorKey, ex.Detail)); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or SocketException)
        { return BrowserCodeResult.Finished(DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, "error.auth.connection", "The sign-in connection failed.")); }
        catch (Exception ex) when (ex is JsonException or MicrosoftOAuthException)
        { return BrowserCodeResult.Finished(DeviceCodeResult.Failure(DeviceCodeOutcome.Failed, "error.auth.failed", "Microsoft returned an invalid token response.")); }
    }

    private static async Task<string> ReceiveCodeAsync(TcpListener listener, int port, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = client.GetStream();
            string? header;
            try { header = await ReadHeaderAsync(stream, requestTimeout.Token).ConfigureAwait(false); }
            catch (Exception ex) when ((ex is OperationCanceledException && !ct.IsCancellationRequested) || ex is IOException)
            { continue; }

            var lines = header?.Split("\r\n");
            var request = lines?.FirstOrDefault()?.Split(' ');
            var host = lines?.Skip(1).FirstOrDefault(x => x.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
            if (request is not { Length: 3 } || request[0] != "GET" ||
                host != $"localhost:{port}" || !request[1].StartsWith("/?", StringComparison.Ordinal))
            {
                await RespondAsync(stream, false, requestTimeout.Token).ConfigureAwait(false);
                continue;
            }

            var query = ParseQuery(request[1][2..]);
            var receivedState = query.GetValueOrDefault("state");
            if (receivedState is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedState), Encoding.ASCII.GetBytes(receivedState)))
            {
                await RespondAsync(stream, false, requestTimeout.Token).ConfigureAwait(false);
                throw new LauncherException("error.auth.state", "The callback did not match this sign-in attempt.");
            }
            if (query.TryGetValue("error", out var error))
            {
                await RespondAsync(stream, false, requestTimeout.Token).ConfigureAwait(false);
                throw new LauncherException(OAuthErrorKey(error), "Microsoft did not authorize this sign-in.");
            }
            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                await RespondAsync(stream, false, requestTimeout.Token).ConfigureAwait(false);
                throw new LauncherException("error.auth.failed", "The callback contained no authorization code.");
            }
            await RespondAsync(stream, true, requestTimeout.Token).ConfigureAwait(false);
            return code;
        }
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0) return null;
            length += count;
            var header = Encoding.ASCII.GetString(buffer, 0, length);
            if (header.Contains("\r\n\r\n", StringComparison.Ordinal)) return header;
        }
        return null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.Split('&'))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!values.TryAdd(key, value)) throw new LauncherException("error.auth.state", "The callback contained duplicate parameters.");
        }
        return values;
    }

    private static async Task RespondAsync(NetworkStream stream, bool accepted, CancellationToken ct)
    {
        // Never include a code, token, query string, or remote error text in HTML.
        var body = Encoding.UTF8.GetBytes(accepted ? BrowserMessages.ReturnToLauncher : BrowserMessages.SignInIncomplete);
        var status = accepted ? "200 OK" : "400 Bad Request";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    internal static string OAuthErrorKey(string? error) => error switch
    {
        "access_denied" or "authorization_declined" => "error.auth.declined",
        "invalid_client" or "unauthorized_client" or "invalid_scope" or "invalid_resource" => "error.auth.configuration",
        "server_error" or "temporarily_unavailable" => "error.auth.connection",
        _ => "error.auth.failed"
    };

    internal static async Task<bool> OpenSystemBrowserAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return false;
        if (OperatingSystem.IsWindows())
            return await TryOpenAsync(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, ct).ConfigureAwait(false);
        if (OperatingSystem.IsMacOS())
            return await TryOpenAsync(Command("open", uri.AbsoluteUri), ct).ConfigureAwait(false);

        // Native handlers work with GNOME, KDE and browsers installed via Flatpak.
        // ArgumentList keeps URL query parameters intact without a shell interpreting them.
        if (await TryOpenAsync(Command("xdg-open", uri.AbsoluteUri), ct).ConfigureAwait(false)) return true;
        return await TryOpenAsync(Command("gio", "open", uri.AbsoluteUri), ct).ConfigureAwait(false);
    }

    private static ProcessStartInfo Command(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<bool> TryOpenAsync(ProcessStartInfo start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(start);
            if (process is null) return start.UseShellExecute;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); return process.ExitCode == 0; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return true; }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { return false; }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static string EncodeForm(Dictionary<string, string> form) => string.Join("&", form.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record BrowserCodeResult(DeviceCodeResult? Result, string? FallbackReasonKey)
{
    public static BrowserCodeResult Unavailable(string reasonKey) => new(null, reasonKey);
    public static BrowserCodeResult Finished(DeviceCodeResult result) => new(result, null);
}

/// <summary>English browser callback copy, kept together and independent of OAuth data.</summary>
internal static class BrowserMessages
{
    private const string Start = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>DUSMP Launcher</title><style>body{background:#101010;color:#eee;font:16px system-ui;max-width:32rem;margin:18vh auto;padding:24px}h1{color:#dfbb6a;font-size:28px}</style>";
    public const string ReturnToLauncher = Start + "<h1>Return to DUSMP Launcher</h1><p>You can close this tab. Sign-in will continue in the launcher.</p></html>";
    public const string SignInIncomplete = Start + "<h1>Sign-in could not finish</h1><p>Return to DUSMP Launcher and try again.</p></html>";
}
