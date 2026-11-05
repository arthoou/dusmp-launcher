using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DusmpLauncher.Core;

void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

async Task Reject(string key, Func<Task> action)
{
    try { await action(); }
    catch (LauncherException ex) when (ex.ErrorKey == key) { return; }
    throw new InvalidOperationException("Expected rejection: " + key);
}

byte[] Hash(string file) => SHA256.HashData(File.ReadAllBytes(file));
var icon = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
void Jar(string path, params (string Name, byte[] Bytes)[] entries)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (name, bytes) in entries)
    {
        var entry = archive.CreateEntry(name);
        using var output = entry.Open();
        output.Write(bytes);
    }
}
byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

var home = Path.Combine(Path.GetTempPath(), "DUSMP mods checks " + Guid.NewGuid().ToString("N"));
try
{
    var mods = Path.Combine(home, "instance", "mods");
    Directory.CreateDirectory(mods);
    var manager = new InstalledModManager(mods);
    Jar(Path.Combine(mods, "fabric-test-1.0.jar"),
        ("fabric.mod.json", Text("""{"id":"fabric-test","name":"Fabric Test","version":"1.2.3","icon":{"16":"icon.png","128":"icon.png"}}""")),
        ("icon.png", icon));
    Jar(Path.Combine(mods, "forge-test.jar.disabled"),
        ("META-INF/neoforge.mods.toml", Text("""
        logoFile = "assets/logo.png"
        [[mods]]
        modId="forge_test"
        displayName="Forge Test"
        version="${file.jarVersion}"
        """)),
        ("META-INF/MANIFEST.MF", Text("Manifest-Version: 1.0\r\nImplementation-Version: 9.8.7\r\n")),
        ("assets/logo.png", icon));
    Jar(Path.Combine(mods, "quilt-test.jar"),
        ("quilt.mod.json", Text("""{"quilt_loader":{"id":"quilt-test","version":"4.5.6","metadata":{"name":"Quilt Test","icon":"icon.png"}}}""")),
        ("icon.png", icon));
    File.WriteAllText(Path.Combine(mods, "broken.jar"), "Not a ZIP file");
    File.WriteAllText(Path.Combine(mods, "notes.txt"), "Player-owned file");
    var list = await manager.ListAsync();
    Require(list.Count == 4, "List must include enabled and disabled JARs, including an unreadable JAR, and ignore other files.");
    var fabric = list.Single(mod => mod.Name == "Fabric Test");
    Require(fabric.Enabled && fabric.Version == "1.2.3" && fabric.Icon == "data:image/png;base64," + Convert.ToBase64String(icon),
        "Fabric names, versions, and embedded PNG icons must be read without executing the JAR.");
    var forge = list.Single(mod => mod.Name == "Forge Test");
    Require(!forge.Enabled && forge.Version == "9.8.7" && forge.FileName == "forge-test.jar.disabled"
        && forge.Icon!.StartsWith("data:image/png;base64,"), "Disabled NeoForge JAR metadata and manifest version must remain visible.");
    Require(list.Single(mod => mod.Name == "Quilt Test").Version == "4.5.6", "Quilt metadata must be supported.");
    Require(list.Single(mod => mod.FileName == "broken.jar").Name == "broken", "Unreadable metadata must fall back to a useful filename.");

    var fabricPath = Path.Combine(mods, fabric.FileName);
    var original = Hash(fabricPath);
    await manager.SetEnabledAsync(fabric.FileName, enabled: false);
    Require(!File.Exists(fabricPath) && File.Exists(fabricPath + ".disabled")
        && Hash(fabricPath + ".disabled").SequenceEqual(original), "Disabling must only append .disabled without changing bytes.");
    await manager.SetEnabledAsync(fabric.FileName + ".disabled", enabled: true);
    Require(File.Exists(fabricPath) && !File.Exists(fabricPath + ".disabled") && Hash(fabricPath).SequenceEqual(original),
        "Enabling must restore .jar without changing bytes.");
    await manager.SetEnabledAsync(fabric.FileName, enabled: true);

    Jar(fabricPath + ".disabled", ("fabric.mod.json", Text("{}")));
    var disabledCollisionHash = Hash(fabricPath + ".disabled");
    await Reject("error.mods.exists", () => manager.SetEnabledAsync(fabric.FileName, false));
    Require(Hash(fabricPath).SequenceEqual(original) && Hash(fabricPath + ".disabled").SequenceEqual(disabledCollisionHash),
        "A toggle collision must preserve both files.");
    await Reject("error.mods.invalidFile", () => manager.SetEnabledAsync("../outside.jar", false));
    await Reject("error.mods.invalidFile", () => manager.SetEnabledAsync("sub\\outside.jar", false));
    await Reject("error.mods.invalidFile", () => manager.SetEnabledAsync(Path.Combine(home, "outside.jar"), false));
    await Reject("error.mods.missing", () => manager.SetEnabledAsync("missing.jar", false));

    var downloads = Path.Combine(home, "Downloads");
    var first = Path.Combine(downloads, "first.jar");
    var second = Path.Combine(downloads, "second.jar");
    Jar(first, ("fabric.mod.json", Text("""{"id":"first","name":"Imported First","version":"1"}""")));
    Jar(second, ("META-INF/mods.toml", Text("[[mods]]\ndisplayName='Imported Second'\nversion='2'\n")));
    Require(await manager.ImportAsync(new[] { first, second }) == 2, "All selected valid JARs should be imported.");
    Require(File.Exists(first) && File.Exists(second) && Hash(first).SequenceEqual(Hash(Path.Combine(mods, "first.jar"))),
        "Import must copy rather than move the selected Downloads files.");
    var third = Path.Combine(downloads, "third.jar");
    Jar(third, ("fabric.mod.json", Text("{}")));
    await Reject("error.mods.exists", () => manager.ImportAsync(new[] { third, first }));
    Require(!File.Exists(Path.Combine(mods, "third.jar")), "The whole batch must be validated before importing an earlier selected JAR.");
    await manager.SetEnabledAsync("second.jar", false);
    await Reject("error.mods.exists", () => manager.ImportAsync(new[] { second }));
    Require(File.Exists(Path.Combine(mods, "second.jar.disabled")) && !File.Exists(Path.Combine(mods, "second.jar")),
        "Import must not replace or silently enable an existing disabled file.");
    var fake = Path.Combine(downloads, "fake.jar");
    File.WriteAllText(fake, "Not a JAR");
    await Reject("error.mods.invalidFile", () => manager.ImportAsync(new[] { third, fake }));
    Require(!File.Exists(Path.Combine(mods, "third.jar")), "An invalid archive must leave the whole batch untouched.");
    await Reject("error.mods.invalidFile", () => manager.ImportAsync(new[] { Path.Combine(mods, "second.jar.disabled") }));
    var duplicate = Path.Combine(home, "Other Downloads", "third.jar");
    Jar(duplicate, ("fabric.mod.json", Text("{}")));
    await Reject("error.mods.exists", () => manager.ImportAsync(new[] { third, duplicate }));

    Jar(Path.Combine(mods, "oversized.jar"), ("fabric.mod.json", new byte[256 * 1024 + 1]));
    Jar(Path.Combine(mods, "unsafe-icon.jar"),
        ("fabric.mod.json", Text("""{"name":"Unsafe Icon","icon":"../outside.png"}""")), ("../outside.png", icon));
    list = await manager.ListAsync();
    Require(list.Single(mod => mod.FileName == "oversized.jar").Name == "oversized", "Oversized metadata must be skipped safely.");
    Require(list.Single(mod => mod.Name == "Unsafe Icon").Icon == "app://launcher/assets/mod-generic.svg",
        "An icon with an archive traversal path must not be used.");
    Require(!Directory.EnumerateFiles(mods).Any(file => Path.GetFileName(file).StartsWith(".dusmp-import-", StringComparison.Ordinal)),
        "Import staging files must be cleaned up.");
    Require(File.ReadAllText(Path.Combine(mods, "notes.txt")) == "Player-owned file", "Unrelated files must remain untouched.");
    using var stop = new CancellationTokenSource();
    stop.Cancel();
    var cancelled = false;
    try { await manager.ImportAsync(new[] { third }, stop.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Require(cancelled && !File.Exists(Path.Combine(mods, "third.jar")), "Cancelled imports must not add a mod.");

    var link = Path.Combine(home, "linked mods");
    try
    {
        Directory.CreateSymbolicLink(link, mods);
        await Reject("error.mods.unsafePath", () => new InstalledModManager(link).ListAsync());
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    {
        // Creating symlinks requires an OS capability not present on every CI host.
    }
    Console.WriteLine("PASS: local mod metadata/icons, enabled and disabled listing, byte-preserving toggles, safe JAR imports, collision preflight, invalid archive/path rejection and cancellation.");
}
finally
{
    var target = Path.GetFullPath(home);
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(target).StartsWith("DUSMP mods checks ", StringComparison.Ordinal))
        throw new InvalidOperationException("The test cleanup escaped its temporary directory.");
    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
}
