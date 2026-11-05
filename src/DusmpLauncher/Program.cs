using DusmpLauncher.Authentication;
using DusmpLauncher.Bridge;
using DusmpLauncher.Configuration;
using DusmpLauncher.Core;
using DusmpLauncher.Downloads;
using DusmpLauncher.Localization;
using DusmpLauncher.Installation;
using DusmpLauncher.Modpack;
using DusmpLauncher.Network;
using DusmpLauncher.Updates;

namespace DusmpLauncher;

/// <summary>
/// Composition root. Built by hand rather than with a container: the graph is
/// small, fixed, and easier to follow written out than discovered at runtime.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Photino's WebView must own the real main thread, so nothing here may
        // block: all launcher work is kicked off from the window-created callback.
        try
        {
            if (args.Length == 1 && args[0] == "--launcher-version")
            {
                Console.WriteLine(LauncherCore.LauncherVersion);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--apply-launcher-update")
                return UpdateTransaction.ApplyAsync(args[1]).GetAwaiter().GetResult();

            // Windows GUI applications have no console handle to configure.
            if (!OperatingSystem.IsWindows())
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            var paths = LauncherPaths.Resolve();
            paths.EnsureCreated();

            if (args.Contains("--updater", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--updater-once", StringComparer.OrdinalIgnoreCase))
            {
                using var stop = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                var updater = new BackgroundUpdater(paths, stop.Token);
                updater.RunAsync(args.Contains("--updater-once", StringComparer.OrdinalIgnoreCase)).GetAwaiter().GetResult();
                return 0;
            }

            var updateRoot = Path.Combine(paths.Root, "updates");
            Directory.CreateDirectory(updateRoot);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(updateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            LauncherUpdateService.EnsureNoLinks(updateRoot);
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify the launcher executable.");
            if (UpdateTransaction.RecoverAsync(executable, updateRoot).GetAwaiter().GetResult()) return 0;
            using var instance = UpdateProcessLock.TryAcquire(Path.Combine(updateRoot, "instance.lock"));
            if (instance is null)
            {
                Console.Error.WriteLine("DUSMP Launcher is already running.");
                return 2;
            }
            using var lifetime = new CancellationTokenSource();

            using var http = CreateHttpClient();

            var config = new ConfigStore(paths);
            // Development assemblies cannot be replaced by swapping just their apphost.
            using var updates = typeof(Program).Assembly.Location.Length == 0 && !ApiEndpoints.UseFixtures()
                ? new LauncherUpdateService(config, updateRoot, executable, LauncherCore.LauncherVersion) : null;
            var languages = new LanguageManager();

            var api = BuildApiClient(http);
            var mirrors = new MirrorManager();
            // Lets the download path prefer Modrinth/CurseForge for the mods that
            // are public anyway, so DUSMP only serves what is actually ours.
            var externalMirrors = new ExternalMirrorResolver(http);
            var downloads = new DownloadManager(http, config);
            var manifests = new ManifestManager(paths);
            var optionalMods = new OptionalModsManager(() => config.Current, () => config.SaveAsync());
            var modpack = new ModpackManager(paths);
            var ping = new PingTester(http);
            var regions = new RegionSelector(ping);
            var connectivity = new ConnectivityChecker(api);
            var newsTranslator = new NewsTranslator(http);
            var modMetadata = new ModrinthMetadataResolver(http, paths.ModIconCacheFile);
            var appearance = new MinecraftAppearance(http);
            var tokens = new SecureTokenStore(paths);
            // The sign-in window must be built on the toolkit thread, which only
            // exists once UiHost has a window — hence the deferred invoker.
            var uiThread = new UiThreadInvoker();
            var loginWindow = OperatingSystem.IsLinux()
                ? new LinuxWebKitLoginWindow(uiThread.Invoke)
                : (IEmbeddedLoginWindow)new UnsupportedLoginWindow();
            var microsoft = new MicrosoftAuth(http, tokens, paths, loginWindow);
            var offline = new OfflineAuth();
            var game = new GameLauncher(paths, http);

            var core = new LauncherCore(
                paths,
                config,
                api,
                languages,
                modpack,
                manifests,
                optionalMods,
                downloads,
                mirrors,
                regions,
                microsoft,
                offline,
                game,
                connectivity,
                externalMirrors,
                newsTranslator,
                modMetadata,
                appearance,
                // Each edition installs into its own folders, so the three
                // components that live inside an installation are rebuilt when
                // the player switches packs.
                editionPaths => (
                    new ModpackManager(editionPaths),
                    new ManifestManager(editionPaths),
                    new GameLauncher(editionPaths, http)),
                launcherUpdates: updates,
                ensureShortcuts: () => LauncherShortcuts.EnsureWindows(executable));

            var host = new UiHost(core, languages);
            if (updates is not null) updates.ExitRequested += host.Close;
            var confirmed = 0;
            if (args.Length == 3 && args[0] == "--confirm-launcher-update")
                host.UiInitialized += () =>
                {
                    if (Interlocked.Exchange(ref confirmed, 1) == 0)
                        _ = Task.Run(async () =>
                        {
                            try { await UpdateTransaction.ConfirmAsync(args[1], args[2]).ConfigureAwait(false); }
                            catch (Exception ex) { Console.Error.WriteLine($"Update confirmation failed: {ex.Message}"); host.Close(); }
                        });
                };
            uiThread.Bind(action => host.UiThread.Invoke(action));

            host.Run(bridge =>
            {
                // Fire and forget by design: StartAsync reports its own failures
                // into the state machine, which the UI renders as the error screen.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await core.StartAsync(lifetime.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        bridge.Log($"startup crashed: {ex}");
                    }
                });
            });
            lifetime.Cancel();

            return 0;
        }
        catch (Exception ex)
        {
            // The window never came up, so there is nowhere to render this but stderr.
            Console.Error.WriteLine($"DusmpLauncher failed to start: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// One HttpClient for the whole process. Downloads, API calls and latency
    /// probes share the connection pool, which is what makes the mirror fallback
    /// cheap instead of a fresh TLS handshake per retry.
    /// </summary>
    internal static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            MaxConnectionsPerServer = 16,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };

        var client = new HttpClient(handler)
        {
            // Per-request timeouts are applied with CancellationTokens; a global
            // one here would kill long, healthy modpack downloads.
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"DusmpLauncher/{LauncherCore.LauncherVersion} (+https://dusmp.online)");

        return client;
    }

    /// <summary>
    /// Real API, or the bundled fixtures during the test phase. Selected by
    /// <c>DUSMP_API_MODE=fixtures</c> so a dev run never depends on the live host.
    /// </summary>
    private static IApiClient BuildApiClient(HttpClient http) =>
        ApiEndpoints.UseFixtures()
            ? new FixtureApiClient()
            : new ApiClient(http, ApiEndpoints.ResolveBaseUrl());
}
