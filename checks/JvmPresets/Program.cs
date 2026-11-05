using System.Text.Json;
using DusmpLauncher.Bridge;
using DusmpLauncher.Configuration;
using DusmpLauncher.Core;
using DusmpLauncher.Authentication;
using DusmpLauncher.Downloads;
using DusmpLauncher.Localization;
using DusmpLauncher.Modpack;
using DusmpLauncher.Network;

void Require(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException(detail);
}

Require(JvmPresets.IsKnown(JvmPresets.Low8Gb) && JvmPresets.IsKnown(JvmPresets.Default) && JvmPresets.IsKnown(JvmPresets.Smooth),
    "All three device presets must be recognized.");
Require(!JvmPresets.IsKnown(null) && !JvmPresets.IsKnown("unknown"), "An absent or invalid preset must not count as a completed device choice.");

// Selecting a device sets an initial heap budget. The launch path must continue
// to respect the player's later RAM adjustment rather than reapplying defaults.
var settings = new GameSettings { ExtraJvmArgs = "-Dpack.setting=retained" };
JvmPresets.Apply(settings, JvmPresets.Low8Gb);
Require(settings.JvmPreset == JvmPresets.Low8Gb && settings.MinRamMb == 1024 && settings.MaxRamMb == 4096,
    "Low 8GB must initially use a 1 GB minimum and 4 GB maximum heap.");
Require(settings.ExtraJvmArgs == "-Dpack.setting=retained", "Low 8GB must preserve unrelated custom arguments.");
Require(JvmPresets.BuildArguments(settings).Contains("-XX:+UseG1GC"), "Low 8GB must select G1.");
JvmPresets.Apply(settings, JvmPresets.Default);
Require(settings.JvmPreset == JvmPresets.Default && settings.MinRamMb == 1024 && settings.MaxRamMb == 6144,
    "Default must initially use a 1 GB minimum and 6 GB maximum heap.");
Require(settings.ExtraJvmArgs == "-Dpack.setting=retained", "Selecting a preset must preserve the player's custom arguments.");
var arguments = JvmPresets.BuildArguments(settings);
Require(arguments.Contains("-XX:+UseG1GC") && !arguments.Contains("-XX:+UseZGC"), "Default must select only G1.");
settings.MaxRamMb = 7168;
_ = JvmPresets.BuildArguments(settings);
Require(settings.MaxRamMb == 7168, "Building launch arguments must not reset manually adjusted RAM.");

JvmPresets.Apply(settings, JvmPresets.Smooth);
Require(settings.JvmPreset == JvmPresets.Smooth && settings.MinRamMb == 2048 && settings.MaxRamMb == 10240,
    "Smooth must initially use a 2 GB minimum and 10 GB maximum heap.");
arguments = JvmPresets.BuildArguments(settings);
Require(arguments.Contains("-XX:+UseZGC") && arguments.Contains("-XX:+ZGenerational") && !arguments.Contains("-XX:+UseG1GC"),
    "Smooth must select Java 21 Generational ZGC without selecting G1.");

// Conflicting old flags can otherwise prevent Java from starting, or silently
// bypass the RAM slider. Preserve unrelated quoted options as a single token.
settings.ExtraJvmArgs = "-XX:+UseG1GC -XX:+UseParallelGC -XX:-UseZGC -XX:G1HeapRegionSize=32M -XX:G1NewSizePercent=20 -Xms8G -Xmx16G -XX:InitialHeapSize=8G -XX:MaxHeapSize=16G -Dpack.label=\"hello world\"";
arguments = JvmPresets.BuildArguments(settings);
Require(arguments.Count(x => x == "-XX:+UseZGC") == 1 && arguments.Count(x => x == "-XX:+ZGenerational") == 1,
    "Smooth must retain exactly one valid collector selection and its generation mode.");
Require(!arguments.Any(x => x.Contains("UseG1GC", StringComparison.Ordinal) || x.Contains("UseParallelGC", StringComparison.Ordinal)
    || x.StartsWith("-XX:G1", StringComparison.Ordinal) || x == "-XX:-UseZGC"), "Smooth must remove incompatible collector and G1 tuning flags.");
Require(!arguments.Any(x => x.StartsWith("-Xms", StringComparison.Ordinal) || x.StartsWith("-Xmx", StringComparison.Ordinal)
    || x.StartsWith("-XX:InitialHeapSize=", StringComparison.Ordinal) || x.StartsWith("-XX:MaxHeapSize=", StringComparison.Ordinal)),
    "Preset launch arguments must not override the adjustable heap fields.");
Require(arguments.Contains("-Dpack.label=hello world"), "A quoted custom property must remain one argument.");

