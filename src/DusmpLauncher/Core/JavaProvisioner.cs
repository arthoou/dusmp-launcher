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

    /// <summary>Cache of binary path to detected major version, so we probe each one once.</summary>
    private readonly Dictionary<string, int> _majorVersionCache = new(StringComparer.Ordinal);

    public JavaProvisioner(MinecraftPath gamePath)
    {
        ArgumentNullException.ThrowIfNull(gamePath);
        _gamePath = gamePath;
        _resolver = new MinecraftJavaPathResolver(gamePath);
        _runtime = gamePath.Runtime;
    }

    public async Task<string> ResolveJavaAsync(
        IVersion version,
        GameSettings? settings,
        IProgress<ProgressSnapshot>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        ct.ThrowIfCancellationRequested();

        // 1. An explicit setting is the player's call; we only check it exists.
        if (!string.IsNullOrWhiteSpace(settings?.JavaPath))
        {
            var configured = Path.GetFullPath(settings.JavaPath);
            if (File.Exists(configured)) return configured;
            throw new GameLaunchException("error.java.notFound", configured);
        }

        var required = RequiredMajorVersion(version);

        // 2. The runtime the version manifest names. CmlLib knows the on-disk
        //    layout for the current OS, so ask it rather than guessing paths.
        var declared = FindDeclaredJava(version);
        if (declared is not null && !string.IsNullOrWhiteSpace(declared.Component))
        {
            var binary = TryResolveComponent(declared);
            if (binary is not null)
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
        var systemJava = await FindOnPathAsync(ct).ConfigureAwait(false);
        if (systemJava is not null && await SatisfiesAsync(systemJava, required, ct).ConfigureAwait(false))
            return systemJava;

        throw new GameLaunchException(
            "error.java.notFound",
            required > 0
                ? $"No Java {required}+ runtime found for version '{version.Id}' under '{_runtime}'."
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
            if (major <= 0 || major < required)
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
        return major >= required;
    }

    /// <summary>
    /// Runs <c>java -version</c> and reads the major version, mapping the old
    /// <c>1.8.0_x</c> scheme onto 8. Returns 0 when it cannot be determined.
    /// </summary>
    private async Task<int> DetectMajorVersionAsync(string binary, CancellationToken ct)
    {
        lock (_majorVersionCache)
        {
            if (_majorVersionCache.TryGetValue(binary, out var cached))
                return cached;
        }

        var detected = 0;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(binary, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is not null)
            {
                // java -version writes to stderr on every JDK worth supporting.
                var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                var match = VersionPattern.Match(stderr + stdout);
                if (match.Success && int.TryParse(match.Groups["major"].Value, out var major))
                {
                    // "1.8.0_202" means 8; "21.0.7" means 21.
                    detected = major == 1 && int.TryParse(match.Groups["legacyMinor"].Value, out var minor)
                        ? minor
                        : major;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            detected = 0;
        }

        lock (_majorVersionCache)
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
