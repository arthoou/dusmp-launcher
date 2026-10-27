namespace DusmpLauncher.Core;

/// <summary>
/// A failure the player should see. Carries a translation key rather than a
/// message, so the UI shows it in the player's language, and a technical detail
/// for the log pane that is never translated and never contains a secret.
/// </summary>
public sealed class LauncherException : Exception
{
    public LauncherException(string errorKey, string? detail = null, Exception? inner = null)
        : base(detail ?? errorKey, inner)
    {
        ErrorKey = errorKey;
        Detail = detail;
    }

    public string ErrorKey { get; }
    public string? Detail { get; }
}
