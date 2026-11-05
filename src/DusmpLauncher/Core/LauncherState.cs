using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

/// <summary>What the game is doing right now, for the Play button's benefit.</summary>
public enum GameRunState
{
    Idle = 0,
    Preparing = 1,
    Launching = 2,
    Running = 3,
    Crashed = 4,
}

/// <summary>
/// The complete snapshot the UI renders from. Sent whole on <c>getState</c> and
/// after any change big enough that a diff would be more code than a resend.
/// Contains no tokens and no absolute paths the player did not choose.
/// </summary>
public sealed record LauncherState
{
    /// <summary>Which screen the UI should be showing.</summary>
    public string Screen { get; init; } = Bridge.BridgeProtocol.Screens.Loading;

    public bool FirstLaunch { get; init; } = true;

    public string? JvmPreset { get; init; }

    /// <summary>Active UI language tag.</summary>
    public string Language { get; init; } = "en-US";

    public bool RightToLeft { get; init; }

    public string? Country { get; init; }
    /// <summary>Which pack is installed and played; null until the player chooses.</summary>
    public string? Edition { get; init; }

    public string PlayMode { get; init; } = "dusmp";
    public string? ExternalLauncherId { get; init; }

    public PlayerProfile Profile { get; init; } = PlayerProfile.Empty;

    public ProgressSnapshot Progress { get; init; } = ProgressSnapshot.Initial;

    /// <summary>Modpack version installed on disk, null before the first install.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Newest modpack version the API knows about.</summary>
    public string? LatestVersion { get; init; }

    public string? InstancePath { get; init; }
    public string? MinecraftVersion { get; init; }

    public string LauncherVersion { get; init; } = "0.0.0";

    /// <summary>
    /// False while the launcher is still on Minecraft's built-in sign-in id,
    /// which Xbox Live refuses. The account screen uses this to say so up front
    /// instead of sending the player through the whole browser flow first.
    /// </summary>
    public bool MicrosoftSignInConfigured { get; init; }

    /// <summary>Set when the API reports a launcher update; the UI shows a banner.</summary>
    public LauncherVersionInfo? LauncherUpdate { get; init; }

    /// <summary>True once the install matches the manifest and Play may run.</summary>
    public bool ReadyToPlay { get; init; }

    public GameRunState GameState { get; init; }

    public IReadOnlyList<GameServerStatus> Servers { get; init; } = Array.Empty<GameServerStatus>();

    public IReadOnlyList<OptionalModView> OptionalMods { get; init; } = Array.Empty<OptionalModView>();

    public IReadOnlyList<NewsItem> News { get; init; } = Array.Empty<NewsItem>();

    public ServiceStatus? Status { get; init; }

    /// <summary>Region the selector chose, for the settings screen and the logs.</summary>
    public string? RegionId { get; init; }

    /// <summary>True when the launcher is running on bundled fixtures, not the real API.</summary>
    public bool OfflineFixtures { get; init; }

    /// <summary>Set only when startup failed outright; the UI shows the error screen.</summary>
    public string? FatalErrorKey { get; init; }
    public string? FatalErrorDetail { get; init; }
    public bool LauncherUpdatePending { get; init; }
}
