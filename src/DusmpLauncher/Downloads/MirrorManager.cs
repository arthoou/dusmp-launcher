using System.Diagnostics;
using DusmpLauncher.Models;

namespace DusmpLauncher.Downloads;

/// <summary>
/// Turns a manifest entry into the ordered list of URLs to try. The ordering is
/// fully deterministic: two players with the same manifest, the same region and
/// the same failures get the same list, which is what makes a mirror problem
/// reproducible from a log instead of a coin flip.
/// </summary>
public sealed class MirrorManager : IMirrorManager
{
    /// <summary>
    /// How long a host stays demoted after it failed. Long enough that a run of
    /// a few thousand files stops leading with a dead CDN, short enough that a
    /// brief blip does not cost the player the fastest mirror for the whole run.
    /// </summary>
    public static readonly TimeSpan DefaultFailureWindow = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();

    /// <summary>Host to the <see cref="Stopwatch"/> timestamp of its last failure.</summary>
    private readonly Dictionary<string, long> _failures = new(StringComparer.OrdinalIgnoreCase);

    private readonly long _failureWindowTicks;

    private string[] _cdnBases = Array.Empty<string>();

    /// <summary>
    /// Relative path to the public-CDN URLs found for it. Empty until the
    /// external resolver has answered, which is why nothing in this class ever
    /// waits on it: a missing entry simply means "download it from DUSMP".
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _externalMirrors = EmptyExternalMirrors;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyExternalMirrors =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <param name="cdnBases">
    /// Initial CDN bases, best first. Usually empty at construction because the
    /// region selector has not run yet; <see cref="SetCdnBases"/> fills them in.
    /// </param>
    /// <param name="failureWindow">Overrides <see cref="DefaultFailureWindow"/>, mainly for tests.</param>
    public MirrorManager(IReadOnlyList<string>? cdnBases = null, TimeSpan? failureWindow = null)
    {
        var window = failureWindow ?? DefaultFailureWindow;
        if (window <= TimeSpan.Zero)
            window = DefaultFailureWindow;

        // Monotonic ticks, not wall clock: a laptop resuming from suspend or an
        // NTP correction must not make a five-minute-old failure look like it
        // happened in the future, which would pin a dead host at the top forever.
        _failureWindowTicks = (long)(Stopwatch.Frequency * window.TotalSeconds);

        if (cdnBases is { Count: > 0 })
            SetCdnBases(cdnBases);
    }

    /// <summary>CDN bases currently in use, in order. Exposed for the log pane.</summary>
    public IReadOnlyList<string> CdnBases
    {
        get
        {
            lock (_gate)
                return _cdnBases;
        }
    }

    /// <inheritdoc />
    public void SetCdnBases(IReadOnlyList<string> bases)
    {
        ArgumentNullException.ThrowIfNull(bases);

        var cleaned = new List<string>(bases.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in bases)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var trimmed = candidate.Trim().TrimEnd('/');
            if (!IsHttpUrl(trimmed) || !seen.Add(trimmed))
                continue;

            cleaned.Add(trimmed);
        }

