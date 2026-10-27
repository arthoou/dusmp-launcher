using System.Reflection;
using System.Text.Json;
using DusmpLauncher.Models;

namespace DusmpLauncher.Network;

/// <summary>Loads bundled deterministic API responses for development while the service is private.</summary>
public sealed class FixtureApiClient : IApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly Assembly _assembly = typeof(FixtureApiClient).Assembly;
    public string BaseUrl => "fixtures://embedded";
    public bool IsOffline => true;
    public Task<LauncherVersionInfo> GetLauncherVersionAsync(CancellationToken ct = default) => ReadAsync<LauncherVersionInfo>("launcher-version.json", ct);
    // Sem arquivo de fixture a lista vem vazia, que e como o launcher trata uma
    // API sem edicoes: instala o pack unico que os outros endpoints servem.
    public async Task<IReadOnlyList<ModpackEdition>> GetEditionsAsync(CancellationToken ct = default) => await ReadItemsAsync<ModpackEdition>("modpack-editions.json", ct).ConfigureAwait(false);
    public Task<ModpackLatestInfo> GetModpackLatestAsync(string? edition = null, CancellationToken ct = default) => ReadAsync<ModpackLatestInfo>("modpack-latest.json", ct);
    public Task<ModpackManifest> GetModpackManifestAsync(string? version = null, CancellationToken ct = default) => ReadAsync<ModpackManifest>("modpack-manifest.json", ct);
    public async Task<IReadOnlyList<OptionalMod>> GetOptionalModsAsync(string? modpackVersion = null, CancellationToken ct = default) => await ReadItemsAsync<OptionalMod>("optional-mods.json", ct).ConfigureAwait(false);
    public async Task<IReadOnlyList<ServerRegion>> GetRegionsAsync(CancellationToken ct = default) => await ReadItemsAsync<ServerRegion>("regions.json", ct).ConfigureAwait(false);
    public async Task<IReadOnlyList<GameServer>> GetServersAsync(CancellationToken ct = default) => await ReadItemsAsync<GameServer>("servers.json", ct).ConfigureAwait(false);
    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) => ReadAsync<ServiceStatus>("status.json", ct);
    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(string? language = null, CancellationToken ct = default) => await ReadItemsAsync<NewsItem>("news.json", ct).ConfigureAwait(false);
    public async Task<RegionHintResponse?> ReportRegionAsync(RegionHintRequest request, CancellationToken ct = default)
    {
        var region = (await GetRegionsAsync(ct).ConfigureAwait(false)).Where(x => x.Available)
            .OrderByDescending(x => x.Countries.Contains(request.Country, StringComparer.OrdinalIgnoreCase)).ThenBy(x => x.Priority).FirstOrDefault();
        return region is null ? null : new() { RegionId = region.Id, CdnBases = region.CdnBases };
    }
    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);

    private async Task<T> ReadAsync<T>(string filename, CancellationToken ct)
    {
        var resource = _assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("Network.Fixtures." + filename, StringComparison.Ordinal));
        if (resource is null) throw new FileNotFoundException("Embedded API fixture is missing.", filename);
        await using var stream = _assembly.GetManifestResourceStream(resource)!;
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Embedded API fixture '{filename}' is empty.");
    }

    private async Task<IReadOnlyList<T>> ReadItemsAsync<T>(string filename, CancellationToken ct)
    {
        var collection = await ReadAsync<FixtureItems<T>>(filename, ct).ConfigureAwait(false);
        return collection.Items is { } items ? items : Array.Empty<T>();
    }
    private sealed class FixtureItems<T> { public List<T>? Items { get; set; } }
}
