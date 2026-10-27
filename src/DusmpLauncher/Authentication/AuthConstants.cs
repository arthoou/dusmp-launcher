namespace DusmpLauncher.Authentication;

/// <summary>
/// Every endpoint, identifier and template the Microsoft sign-in touches.
/// Each one has a compiled-in default and an environment override, so a test
/// build can be pointed at a different Azure app or a local mock without a
/// rebuild — and so nothing here has to be duplicated in the UI or in config.
/// </summary>
public static class AuthConstants
{
    /// <summary>
    /// The application id every third-party launcher signs in with.
    ///
    /// This is Minecraft's own built-in identifier, and it is what msmc — the
    /// library behind most Electron launchers — uses unchanged. It is already
    /// allow-listed by Mojang, so it works without waiting for an approval that
    /// a brand-new registration still needs.
    ///
    /// It only works on the login.live.com endpoints below, with the desktop
    /// redirect and the XboxLive.signin scope. A loopback redirect is refused
    /// for it, which is why <see cref="HasBrowserClient"/> also decides which
    /// sign-in flow runs.
    /// </summary>
    public const string DefaultClientId = "00000000402b5328";

    /// <summary>
    /// DUSMP's own registration, kept here for the day Mojang allow-lists it.
    /// Not the default: a new application id is refused by Minecraft Services
    /// until that approval lands, while the id above works now.
    /// Set it through the API's <c>microsoftClientId</c> or
    /// <see cref="ClientIdVariable"/> to switch over without a rebuild.
    /// </summary>
    public const string DusmpClientId = "ebf296cf-8547-427e-afb9-a7b12962ed76";

    /// <summary>
    /// Legacy Xbox Live scope. Deliberately not the modern
    /// <c>XboxLive.signin offline_access</c> pair: that pair belongs to Azure AD
    /// v2 applications, and the live.com device endpoint below rejects it.
    /// </summary>
    public const string DefaultScope = "service::user.auth.xboxlive.com::MBI_SSL";

    /// <summary>Device authorization endpoint. Verified by real request; returns <c>user_code</c> and <c>device_code</c>.</summary>
    public const string DefaultDeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";

    /// <summary>Token endpoint, used both for device-code polling and for refresh.</summary>
    public const string DefaultTokenUrl = "https://login.live.com/oauth20_token.srf";

    /// <summary>
    /// Interactive authorize endpoint. Unused by the device-code flow itself,
    /// but kept here so nothing else in the launcher invents its own copy if a
    /// browser-based fallback is ever added.
    /// </summary>
    public const string DefaultAuthorizeUrl = "https://login.live.com/oauth20_authorize.srf";

    public const string DesktopAuthorizeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    public const string DesktopTokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    public const string DesktopDeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    public const string DesktopScope = "XboxLive.signin offline_access";

    /// <summary>
    /// Head render, keyed by UUID. A third-party renderer is used because the
    /// launcher has no image pipeline of its own, and because an unknown UUID
    /// (an offline account) still renders the default skin, which is exactly the
    /// placeholder head the account screen wants.
    /// </summary>
    public const string DefaultAvatarUrlTemplate = "https://crafatar.com/avatars/{uuid}?size=64&overlay";

    /// <summary>Full body render of the same skin, for the larger account card.</summary>
    public const string DefaultSkinUrlTemplate = "https://crafatar.com/renders/body/{uuid}?overlay";

    public const string ClientIdVariable = "DUSMP_MS_CLIENT_ID";
    public const string DeviceCodeUrlVariable = "DUSMP_MS_DEVICECODE_URL";
    public const string TokenUrlVariable = "DUSMP_MS_TOKEN_URL";
    public const string AuthorizeUrlVariable = "DUSMP_MS_AUTHORIZE_URL";
    public const string ScopeVariable = "DUSMP_MS_SCOPE";
    public const string AvatarUrlTemplateVariable = "DUSMP_AVATAR_URL_TEMPLATE";
    public const string SkinUrlTemplateVariable = "DUSMP_SKIN_URL_TEMPLATE";

    /// <summary>OAuth 2.0 device authorization grant type (RFC 8628).</summary>
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    public const string RefreshTokenGrantType = "refresh_token";

    /// <summary>What the start request asks for; the live.com endpoint keys off this.</summary>
    public const string DeviceCodeResponseType = "device_code";

