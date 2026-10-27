using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using DusmpLauncher.Downloads;
using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

public sealed record ExternalLauncherInfo(string Id, string Name, bool Detected, string? Root, string Platform, bool CanManage, string? Reason)
{
    public string IconUrl { get; init; } = "app://launcher/assets/launchers/generic.svg";
    public string? InstallationKind { get; init; }
    public string? ReasonKey { get; init; }
    [JsonIgnore] public ExternalLaunchCommand? Command { get; init; }
}
public sealed record ManagedInstallation(string Id, string Launcher, string Platform, string InstancePath, bool Managed, string ModpackVersion, DateTimeOffset RegisteredAt, IReadOnlyList<string> ManagedFiles)
{
    public IReadOnlyList<string> WriteOnceFiles { get; init; } = Array.Empty<string>();
    public string? LauncherRoot { get; init; }
}

/// <summary>Detects supported external launchers and owns only dedicated DUSMP profiles.</summary>
public sealed class ExternalLauncherManager
{
    private readonly LauncherPaths _paths;
    private readonly string _registry;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ExternalLauncherDiscovery _discovery;
    private readonly SemaphoreSlim _registryLock = new(1, 1);

    public ExternalLauncherManager(LauncherPaths paths, ExternalLauncherEnvironment? environment = null)
    {
        _paths = paths;
        _registry = Path.Combine(paths.Root, "installations.json");
        _discovery = new ExternalLauncherDiscovery(environment ?? ExternalLauncherEnvironment.Current());
    }

    public IReadOnlyList<ExternalLauncherInfo> Detect() => _discovery.Detect();

