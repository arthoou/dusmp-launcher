using DusmpLauncher.Models;

namespace DusmpLauncher.Network;

/// <summary>
/// Everything the launcher asks of api.dusmp.online. No other type in the
/// codebase is allowed to hold a launcher API URL.
/// </summary>
public interface IApiClient
{
    /// <summary>Base URL in use, for the log pane and the settings screen.</summary>
    string BaseUrl { get; }

    /// <summary>True when the client is serving bundled fixtures instead of HTTP.</summary>
    bool IsOffline { get; }

    Task<LauncherVersionInfo> GetLauncherVersionAsync(CancellationToken ct = default);
    /// <summary>The packs the player can choose between. Empty when the API has none.</summary>
    Task<IReadOnlyList<ModpackEdition>> GetEditionsAsync(CancellationToken ct = default);

    Task<ModpackLatestInfo> GetModpackLatestAsync(string? edition = null, CancellationToken ct = default);

    /// <summary>
    /// The manifest for a version, or for the newest one when
    /// <paramref name="version"/> is null.
    /// </summary>
    Task<ModpackManifest> GetModpackManifestAsync(string? version = null, CancellationToken ct = default);

    Task<IReadOnlyList<OptionalMod>> GetOptionalModsAsync(string? modpackVersion = null, CancellationToken ct = default);
    Task<IReadOnlyList<ServerRegion>> GetRegionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GameServer>> GetServersAsync(CancellationToken ct = default);
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NewsItem>> GetNewsAsync(string? language = null, CancellationToken ct = default);

    /// <summary>
    /// Tells the API which country the player picked and what we measured, and
    /// takes back the CDN bases it wants us to use. Best-effort: a failure here
    /// must not stop the launcher, only fall back to local region selection.
    /// </summary>
    Task<RegionHintResponse?> ReportRegionAsync(RegionHintRequest request, CancellationToken ct = default);

    /// <summary>Cheap liveness probe used by the connection check.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}
