namespace DusmpLauncher.Bridge;

/// <summary>
/// What the UI shows while a Microsoft sign-in is pending. Carries
/// the user code only — the device code itself stays in C# and never crosses
/// the bridge, because it is the credential that redeems the token.
/// </summary>
public sealed record DeviceCodePrompt(
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    string? VerificationUriComplete = null,
    string Mode = "deviceCode",
    string? BrowserUri = null,
    string? FallbackReasonKey = null,
    bool BrowserOpened = false);

/// <summary>Modes the account screen can be asked to render.</summary>
public static class SignInMode
{
    /// <summary>Code shown in the launcher, entered on microsoft.com/link.</summary>
    public const string DeviceCode = "deviceCode";

    /// <summary>Loopback receiver; the launcher continues on its own.</summary>
    public const string Browser = "browser";


}

/// <summary>Terminal outcome of a device-code sign-in, for the UI's benefit.</summary>
public enum DeviceCodeOutcome
{
    Pending = 0,
    Succeeded = 1,
    Declined = 2,
    Expired = 3,
    Cancelled = 4,
    Failed = 5,
}
