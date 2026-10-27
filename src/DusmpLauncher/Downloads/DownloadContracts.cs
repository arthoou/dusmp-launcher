using DusmpLauncher.Models;

namespace DusmpLauncher.Downloads;

/// <summary>One file to fetch, already resolved to concrete candidate URLs.</summary>
public sealed record DownloadJob(
    string RelativePath,
    string AbsolutePath,
    long ExpectedSize,
    string ExpectedSha256,
    IReadOnlyList<string> Urls);

/// <summary>Aggregate state of a download run, recomputed as workers report in.</summary>
public sealed record DownloadStats(
    int FilesTotal,
    int FilesCompleted,
    int FilesFailed,
    long BytesTotal,
    long BytesCompleted,
    double? BytesPerSecond,
    string? CurrentFile);

/// <summary>Why a job could not be completed, after every mirror was tried.</summary>
public sealed record DownloadFailure(DownloadJob Job, string ErrorKey, string Detail);

public sealed record DownloadResult(
    IReadOnlyList<DownloadJob> Completed,
    IReadOnlyList<DownloadFailure> Failed,
    bool Cancelled)
{
    public bool Success => Failed.Count == 0 && !Cancelled;
}

/// <summary>
/// Parallel, resumable, hash-verified downloader. Progress is always measured
/// from real byte counters; nothing here ever reports a synthetic percentage.
/// </summary>
public interface IDownloadManager
{
    /// <summary>Fires on a short timer while a run is active, never per byte.</summary>
    event Action<DownloadStats>? StatsChanged;

    /// <summary>
    /// Runs every job, honouring the concurrency limit, retrying and falling
    /// back across mirrors. Completes normally on cancellation with
    /// <see cref="DownloadResult.Cancelled"/> set, leaving <c>.part</c> files in
    /// place so the next run resumes.
    /// </summary>
    Task<DownloadResult> RunAsync(IReadOnlyList<DownloadJob> jobs, CancellationToken ct = default);
}

/// <summary>Resolves a manifest file to the URLs to try, best first.</summary>
public interface IMirrorManager
{
    /// <summary>Candidate URLs for a file: region CDN bases first, then manifest mirrors.</summary>
    IReadOnlyList<string> ResolveUrls(ModFile file);

    /// <summary>Replaces the CDN bases after region selection or an API hint.</summary>
    void SetCdnBases(IReadOnlyList<string> bases);

    /// <summary>
    /// Registers public-CDN URLs — Modrinth, CurseForge — keyed by
    /// <see cref="ModFile.Path"/>, to be tried ahead of anything DUSMP serves.
    /// Only files that were positively identified on a public CDN appear here, so
    /// the rest keep downloading exactly as before. Replaces any previous set,
    /// because a new install plan may resolve to entirely different files.
    /// </summary>
    void SetExternalMirrors(IReadOnlyDictionary<string, IReadOnlyList<string>> mirrors);

    /// <summary>
    /// Records that a host just failed, so later files in the same run stop
    /// leading with it. Purely in-memory and reset each launch.
    /// </summary>
    void ReportFailure(string url);
}
