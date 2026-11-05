using CmlLib.Core;
using CmlLib.Core.Installers;
using CmlLib.Core.Java;
using CmlLib.Core.Rules;
using CmlLib.Core.Version;
using DusmpLauncher.Core;
using DusmpLauncher.Models;

internal static class LiveJavaCheck
{
    // Explicit opt-in: download a real Mojang runtime into a fresh temporary
    // directory and probe Java. No Minecraft game, account, or user data is used.
    public static async Task RunAsync()
    {
        var home = Path.Combine(Path.GetTempPath(), "DUSMP clean Java " + Guid.NewGuid().ToString("N"));
        try
        {
            var path = new MinecraftPath(home);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var installer = new MojangJavaRuntimeInstaller(http, path,
                new ParallelGameInstaller(4, 6, 24, http), new RulesEvaluatorContext(LauncherOSRule.Current));
            var attempts = 0;
            var java = new JavaProvisioner(path, installRuntime: async (component, progress, ct) =>
            {
                attempts++;
                await installer.InstallAsync(component, progress, ct);
            }, findSystemJava: _ => Task.FromResult<string?>(null));
            var version = new MinecraftVersion("1.21.1") { JavaVersion = new JavaVersion("java-runtime-delta", "21") };
            var lastReport = DateTime.MinValue;
            var progress = new LiveProgress(value =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalSeconds < 3 || value.TotalBytes <= 0) return;
                lastReport = now;
                Console.WriteLine($"Java runtime: {value.DownloadedBytes / 1048576d:F1}/{value.TotalBytes / 1048576d:F1} MiB");
            });
            var binary = await java.ResolveJavaAsync(version, null, progress, budget.Token);
            if (!File.Exists(binary) || attempts != 1)
                throw new Exception("A clean runtime directory did not trigger one successful Java installation.");
            if (await java.ResolveJavaAsync(version, null, null, budget.Token) != binary || attempts != 1)
                throw new Exception("The installed runtime was not reused on the second resolve.");
            Console.WriteLine("PASS: real Mojang Java 21 installed, every file verified, executable version probed, and warm runtime reused; system Java discovery disabled.");
        }
        finally
        {
            var target = Path.GetFullPath(home);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("DUSMP clean Java ", StringComparison.Ordinal))
                throw new InvalidOperationException("The live-check cleanup path escaped its temporary directory.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private sealed class LiveProgress(Action<ProgressSnapshot> report) : IProgress<ProgressSnapshot>
    {
        public void Report(ProgressSnapshot value) => report(value);
    }
}
