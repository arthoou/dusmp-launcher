using System.Runtime.InteropServices;

namespace DusmpLauncher.Core;

/// <summary>
/// Every path the launcher touches, resolved once. Also the single place that
/// decides which files are ours and which belong to the player — the update
/// logic asks here before it deletes anything.
/// </summary>
public sealed class LauncherPaths
{
    /// <summary>Folder name used under the per-user data directory.</summary>
    public const string AppFolderName = "DusmpLauncher";

    private LauncherPaths(string root, string? edition = null)
    {
        Root = root;
        Edition = edition;
        Cache = System.IO.Path.Combine(root, "cache");
        Logs = System.IO.Path.Combine(root, "logs");
        ConfigFile = System.IO.Path.Combine(root, "config.json");
        AccountsFile = System.IO.Path.Combine(root, "accounts.json");
        ModIconCacheFile = System.IO.Path.Combine(Cache, "mod-icons.json");
        SecretsFile = System.IO.Path.Combine(root, "secrets.bin");
        SecretsKeyFile = System.IO.Path.Combine(root, "secrets.key");

        // An edition is its own game folder, its own ledger and its own cached
        // manifest: the two packs are different Minecraft versions with
        // different mods, and sharing any of those would have each one treating
        // the other's files as obsolete and deleting them.
        //
        // No edition means the original single-pack layout, which is what an
        // install from before editions still has on disk.
        if (string.IsNullOrWhiteSpace(edition))
        {
            Instance = System.IO.Path.Combine(root, "instance");
            ManagedIndexFile = System.IO.Path.Combine(root, "managed-files.json");
            ManifestCacheFile = System.IO.Path.Combine(Cache, "manifest.json");
            return;
        }

        Instance = System.IO.Path.Combine(root, "instances", edition);
        ManagedIndexFile = System.IO.Path.Combine(root, "instances", edition, "managed-files.json");
        ManifestCacheFile = System.IO.Path.Combine(Cache, $"manifest-{edition}.json");
    }

    /// <summary>The edition these paths belong to, or null for the pre-editions layout.</summary>
    public string? Edition { get; }

    /// <summary>
    /// The same launcher folder seen as one edition's installation. Everything
    /// shared — config, accounts, tokens, logs — keeps its place; only the game
    /// folder and the two files that describe it move.
    /// </summary>
    public LauncherPaths ForEdition(string edition) =>
        string.IsNullOrWhiteSpace(edition) ? this : new LauncherPaths(Root, edition.Trim().ToLowerInvariant());

    public string Root { get; }

    /// <summary>The .minecraft-equivalent folder. Mods, configs, saves all live under here.</summary>
    public string Instance { get; }

    public string Cache { get; }
    public string Logs { get; }
    public string ConfigFile { get; }
    public string AccountsFile { get; }
    public string ManifestCacheFile { get; }

    /// <summary>Modrinth answers about pack jars, keyed by SHA-1. Safe to delete.</summary>
    public string ModIconCacheFile { get; }

    /// <summary>Ledger of files the launcher wrote, so obsolete ones can be removed safely.</summary>
    public string ManagedIndexFile { get; }

    public string SecretsFile { get; }
    public string SecretsKeyFile { get; }

    /// <summary>
    /// Instance-relative directories the launcher must never delete from, even
    /// when a path there is absent from the manifest. Player-owned, full stop.
    /// </summary>
    public static readonly string[] PlayerOwnedDirectories =
    {
        "saves",
        "screenshots",
        "resourcepacks",
        "shaderpacks",
        "logs",
        "crash-reports",
        "backups",
        "schematics",
        "journeymap",
        "xaero",
        "replay_recordings",
        "local",
    };

    /// <summary>
    /// Instance-relative files the launcher never overwrites once they exist,
    /// because the player edits them in game.
    /// </summary>
    public static readonly string[] PlayerOwnedFiles =
    {
        "options.txt",
        "optionsof.txt",
        "optionsshaders.txt",
        "servers.dat",
        "servers.dat_old",
        "usercache.json",
        "usernamecache.json",
        "realms_persistence.json",
        "hotbar.nbt",
        "knownhosts.txt",
    };

    public static LauncherPaths Resolve()
    {
        var env = Environment.GetEnvironmentVariable("DUSMP_LAUNCHER_HOME");
        if (!string.IsNullOrWhiteSpace(env))
            return new LauncherPaths(System.IO.Path.GetFullPath(env));

        string baseDir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            baseDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
        }
        else
        {
            // XDG on Linux, with the spec's own fallback when the var is unset.
            baseDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");
        }

        return new LauncherPaths(System.IO.Path.Combine(baseDir, AppFolderName));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Instance);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Logs);
    }

    /// <summary>Absolute path for a manifest-relative path, rejecting traversal.</summary>
    public string ResolveInstanceFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Manifest path is empty.", nameof(relativePath));

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var combined = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Instance, normalized.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        var instanceRoot = System.IO.Path.GetFullPath(Instance) + System.IO.Path.DirectorySeparatorChar;
        if (!combined.StartsWith(instanceRoot, StringComparison.Ordinal))
            throw new InvalidOperationException($"Manifest path escapes the instance folder: {relativePath}");

        return combined;
    }

    /// <summary>
    /// True when a manifest-relative path may be deleted by the updater. Player
    /// directories and player files are protected regardless of the manifest.
    /// </summary>
    /// <summary>
    /// Deletes everything the launcher put in the instance, and nothing else.
    /// Returns how many entries were removed.
    ///
    /// Works top-down over the instance root rather than from the managed-file
    /// index on purpose: the index only lists what the *current* manifest
    /// declared, so a loader, its libraries or a mod from a manifest that has
    /// since changed would survive exactly the cleanup meant to remove them.
    /// </summary>
    public static int ResetInstance(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.Instance)) return 0;

        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.Instance))
        {
            var name = Path.GetFileName(entry);
            // A player-owned name is off limits whether it is a file or a folder.
            if (!IsDeletable(name + "/") || !IsDeletable(name)) continue;

            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file held open by something else must not abort the rest.
                Console.Error.WriteLine($"could not remove {entry}: {ex.GetType().Name}");
            }
        }
        return removed;
    }

    public static bool IsDeletable(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        foreach (var dir in PlayerOwnedDirectories)
        {
            if (normalized.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var file in PlayerOwnedFiles)
        {
            if (normalized.Equals(file, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
