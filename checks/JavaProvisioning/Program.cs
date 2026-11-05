using CmlLib.Core;
using CmlLib.Core.Java;
using CmlLib.Core.Rules;
using CmlLib.Core.Version;
using DusmpLauncher.Configuration;
using DusmpLauncher.Core;
using DusmpLauncher.Models;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task ExpectInstallFailure(Func<Task> action, string message)
{
    try { await action(); }
    catch (GameLaunchException ex) when (ex.ErrorKey == "error.install.minecraftFailed") { return; }
    throw new InvalidOperationException(message);
}

// Every executable is a harmless placeholder. Version probes and system-Java
// discovery are injected, so these checks never depend on the developer's PATH,
// execute a Java binary, download anything, or touch a player's instance.
using (var rig = new RuntimeRig())
{
    var downloads = 0;
    var progress = new List<ProgressSnapshot>();
    var provisioner = rig.Create((component, reporter, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        Require(component.Component == "java-runtime-delta" && component.MajorVersion == "21",
            "A fresh NeoForge installation must request the exact inherited Java 21 component.");
        downloads++;
        rig.Install(component, 21);
        reporter?.Report(new ProgressSnapshot { Stage = LauncherStage.InstallingMinecraft, DownloadedBytes = 7, TotalBytes = 7 });
        return Task.CompletedTask;
    });
    Require(JavaProvisioner.RequiredMajorVersion(rig.Loader) == 21, "NeoForge must inherit Minecraft's Java requirement.");
    var path = await provisioner.ResolveJavaAsync(rig.Loader, null, new InlineProgress<ProgressSnapshot>(progress.Add), default);
    Require(path == rig.Binary(rig.Java21) && downloads == 1,
        "A clean instance must provision Java before returning its executable.");
    Require(path.Contains("DUSMP Java checks ", StringComparison.Ordinal), "The test must exercise a path containing spaces.");
    Require(progress.Any(p => p.DownloadedBytes == 7 && p.TotalBytes == 7), "Provisioning progress must reach the caller.");
    Require(await provisioner.ResolveJavaAsync(rig.Loader, null, null, default) == path && downloads == 1,
        "A warm resolve must reuse the installed runtime without downloading it again.");
}

using (var rig = new RuntimeRig())
{
    rig.Install(rig.Java21, 21);
    var provisioner = rig.Create((_, _, _) => throw new InvalidOperationException("An already valid Java runtime must not download."));
    Require(await provisioner.ResolveJavaAsync(rig.Loader, null, null, default) == rig.Binary(rig.Java21),
        "A preinstalled declared Java 21 runtime must be reused.");
}

using (var rig = new RuntimeRig())
{
    rig.Install(new JavaVersion("jre-legacy", "8"), 8);
    var downloads = 0;
    var provisioner = rig.Create((component, _, _) => { downloads++; rig.Install(component, 21); return Task.CompletedTask; });
    Require(await provisioner.ResolveJavaAsync(rig.Loader, null, null, default) == rig.Binary(rig.Java21) && downloads == 1,
        "Java 8 in another managed component must not satisfy a Java 21 loader.");
}

foreach (var brokenMajor in new[] { 0, 8, 25 })
{
    using var rig = new RuntimeRig();
    rig.Install(rig.Java21, brokenMajor);
    var downloads = 0;
    var provisioner = rig.Create((component, _, _) => { downloads++; rig.Install(component, 21); return Task.CompletedTask; });
    Require(await provisioner.ResolveJavaAsync(rig.Loader, null, null, default) == rig.Binary(rig.Java21) && downloads == 1,
        "An existing but corrupt or wrong-version declared component must be repaired.");
    Require(rig.ProbeCalls.GetValueOrDefault(rig.Binary(rig.Java21)) >= 2,
        "A repaired runtime must be probed again instead of reusing a cached failure.");
}

using (var rig = new RuntimeRig())
{
    var attempts = 0;
    var provisioner = rig.Create((component, _, _) =>
    {
        if (++attempts == 1) throw new IOException("simulated interrupted download");
        rig.Install(component, 21);
        return Task.CompletedTask;
    });
    await ExpectInstallFailure(() => provisioner.ResolveJavaAsync(rig.Loader, null, null, default),
        "A failed runtime download must report an installation failure.");
    Require(await provisioner.ResolveJavaAsync(rig.Loader, null, null, default) == rig.Binary(rig.Java21) && attempts == 2,
        "Retry after a failed download must be able to provision the runtime.");
}

