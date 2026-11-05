using System.Diagnostics;
using System.Text.RegularExpressions;
using CmlLib.Core;
using CmlLib.Core.Java;
using CmlLib.Core.Rules;
using CmlLib.Core.Version;
using DusmpLauncher.Configuration;
using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

/// <summary>
/// Picks the Java binary a given Minecraft version must run on.
///
/// This has to be exact, not approximate. A modpack instance usually has more
/// than one managed runtime installed at once — 1.21.1 pulls
/// <c>java-runtime-delta</c> (Java 21) while older assets pull
/// <c>jre-legacy</c> (Java 8) — and launching NeoForge 1.21 on the Java 8
/// binary fails with "Unrecognized option: -p", because module arguments did
/// not exist before Java 9. So the runtime is chosen from the component name
/// the version manifest declares, never by scanning the directory.
/// </summary>
public sealed class JavaProvisioner
{
    /// <summary>Matches the major version out of <c>java -version</c> on stderr.</summary>
    private static readonly Regex VersionPattern =
        new(@"version\s+""(?<major>\d+)(?:\.(?<legacyMinor>\d+))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MinecraftPath _gamePath;
    private readonly MinecraftJavaPathResolver _resolver;
    private readonly string _runtime;
    private readonly Func<JavaVersion, IProgress<ProgressSnapshot>?, CancellationToken, Task>? _installRuntime;
    private readonly Func<string, CancellationToken, Task<int>>? _probeVersion;
    private readonly Func<CancellationToken, Task<string?>> _findSystemJava;

    /// <summary>Cache of binary path to detected major version, so we probe each one once.</summary>
    private readonly Dictionary<string, int> _majorVersionCache = new(StringComparer.Ordinal);

    public JavaProvisioner(MinecraftPath gamePath,
        Func<JavaVersion, IProgress<ProgressSnapshot>?, CancellationToken, Task>? installRuntime = null,
        Func<string, CancellationToken, Task<int>>? probeVersion = null,
        Func<CancellationToken, Task<string?>>? findSystemJava = null)
    {
        ArgumentNullException.ThrowIfNull(gamePath);
        _gamePath = gamePath;
        _resolver = new MinecraftJavaPathResolver(gamePath);
        _runtime = gamePath.Runtime;
        _installRuntime = installRuntime;
        _probeVersion = probeVersion;
        _findSystemJava = findSystemJava ?? FindOnPathAsync;
    }

    public async Task<string> ResolveJavaAsync(
        IVersion version,
        GameSettings? settings,
        IProgress<ProgressSnapshot>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        ct.ThrowIfCancellationRequested();

        var required = RequiredMajorVersion(version);

        // 1. Respect an explicit runtime, but report an incompatible Java here
        // instead of letting the JVM fail on the loader's module arguments.
        if (!string.IsNullOrWhiteSpace(settings?.JavaPath))
        {
            var configured = Path.GetFullPath(settings.JavaPath);
            if (File.Exists(configured) && await SatisfiesAsync(configured, required, ct).ConfigureAwait(false))
                return configured;
            throw new GameLaunchException("error.java.notFound", configured);
        }

        // 2. The runtime the version manifest names. CmlLib knows the on-disk
        //    layout for the current OS, so ask it rather than guessing paths.
        var declared = FindDeclaredJava(version);
        if (declared is not null && !string.IsNullOrWhiteSpace(declared.Component))
        {
            var binary = TryResolveComponent(declared);
            if (binary is not null && await SatisfiesAsync(binary, required, ct).ConfigureAwait(false))
                return binary;
        }

        // 3. Nothing declared, or it is not installed: take any managed runtime
        //    that is actually new enough, newest major first.
        var managed = await FindManagedAsync(required, ct).ConfigureAwait(false);
        if (managed is not null)
            return managed;

        // 4. Last resort, a system Java — but only when it satisfies the version.
        //    Launching on a too-old system JVM produces a cryptic JVM error
        //    instead of an actionable message, so it is better to fail here.
        var systemJava = await _findSystemJava(ct).ConfigureAwait(false);
        if (systemJava is not null && await SatisfiesAsync(systemJava, required, ct).ConfigureAwait(false))
            return systemJava;

        // Forge/NeoForge need Java BEFORE their installer can run. Waiting for
        // the later game-install step to fetch it leaves fresh PCs in a loop.
        if (declared is not null && _installRuntime is not null)
        {
            try
            {
                await _installRuntime(declared, progress, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_majorVersionCache) _majorVersionCache.Clear();
                var installed = TryResolveComponent(declared);
                if (installed is not null && await SatisfiesAsync(installed, required, ct).ConfigureAwait(false))
                    return installed;
                throw new InvalidDataException($"The installed {declared.Component} runtime does not provide Java {required}.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new GameLaunchException("error.install.minecraftFailed",
                    $"Could not install Java {required} ({declared.Component}): {ex.Message}", ex);
            }
        }

        throw new GameLaunchException(
            "error.java.notFound",
            required > 0
                ? $"No Java {required} runtime found for version '{version.Id}' under '{_runtime}'."
                : $"No Java runtime found for version '{version.Id}' under '{_runtime}'.");
    }

    /// <summary>
    /// Major version the version manifest asks for, or 0 when nothing in the
    /// inheritance chain says.
    /// </summary>
    public static int RequiredMajorVersion(IVersion version)
    {
        var declared = FindDeclaredJava(version)?.MajorVersion;
        return int.TryParse(declared, out var major) ? major : 0;
    }

    /// <summary>
    /// Walks the version inheritance chain for a declared runtime.
    /// A loader version such as <c>neoforge-21.1.244</c> carries no
    /// <c>javaVersion</c> of its own — it inherits 1.21.1's — so reading only
    /// the top version reports "no requirement" and any runtime looks
    /// acceptable, including Java 8.
    /// </summary>
    private static JavaVersion? FindDeclaredJava(IVersion? version)
    {
        // Bounded in case a malformed pair of versions points at each other.
        for (var depth = 0; version is not null && depth < 16; depth++)
        {
            if (version.JavaVersion is { } java && !string.IsNullOrWhiteSpace(java.Component))
                return java;
            version = version.ParentVersion;
        }
        return null;
    }

    /// <summary>The binary for a declared component, or null when it is not installed.</summary>
    private string? TryResolveComponent(JavaVersion component)
    {
        try
        {
            var binary = _resolver.GetJavaBinaryPath(component, RulesContext());
            return File.Exists(binary) ? binary : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // An unknown component name is not fatal; the scan below still runs.
            return null;
        }
    }

    /// <summary>
    /// Scans the managed runtime directory and returns the newest binary that
    /// meets <paramref name="required"/>, probing each candidate's real version.
    /// </summary>
    private async Task<string?> FindManagedAsync(int required, CancellationToken ct)
    {
        if (!Directory.Exists(_runtime))
            return null;

        var names = OperatingSystem.IsWindows()
            ? new[] { "javaw.exe", "java.exe" }
            : new[] { "java" };

        var candidates = names
            .SelectMany(name => Directory.EnumerateFiles(_runtime, name, SearchOption.AllDirectories))
            // Only ever a real launcher binary, never something under lib/.
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "bin", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string? best = null;
        var bestMajor = -1;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var major = await DetectMajorVersionAsync(candidate, ct).ConfigureAwait(false);
            if (major <= 0 || (required > 0 && major != required))
                continue;

            // With a known requirement, take the lowest runtime that meets it so
            // a pinned pack is not silently moved onto a newer JVM. With no
            // requirement at all, take the highest: an instance often still has
            // jre-legacy (Java 8) lying around for old assets, and defaulting to
            // it breaks every modern loader.
            var better = bestMajor < 0 || (required > 0 ? major < bestMajor : major > bestMajor);
            if (better)
            {
                best = candidate;
                bestMajor = major;
            }
        }

        return best;
    }

