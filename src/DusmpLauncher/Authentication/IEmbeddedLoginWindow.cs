namespace DusmpLauncher.Authentication;

/// <summary>
/// Hosts the Microsoft sign-in page in a window the launcher owns, so the
/// authorization code can be read off the redirect instead of copied by hand.
///
/// Minecraft's built-in client id accepts exactly one redirect,
/// <c>https://login.live.com/oauth20_desktop.srf</c> — verified against the live
/// endpoint, along with the refusal of <c>http://localhost</c> and of any
/// DUSMP-owned https callback, and the Xbox Live rejection of the device-code
/// token. A redirect that cannot be listened on can only be watched, and Photino
/// exposes no navigation event, so the launcher brings its own window.
///
/// Implementations must degrade rather than fail: <see cref="IsSupported"/>
/// reports up front when the platform pieces are missing, and the caller then
/// uses another flow.
/// </summary>
public interface IEmbeddedLoginWindow
{
    /// <summary>
    /// True when this machine has what the implementation needs. False — never
    /// an exception — when it does not.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Opens <paramref name="authorizeUri"/> and returns the authorization code
    /// once the page redirects to <paramref name="redirectUri"/>.
    ///
    /// Returns null when the player closes the window, when the callback carries
    /// an error, or when the platform turns out to be unusable — every one of
    /// those means "fall back", never "crash".
    ///
    /// The code is a credential: implementations must not log the callback URL.
    /// </summary>
    Task<string?> CaptureCodeAsync(Uri authorizeUri, string redirectUri, CancellationToken ct);
}

/// <summary>Stand-in for platforms with no embedded window.</summary>
public sealed class UnsupportedLoginWindow : IEmbeddedLoginWindow
{
    public bool IsSupported => false;

    public Task<string?> CaptureCodeAsync(Uri authorizeUri, string redirectUri, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
