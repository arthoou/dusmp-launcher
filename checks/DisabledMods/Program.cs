using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DusmpLauncher.Core;
using DusmpLauncher.Models;
using DusmpLauncher.Modpack;

static void Require(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException(detail);
}

static ModpackManifest Manifest(string content, string? optionalId = null) => new()
{
    Version = "test-" + content.Length,
    Minecraft = "1.21.1",
    Files = [new ModFile
    {
        Path = "mods/example.jar", Size = Encoding.UTF8.GetByteCount(content),
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
        Url = "https://example.invalid/example.jar", OptionalId = optionalId,
    }],
};

var home = Path.Combine(Path.GetTempPath(), "DUSMP disabled checks " + Guid.NewGuid().ToString("N"));
var previousHome = Environment.GetEnvironmentVariable("DUSMP_LAUNCHER_HOME");
try
{
    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", home);
    var paths = LauncherPaths.Resolve();
    paths.EnsureCreated();
    var active = paths.ResolveInstanceFile("mods/example.jar");
    var disabled = active + ".disabled";
    var imported = paths.ResolveInstanceFile("mods/player-imported.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(active)!);
    await File.WriteAllTextAsync(active, "managed original");
    await File.WriteAllTextAsync(imported, "player imported");
    var original = Manifest("managed original");
    var manager = new ModpackManager(paths);
    var choices = new Dictionary<string, bool>();
    var initial = await manager.BuildPlanAsync(original, choices);
    await manager.CommitAsync(initial);
    Require((await ManagedFileIndex.LoadAsync(paths.ManagedIndexFile)).TryGet("mods/example.jar", out _),
        "The initial fixture must be a managed mod before it is disabled.");
    File.Move(active, disabled);
    var disabledBytes = await File.ReadAllBytesAsync(disabled);

    foreach (var force in new[] { false, true })
    foreach (var manifest in new[] { original, Manifest("a newer and different managed version") })
    {
        var plan = await manager.BuildPlanAsync(manifest, choices, force);
        Require(plan.ToDownload.Count == 0 && plan.ToDelete.Count == 0,
            "Launch, manifest updates, and forced repair must not redownload or delete a disabled mod.");
        Require(plan.Preserved.Count == 1 && plan.Preserved[0].AbsolutePath == disabled,
            "The disabled physical file must be recognised as the preserved user choice.");
        await manager.ApplyDeletionsAsync(plan);
        await manager.CommitAsync(plan);
        Require(!File.Exists(active) && (await File.ReadAllBytesAsync(disabled)).SequenceEqual(disabledBytes),
            "Verification must leave the disabled bytes unchanged without creating an enabled JAR.");
        Require(!(await ManagedFileIndex.LoadAsync(paths.ManagedIndexFile)).TryGet("mods/example.jar", out _),
            "Disabled content must not be vouched for under the active manifest path.");
    }

    var deselected = await manager.BuildPlanAsync(Manifest("managed original", "optional-example"),
        new Dictionary<string, bool> { ["optional-example"] = false }, force: true);
    Require(deselected.ToDelete.Count == 0 && deselected.ToDownload.Count == 0,
        "Optional-mod deselection must also preserve an existing disabled artifact.");

    // A stale/future ledger must not claim ownership of the user's disabled copy.
    var ledger = new ManagedFileIndex();
    ledger.Set(new ManagedFileEntry
    {
        Path = "mods/example.jar.disabled", Size = disabledBytes.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(disabledBytes)).ToLowerInvariant(),
    });
    await ledger.SaveAsync(paths.ManagedIndexFile);
    var obsolete = await manager.BuildPlanAsync(new ModpackManifest { Files = [] }, choices);
    Require(obsolete.ToDelete.Count == 0, "A disabled mod must survive removal from the manifest, even with an old ledger entry.");
    await manager.CommitAsync(obsolete);
    Require(await File.ReadAllTextAsync(imported) == "player imported", "Untracked imported mods must remain untouched.");

    await File.WriteAllTextAsync(active, "existing enabled duplicate");
    var both = await manager.BuildPlanAsync(Manifest("a newer and different managed version"), choices, force: true);
    Require(both.ToDownload.Count == 0, "A disabled counterpart must prevent update writes even when an enabled duplicate exists.");
    await manager.CommitAsync(both);
    Require(!(await ManagedFileIndex.LoadAsync(paths.ManagedIndexFile)).TryGet("mods/example.jar", out _),
        "An unverified enabled duplicate must not inherit the manifest hash.");
    File.Delete(active);
    File.Move(disabled, active);
    var enabledAgain = await manager.BuildPlanAsync(Manifest("a newer and different managed version"), choices, force: true);
    Require(enabledAgain.ToDownload.Count == 1, "Re-enabling a mod must restore normal manifest verification.");

    // The external adapter uses a synthetic registered instance; no installed
    // launcher, UI, game process or network request is involved.
    var target = Path.Combine(home, "external", "dusmp");
    Directory.CreateDirectory(Path.Combine(target, "mods"));
    var installation = new ManagedInstallation("external-test", "official", "test", target, true,
        original.Version, DateTimeOffset.UtcNow, ["mods/example.jar"])
    { LauncherRoot = Path.GetDirectoryName(target) };
    await File.WriteAllTextAsync(Path.Combine(paths.Root, "installations.json"),
        JsonSerializer.Serialize(new[] { installation }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var targetActive = Path.Combine(target, "mods", "example.jar");
    var targetDisabled = targetActive + ".disabled";
    await File.WriteAllTextAsync(targetDisabled, "external user choice");
    var external = new ExternalLauncherManager(paths);
    await external.SyncManagedFilesAsync(installation, original, choices);
    Require(!File.Exists(targetActive) && await File.ReadAllTextAsync(targetDisabled) == "external user choice",
        "External sync must not recreate an active copy or overwrite the target's disabled copy.");

    File.Delete(targetDisabled);
    await File.WriteAllTextAsync(targetActive, "external copy before disable");
    File.Move(active, disabled);
    await external.SyncManagedFilesAsync(installation, original, choices);
    Require(!File.Exists(targetActive) && await File.ReadAllTextAsync(targetDisabled) == "external copy before disable",
        "A disabled staged source must not fail sync or leave its external managed copy enabled.");
    await external.SyncManagedFilesAsync(installation, new ModpackManifest { Files = [] }, choices);
    Require(await File.ReadAllTextAsync(targetDisabled) == "external copy before disable",
        "An obsolete external disabled copy must survive removal from the manifest.");

    Console.WriteLine("PASS: disabled mods survive launch plans, forced repair, changed/obsolete manifests, optional deselection and external sync; imported mods remain untouched; re-enabled mods verify normally.");
}
finally
{
    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", previousHome);
    var target = Path.GetFullPath(home);
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(target).StartsWith("DUSMP disabled checks ", StringComparison.Ordinal))
        throw new InvalidOperationException("The disabled-mod test cleanup escaped its temporary directory.");
    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
}
