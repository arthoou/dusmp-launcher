using DusmpLauncher.Configuration;
using DusmpLauncher.Modpack;
using DusmpLauncher.Network;

namespace DusmpLauncher.Core;

/// <summary>Preserve the cause across background tasks and the UI bridge.</summary>
public static class LauncherErrors
{
    public static LauncherException Describe(Exception error) => error switch
    {
        LauncherException known => known,
        GameLaunchException game => new(game.ErrorKey, game.Detail, game),
        ManifestValidationException manifest => new(manifest.ErrorKey, manifest.Detail, manifest),
        ConfigStoreException config => new(config.ErrorKey, config.Detail, config),
        ApiException api => new(api.ErrorKey, api.Detail, api),
        UnauthorizedAccessException access => new("error.install.permission", access.Message, access),
        IOException disk => new("error.install.disk", disk.Message, disk),
        HttpRequestException network => new("error.network.request", network.Message, network),
        _ => new("error.unknown", error.Message, error),
    };
}
