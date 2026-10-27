namespace DusmpLauncher.Configuration;

/// <summary>
/// Persists <see cref="LauncherConfig"/>. Writes are atomic and serialised, so a
/// crash or two concurrent saves cannot leave the file unparseable and push the
/// player back through onboarding.
/// </summary>
public interface IConfigStore
{
    LauncherConfig Current { get; }

    /// <summary>
    /// Loads from disk, or produces a fresh config on a first run. A corrupt file
    /// is moved aside rather than deleted, and reported through
    /// <see cref="LastRecoveryKey"/> instead of throwing.
    /// </summary>
    Task<LauncherConfig> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Translation key set when the last load had to recover from damage.</summary>
    string? LastRecoveryKey { get; }
}
