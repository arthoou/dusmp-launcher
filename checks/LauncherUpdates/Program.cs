using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DusmpLauncher.Authentication;
using DusmpLauncher.Bridge;
using DusmpLauncher.Configuration;
using DusmpLauncher.Core;
using DusmpLauncher.Downloads;
using DusmpLauncher.Localization;
using DusmpLauncher.Modpack;
using DusmpLauncher.Models;
using DusmpLauncher.Network;
using DusmpLauncher.Updates;

if (TransactionChecks.TryRunProbe(args) is { } probeCode) return probeCode;
if (args.Length == 2 && args[0] == "--verify-release")
{
    var published = ReleaseManifest.Verify(await File.ReadAllTextAsync(args[1]));
    Console.WriteLine($"PASS C#: published release {published.Version} authenticated with the embedded production public key; {published.Artifacts.Count} platforms.");
    return 0;
}
if (args.Contains("--native-transactions"))
{
    await TransactionChecks.RunNativeProcessChecksAsync();
    return 0;
}

using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var publicKey = key.ExportSubjectPublicKeyInfoPem();
var root = Path.Combine(Path.GetTempPath(), "dusmp-updates-" + Guid.NewGuid().ToString("N"));
var previousHome = Environment.GetEnvironmentVariable("DUSMP_LAUNCHER_HOME");
Directory.CreateDirectory(root);
try
{
    string Sign(string payload) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, keyId = ReleaseManifest.KeyId, payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)),
        signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
    });
    string Manifest(string version = "1.3.0", string minimum = "1.2.0", bool mandatory = false) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, channel = "stable", version, minimumVersion = minimum, mandatory,
        publishedAt = DateTimeOffset.UtcNow.AddMinutes(-1), notes = "Real signed contract test.",
        artifacts = new[] { "linux", "windows" }.Select(os => new
        {
            os, arch = "x64", fileName = os == "windows" ? "DusmpLauncher.exe" : "DusmpLauncher",
            url = $"https://dusmp.online/launcher/releases/{version}/{os}-x64/" + (os == "windows" ? "DusmpLauncher.exe" : "DusmpLauncher"),
            size = 128, sha256 = new string('a', 64),
        }),
    });
    var payload = Manifest();
    var envelope = Sign(payload);
    Check(ReleaseManifest.Verify(envelope, publicKey).Version == "1.3.0", "A valid signed release verifies.");
    Expect<CryptographicException>(() => ReleaseManifest.Verify(envelope));
    using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        Expect<CryptographicException>(() => ReleaseManifest.Verify(envelope, other.ExportSubjectPublicKeyInfoPem()));
    var tampered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(envelope)!;
    var changed = JsonSerializer.Serialize(new { schemaVersion = 1, keyId = ReleaseManifest.KeyId,
        payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.Replace("1.3.0", "9.9.9"))), signature = tampered["signature"].GetString() });
    Expect<CryptographicException>(() => ReleaseManifest.Verify(changed, publicKey));
    foreach (var malformed in new[]
    {
        payload.Replace("https://dusmp.online", "http://dusmp.online"),
        payload.Replace("https://dusmp.online", "https://evil.invalid"),
        payload.Replace("linux-x64/", "linux-x64/../"),
        payload.Replace("\"x64\"", "\"arm64\""),
        payload.Replace("\"1.3.0\"", "\"v1.3.0\""),
        payload.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
        payload.Replace("\"stable\"", "\"nightly\""),
        payload.Replace("\"notes\":", "\"unexpected\":true,\"notes\":"),
        payload.Replace("\"mandatory\":false,", ""),
    }) Expect<InvalidDataException>(() => ReleaseManifest.Verify(Sign(malformed), publicKey));
    Expect<InvalidDataException>(() => ReleaseManifest.ParseVersion("1.2.3.4"));
    Expect<InvalidDataException>(() => ReleaseManifest.Verify(Sign(Manifest(minimum: "4.0.0")), publicKey));

    // Header, size, and SHA checks all run before the helper can execute bytes.
    var elf = new byte[128];
    new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 }.CopyTo(elf, 0);
    elf[18] = 62;
    var elfPath = Path.Combine(root, "verified-elf");
    await File.WriteAllBytesAsync(elfPath, elf);
    var artifact = new ReleaseArtifact { Os = "linux", Size = elf.Length, Sha256 = Convert.ToHexString(SHA256.HashData(elf)).ToLowerInvariant() };
    await ReleaseManifest.VerifyFileAsync(elfPath, artifact);
    await ExpectAsync<CryptographicException>(() => ReleaseManifest.VerifyFileAsync(elfPath,
        new ReleaseArtifact { Os = "linux", Size = elf.Length, Sha256 = new string('0', 64) }));
    await ExpectAsync<InvalidDataException>(() => ReleaseManifest.VerifyFileAsync(elfPath,
        new ReleaseArtifact { Os = "linux", Size = elf.Length + 1, Sha256 = artifact.Sha256 }));
    Expect<InvalidDataException>(() => ReleaseManifest.ValidateExecutable(elfPath, "windows"));

    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", root);
    var paths = LauncherPaths.Resolve();
    var config = new ConfigStore(paths);
    await config.LoadAsync();
    var updateRoot = Path.Combine(root, "service");
    using var service = new LauncherUpdateService(config, updateRoot, Environment.ProcessPath!, "1.3.0",
        new Replies(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope) }), publicKey);
    var stages = new List<LauncherStage>();
    Check(!await service.CheckAsync(new Reports(p => stages.Add(p.Stage)), CancellationToken.None), "Same version continues automatically, with no update choice.");
    Check(stages.Contains(LauncherStage.LauncherUpToDate), "Current version is reported accurately.");
    using var offline = new LauncherUpdateService(config, updateRoot, Environment.ProcessPath!, "1.3.0",
        new Replies(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), publicKey);
    Check(!await offline.CheckAsync(new Reports(_ => { }), CancellationToken.None), "Previously verified current release can start offline automatically.");
    using var noCache = new LauncherUpdateService(config, Path.Combine(root, "fresh"), Environment.ProcessPath!, "1.3.0",
        new Replies(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), publicKey);
    await ExpectUpdateAsync("error.launcher.updateUnavailable", () => noCache.CheckAsync(new Reports(_ => { }), CancellationToken.None));
    using var badSignature = new LauncherUpdateService(config, updateRoot, Environment.ProcessPath!, "1.3.0",
        new Replies(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(changed) }), publicKey);
    await ExpectUpdateAsync("error.launcher.updateSignature", () => badSignature.CheckAsync(new Reports(_ => { }), CancellationToken.None));
    await File.WriteAllTextAsync(Path.Combine(updateRoot, "release-cache.json"), Sign(Manifest("2.0.0", "2.0.0", true)));
    await ExpectUpdateAsync("error.launcher.updateRequired", () => offline.CheckAsync(new Reports(_ => { }), CancellationToken.None));
    using var downgrade = new LauncherUpdateService(config, updateRoot, Environment.ProcessPath!, "1.3.0",
        new Replies(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope) }), publicKey);
    await ExpectUpdateAsync("error.launcher.updateDowngrade", () => downgrade.CheckAsync(new Reports(_ => { }), CancellationToken.None));

    // A real resumed file uses signed identity, then a real SHA-256 comparison.
    var download = Path.Combine(root, "download", "DusmpLauncher");
    Directory.CreateDirectory(Path.GetDirectoryName(download)!);
    await File.WriteAllBytesAsync(download + ".part", elf[..40]);
    await File.WriteAllTextAsync(download + ".part.sha256", artifact.Sha256);
    var requests = 0;
    using var downloadHttp = new HttpClient(new Replies(request =>
    {
        requests++;
        Check(request.Headers.Range?.Ranges.Single().From == 40, "Resumed request starts at the actual partial-file length.");
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(elf[40..]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(40, elf.Length - 1, elf.Length);
        return response;
    }));
    var result = await new DownloadManager(downloadHttp, config).RunAsync(new[]
    { new DownloadJob("DusmpLauncher", download, elf.Length, artifact.Sha256, new[] { "https://dusmp.online/launcher/releases/1.3.0/linux-x64/DusmpLauncher" }) });
    Check(result.Success && requests == 1 && (await File.ReadAllBytesAsync(download)).SequenceEqual(elf), "A resumed download produces exactly the signed bytes.");

    using var http = new HttpClient();
    var api = new FixtureApiClient();
    var fakeUpdate = new Gate();
    var core = new LauncherCore(paths, config, api, new LanguageManager(), new ModpackManager(paths), new ManifestManager(paths),
        new OptionalModsManager(() => config.Current, () => config.SaveAsync()), new DownloadManager(http, config), new MirrorManager(),
        new RegionSelector(new PingTester(http)), new MicrosoftAuth(http, new SecureTokenStore(paths), paths), new OfflineAuth(),
        new GameLauncher(paths, http), new ConnectivityChecker(api), launcherUpdates: fakeUpdate);
    await core.StartAsync();
    Check(core.State.Screen == "error" && core.State.FatalErrorKey == "error.launcher.updateRequired" && core.State.LauncherUpdatePending,
        "Pending update blocks startup before modpack work.");
    await ExpectAsync<LauncherException>(() => core.FinishOnboardingAsync());
    await ExpectAsync<LauncherException>(() => core.PlayAsync());
    fakeUpdate.Fail = false;
    await core.RetryAsync();
    Check(core.State.Screen == "loading", "A prepared automatic restart never shows the main screen.");

    await TransactionChecks.RunAsync();
    Console.WriteLine("PASS: signed releases, strict metadata, origin/platform protection, hash/size/headers, current/offline startup, required-update gates, downgrade protection, resume and recovery.");
}
finally
{
    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", previousHome);
    Directory.Delete(root, true);
}
return 0;

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
static async Task ExpectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
static async Task ExpectUpdateAsync(string key, Func<Task> action)
{
    try { await action(); }
    catch (LauncherUpdateException ex) when (ex.ErrorKey == key) { return; }
    throw new InvalidOperationException("Expected update error " + key);
}
sealed class Replies(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
}
sealed class Reports(Action<ProgressSnapshot> report) : IProgress<ProgressSnapshot> { public void Report(ProgressSnapshot value) => report(value); }
sealed class Gate : ILauncherUpdateService
{
    public bool Fail { get; set; } = true;
    public Task<bool> CheckAsync(IProgress<ProgressSnapshot> progress, CancellationToken ct)
    {
        if (Fail) throw new LauncherUpdateException("error.launcher.updateRequired");
        return Task.FromResult(true);
    }
}