    /// <summary>Key the Microsoft refresh token lives under in <see cref="ISecureTokenStore"/>.</summary>
    public const string RefreshTokenStoreKey = "microsoft.refreshToken";

    /// <summary>
    /// Placeholder token for offline sessions. Minecraft never validates it when
    /// the server is in offline mode, but CmlLib's <c>MSession.CheckIsValid()</c>
    /// rejects an empty one, so it has to be some non-empty value; this is the
    /// same literal CmlLib's own offline session uses.
    /// </summary>
    public const string OfflineAccessToken = "access_token";

    /// <summary>Placeholder in the avatar and skin templates, replaced by the account UUID.</summary>
    public const string UuidPlaceholder = "{uuid}";

    /// <summary>Set once from the API response; the env override still wins.</summary>
    private static string? _clientIdFromApi;

    /// <summary>
    /// Adopts the client id the API serves. Ignores anything that is not a GUID:
    /// only a real Entra application id enables the browser flow, and a bad value
    /// would silently push every player back onto the built-in id.
    /// </summary>
    public static void UseApiClientId(string? clientId)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && Guid.TryParse(clientId.Trim(), out var parsed) && parsed != Guid.Empty)
            _clientIdFromApi = clientId.Trim();
    }

    public static string ClientId
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(ClientIdVariable);
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return _clientIdFromApi ?? DefaultClientId;
        }
    }

    /// <summary>A GUID identifies the launcher's own Entra public desktop app.</summary>
    public static bool HasBrowserClient => Guid.TryParse(ClientId, out var parsed) && parsed != Guid.Empty;

    /// <summary>
    /// The modern scope, on both authorities.
    ///
    /// Copied from msmc, the library every Electron launcher uses, because it is
    /// the combination proven to work today: the built-in client id on the
    /// <c>login.live.com</c> endpoints, but asking for <c>XboxLive.signin</c>
    /// rather than the old <c>MBI_SSL</c> service scope. Pairing the built-in id
    /// with MBI_SSL is what this launcher did before, and Xbox Live has no use
    /// for the ticket that produces.
    /// </summary>
    public static string Scope => Resolve(ScopeVariable, DesktopScope);

    /// <summary>
    /// Always <c>d=</c>. Both authorities now issue the same kind of token for
    /// <see cref="Scope"/>, and msmc sends <c>d=</c> for exactly this flow.
    /// </summary>
    public static string XboxTicketPrefix => "d=";

    public static string DeviceCodeUrl => ResolveUrl(DeviceCodeUrlVariable, HasBrowserClient ? DesktopDeviceCodeUrl : DefaultDeviceCodeUrl);

    public static string TokenUrl => ResolveUrl(TokenUrlVariable, HasBrowserClient ? DesktopTokenUrl : DefaultTokenUrl);

    public static string AuthorizeUrl => ResolveUrl(AuthorizeUrlVariable, HasBrowserClient ? DesktopAuthorizeUrl : DefaultAuthorizeUrl);

    public static string AvatarUrlTemplate => ResolveUrl(AvatarUrlTemplateVariable, DefaultAvatarUrlTemplate);

    public static string SkinUrlTemplate => ResolveUrl(SkinUrlTemplateVariable, DefaultSkinUrlTemplate);

    /// <summary>Head render URL for a UUID in either dashed or undashed form.</summary>
    public static string BuildAvatarUrl(string uuid) => Fill(AvatarUrlTemplate, uuid);

    /// <summary>Full body render URL for a UUID in either dashed or undashed form.</summary>
    public static string BuildSkinUrl(string uuid) => Fill(SkinUrlTemplate, uuid);

    private static string Fill(string template, string uuid) =>
        template.Replace(UuidPlaceholder, Uri.EscapeDataString(uuid), StringComparison.Ordinal);

    private static string Resolve(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>
    /// Same as <see cref="Resolve"/>, but an override that is not an absolute
    /// http(s) URL is ignored rather than trusted: an environment variable must
    /// not be able to redirect a token POST to something that is not a URL, or
    /// to a scheme that will not carry the request at all.
    /// </summary>
    private static string ResolveUrl(string variable, string fallback)
    {
        var value = Resolve(variable, fallback);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return fallback;

        return uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback) ? value : fallback;
    }
}
