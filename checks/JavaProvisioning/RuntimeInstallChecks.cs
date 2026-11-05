using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Installers;
using CmlLib.Core.Java;
using CmlLib.Core.Rules;
using DusmpLauncher.Core;
using DusmpLauncher.Models;

internal static class RuntimeInstallChecks
{
    public static async Task RunAsync()
    {
        // Real CmlLib extraction targets and download scheduling, with every HTTP
        // response supplied in memory. The downloaded placeholders are never run.
        using (var fixture = new RuntimeHttpFixture())
        {
            var snapshots = new ConcurrentQueue<ProgressSnapshot>();
            await fixture.Installer.InstallAsync(fixture.Component,
                new RecordedProgress(snapshots.Enqueue), default);
            var binary = fixture.BinaryPath;
            Require(File.Exists(binary) && File.ReadAllBytes(binary).SequenceEqual(fixture.BinaryBytes),
                "A fresh runtime must download the declared binary into its platform/component directory.");
            Require(fixture.Requests.Contains(MojangServer.JavaManifest)
                && fixture.Requests.Contains(RuntimeHttpFixture.ManifestUrl)
                && fixture.Requests.Contains(RuntimeHttpFixture.BinaryUrl),
                "Runtime provisioning must obtain the index, exact component manifest, and file bytes.");
            Require(snapshots.Any(p => p.TotalBytes == fixture.BinaryBytes.Length
                && p.DownloadedBytes == fixture.BinaryBytes.Length),
                "The real runtime downloader must report measured bytes through the launcher's progress bridge.");

            var downloadsBefore = fixture.Requests.Count(url => url == RuntimeHttpFixture.BinaryUrl);
            await fixture.Installer.InstallAsync(fixture.Component, null, default);
            Require(fixture.Requests.Count(url => url == RuntimeHttpFixture.BinaryUrl) == downloadsBefore,
                "An existing verified runtime file must be reused rather than downloaded again.");
        }

        foreach (var missingPlatform in new[] { false, true })
        {
            using var fixture = new RuntimeHttpFixture();
            fixture.IndexBytes = RuntimeHttpFixture.CreateIndex(fixture.ManifestBytes,
                missingPlatform ? "linux" : "windows-x64", "jre-legacy");
            await ExpectInvalidData(() => fixture.Installer.InstallAsync(fixture.Component, null, default),
                missingPlatform
                    ? "A missing runtime platform must be rejected."
                    : "A missing Java 21 component must be rejected rather than falling back to legacy Java.");
            Require(fixture.Requests.Count == 1 && !File.Exists(fixture.BinaryPath),
                "An unavailable platform or component must not download or install the legacy runtime.");
        }

        using (var fixture = new RuntimeHttpFixture())
        {
            // The index still names the original manifest's digest.
            fixture.ManifestBytes = Encoding.UTF8.GetBytes("{\"files\":{}}");
            await ExpectInvalidData(() => fixture.Installer.InstallAsync(fixture.Component, null, default),
                "A runtime manifest whose SHA-1 disagrees with its metadata must be rejected.");
            Require(!fixture.Requests.Contains(RuntimeHttpFixture.BinaryUrl),
                "A tampered manifest must fail before downloading executable bytes.");
        }

        using (var fixture = new RuntimeHttpFixture())
        {
            fixture.DownloadBytes = fixture.BinaryBytes.Select(value => (byte)(value ^ 0x5a)).ToArray();
            await ExpectInvalidData(() => fixture.Installer.InstallAsync(fixture.Component, null, default),
                "Downloaded runtime bytes must be verified before a Java probe may execute them.");
            Require(fixture.Requests.Contains(RuntimeHttpFixture.BinaryUrl),
                "The corrupted-file check must exercise a completed HTTP download, not only manifest validation.");
        }

        using (var fixture = new RuntimeHttpFixture())
        using (var stop = new CancellationTokenSource())
        {
            fixture.CancelIndexRequest = stop;
            var cancelled = false;
            try { await fixture.Installer.InstallAsync(fixture.Component, null, stop.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && fixture.Requests.Count == 1 && !File.Exists(fixture.BinaryPath),
                "Cancellation during runtime metadata retrieval must propagate without installing anything.");
        }

        Console.WriteLine("PASS: runtime-only HTTP provisioning, exact platform/component selection, measured byte progress, warm reuse, manifest/file hash verification and cancellation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectInvalidData(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class RecordedProgress(Action<ProgressSnapshot> report) : IProgress<ProgressSnapshot>
    {
        public void Report(ProgressSnapshot value) => report(value);
    }

    private sealed class RuntimeHttpFixture : HttpMessageHandler
    {
        public const string ManifestUrl = "https://runtime.test/java21-manifest.json";
        public const string BinaryUrl = "https://runtime.test/java21-bin";
        private readonly string _home = Path.Combine(Path.GetTempPath(),
            "DUSMP runtime installer checks " + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;

        public JavaVersion Component { get; } = new("java-runtime-delta", "21");
        public byte[] BinaryBytes { get; } = Encoding.UTF8.GetBytes("harmless runtime test bytes; never execute");
        public byte[] IndexBytes { get; set; }
        public byte[] ManifestBytes { get; set; }
        public byte[] DownloadBytes { get; set; }
        public ConcurrentQueue<string> Requests { get; } = new();
        public CancellationTokenSource? CancelIndexRequest { get; set; }
        public MojangJavaRuntimeInstaller Installer { get; }
        public string BinaryPath { get; }

        public RuntimeHttpFixture()
        {
            DownloadBytes = BinaryBytes;
            ManifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                files = new Dictionary<string, object>
                {
                    ["bin/javaw.exe"] = new
                    {
                        type = "file", executable = true,
                        downloads = new
                        {
                            raw = new { url = BinaryUrl, sha1 = Digest(BinaryBytes), size = BinaryBytes.Length }
                        }
                    }
                }
            });
            IndexBytes = CreateIndex(ManifestBytes, "windows-x64", Component.Component);
            _http = new HttpClient(this, disposeHandler: false);
            var gamePath = new MinecraftPath(_home);
            var rules = new RulesEvaluatorContext(new LauncherOSRule("windows", "64", "10.0"));
            BinaryPath = new MinecraftJavaPathResolver(gamePath).GetJavaBinaryPath(Component, rules);
            Installer = new MojangJavaRuntimeInstaller(_http, gamePath,
                new ParallelGameInstaller(2, 2, 8, _http), rules);
        }

        public static byte[] CreateIndex(byte[] manifest, string platform, string component) =>
            JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                [platform] = new Dictionary<string, object>
                {
                    [component] = new[] { new { manifest = new { url = ManifestUrl, sha1 = Digest(manifest), size = manifest.Length } } }
                }
            });

        private static string Digest(byte[] content) => Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Enqueue(url);
            cancellationToken.ThrowIfCancellationRequested();
            if (url == MojangServer.JavaManifest && CancelIndexRequest is not null)
            {
                CancelIndexRequest.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            byte[]? content = url == MojangServer.JavaManifest ? IndexBytes
                : url == ManifestUrl ? ManifestBytes
                : url == BinaryUrl ? DownloadBytes : null;
            return Task.FromResult(new HttpResponseMessage(content is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content ?? Array.Empty<byte>())
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _http.Dispose();
                var target = Path.GetFullPath(_home);
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(target).StartsWith("DUSMP runtime installer checks ", StringComparison.Ordinal))
                    throw new InvalidOperationException("Runtime-check cleanup escaped its dedicated temporary directory.");
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            }
            base.Dispose(disposing);
        }
    }
}
