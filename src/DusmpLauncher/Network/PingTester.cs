using System.Diagnostics;

namespace DusmpLauncher.Network;

public sealed class PingTester : IPingTester
{
    private readonly HttpClient _http;
    public PingTester(HttpClient http) => _http = http;

    public async Task<int?> MeasureAsync(string url, int probes = 3, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var samples = new List<int>();
        for (var i = 0; i < Math.Clamp(probes, 1, 5); i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var clock = Stopwatch.StartNew();
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode < 500) samples.Add((int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        if (samples.Count == 0) return null;
        samples.Sort();
        return samples[samples.Count / 2];
    }
}
