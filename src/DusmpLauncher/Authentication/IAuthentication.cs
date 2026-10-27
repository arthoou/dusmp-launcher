using DusmpLauncher.Bridge;
using DusmpLauncher.Models;

namespace DusmpLauncher.Authentication;

/// <summary>
/// A session ready to launch the game. Holds the access token, so it stays in
/// C# and is never serialised to the UI — the UI only sees <see cref="Profile"/>.
/// </summary>
public sealed record GameSession(
    PlayerProfile Profile,
    string AccessToken,
    string Uuid,
    string Username,
    string? Xuid);

/// <summary>Microsoft browser sign-in with device authorization as a fallback.</summary>
public interface IMicrosoftAuth
{
    /// <summary>
    /// Starts an interactive sign-in. <paramref name="onPrompt"/> carries the
    /// browser waiting state or fallback verification link; completion occurs in
    /// their browser, or faults with a translation key.
    /// </summary>
    Task<GameSession> SignInAsync(
        Action<DeviceCodePrompt> onPrompt,
        CancellationToken ct = default,
        bool useDeviceCode = false);

    /// <summary>
    /// Refreshes a stored account with no user interaction. Returns null when the
    /// refresh token is gone or rejected, which means "ask the player again".
    /// </summary>
    /// <summary>Resumes a stored account, or the last one when no id is given.</summary>
    Task<GameSession?> SignInSilentlyAsync(string? accountId = null, CancellationToken ct = default);

    /// <summary>Forgets one account's stored token.</summary>
    Task ForgetAsync(string accountId, CancellationToken ct = default);

    Task SignOutAsync(CancellationToken ct = default);
}

/// <summary>Offline accounts: a validated name and a stable offline UUID, no auth.</summary>
public interface IOfflineAuth
{
    /// <summary>Null when the name is acceptable, otherwise a translation key.</summary>
    string? ValidateUsername(string username);

    GameSession CreateSession(string username);
}

/// <summary>
/// Stores refresh tokens using OS-provided protection: DPAPI on Windows, and on
/// Linux an AES key in a 0600 file under the launcher's own directory. Never
/// writes a token into <c>config.json</c>.
/// </summary>
public interface ISecureTokenStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}