foreach (var preset in new[] { JvmPresets.Low8Gb, JvmPresets.Default })
{
    settings.ExtraJvmArgs = "-XX:+UseZGC -XX:+ZGenerational -XX:SoftMaxHeapSize=8G -XX:ZUncommitDelay=120 -Xms8G -Xmx16G -XX:InitialHeapSize=8G -XX:MinHeapSize=8G -XX:MaxHeapSize=16G -Dother=keep";
    JvmPresets.Apply(settings, preset);
    arguments = JvmPresets.BuildArguments(settings);
    Require(arguments.Count(x => x == "-XX:+UseG1GC") == 1 && !arguments.Any(x => x.Contains("UseZGC", StringComparison.Ordinal)
        || x.Contains("ZGenerational", StringComparison.Ordinal) || x.StartsWith("-XX:SoftMaxHeapSize=", StringComparison.Ordinal)
        || x.StartsWith("-XX:ZUncommitDelay=", StringComparison.Ordinal)), $"Switching to {preset} must remove the old ZGC policy.");
    Require(!arguments.Any(x => x.StartsWith("-Xms", StringComparison.Ordinal) || x.StartsWith("-Xmx", StringComparison.Ordinal)
        || x.StartsWith("-XX:InitialHeapSize=", StringComparison.Ordinal) || x.StartsWith("-XX:MinHeapSize=", StringComparison.Ordinal)
        || x.StartsWith("-XX:MaxHeapSize=", StringComparison.Ordinal)), $"{preset} must not allow custom arguments to override the heap fields.");
    Require(arguments.Contains("-Dother=keep"), "Switching collector must preserve unrelated custom arguments.");
}

// Upgrading an existing installation must not silently replace a custom setup
// before its owner explicitly chooses a device profile.
var legacy = new GameSettings { JvmPreset = null, MinRamMb = 512, MaxRamMb = 8192,
    ExtraJvmArgs = "-Xmx9G -XX:+UseParallelGC -Dlegacy=\"two words\"" };
arguments = JvmPresets.BuildArguments(legacy);
Require(arguments.Contains("-Xmx9G") && arguments.Contains("-XX:+UseParallelGC") && arguments.Contains("-Dlegacy=two words"),
    "Legacy custom options must remain unchanged until a device preset is selected.");
Require(legacy.MinRamMb == 512 && legacy.MaxRamMb == 8192 && legacy.JvmPreset is null, "Reading legacy arguments must not mutate settings.");

var rejected = false;
try { JvmPresets.Apply(legacy, "unsupported"); }
catch (ArgumentException) { rejected = true; }
Require(rejected && legacy.JvmPreset is null && legacy.MaxRamMb == 8192, "An invalid choice must be rejected without replacing existing settings.");

settings.MaxRamMb = 8192;
var saved = JsonSerializer.Serialize(settings, BridgeProtocol.Json);
var restored = JsonSerializer.Deserialize<GameSettings>(saved, BridgeProtocol.Json)!;
Require(restored.JvmPreset == JvmPresets.Default && restored.MaxRamMb == 8192,
    "Preset selection and a RAM override must both survive saving and loading.");