foreach (var downloadedMajor in new[] { 0, 8, 25 })
{
    using var rig = new RuntimeRig();
    var provisioner = rig.Create((component, _, _) => { rig.Install(component, downloadedMajor); return Task.CompletedTask; });
    await ExpectInstallFailure(() => provisioner.ResolveJavaAsync(rig.Loader, null, null, default),
        "A downloader that leaves a corrupt or wrong-version binary must not be treated as successful.");
}

using (var rig = new RuntimeRig())
using (var stop = new CancellationTokenSource())
{
    var attempts = 0;
    var provisioner = rig.Create((component, _, ct) =>
    {
        attempts++;
        if (attempts == 1) { stop.Cancel(); return Task.FromCanceled(ct); }
        rig.Install(component, 21);
        return Task.CompletedTask;
    });
    var cancelled = false;
    try { await provisioner.ResolveJavaAsync(rig.Loader, null, null, stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
    Require(cancelled, "Cancelling runtime provisioning must propagate cancellation, not a missing-Java error.");
    Require(await provisioner.ResolveJavaAsync(rig.Loader, null, null, default) == rig.Binary(rig.Java21) && attempts == 2,
        "Retry after cancellation must still be able to provision the runtime.");
}

using (var rig = new RuntimeRig())
using (var stop = new CancellationTokenSource())
{
    stop.Cancel();
    var provisioner = rig.Create((_, _, _) => throw new InvalidOperationException("Pre-cancelled work must not start a download."));
    var cancelled = false;
    try { await provisioner.ResolveJavaAsync(rig.Loader, null, null, stop.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Require(cancelled, "A pre-cancelled resolve must terminate immediately.");
}

using (var rig = new RuntimeRig())
{
    var provisioner = rig.Create((_, _, _) => throw new InvalidOperationException("A missing explicit Java path must not silently install an override."));
    var rejected = false;
    try { await provisioner.ResolveJavaAsync(rig.Loader, new GameSettings { JavaPath = Path.Combine(rig.Home, "missing", "java.exe") }, null, default); }
    catch (GameLaunchException ex) when (ex.ErrorKey == "error.java.notFound") { rejected = true; }
    Require(rejected, "An explicitly selected nonexistent Java binary must produce the Java-path error.");
}

await RuntimeInstallChecks.RunAsync();
if (args.Contains("--live", StringComparer.OrdinalIgnoreCase)) await LiveJavaCheck.RunAsync();
Console.WriteLine("PASS: fresh Java provisioning, inherited Java 21 requirement, exact runtime version, warm reuse, corrupt runtime repair, verified downloads, cancellation/retry and paths with spaces.");

sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

sealed class RuntimeRig : IDisposable
{
    private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ProbeCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "DUSMP Java checks " + Guid.NewGuid().ToString("N"));
    public MinecraftPath GamePath { get; }
    public JavaVersion Java21 { get; } = new("java-runtime-delta", "21");
    public MinecraftVersion Loader { get; }

    public RuntimeRig()
    {
        GamePath = new MinecraftPath(Home);
        var vanilla = new MinecraftVersion("1.21.1") { JavaVersion = Java21 };
        Loader = new MinecraftVersion("neoforge-21.1.248") { InheritsFrom = vanilla.Id, ParentVersion = vanilla };
    }

    public string Binary(JavaVersion component) => new MinecraftJavaPathResolver(GamePath)
        .GetJavaBinaryPath(component, new RulesEvaluatorContext(LauncherOSRule.Current));

    public void Install(JavaVersion component, int reportedMajor)
    {
        var binary = Binary(component);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "test-placeholder-not-an-executable");
        _versions[binary] = reportedMajor;
    }

    public JavaProvisioner Create(Func<JavaVersion, IProgress<ProgressSnapshot>?, CancellationToken, Task> installer) =>
        new(GamePath, installRuntime: installer, probeVersion: (binary, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            ProbeCalls[binary] = ProbeCalls.GetValueOrDefault(binary) + 1;
            return Task.FromResult(_versions.GetValueOrDefault(binary));
        }, findSystemJava: _ => Task.FromResult<string?>(null));

    public void Dispose()
    {
        var target = Path.GetFullPath(Home);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("DUSMP Java checks ", StringComparison.Ordinal))
            throw new InvalidOperationException("The test cleanup target escaped its temporary directory.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
