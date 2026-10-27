using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using DusmpLauncher.Configuration;

namespace DusmpLauncher.Downloads;

/// <summary>Bounded streaming downloads, verified before atomically replacing installed files.</summary>
public sealed class DownloadManager : IDownloadManager
{
    private readonly HttpClient _http;
    private readonly IConfigStore _config;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    public DownloadManager(HttpClient http, IConfigStore config) { _http = http; _config = config; }
    public event Action<DownloadStats>? StatsChanged;

    public async Task<DownloadResult> RunAsync(IReadOnlyList<DownloadJob> jobs, CancellationToken ct = default)
    {
        var failures = new ConcurrentBag<DownloadFailure>();
        var completed = new ConcurrentBag<DownloadJob>();
        var positions = new long[jobs.Count];
        var total = jobs.Any(j => j.ExpectedSize <= 0) ? 0 : jobs.Sum(j => j.ExpectedSize);
        var cpuLimit = Math.Clamp(Environment.ProcessorCount * 2, 2, 16);
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var cap = Math.Min(Math.Clamp(_config.Current.Game.DownloadConcurrency, 1, 16), cpuLimit);
        if (memory > 0 && memory < 2L * 1024 * 1024 * 1024) cap = Math.Min(cap, 3);
        var limit = cap;
        var successes = 0;
        var next = -1;
        long wireBytes = 0;
        string? currentFile = null;
        var timer = Stopwatch.StartNew();
        var samples = new Queue<(long At, long Bytes)>();
        var notifyLock = new object();

        void Notify()
        {
            lock (notifyLock)
            {
                var now = timer.ElapsedMilliseconds;
                var received = Interlocked.Read(ref wireBytes);
                samples.Enqueue((now, received));
                while (samples.Count > 1 && now - samples.Peek().At > 4000) samples.Dequeue();
                var first = samples.Peek();
                var speed = now - first.At >= 500 ? Math.Max(0, (received - first.Bytes) * 1000d / (now - first.At)) : (double?)null;
                long retained = 0;
                for (var i = 0; i < positions.Length; i++) retained += Interlocked.Read(ref positions[i]);
                StatsChanged?.Invoke(new(jobs.Count, completed.Count, failures.Count, total, retained, speed, currentFile));
            }
        }

        using var reporting = new CancellationTokenSource();
        var reportTask = Task.Run(async () =>
        {
            using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            try { while (await ticker.WaitForNextTickAsync(reporting.Token).ConfigureAwait(false)) Notify(); }
            catch (OperationCanceledException) when (reporting.IsCancellationRequested) { }
        });
        Notify();
        try
        {
            // A fixed number of workers avoids allocating a task for every file in a large pack.
            await Task.WhenAll(Enumerable.Range(0, Math.Min(cap, jobs.Count)).Select(async worker =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // Retrying workers reduce pressure; successful transfers gradually recover it.
                        while (worker >= Volatile.Read(ref limit)) await Task.Delay(100, ct).ConfigureAwait(false);
                        var index = Interlocked.Increment(ref next);
                        if (index >= jobs.Count) break;
                        var job = jobs[index];
                        currentFile = job.RelativePath;
                        try
                        {
                            await FetchAsync(job, (position, received) =>
                            {
                                Interlocked.Exchange(ref positions[index], position);
                                Interlocked.Add(ref wireBytes, received);
                            }, () =>
                            {
                                Interlocked.Exchange(ref limit, Math.Max(1, Volatile.Read(ref limit) / 2));
                                Interlocked.Exchange(ref successes, 0);
                            }, ct).ConfigureAwait(false);
                            completed.Add(job);
                            if (Interlocked.Increment(ref successes) >= Volatile.Read(ref limit) * 2)
                            {
                                Interlocked.Exchange(ref successes, 0);
                                Interlocked.Exchange(ref limit, Math.Min(cap, Volatile.Read(ref limit) + 1));
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                        catch (Exception ex) { failures.Add(new(job, "error.download.failed", ex.Message)); }
                        // Wake paused workers when the queue is empty, even after the last file failed.
                        if (Volatile.Read(ref next) >= jobs.Count - 1) Interlocked.Exchange(ref limit, cap);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            })).ConfigureAwait(false);
        }
        finally
        {
            reporting.Cancel();
            await reportTask.ConfigureAwait(false);
            currentFile = null;
            Notify();
        }
        return new(completed.ToArray(), failures.ToArray(), ct.IsCancellationRequested);
    }

    private async Task FetchAsync(DownloadJob job, Action<long, int> progress, Action backOff, CancellationToken ct)
    {
        if (!HashUtil.IsWellFormedSha256(job.ExpectedSha256))
            throw new InvalidDataException("The manifest contains an invalid SHA-256 hash.");
        Directory.CreateDirectory(Path.GetDirectoryName(job.AbsolutePath)!);
        if (await MatchesAsync(job.AbsolutePath, job, ct).ConfigureAwait(false))
        {
            progress(new FileInfo(job.AbsolutePath).Length, 0);
            return;
        }

        var part = job.AbsolutePath + ".part";
        var identityFile = part + ".sha256";
        // A changed manifest must not append the new version to a previous version's partial file.
        if (File.Exists(part) && (!File.Exists(identityFile) ||
            !string.Equals((await File.ReadAllTextAsync(identityFile, ct).ConfigureAwait(false)).Trim(), job.ExpectedSha256, StringComparison.OrdinalIgnoreCase)))
            File.Delete(part);
        await File.WriteAllTextAsync(identityFile, job.ExpectedSha256, ct).ConfigureAwait(false);
        string? lastError = null;
        foreach (var url in job.Urls.Distinct(StringComparer.Ordinal))
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (have > 0 && (job.ExpectedSize <= 0 || have == job.ExpectedSize) &&
                    await MatchesAsync(part, job, ct).ConfigureAwait(false))
                {
                    File.Move(part, job.AbsolutePath, true);
                    File.Delete(identityFile);
                    progress(have, 0);
                    return;
                }
                if (job.ExpectedSize > 0 && have >= job.ExpectedSize && have > 0)
                {
                    File.Delete(part);
                    have = 0;
                }
                progress(have, 0);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // Range offsets must refer to the original bytes, not transparent gzip content.
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(StallTimeout);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
                {
                    File.Delete(part);
                    progress(0, 0);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.Unit != "bytes" || range.From != have ||
                        (job.ExpectedSize > 0 && range.Length != job.ExpectedSize))
                        throw new InvalidDataException("The server returned an invalid download range.");
                }
                else if (have > 0)
                {
                    // Server ignored Range. Start clean and subtract the discarded partial bytes.
                    have = 0;
                    progress(0, 0);
                }

                var buffer = ArrayPool<byte>.Shared.Rent(HashUtil.DefaultBufferSize);
                try
                {
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    // End this scope BEFORE hashing and renaming: FileShare.None otherwise locks out
                    // the verifier, causing the former failure immediately after a complete download.
                    await using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        while (true)
                        {
                            timeout.CancelAfter(StallTimeout);
                            var count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                            if (count == 0) break;
                            if (job.ExpectedSize > 0 && have + count > job.ExpectedSize)
                                throw new InvalidDataException("Downloaded file exceeds the manifest size.");
                            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                            have += count;
                            progress(have, count);
                        }
                        await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                // Disk verification is cancellable but not subject to a network inactivity timer.
                if (job.ExpectedSize > 0 && have < job.ExpectedSize)
                    throw new IOException("The connection ended before the download completed.");
                if (!await MatchesAsync(part, job, ct).ConfigureAwait(false))
                    throw new InvalidDataException("Downloaded file SHA-256 does not match the manifest.");
                File.Move(part, job.AbsolutePath, true);
                File.Delete(identityFile);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                lastError = ex.Message;
                backOff();
                if (ex is InvalidDataException)
                {
                    if (File.Exists(part)) File.Delete(part);
                    progress(0, 0);
                }
                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(350 * (1 << attempt)), ct).ConfigureAwait(false);
            }
        }
        throw new IOException(lastError ?? "No usable download URL was present in the manifest.");
    }

    private static async Task<bool> MatchesAsync(string path, DownloadJob job, CancellationToken ct)
    {
        if (!File.Exists(path) || (job.ExpectedSize > 0 && new FileInfo(path).Length != job.ExpectedSize)) return false;
        var hash = await HashUtil.ComputeFileSha256Async(path, ct: ct).ConfigureAwait(false);
        return HashUtil.HashesEqual(job.ExpectedSha256, hash);
    }
}
