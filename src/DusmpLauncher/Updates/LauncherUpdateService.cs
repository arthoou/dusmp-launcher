using System.Net;
using System.Security.Cryptography;
using System.Text;
using DusmpLauncher.Configuration;
using DusmpLauncher.Downloads;
using DusmpLauncher.Models;

namespace DusmpLauncher.Updates;

public interface ILauncherUpdateService
{
    Task<bool> CheckAsync(IProgress<ProgressSnapshot> progress, CancellationToken ct);
}

public sealed class LauncherUpdateException : Exception
{
    public string ErrorKey { get; }
    public LauncherUpdateException(string key, string? detail = null, Exception? inner = null) : base(detail, inner) => ErrorKey = key;
}

/// <summary>Runs before modpack startup. Only signed, official, platform-specific bytes reach the helper.</summary>
public sealed class LauncherUpdateService : ILauncherUpdateService, IDisposable
{
    private readonly HttpClient _http;
    private readonly IConfigStore _config;
    private readonly string _root;
    private readonly string _executable;
    private readonly string _version;
    private readonly string? _testKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const string ManifestPath = "/launcher/releases/stable.json";
    public bool CanContinue { get; private set; } = true;
    public event Action? ExitRequested;

    public LauncherUpdateService(IConfigStore config, string updateRoot, string executable, string version,
        HttpMessageHandler? handler = null, string? publicKeyForTests = null)
    {
        _config = config;
        _root = Path.GetFullPath(updateRoot);
        _executable = Path.GetFullPath(executable);
        _version = version;
        _testKey = publicKeyForTests;
        // No automatic redirect may silently move an executable request to a different host or HTTP.
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DusmpLauncher/{version}");
    }