    /// <summary>
    /// Creates a DUSMP instance for a launcher of the MultiMC family.
    ///
    /// Prism, PolyMC and MultiMC keep instances the same way — a folder holding
    /// <c>instance.cfg</c>, <c>mmc-pack.json</c> and a <c>.minecraft</c> — so one
    /// adapter serves all three. What differs is the name of the settings file
    /// and, on PolyMC, the name NeoForge goes by in the pack file.
    /// </summary>
    public async Task<ManagedInstallation> CreateOrSelectMmcAsync(string launcherId, string? selectedRoot, ModpackManifest manifest, CancellationToken ct = default)
    {
        if (!ExternalLauncherDiscovery.MmcFamily.Contains(launcherId))
            throw new LauncherException("error.external.adapterUnavailable", launcherId);
        var host = Detect().First(x => x.Id == launcherId);
        var root = !string.IsNullOrWhiteSpace(selectedRoot) ? Path.GetFullPath(selectedRoot) : host.Root;
        if (string.IsNullOrWhiteSpace(root)) throw new LauncherException("error.external.pathRequired", $"Choose the {host.Name} data directory.");
        if (Path.GetFileName(root).Equals("instances", PathComparison)) root = Path.GetDirectoryName(root)!;
        if (File.Exists(Path.Combine(root, "instance.cfg")))
            throw new LauncherException("error.external.invalidPath", $"Choose {host.Name}'s data folder, not an individual instance.");
        if (_discovery.ResolveCommand(launcherId, root) is null)
            throw new LauncherException("error.external.launchFailed", $"No {host.Name} executable matches the selected data folder.");
        // An installed Flatpak can have no profile yet. Always create its instances subdirectory.
        Directory.CreateDirectory(root);
        var instances = ResolveInstancesDirectory(launcherId, root);
        var target = Path.Combine(instances, "DUSMP");
        var records = await ReadRegistryAsync(ct).ConfigureAwait(false);
        var prior = records.FirstOrDefault(x => x.Launcher == launcherId && x.Managed && Path.GetFullPath(x.InstancePath).Equals(Path.GetFullPath(target), PathComparison));
        if (Directory.Exists(target) && prior is null) throw new LauncherException("error.external.instanceExists", "A folder named DUSMP already exists and is not registered as managed.");
        EnsureNoLinks(root, target);
        Directory.CreateDirectory(target);
        var dotMinecraft = Path.Combine(target, ".minecraft");
        EnsureNoLinks(target, dotMinecraft);
        Directory.CreateDirectory(dotMinecraft);
        var cfg = "InstanceType=OneSix\nname=DUSMP\nOverrideCommands=true\n";
        if (!File.Exists(Path.Combine(target, "instance.cfg")))
            await File.WriteAllTextAsync(Path.Combine(target, "instance.cfg"), cfg, new UTF8Encoding(false), ct).ConfigureAwait(false);
        await WriteMmcPackAsync(launcherId, target, manifest, ct).ConfigureAwait(false);
        var id = prior?.Id ?? $"dusmp-{launcherId}-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(target))))[..12].ToLowerInvariant();
        var installation = new ManagedInstallation(id, launcherId, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux", target, true, manifest.Version, prior?.RegisteredAt ?? DateTimeOffset.UtcNow, prior?.ManagedFiles ?? Array.Empty<string>())
        { WriteOnceFiles = prior?.WriteOnceFiles ?? Array.Empty<string>(), LauncherRoot = root };
        await SaveInstallationAsync(installation, ct).ConfigureAwait(false);
        return installation;
    }

    /// <summary>Name of the game directory DUSMP creates inside a host launcher.</summary>
    private const string OfficialGameFolder = "dusmp";

    /// <summary>
    /// Where a launcher expects the pack's own files. Prism nests a
    /// <c>.minecraft</c> inside each instance; the official launcher points a
    /// profile straight at a game directory.
    /// </summary>
    private static string ContentRoot(ManagedInstallation installation) =>
        ExternalLauncherDiscovery.MmcFamily.Contains(installation.Launcher)
            ? Path.Combine(installation.InstancePath, ".minecraft")
            : installation.InstancePath;

    /// <summary>
    /// Prepares a DUSMP profile inside the official Minecraft Launcher.
    ///
    /// The point of this path is sign-in: Mojang allow-lists third-party
    /// Microsoft applications one at a time and DUSMP has none, so instead of
    /// asking the player to sign in here, the pack is installed into the
    /// launcher they already use and already signed into.
    ///
    /// Nothing outside our own game directory and our own profile entry is
    /// touched — the player's other profiles, worlds and settings are left alone.
    /// </summary>
    public async Task<ManagedInstallation> CreateOrSelectOfficialAsync(string? selectedRoot, ModpackManifest manifest, CancellationToken ct = default)
    {
        var official = Detect().First(x => x.Id == "official");
        var root = !string.IsNullOrWhiteSpace(selectedRoot) ? Path.GetFullPath(selectedRoot) : official.Root;
        if (string.IsNullOrWhiteSpace(root))
            throw new LauncherException("error.external.pathRequired", "Choose the Minecraft Launcher's .minecraft directory.");
        var command = _discovery.ResolveOfficialCommand(root);
        if (command is null)
            throw new LauncherException("error.external.launchFailed", "No Minecraft Launcher executable matches the selected data folder.");
        if (command.Kind == "Microsoft Store" && official.Root is { } storeRoot && !Path.GetFullPath(root).Equals(Path.GetFullPath(storeRoot), PathComparison))
            throw new LauncherException("error.external.invalidPath", "Select the Microsoft Store launcher's detected Minecraft directory.");

        Directory.CreateDirectory(root);
        var target = Path.Combine(root, OfficialGameFolder);
        var records = await ReadRegistryAsync(ct).ConfigureAwait(false);
        var prior = records.FirstOrDefault(x => x.Launcher == "official" && x.Managed
            && Path.GetFullPath(x.InstancePath).Equals(Path.GetFullPath(target), PathComparison));
        if (Directory.Exists(target) && prior is null)
            throw new LauncherException("error.external.instanceExists", $"A folder named {OfficialGameFolder} already exists and is not registered as managed.");

        EnsureNoLinks(root, target);
        Directory.CreateDirectory(target);

        var id = prior?.Id ?? "dusmp-official-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(target))))[..12].ToLowerInvariant();
        var installation = new ManagedInstallation(id, "official",
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux",
            target, true, manifest.Version, prior?.RegisteredAt ?? DateTimeOffset.UtcNow, prior?.ManagedFiles ?? Array.Empty<string>())
        { WriteOnceFiles = prior?.WriteOnceFiles ?? Array.Empty<string>(), LauncherRoot = root };

        await SaveInstallationAsync(installation, ct).ConfigureAwait(false);
        return installation;
    }

    /// <summary>
    /// Registers the version with the official launcher so the profile can point
    /// at it. Separate from creating the installation because the version has to
    /// be installed first, and that is the launcher core's job.
    /// </summary>
    public Task WriteOfficialProfileAsync(ManagedInstallation installation, string versionId, CancellationToken ct = default)
    {
        var root = installation.LauncherRoot
            ?? Path.GetDirectoryName(Path.GetFullPath(installation.InstancePath))
            ?? throw new LauncherException("error.external.registrationMissing", installation.InstancePath);

        EnsureNoLinks(root, installation.InstancePath);
        var command = _discovery.ResolveOfficialCommand(root)
            ?? throw new LauncherException("error.external.launchFailed", "The selected Minecraft Launcher is no longer installed.");
        var gameDirectory = command.Kind == "Flatpak" ? VisibleToLauncher(installation.InstancePath) : installation.InstancePath;
        return OfficialLauncherProfile.WriteAsync(root, versionId, gameDirectory, null, ct, microsoftStore: command.Kind == "Microsoft Store");
    }

    /// <summary>
    /// The path as the launcher itself will resolve it.
    ///
    /// A Flatpak launcher runs with its own home: DUSMP writes to
    /// <c>~/.var/app/&lt;app-id&gt;/.minecraft/…</c>, but inside the sandbox that
    /// same directory is simply <c>~/.minecraft/…</c>, and the profile has to
    /// carry the path the sandbox sees or the game directory will not exist.
    /// Verified against the installed Flatpak, which exports only
    /// <c>persistent=.minecraft</c> and no access to the real home at all.
    /// </summary>
    internal static string VisibleToLauncher(string path)
    {
        var full = Path.GetFullPath(path);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return full;

        // Only Minecraft's --persist=.minecraft is remapped to the sandbox home.
        // Flatpak's XDG data/config directories keep their full host paths.
        var persisted = Path.Combine(home, ".var", "app", "com.mojang.Minecraft", ".minecraft");
        if (full.Equals(persisted, PathComparison)) return Path.Combine(home, ".minecraft");
        return full.StartsWith(persisted + Path.DirectorySeparatorChar, PathComparison)
            ? Path.Combine(home, ".minecraft", full[(persisted.Length + 1)..]) : full;
    }

    /// <summary>Opens the official launcher. It then shows the DUSMP profile.</summary>
    public async Task LaunchOfficialAsync(ManagedInstallation installation, CancellationToken ct = default)
    {
        var registered = (await ReadRegistryAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == installation.Id && x.Managed
            && Path.GetFullPath(x.InstancePath).Equals(Path.GetFullPath(installation.InstancePath), PathComparison));
        if (registered is null) throw new LauncherException("error.external.registrationMissing", installation.InstancePath);

        var root = registered.LauncherRoot ?? Path.GetDirectoryName(registered.InstancePath)!;
        var command = _discovery.ResolveOfficialCommand(root)
            ?? throw new LauncherException("error.external.launchFailed", "The Minecraft Launcher could not be found.");

        // Deliberately no instance arguments: the official launcher has no
        // command line for picking a profile, so it opens on its own and the
        // player presses Play on the DUSMP profile.
        var start = command.CreateStartInfo(root);
        if (command.Kind is not ("Flatpak" or "Microsoft Store"))
        { start.ArgumentList.Add("--workDir"); start.ArgumentList.Add(root); }

        await StartExternalAsync(start, ct).ConfigureAwait(false);
    }

    public async Task SyncManagedFilesAsync(ManagedInstallation installation, ModpackManifest manifest, IReadOnlyDictionary<string,bool> optional, CancellationToken ct = default)
    {
        if (!installation.Managed) throw new LauncherException("error.external.instanceExists", "This instance is not managed by DUSMP.");
        var registered = (await ReadRegistryAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == installation.Id && x.Managed &&
            Path.GetFullPath(x.InstancePath).Equals(Path.GetFullPath(installation.InstancePath), PathComparison));
        if (registered is null) throw new LauncherException("error.external.instanceExists", "This instance is not registered with DUSMP.");
        installation = registered;
        var target = ContentRoot(installation);
        EnsureNoLinks(installation.InstancePath, target);
        Directory.CreateDirectory(target);
        var selected = manifest.Files.Where(f => (f.OptionalId is null || optional.GetValueOrDefault(f.OptionalId, true)) && LauncherPaths.IsDeletable(f.Path)).ToArray();
        var wanted = selected.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var writeOnce = new HashSet<string>(installation.WriteOnceFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in selected)
        {
            ct.ThrowIfCancellationRequested(); var relative = entry.Path;
            if (!LauncherPaths.IsDeletable(relative)) continue;
            var source = _paths.ResolveInstanceFile(relative); var dest = ResolveUnder(target, relative);
            EnsureNoLinks(target, dest);
            if (!File.Exists(source)) throw new LauncherException("error.download.failed", "Missing staged modpack file: " + relative);
            if ((entry.WriteOnce || writeOnce.Contains(relative)) && File.Exists(dest)) { writeOnce.Add(relative); continue; }
            if (File.Exists(dest) && new FileInfo(dest).Length == entry.Size
                && string.Equals(await HashUtil.ComputeFileSha256Async(dest, ct: ct).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            { if (entry.WriteOnce) writeOnce.Add(relative); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); var temp = dest + "." + Guid.NewGuid().ToString("N") + ".dusmp.tmp";
            try
            {
                await using (var input = File.OpenRead(source)) await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true)) await input.CopyToAsync(output, ct).ConfigureAwait(false);
                if (!entry.WriteOnce && (new FileInfo(temp).Length != entry.Size || !string.Equals(await HashUtil.ComputeFileSha256Async(temp, ct: ct).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new LauncherException("error.download.failed", "Staged file changed during synchronization: " + relative);
                File.Move(temp, dest, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            if (entry.WriteOnce) writeOnce.Add(relative); else writeOnce.Remove(relative);
        }
        foreach (var old in installation.ManagedFiles.Where(x => !wanted.Contains(x)))
        {
            if (writeOnce.Contains(old) || !LauncherPaths.IsDeletable(old)) continue;
            var file = ResolveUnder(target, old); EnsureNoLinks(target, file); if (File.Exists(file)) File.Delete(file);
        }
        if (ExternalLauncherDiscovery.MmcFamily.Contains(installation.Launcher)) await WriteMmcPackAsync(installation.Launcher, installation.InstancePath, manifest, ct).ConfigureAwait(false);
        var updated = installation with { ModpackVersion = manifest.Version, ManagedFiles = wanted.ToArray(), WriteOnceFiles = writeOnce.ToArray() };
        await SaveInstallationAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task LaunchMmcAsync(ManagedInstallation installation, CancellationToken ct = default)
    {
        var registered = (await ReadRegistryAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == installation.Id && x.Managed
            && Path.GetFullPath(x.InstancePath).Equals(Path.GetFullPath(installation.InstancePath), PathComparison));
        if (registered is null) throw new LauncherException("error.external.instanceExists", "This instance is not registered with DUSMP.");
        var start = CreateMmcStartInfo(registered);
        await StartExternalAsync(start, ct).ConfigureAwait(false);
    }

    private static async Task StartExternalAsync(ProcessStartInfo start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(start);
            if (process is null) throw new LauncherException("error.external.launchFailed", start.FileName);
            // Catch immediate failures such as a missing AppImage runtime. A
            // zero exit can be a successful handoff to an already-open launcher.
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(1));
            try { await process.WaitForExitAsync(startup.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
            if (process.ExitCode != 0)
                throw new LauncherException("error.external.launchFailed", $"{Path.GetFileName(start.FileName)} exited with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception error)
        { throw new LauncherException("error.external.launchFailed", error.Message); }
    }

    /// <summary>Builds a command without starting a process, including Flatpak and custom roots.</summary>
    public ProcessStartInfo CreateMmcStartInfo(ManagedInstallation installation)
    {
        var launcherId = installation.Launcher;
        var info = Detect().First(x => x.Id == launcherId);
        var instance = Path.GetFullPath(installation.InstancePath);
        var instancesDirectory = Path.GetDirectoryName(instance)!;
        var root = installation.LauncherRoot ?? Path.GetDirectoryName(instancesDirectory)!;
        // Custom instance directories are resolved from each known root.
        if (installation.LauncherRoot is null && info.Root is { } knownRoot && Path.GetFullPath(ResolveInstancesDirectory(launcherId, knownRoot)).Equals(instancesDirectory, PathComparison)) root = knownRoot;
        var command = _discovery.ResolveCommand(launcherId, root);
        if (command is null) throw new LauncherException("error.external.launchFailed", $"{info.Name} could not be found. Install it or select its data directory.");
        return command.ForInstance(root, Path.GetFileName(instance));
    }

    private async Task WriteMmcPackAsync(string launcherId, string instance, ModpackManifest manifest, CancellationToken ct)
    {
        var loader = (manifest.Loader ?? "vanilla").Trim().ToLowerInvariant();
        if (loader is not ("neoforge" or "forge" or "fabric" or "quilt" or "vanilla" or ""))
            throw new LauncherException("error.manifest.invalid", "Unsupported loader: " + loader);
        var uid = ExternalLauncherDiscovery.LoaderUid(launcherId, loader);
        if (uid is not null && string.IsNullOrWhiteSpace(manifest.LoaderVersion))
            throw new LauncherException("error.manifest.loaderVersionMissing", loader);
        var components = new List<object> { new { uid = "net.minecraft", version = manifest.Minecraft } };
        if (uid is not null) components.Add(new { uid, version = manifest.LoaderVersion });
        var file = Path.Combine(instance, "mmc-pack.json");
        EnsureNoLinks(instance, file);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { formatVersion = 1, components }, _json), new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<ManagedInstallation>> GetInstallationsAsync(CancellationToken ct = default) => await ReadRegistryAsync(ct).ConfigureAwait(false);

    private async Task<List<ManagedInstallation>> ReadRegistryAsync(CancellationToken ct)
    {
        if (!File.Exists(_registry)) return new();
        await using var stream = new FileStream(_registry, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, true);
        return await JsonSerializer.DeserializeAsync<List<ManagedInstallation>>(stream, _json, ct).ConfigureAwait(false) ?? new();
    }

    private async Task SaveInstallationAsync(ManagedInstallation installation, CancellationToken ct)
    {
        await _registryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadRegistryAsync(ct).ConfigureAwait(false);
            records.RemoveAll(x => x.Id == installation.Id); records.Add(installation);
            Directory.CreateDirectory(_paths.Root);
            var temp = _registry + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(records, _json), new UTF8Encoding(false), ct).ConfigureAwait(false);
                File.Move(temp, _registry, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _registryLock.Release(); }
    }

    private static string ResolveInstancesDirectory(string launcherId, string root)
    {
        var config = Path.Combine(root, ExternalLauncherDiscovery.ConfigFileFor(launcherId));
        if (File.Exists(config))
        {
            var setting = File.ReadLines(config, Encoding.UTF8).FirstOrDefault(x => x.StartsWith("InstanceDir=", StringComparison.Ordinal));
            var path = setting?["InstanceDir=".Length..].Trim().Trim('"');
            if (OperatingSystem.IsWindows()) path = path?.Replace("\\\\", "\\");
            if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(Path.Combine(root, path));
        }
        return Path.Combine(root, "instances");
    }

    private static void EnsureNoLinks(string root, string path)
    {
        // Never follow instance symlinks/junctions into an unmanaged player's directory.
        var current = Path.GetFullPath(path);
        var stop = Path.GetPathRoot(current);
        while (current != stop)
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new LauncherException("error.external.invalidPath", "DUSMP cannot manage files through symbolic links: " + current);
            current = Path.GetDirectoryName(current)!;
        }
    }
    private static string ResolveUnder(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, PathComparison)) throw new LauncherException("error.external.invalidPath", relative);
        return full;
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
