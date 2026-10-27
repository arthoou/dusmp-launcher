using DusmpLauncher.Models;

namespace DusmpLauncher.Network;

public sealed class RegionSelector : IRegionSelector
{
    private readonly IPingTester _ping;
    public RegionSelector(IPingTester ping) => _ping = ping;

    public async Task<IReadOnlyList<RegionMeasurement>> EvaluateAsync(IReadOnlyList<ServerRegion> regions, string? country, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(regions.Select(async region =>
        {
            if (!region.Available) return new RegionMeasurement(region, null, false, double.MaxValue, "unavailable");
            var latency = string.IsNullOrWhiteSpace(region.PingUrl) ? null : await _ping.MeasureAsync(region.PingUrl, 3, ct).ConfigureAwait(false);
            var match = !string.IsNullOrWhiteSpace(country) && region.Countries.Contains(country, StringComparer.OrdinalIgnoreCase);
            var score = (latency ?? 5000) - (match ? 25 : 0) + Math.Max(0, region.Priority) * 0.01;
            return new RegionMeasurement(region, latency, match, score, latency is null ? "probe-failed" : null);
        })).ConfigureAwait(false);
        return results.OrderBy(r => r.Score).ThenBy(r => r.Region.Id, StringComparer.Ordinal).ToArray();
    }
}
