using System.Security.Cryptography;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using CmlLib.Core.Java;
using CmlLib.Core.Rules;
using CmlLib.Core.Tasks;
using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

/// <summary>Installs the declared Mojang runtime before a mod loader needs Java.</summary>
internal sealed class MojangJavaRuntimeInstaller
{
    private readonly HttpClient _http;
    private readonly MinecraftPath _path;
    private readonly IGameInstaller _installer;
    private readonly RulesEvaluatorContext _rules;

    public MojangJavaRuntimeInstaller(HttpClient http, MinecraftPath path,
        IGameInstaller installer, RulesEvaluatorContext rules)
    {
        _http = http;
        _path = path;
        _installer = installer;
        _rules = rules;
    }

    public async Task InstallAsync(JavaVersion component, IProgress<ProgressSnapshot>? progress, CancellationToken ct)
    {
        var aggregator = new InstallProgressAggregator(progress, LauncherStage.InstallingMinecraft);
        aggregator.EmitNow();
        var platform = MinecraftJavaManifestResolver.GetOSNameForJava(_rules.OS);
        using var index = await ReadJsonAsync(MojangServer.JavaManifest, null, ct).ConfigureAwait(false);
        if (!index.RootElement.TryGetProperty(platform, out var platformEntry)
            || !platformEntry.TryGetProperty(component.Component, out var entries))
            throw new InvalidDataException($"Mojang does not provide {component.Component} for {platform}.");

        // Choose this exact component. CmlLib's generic extractor may fall back
        // to Java 8, which cannot run modern NeoForge installers.
        var entry = entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().FirstOrDefault() : entries;
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("manifest", out var metadata))
            throw new InvalidDataException($"No runtime manifest for {component.Component}/{platform}.");
        using var manifest = await ReadJsonAsync(metadata.GetProperty("url").GetString()!,
            metadata.GetProperty("sha1").GetString(), ct).ConfigureAwait(false);

        var root = Path.GetFullPath(new MinecraftJavaPathResolver(_path).GetJavaDirPath(component, _rules));
        var files = new List<GameFile>();
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            if (item.Value.GetProperty("type").GetString() != "file") continue;
            var raw = item.Value.GetProperty("downloads").GetProperty("raw");
            var target = Path.GetFullPath(Path.Combine(root, item.Name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Java runtime path escapes its installation directory.");
            var executable = item.Value.TryGetProperty("executable", out var flag) && flag.GetBoolean();
            files.Add(new GameFile(item.Name)
            {
                Path = target, Url = raw.GetProperty("url").GetString(),
                Hash = raw.GetProperty("sha1").GetString(), Size = raw.GetProperty("size").GetInt64(),
                UpdateTask = executable ? [new ChmodTask(493)] : [],
            });
        }
        if (files.Count == 0) throw new InvalidDataException("The Java runtime manifest contains no files.");
        await _installer.Install(files, aggregator.FileProgress, aggregator.ByteProgress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // The library checks existing files, so also verify newly downloaded
        // bytes before the provisioner runs the executable for the first time.
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(file.Path) || new FileInfo(file.Path!).Length != file.Size)
                throw new InvalidDataException($"Java runtime file is incomplete: {file.Name}");
            await using var input = new FileStream(file.Path!, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA1.HashDataAsync(input, ct).ConfigureAwait(false));
            if (!hash.Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Java runtime file failed SHA-1 verification: {file.Name}");
        }
        aggregator.EmitNow();
    }

    private async Task<JsonDocument> ReadJsonAsync(string url, string? sha1, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (sha1 is not null && !Convert.ToHexString(SHA1.HashData(bytes)).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Java runtime manifest failed SHA-1 verification.");
            return JsonDocument.Parse(bytes);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The Java runtime metadata download timed out.", ex);
        }
    }
}