    public async Task<bool> CheckAsync(IProgress<ProgressSnapshot> progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CanContinue = true;
            var current = ReleaseManifest.ParseVersion(_version);
            Directory.CreateDirectory(_root);
            var cachePath = Path.Combine(_root, "release-cache.json");
            ReleaseManifest? cached = null;
            if (File.Exists(cachePath))
            {
                try { cached = ReleaseManifest.Verify(await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false), _testKey); }
                catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException) { }
            }
            if (cached is not null) CanContinue = MayContinue(cached, current);
            progress.Report(new() { Stage = LauncherStage.CheckingLauncherVersion });
            string envelope;
            try { envelope = await FetchManifestAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // Offline use is automatic only after this exact installed version has
                // verified a signed release, with no newer known release left pending.
                if (cached is not null && CanContinue && current >= ReleaseManifest.ParseVersion(cached.Version)) return false;
                throw new LauncherUpdateException(CanContinue ? "error.launcher.updateUnavailable" : "error.launcher.updateRequired", null, ex);
            }

            ReleaseManifest release;
            try { release = ReleaseManifest.Verify(envelope, _testKey); }
            catch (CryptographicException ex) { throw new LauncherUpdateException("error.launcher.updateSignature", null, ex); }
            catch (InvalidDataException ex) { throw new LauncherUpdateException("error.launcher.updateManifest", null, ex); }

            if (cached is not null && ReleaseManifest.ParseVersion(release.Version) < ReleaseManifest.ParseVersion(cached.Version))
                throw new LauncherUpdateException("error.launcher.updateDowngrade");
            CanContinue = MayContinue(release, current);
            await AtomicWriteAsync(cachePath, envelope, ct).ConfigureAwait(false);
            if (ReleaseManifest.ParseVersion(release.Version) <= current)
            {
                progress.Report(new() { Stage = LauncherStage.LauncherUpToDate });
                return false;
            }
            if (UpdateTransaction.IsFailedVersion(_root, release.Version))
                throw new LauncherUpdateException("error.launcher.updateRecovered");
            if (!CanUpdateInPlace(_executable))
                throw new LauncherUpdateException("error.launcher.updatePermissions");

            ReleaseArtifact artifact;
            try { artifact = release.Select(ReleaseManifest.Os, ReleaseManifest.Arch); }
            catch (PlatformNotSupportedException ex) { throw new LauncherUpdateException("error.launcher.updatePlatform", null, ex); }
            var stageDir = Path.Combine(_root, "staging", release.Version, artifact.Os + "-" + artifact.Arch);
            Directory.CreateDirectory(stageDir);
            EnsureNoLinks(stageDir);
            var target = Path.Combine(stageDir, artifact.FileName);
            EnsureDiskSpace(stageDir, artifact.Size * 3);
            EnsureDiskSpace(Path.GetDirectoryName(_executable)!, artifact.Size * 3);
            progress.Report(new() { Stage = LauncherStage.DownloadingLauncher, CurrentFile = release.Version, TotalBytes = artifact.Size });
            var downloads = new DownloadManager(_http, _config);
            downloads.StatsChanged += stats => progress.Report(new()
            {
                Stage = LauncherStage.DownloadingLauncher,
                CurrentFile = release.Version,
                TotalBytes = stats.BytesTotal,
                DownloadedBytes = stats.BytesCompleted,
                BytesPerSecond = stats.BytesPerSecond,
                Percent = stats.BytesTotal > 0 ? Math.Clamp(100d * stats.BytesCompleted / stats.BytesTotal, 0, 100) : null,
            });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(20));
            try
            {
                var result = await downloads.RunAsync(new[] { new DownloadJob(artifact.FileName, target, artifact.Size,
                    artifact.Sha256, new[] { artifact.Url }) }, deadline.Token).ConfigureAwait(false);
                if (result.Cancelled) deadline.Token.ThrowIfCancellationRequested();
                if (!result.Success) throw new LauncherUpdateException("error.launcher.updateDownload");
                progress.Report(new() { Stage = LauncherStage.VerifyingLauncher, CurrentFile = release.Version });
                await ReleaseManifest.VerifyFileAsync(target, artifact, ct).ConfigureAwait(false);
                var plan = await UpdateTransaction.PrepareAsync(_executable, target, envelope, _root, ct).ConfigureAwait(false);
                progress.Report(new() { Stage = LauncherStage.InstallingLauncher, CurrentFile = release.Version });
                UpdateTransaction.StartHelper(plan);
                ExitRequested?.Invoke();
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) { throw new LauncherUpdateException("error.launcher.updateTimeout", null, ex); }
            catch (CryptographicException ex) { throw new LauncherUpdateException("error.launcher.updateIntegrity", null, ex); }
            catch (InvalidDataException ex) { throw new LauncherUpdateException("error.launcher.updateIntegrity", null, ex); }
            catch (UnauthorizedAccessException ex) { throw new LauncherUpdateException("error.launcher.updatePermissions", null, ex); }
            catch (IOException ex) { throw new LauncherUpdateException("error.launcher.updateDownload", null, ex); }
        }
        catch (UnauthorizedAccessException ex) { throw new LauncherUpdateException("error.launcher.updatePermissions", null, ex); }
        finally { _gate.Release(); }
    }

    private static bool MayContinue(ReleaseManifest release, Version current) =>
        current >= ReleaseManifest.ParseVersion(release.MinimumVersion)
        && !(release.Mandatory && current < ReleaseManifest.ParseVersion(release.Version));

    private async Task<string> FetchManifestAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var host in new[] { "https://dusmp.online", "https://api.dusmp.online" })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseManifest.RequireOfficialUrl(host + ManifestPath));
                request.Headers.CacheControl = new() { NoCache = true };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > ReleaseManifest.MaxManifestBytes)
                    throw new InvalidDataException("Release manifest is too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (body.Length + count > ReleaseManifest.MaxManifestBytes) throw new InvalidDataException("Release manifest is too large.");
                    body.Write(buffer, 0, count);
                }
                return new UTF8Encoding(false, true).GetString(body.ToArray());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { last = ex; }
        }
        throw new HttpRequestException("The release service is unavailable.", last);
    }

    private static bool CanUpdateInPlace(string path)
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 }) return false;
        UpdateTransaction.ValidateTarget(path);
        var probe = Path.Combine(Path.GetDirectoryName(path)!, ".dusmp-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    internal static void EnsureNoLinks(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
        {
            FileSystemInfo info = Directory.Exists(item) ? new DirectoryInfo(item) : new FileInfo(item);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new UnauthorizedAccessException("Updates cannot use symbolic links.");
        }
    }

    private static void EnsureDiskSpace(string path, long required)
    {
        var drive = DriveInfo.GetDrives().Where(d => path.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        if (drive is { IsReady: true } && drive.AvailableFreeSpace < required + 16 * 1024 * 1024)
            throw new LauncherUpdateException("error.launcher.updateSpace");
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken ct)
    {
        EnsureNoLinks(path);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            8192, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            stream.Flush(true);
        }
        File.Move(pending, path, true);
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
