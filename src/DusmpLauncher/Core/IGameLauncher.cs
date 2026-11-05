using System.Diagnostics;
using DusmpLauncher.Authentication;
using DusmpLauncher.Configuration;
using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

/// <summary>
/// Installs the mod loader and starts Minecraft. Knows nothing about the API or
/// about authentication beyond the session it is handed.
/// </summary>
public interface IGameLauncher
{
    /// <summary>Raised for each line of game output, for the log pane.</summary>
    event Action<string>? GameOutput;

    /// <summary>Raised when the game process ends; the int is its exit code.</summary>
    event Action<int>? GameExited;

    /// <summary>
    /// Makes sure the loader named in the manifest is installed and returns the
    /// launchable version id. Cheap when already installed.
    /// </summary>
    Task<string> EnsureLoaderInstalledAsync(
        ModpackManifest manifest,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Same, but installing into another launcher's <c>.minecraft</c> instead of
    /// ours. Used when the pack is handed to the official Minecraft Launcher so
    /// the player can sign in there; it needs the version, its libraries and its
    /// assets under its own directory.
    /// </summary>
    Task<string> EnsureLoaderInstalledInAsync(
        string minecraftRoot,
        ModpackManifest manifest,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default);

    /// <summary>Ends the running game, including its child processes. No-op when idle.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Starts the game. It opens on its own menu: the launcher does not connect
    /// to anything, and the player joins DUSMP from Minecraft's own server list
    /// like any other server.
    /// </summary>
    Task<Process> LaunchAsync(
        string versionId,
        GameSession session,
        GameSettings settings,
        CancellationToken ct = default);
}

/// <summary>
/// Builds the three components that live inside one edition's folders. The
/// launcher calls this when the player switches packs.
/// </summary>
public delegate (Modpack.IModpackManager Modpack, Modpack.IManifestManager Manifests, IGameLauncher Game)
    EditionComponentFactory(LauncherPaths paths);