        lock (_gate)
            _cdnBases = cleaned.ToArray();
    }

    /// <inheritdoc />
    public void SetExternalMirrors(IReadOnlyDictionary<string, IReadOnlyList<string>> mirrors)
    {
        ArgumentNullException.ThrowIfNull(mirrors);

        // Copied rather than stored by reference: the resolver builds this map on
        // a background task, and a snapshot is what keeps ResolveUrls free of any
        // assumption about who else still holds the original.
        var cleaned = new Dictionary<string, IReadOnlyList<string>>(mirrors.Count, StringComparer.Ordinal);

        foreach (var (path, urls) in mirrors)
        {
            if (string.IsNullOrWhiteSpace(path) || urls is null || urls.Count == 0)
                continue;

            var kept = new List<string>(urls.Count);
            foreach (var url in urls)
            {
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var trimmed = url.Trim();
                // Absolute only: an external mirror is a foreign host's URL, so
                // joining it to a DUSMP CDN base would be meaningless.
                if (IsHttpUrl(trimmed) && !kept.Contains(trimmed, StringComparer.Ordinal))
                    kept.Add(trimmed);
            }

            if (kept.Count > 0)
                cleaned[path.Replace('\\', '/').Trim()] = kept;
        }

        lock (_gate)
            _externalMirrors = cleaned;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ResolveUrls(ModFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        string[] bases;
        IReadOnlyDictionary<string, IReadOnlyList<string>> external;
        lock (_gate)
        {
            bases = _cdnBases;
            external = _externalMirrors;
        }

        // Small initial capacity: the overwhelmingly common shape is one absolute
        // URL, or one relative path against two or three CDN bases.
        var ordered = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Public CDNs first. They serve the very same artifact — matched by hash,
        // and re-verified by SHA-256 after download like every other source — so
        // leading with them saves DUSMP bandwidth without weakening anything. If
        // one is unreachable the manifest URL is still right behind it, and a
        // host that just failed gets demoted below by Demote().
        if (external.Count > 0 && external.TryGetValue(file.Path, out var mirrors))
        {
            foreach (var mirror in mirrors)
                AddCandidate(mirror, bases, ordered, seen);
        }

        AddCandidate(file.Url, bases, ordered, seen);

        foreach (var mirror in file.Mirrors)
            AddCandidate(mirror, bases, ordered, seen);

        return Demote(ordered);
    }

    /// <inheritdoc />
    public void ReportFailure(string url)
    {
        var host = HostOf(url);
        if (host is null)
            return;

        var now = Stopwatch.GetTimestamp();
        lock (_gate)
            _failures[host] = now;
    }

    /// <summary>
    /// Expands one manifest URL into candidates. An absolute URL is used exactly
    /// as written — the manifest author picked that host on purpose — while a
    /// relative path is fanned out across every CDN base so a region with three
    /// edges gives three chances at the same file.
    /// </summary>
    private static void AddCandidate(string? candidate, string[] bases, List<string> into, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        var value = candidate.Trim();

        if (IsHttpUrl(value))
        {
            if (seen.Add(value))
                into.Add(value);
            return;
        }

        foreach (var baseUrl in bases)
        {
            var joined = Join(baseUrl, value);
            if (IsHttpUrl(joined) && seen.Add(joined))
                into.Add(joined);
        }
    }

    /// <summary>
    /// Joins a CDN base and a relative path by hand rather than through
    /// <c>new Uri(base, relative)</c>, because Uri resolution drops the last
    /// segment of a base that has no trailing slash — turning
    /// <c>https://cdn/packs/v3</c> + <c>mods/a.jar</c> into <c>https://cdn/packs/mods/a.jar</c>,
    /// a 404 that only shows up on whichever region forgot the slash.
    /// </summary>
    private static string Join(string baseUrl, string relative)
        => string.Concat(baseUrl.TrimEnd('/'), "/", relative.Replace('\\', '/').TrimStart('/'));

    /// <summary>
    /// Stable partition that moves URLs on recently failed hosts to the end
    /// while preserving the relative order of both groups. A demoted host is
    /// still tried — it may have been a single bad object, not a dead edge — it
    /// just stops being the one that costs every remaining file a timeout.
    /// </summary>
    private List<string> Demote(List<string> urls)
    {
        if (urls.Count < 2)
            return urls;

        var now = Stopwatch.GetTimestamp();
        List<string>? sunk = null;
        var kept = new List<string>(urls.Count);

        foreach (var url in urls)
        {
            if (HasRecentFailure(url, now))
                (sunk ??= new List<string>()).Add(url);
            else
                kept.Add(url);
        }

        if (sunk is null)
            return urls;

        kept.AddRange(sunk);
        return kept;
    }

    private bool HasRecentFailure(string url, long now)
    {
        var host = HostOf(url);
        if (host is null)
            return false;

        lock (_gate)
        {
            if (!_failures.TryGetValue(host, out var failedAt))
                return false;

            if (now - failedAt <= _failureWindowTicks)
                return true;

            // Prune on read: the set of hosts is tiny and a run touches every
            // entry, so this is cheaper than keeping a sweep timer alive.
            _failures.Remove(host);
            return false;
        }
    }

    private static string? HostOf(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            ? uri.Host
            : null;

    /// <summary>
    /// Only http and https are downloadable. Filtering here keeps every other
    /// scheme the manifest might carry — <c>app://</c> placeholders, a stray
    /// <c>file://</c> from a packaging script — out of the download path.
    /// </summary>
    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