var previousHome = Environment.GetEnvironmentVariable("DUSMP_LAUNCHER_HOME");
try
{
    var home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DUSMP checks", "preset-path-check"));
    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", home);
    var paths = LauncherPaths.Resolve();
    Require(paths.Instances == Path.Combine(home, "instances"), "The folder-button destination must use the plural instances directory.");
    Require(paths.ForEdition("islands").Instances == paths.Instances, "Choosing an edition must not change the parent instances directory.");
}
finally { Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", previousHome); }

// Exercise the real onboarding and persistence methods without opening a window,
// downloading a mod, signing in, or reading any player's configuration.
var setupHome = Path.Combine(Path.GetTempPath(), "dusmp-preset-check-" + Guid.NewGuid().ToString("N"));
try
{
    Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", setupHome);
    var paths = LauncherPaths.Resolve();
    var config = new ConfigStore(paths);
    await config.LoadAsync();
    using var http = new HttpClient();
    var api = new FixtureApiClient();
    var languages = new LanguageManager();
    var shortcutCalls = 0;
    var shortcutsAfterSetup = false;
    var shortcutFailure = false;
    var core = new LauncherCore(paths, config, api, languages,
        new ModpackManager(paths), new ManifestManager(paths),
        new OptionalModsManager(() => config.Current, () => config.SaveAsync()),
        new DownloadManager(http, config), new MirrorManager(), new RegionSelector(new PingTester(http)),
        new MicrosoftAuth(http, new SecureTokenStore(paths), paths), new OfflineAuth(),
        new GameLauncher(paths, http), new ConnectivityChecker(api), ensureShortcuts: () =>
        {
            shortcutCalls++;
            shortcutsAfterSetup = !config.Current.FirstLaunch;
            if (shortcutFailure) throw new IOException("Synthetic shortcut failure");
        });

    core.GoToScreen(BridgeProtocol.Screens.OnboardingLanguage);
    await core.SetLanguageAsync("en-US");
    Require(core.State.Screen == BridgeProtocol.Screens.OnboardingDevice, "Language selection must lead to the required device step.");
    foreach (var attempt in new Func<Task>[] { () => core.FinishOnboardingAsync(), () => core.PlayAsync() })
    {
        var blocked = false;
        try { await attempt(); }
        catch (LauncherException ex) when (ex.ErrorKey == "error.device.required") { blocked = true; }
        Require(blocked && config.Current.FirstLaunch, "A new installation must not complete or play before choosing a device.");
        Require(shortcutCalls == 0, "Incomplete onboarding must not create shortcuts.");
    }
    foreach (var profile in new[]
    {
        (Preset: JvmPresets.Low8Gb, MinRamMb: 1024, MaxRamMb: 4096),
        (Preset: JvmPresets.Default, MinRamMb: 1024, MaxRamMb: 6144),
        (Preset: JvmPresets.Smooth, MinRamMb: 2048, MaxRamMb: 10240),
    })
    {
        core.GoToScreen(BridgeProtocol.Screens.OnboardingDevice);
        await core.SetJvmPresetAsync(profile.Preset);
        Require(core.State.Screen == BridgeProtocol.Screens.OnboardingPlayMode
            && core.State.JvmPreset == profile.Preset && core.CurrentSettings.MinRamMb == profile.MinRamMb
            && core.CurrentSettings.MaxRamMb == profile.MaxRamMb,
            $"The {profile.Preset} device choice must persist its heap and advance to play mode.");
        var reloaded = await new ConfigStore(paths).LoadAsync();
        Require(reloaded.Game.JvmPreset == profile.Preset && reloaded.Game.MinRamMb == profile.MinRamMb
            && reloaded.Game.MaxRamMb == profile.MaxRamMb, $"The initial {profile.Preset} choice must survive saving and loading.");
        core.CurrentSettings.JvmPreset = " " + profile.Preset.ToUpperInvariant() + " ";
        core.CurrentSettings.MaxRamMb = 8192;
        await core.SaveSettingsAsync(core.CurrentSettings);
        reloaded = await new ConfigStore(paths).LoadAsync();
        Require(reloaded.Game.JvmPreset == profile.Preset && reloaded.Game.MaxRamMb == 8192,
            $"Reloading {profile.Preset} must normalize the stored ID and preserve a manually adjusted heap.");
    }

    await core.FinishOnboardingAsync();
    Require(shortcutCalls == 1 && shortcutsAfterSetup && !config.Current.FirstLaunch,
        "Finishing setup must create shortcuts after saving the completed configuration.");
    Require(!(await new ConfigStore(paths).LoadAsync()).FirstLaunch,
        "Shortcut creation must preserve the saved completed setup.");
    shortcutFailure = true;
    await core.FinishOnboardingAsync();
    Require(shortcutCalls == 2 && !config.Current.FirstLaunch && core.State.Screen == BridgeProtocol.Screens.Main,
        "A shortcut write failure must not undo setup or prevent opening the launcher.");

    config.Current.Game.JvmPreset = null;
    await config.SaveAsync();
    var upgradeBlocked = false;
    try { await core.PlayAsync(); }
    catch (LauncherException ex) when (ex.ErrorKey == "error.device.required") { upgradeBlocked = true; }
    Require(upgradeBlocked && core.State.Screen == BridgeProtocol.Screens.OnboardingDevice,
        "An upgraded installation must also ask once when it has no device selection.");
    await core.SetJvmPresetAsync(JvmPresets.Default);
    Require(!config.Current.FirstLaunch && core.State.Screen == BridgeProtocol.Screens.Main,
        "An upgrade device choice must return to the home screen without restarting account onboarding.");
}
finally { Environment.SetEnvironmentVariable("DUSMP_LAUNCHER_HOME", previousHome); }

var onboarding = BridgeProtocol.Screens.Onboarding;
Require(Array.IndexOf(onboarding, "onboarding-device") > Array.IndexOf(onboarding, BridgeProtocol.Screens.OnboardingLanguage)
    && Array.IndexOf(onboarding, "onboarding-device") < Array.IndexOf(onboarding, BridgeProtocol.Screens.OnboardingPlayMode),
    "Device choice must precede play mode in the first-run setup.");

Console.WriteLine("PASS: required onboarding device choice, preset heaps, adjustable RAM, collector conflicts, quoted arguments, legacy settings, persistence and instances folder path.");
