using System.Text.Json;
using DusmpLauncher.Core;

namespace DusmpLauncher.Authentication;

/// <summary>
/// Authorization code flow against the legacy desktop redirect
/// (<c>login.live.com/oauth20_desktop.srf</c>) — the only redirect Minecraft's
/// built-in client id accepts. Verified against the live endpoint: localhost and
/// DUSMP-owned https callbacks are refused, and the device-code token is
/// rejected by Xbox Live with HTTP 400. The code is captured by
/// <see cref="IEmbeddedLoginWindow"/>, which watches the redirect.
/// </summary>
public sealed class DesktopRedirectFlow
{
    /// <summary>The only redirect the built-in client id will accept.</summary>
    public const string DesktopRedirectUri = "https://login.live.com/oauth20_desktop.srf";

    private readonly HttpClient _http;

    public DesktopRedirectFlow(HttpClient http) => _http = http;

    /// <summary>The page the sign-in window opens.</summary>
    public static Uri BuildAuthorizeUri() => new(
        AuthConstants.AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = AuthConstants.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = DesktopRedirectUri,
            ["scope"] = AuthConstants.Scope,
            ["prompt"] = "select_account",
        }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));

    /// <summary>Exchanges the authorization code for a Microsoft access token.</summary>
    public async Task<TokenPair> ExchangeAsync(string code, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthConstants.ClientId,
            ["code"] = code,
            ["redirect_uri"] = DesktopRedirectUri,
            ["grant_type"] = "authorization_code",
            ["scope"] = AuthConstants.Scope,
        });

        using var response = await _http.PostAsync(AuthConstants.TokenUrl, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var oauthError = ReadString(body, "error");
            throw new LauncherException(
                oauthError switch
                {
                    "invalid_grant" => "error.auth.expired",
                    "access_denied" => "error.auth.declined",
                    _ => "error.auth.failed",
                },
                // Never echo the body: a success body carries tokens.
                $"Token exchange returned HTTP {(int)response.StatusCode}"
                    + (oauthError is null ? "." : $" ({oauthError})."));
        }

        var accessToken = ReadString(body, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new LauncherException("error.auth.failed", "Token exchange returned no access token.");

        return new TokenPair(accessToken, ReadString(body, "refresh_token"));
    }

    private static string? ReadString(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    public sealed record TokenPair(string AccessToken, string? RefreshToken);
}