    private async Task<bool> SatisfiesAsync(string binary, int required, CancellationToken ct)
    {
        if (required <= 0) return true;
        var major = await DetectMajorVersionAsync(binary, ct).ConfigureAwait(false);
        // Keep the declared major: Java 21 GC flags and loader compatibility do
        // not automatically carry over to Java 24+ or another arbitrary major.
        return major == required;
    }

    /// <summary>
    /// Runs <c>java -version</c> and reads the major version, mapping the old
    /// <c>1.8.0_x</c> scheme onto 8. Returns 0 when it cannot be determined.
    /// </summary>
    private async Task<int> DetectMajorVersionAsync(string binary, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_probeVersion is not null)
            return await _probeVersion(binary, ct).ConfigureAwait(false);
        lock (_majorVersionCache)
        {
            if (_majorVersionCache.TryGetValue(binary, out var cached))
                return cached;
        }

        var detected = 0;
        Process? process = null;
        try
        {
            // javaw may not expose version output; its console sibling is safe
            // to probe with CreateNoWindow, while the game still uses javaw.
            var consoleJava = Path.Combine(Path.GetDirectoryName(binary)!, "java.exe");
            var probe = OperatingSystem.IsWindows() && Path.GetFileName(binary).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(consoleJava) ? consoleJava : binary;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            process = Process.Start(new ProcessStartInfo(probe, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is not null)
            {
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(stderr, stdout, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);

                var match = VersionPattern.Match(await stderr.ConfigureAwait(false) + await stdout.ConfigureAwait(false));
                if (process.ExitCode == 0 && match.Success && int.TryParse(match.Groups["major"].Value, out var major))
                {
                    // "1.8.0_202" means 8; "21.0.7" means 21.
                    detected = major == 1 && int.TryParse(match.Groups["legacyMinor"].Value, out var minor)
                        ? minor
                        : major;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            detected = 0;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
        }

        if (detected > 0) lock (_majorVersionCache)
        {
            _majorVersionCache[binary] = detected;
        }

        return detected;
    }

    private static RulesEvaluatorContext RulesContext() => new(LauncherOSRule.Current);

    private static Task<string?> FindOnPathAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var executable = OperatingSystem.IsWindows() ? "where.exe" : "/bin/sh";
        var args = OperatingSystem.IsWindows() ? "java.exe" : "-lc \"command -v java\"";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            process.WaitForExit(1500);
            return !string.IsNullOrWhiteSpace(output) && File.Exists(output.Trim()) ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }, ct);
}
