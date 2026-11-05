namespace DusmpLauncher.Models;

/// <summary>
/// The stages the loading screen walks through. The UI maps each to a
/// translation key; it never hardcodes wording.
/// </summary>
public enum LauncherStage
{
    Starting = 0,
    CheckingConnection = 1,
    ContactingApi = 2,
    CheckingLauncherVersion = 3,
    CheckingModpackVersion = 4,
    ChoosingRegion = 5,
    VerifyingInstallation = 6,
    PreparingModpack = 7,
    InstallingMinecraft = 8,
    DownloadingFiles = 9,
    VerifyingFiles = 10,
    Finishing = 11,
    Ready = 12,
    Failed = 13,
    LauncherUpToDate = 14,
    DownloadingLauncher = 15,
    VerifyingLauncher = 16,
    InstallingLauncher = 17,
}

/// <summary>
/// A real progress snapshot. Every number here is measured — the UI is never
/// given a synthetic percentage, and <see cref="Percent"/> is null while the
/// total is genuinely unknown.
/// </summary>
public sealed record ProgressSnapshot
{
    public LauncherStage Stage { get; init; }

    /// <summary>0–100, or null when the work has no known total yet.</summary>
    public double? Percent { get; init; }

    /// <summary>Relative path of the file in flight, for display.</summary>
    public string? CurrentFile { get; init; }

    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>Bytes per second over a short sliding window. Null before the first sample.</summary>
    public double? BytesPerSecond { get; init; }

    public int FilesCompleted { get; init; }
    public int FilesTotal { get; init; }

    /// <summary>Set only when <see cref="Stage"/> is <see cref="LauncherStage.Failed"/>.</summary>
    public string? ErrorKey { get; init; }
    public string? ErrorDetail { get; init; }

    public static ProgressSnapshot Initial { get; } = new() { Stage = LauncherStage.Starting };
}
