using DusmpLauncher.Models;

namespace DusmpLauncher.Network;

/// <summary>
/// Picks the region to download from. The player's country is one input among
/// several — measured latency and availability matter just as much, and the
/// final choice is always automatic.
/// </summary>
public interface IRegionSelector
{
    /// <summary>
    /// Scores every region and returns them best-first. Never throws for a
    /// region that fails to answer: that region just scores last with a reason.
    /// </summary>
    Task<IReadOnlyList<RegionMeasurement>> EvaluateAsync(
        IReadOnlyList<ServerRegion> regions,
        string? country,
        CancellationToken ct = default);
}

/// <summary>Measures round-trip latency to a URL or host.</summary>
public interface IPingTester
{
    /// <summary>
    /// Median latency in ms over a few small probes, or null when every probe
    /// failed. Must respect <paramref name="ct"/> promptly — this runs on the
    /// loading screen and may not stall startup.
    /// </summary>
    Task<int?> MeasureAsync(string url, int probes = 3, CancellationToken ct = default);
}
