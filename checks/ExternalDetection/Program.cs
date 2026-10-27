using System.Text.Json;
using DusmpLauncher.Core;
using DusmpLauncher.Models;

// Synthetic filesystem only: no process starts, no installed launchers, no user data.
var temporary = Path.Combine(Path.GetTempPath(), "dusmp-external-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var previous = Environment.GetEnvironmentVariable("DUSMP_LAUNCHER_HOME");
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void FileAt(string path, string text = "")
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
}
void ExecutableAt(string path)
{
    FileAt(path);
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
ExternalLauncherEnvironment Fixture(string name)
{
    var root = Path.Combine(temporary, name); var home = Path.Combine(root, "home");
    return new(false, home, Path.Combine(home, "custom-data"), Path.Combine(home, "custom-config"),
        Path.Combine(home, "local-app"), Path.Combine(home, "roaming"), new[] { Path.Combine(root, "programs") },
        new[] { Path.Combine(root, "bin") }, new[] { Path.Combine(home, "applications") },
        new[] { Path.Combine(home, "custom-data", "flatpak"), Path.Combine(root, "system-flatpak") },
        new[] { Path.Combine(home, "icons") });
}
try
{
    var empty = Fixture("empty"); Directory.CreateDirectory(Path.Combine(empty.Home, ".minecraft"));
    var entries = new ExternalLauncherDiscovery(empty).Detect();
    Require(!entries.Single(x => x.Id == "sklauncher").Detected && !entries.Single(x => x.Id == "tlauncher").Detected,
        "A Minecraft data directory must not count as SKLauncher/TLauncher.");
    // The MultiMC family gets a real instance, the official launcher gets a
    // profile pointed at a game directory. Everything else is detection only and
    // must not offer to be set up: an adapter that writes a format nobody has
    // checked produces a launcher that silently refuses to start the pack.
    var adapters = new[] { "prism", "polymc", "multimc", "official" };
    Require(entries.Where(x => !adapters.Contains(x.Id)).All(x => !x.CanManage), "Unverified adapters must not claim management support.");
    Require(entries.Where(x => adapters.Contains(x.Id)).All(x => x.CanManage), "Every supported adapter must offer management.");

    var native = Fixture("native-xdg");
    ExecutableAt(Path.Combine(native.PathDirectories[0], "prismlauncher"));
    Directory.CreateDirectory(Path.Combine(native.DataHome, "PrismLauncher"));
    var nativeInfo = new ExternalLauncherDiscovery(native).Detect().Single(x => x.Id == "prism");
    Require(nativeInfo.Root == Path.Combine(native.DataHome, "PrismLauncher") && nativeInfo.InstallationKind == "Native", "Custom XDG data root was not respected.");

    foreach (var installIndex in new[] { 0, 1 })
    {
        var flatpak = Fixture("flatpak-" + installIndex);
        ExecutableAt(Path.Combine(flatpak.PathDirectories[0], "flatpak"));
        Directory.CreateDirectory(Path.Combine(flatpak.FlatpakInstallations[installIndex], "app", "org.prismlauncher.PrismLauncher"));
        var info = new ExternalLauncherDiscovery(flatpak).Detect().Single(x => x.Id == "prism");
        var expectedRoot = Path.Combine(flatpak.Home, ".var", "app", "org.prismlauncher.PrismLauncher", "data", "PrismLauncher");
        Require(info.Detected && info.Root == expectedRoot && info.InstallationKind == "Flatpak", "Installed Flatpak must be detected before its first launch.");
        var start = info.Command!.ForInstance(info.Root!, "DUSMP");
        Require(start.ArgumentList.SequenceEqual(new[] { "run", installIndex == 0 ? "--user" : "--system", "org.prismlauncher.PrismLauncher", "--dir", expectedRoot, "--launch", "DUSMP" }), "Flatpak launch arguments/root are wrong.");
        Require(!start.UseShellExecute, "External launch arguments must not pass through the shell.");
    }

    var desktop = Fixture("desktop-appimage");
    var appImage = Path.Combine(desktop.Home, "Applications", "PrismLauncher-11.AppImage"); ExecutableAt(appImage);
    var customRoot = Path.Combine(desktop.Home, "Game data", "Prism");
    FileAt(Path.Combine(desktop.ApplicationDirectories[0], "prism.desktop"), $"[Desktop Entry]\nType=Application\nExec=\"{appImage}\" --dir \"{customRoot}\" %U\n[Desktop Action other]\nExec=wrong\n");
    var portableInfo = new ExternalLauncherDiscovery(desktop).Detect().Single(x => x.Id == "prism");
    Require(portableInfo.Command?.Executable == appImage && portableInfo.Root == customRoot && portableInfo.InstallationKind == "AppImage", "Quoted desktop Exec, custom root or AppImage discovery failed.");

    var windows = Fixture("windows") with { IsWindows = true };
    ExecutableAt(Path.Combine(windows.ProgramDirectories[0], "PrismLauncher", "PrismLauncher.exe"));
    Directory.CreateDirectory(Path.Combine(windows.RoamingAppData, "PrismLauncher"));
    var windowsInfo = new ExternalLauncherDiscovery(windows).Detect().Single(x => x.Id == "prism");
    Require(windowsInfo.Detected && windowsInfo.Root == Path.Combine(windows.RoamingAppData, "PrismLauncher"), "Windows data root/executable must be resolved independently.");

    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", Path.Combine(temporary, "dusmp-data"));
    var paths = LauncherPaths.Resolve(); paths.EnsureCreated();
    var manager = new ExternalLauncherManager(paths, native);
    var manifest = new ModpackManifest { Version = "1.0.0", Minecraft = "1.21.1", Loader = "neoforge", LoaderVersion = "21.1.1" };
    var freshRoot = Path.Combine(temporary, "new-prism");
    var instance = await manager.CreateOrSelectMmcAsync("prism", freshRoot, manifest);
    Require(instance.InstancePath == Path.Combine(freshRoot, "instances", "DUSMP"), "First-run instance must be placed inside instances/, never the data root.");
    Require(manager.CreateMmcStartInfo(instance).ArgumentList.SequenceEqual(new[] { "--dir", freshRoot, "--launch", "DUSMP" }), "Selected root must survive into the native launch command.");
    var secondRoot = Path.Combine(temporary, "second-prism");
    await manager.CreateOrSelectMmcAsync("prism", secondRoot, manifest);
    Require((await manager.GetInstallationsAsync()).Count == 2, "Creating another managed root must preserve the first registry record.");
    var unmanaged = Path.Combine(temporary, "unmanaged");
    FileAt(Path.Combine(unmanaged, "instances", "DUSMP", "instance.cfg"), "name=Personal\n");
    var refused = false;
    try { await manager.CreateOrSelectMmcAsync("prism", unmanaged, manifest); } catch (LauncherException) { refused = true; }
    Require(refused && File.ReadAllText(Path.Combine(unmanaged, "instances", "DUSMP", "instance.cfg")) == "name=Personal\n", "An unmanaged DUSMP folder must never be overwritten.");
    await File.WriteAllTextAsync(Path.Combine(instance.InstancePath, "instance.cfg"), "name=My DUSMP\n");
    await manager.CreateOrSelectMmcAsync("prism", freshRoot, manifest);
    Require(File.ReadAllText(Path.Combine(instance.InstancePath, "instance.cfg")) == "name=My DUSMP\n", "Existing managed instance preferences should be preserved.");
    var customInstancesRoot = Path.Combine(temporary, "prism-custom-instances");
    FileAt(Path.Combine(customInstancesRoot, "prismlauncher.cfg"), "[General]\nInstanceDir=My instances\n");
    var customInstance = await manager.CreateOrSelectMmcAsync("prism", customInstancesRoot, manifest);
    Require(customInstance.InstancePath == Path.Combine(customInstancesRoot, "My instances", "DUSMP"), "Prism's configured InstanceDir must be honored.");
    Require(manager.CreateMmcStartInfo(customInstance).ArgumentList[1] == customInstancesRoot, "Custom instance root must launch with the correct Prism root.");
    if (!OperatingSystem.IsWindows())
    {
        var linked = Path.Combine(temporary, "linked-prism"); Directory.CreateDirectory(Path.Combine(linked, "instances"));
        Directory.CreateSymbolicLink(Path.Combine(linked, "instances", "DUSMP"), instance.InstancePath);
        refused = false;
        try { await manager.CreateOrSelectMmcAsync("prism", linked, manifest); } catch (LauncherException) { refused = true; }
        Require(refused, "Symlinked instances must never be taken over.");
    }
    var assets = new DusmpLauncher.Bridge.EmbeddedAssets();
    foreach (var item in entries)
    {
        using var stream = assets.Resolve(assets, DusmpLauncher.Bridge.EmbeddedAssets.Scheme, item.IconUrl, out var mime);
        Require(stream is not null && stream.Length > 0 && mime.StartsWith("image/svg+xml"), "Missing embedded launcher logo: " + item.IconUrl);
    }

    // An installed launcher's own icon must win over our stand-in, and only
    // from a name the desktop entry actually declares.
    var themed = Fixture("installed-icon");
    ExecutableAt(Path.Combine(themed.PathDirectories[0], "prismlauncher"));
    const string iconBody = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"><rect width=\"8\" height=\"8\"/></svg>";
    FileAt(Path.Combine(themed.IconDirectories[0], "hicolor", "scalable", "apps", "vendor-mark.svg"), iconBody);
    FileAt(Path.Combine(themed.ApplicationDirectories[0], "prism.desktop"),
        $"[Desktop Entry]\nType=Application\nExec={Path.Combine(themed.PathDirectories[0], "prismlauncher")}\nIcon=vendor-mark\n");
    var themedInfo = new ExternalLauncherDiscovery(themed).Detect().Single(x => x.Id == "prism");
    var expectedIcon = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(iconBody));
    Require(themedInfo.IconUrl == expectedIcon, "The installed vendor icon was not used: " + themedInfo.IconUrl);

    // A traversal attempt in Icon= must never reach outside the theme directories.
    FileAt(Path.Combine(themed.ApplicationDirectories[0], "prism.desktop"),
        $"[Desktop Entry]\nType=Application\nExec={Path.Combine(themed.PathDirectories[0], "prismlauncher")}\nIcon=../../../../etc/passwd\n");
    var escaped = new ExternalLauncherDiscovery(themed).Detect().Single(x => x.Id == "prism");
    Require(escaped.IconUrl == "app://launcher/assets/launchers/prism.svg", "A path in Icon= must be refused: " + escaped.IconUrl);

    // PolyMC and MultiMC keep instances exactly as Prism does, so one adapter
    // serves all three — but PolyMC's metadata server publishes NeoForge under
    // a different name, and each fork named its settings file after itself.
    // Getting either wrong produces an instance that is simply refused.
    foreach (var (id, executable, config, neoforge) in new[]
    {
        ("prism", "prismlauncher", "prismlauncher.cfg", "net.neoforged"),
        ("polymc", "polymc", "polymc.cfg", "net.neoforged.neoforge"),
        ("multimc", "multimc", "multimc.cfg", "net.neoforged"),
    })
    {
        var fork = Fixture("mmc-" + id);
        ExecutableAt(Path.Combine(fork.PathDirectories[0], executable));
        var forkManager = new ExternalLauncherManager(paths, fork);
        var forkRoot = Path.Combine(temporary, "mmc-root-" + id);
        // Each fork reads its instance directory out of its own settings file.
        FileAt(Path.Combine(forkRoot, config), "[General]\nInstanceDir=Minhas instancias\n");
        var forkInstance = await forkManager.CreateOrSelectMmcAsync(id, forkRoot, manifest);
        Require(forkInstance.Launcher == id && forkInstance.Managed, $"{id} did not register a managed installation.");
        Require(Path.GetFullPath(forkInstance.InstancePath) == Path.GetFullPath(Path.Combine(forkRoot, "Minhas instancias", "DUSMP")),
            $"{id} must honour the InstanceDir in {config}: " + forkInstance.InstancePath);
        Require(Directory.Exists(Path.Combine(forkInstance.InstancePath, ".minecraft")),
            $"{id} keeps the pack's files in a .minecraft inside the instance.");
        var pack = File.ReadAllText(Path.Combine(forkInstance.InstancePath, "mmc-pack.json"));
        Require(pack.Contains($"\"{neoforge}\""), $"{id} must name NeoForge {neoforge}; got: " + pack);
        Require(pack.Contains(manifest.LoaderVersion!) && pack.Contains("net.minecraft"), $"{id} lost the pinned versions: " + pack);
        Require(forkManager.CreateMmcStartInfo(forkInstance).ArgumentList[1] == forkRoot,
            $"{id} must launch against the data folder it was set up in.");
    }

    // The official-launcher adapter: a game directory inside the launcher's own
    // .minecraft, a profile that does not disturb the player's other profiles,
    // and managed files landing in the game directory itself (no nested
    // .minecraft, which is Prism's layout and not this one).
    var vanilla = Fixture("official-launcher");
    // The adapter refuses a data folder with no launcher to run it, so the
    // fixture has to look like an installation rather than a bare directory.
    ExecutableAt(Path.Combine(vanilla.PathDirectories[0], "minecraft-launcher"));
    var dotMinecraft = Path.Combine(vanilla.Home, ".minecraft");
    Directory.CreateDirectory(dotMinecraft);
    FileAt(Path.Combine(dotMinecraft, "launcher_profiles.json"),
        "{\"profiles\":{\"mine\":{\"name\":\"Meu perfil\",\"lastVersionId\":\"1.21.1\"}},\"settings\":{\"enableSnapshots\":false},\"version\":3}");
    var vanillaManager = new ExternalLauncherManager(paths, vanilla);
    var vanillaInstall = await vanillaManager.CreateOrSelectOfficialAsync(dotMinecraft, manifest);
    Require(vanillaInstall.Launcher == "official" && vanillaInstall.Managed, "The official adapter did not register a managed installation.");
    Require(Path.GetFullPath(vanillaInstall.InstancePath) == Path.GetFullPath(Path.Combine(dotMinecraft, "dusmp")),
        "The game directory must live inside the launcher's own .minecraft: " + vanillaInstall.InstancePath);

    await vanillaManager.WriteOfficialProfileAsync(vanillaInstall, "neoforge-21.1.244");
    using (var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(dotMinecraft, "launcher_profiles.json"))))
    {
        var profiles = written.RootElement.GetProperty("profiles");
        Require(profiles.TryGetProperty("mine", out var kept) && kept.GetProperty("name").GetString() == "Meu perfil",
            "Writing the DUSMP profile destroyed the player's own profile.");
        Require(written.RootElement.TryGetProperty("settings", out _), "Writing the DUSMP profile dropped the launcher settings.");
        var ours = profiles.GetProperty("dusmp");
        Require(ours.GetProperty("lastVersionId").GetString() == "neoforge-21.1.244", "The DUSMP profile points at the wrong version.");
        Require(ours.GetProperty("gameDir").GetString() == Path.Combine(dotMinecraft, "dusmp"), "The DUSMP profile points at the wrong game directory.");
    }

    Console.WriteLine("PASS: isolated native/XDG, user/system Flatpak, desktop/AppImage, Windows detection; real CLI arguments; first-run/custom managed directories; registry preservation; unmanaged instance protection; bundled SVG assets; installed vendor icons and Icon= traversal refusal; official-launcher profile handoff.");
}
finally
{
    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", previous);
    Directory.Delete(temporary, true);
}
