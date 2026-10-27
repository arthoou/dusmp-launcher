namespace DusmpLauncher.Network;

/// <summary>Fast best-effort check against the same API the launcher will use.</summary>
public sealed class ConnectivityChecker
{
    private readonly IApiClient _api;

    public ConnectivityChecker(IApiClient api) => _api = api;

    public async Task<bool> IsOnlineAsync(CancellationToken ct = default)
    {
        if (_api.IsOffline) return true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            return await _api.PingAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
}
