using DusmpLauncher.Models;

namespace DusmpLauncher.Modpack;

/// <summary>
/// Owns the installed modpack: what is on disk, how it differs from the remote
/// manifest, and how to bring it in line without touching player files.
/// </summary>
public interface IModpackManager
{
    /// <summary>
    /// Diffs the manifest against disk. Hashes are only recomputed when size or
    /// mtime changed since the managed-file ledger was written, so a warm start
    /// stays fast, and <paramref name="force"/> rehashes everything for Repair.
    /// </summary>
    Task<InstallPlan> BuildPlanAsync(
        ModpackManifest manifest,
        IReadOnlyDictionary<string, bool> optionalChoices,
        bool force = false,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes what the plan marked obsolete or deselected, refusing anything
    /// <see cref="Core.LauncherPaths.IsDeletable"/> rejects. Returns paths removed.
    /// </summary>
    Task<IReadOnlyList<string>> ApplyDeletionsAsync(InstallPlan plan, CancellationToken ct = default);

    /// <summary>Records a completed install so the next start can trust sizes and mtimes.</summary>
    Task CommitAsync(InstallPlan plan, CancellationToken ct = default);
}

/// <summary>Fetches and caches manifests, so an offline start still has something to verify against.</summary>
public interface IManifestManager
{
    /// <summary>Last manifest written to the cache, or null on a fresh install.</summary>
    Task<ModpackManifest?> LoadCachedAsync(CancellationToken ct = default);

    Task SaveCacheAsync(ModpackManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Validates a manifest before anything acts on it: paths must not escape
    /// the instance, hashes must be well-formed, sizes must be positive.
    /// Throws with a translation key on the first problem found.
    /// </summary>
    void Validate(ModpackManifest manifest);
}

/// <summary>Reconciles remote optional mods with the player's saved choices.</summary>
public interface IOptionalModsManager
{
    /// <summary>
    /// Merges the remote list with saved choices: known ids keep the player's
    /// answer, unknown ids come in enabled and flagged as new.
    /// </summary>
    IReadOnlyList<OptionalModView> Merge(IReadOnlyList<OptionalMod> remote);

    /// <summary>Saved choices by mod id, for feeding the install plan.</summary>
    IReadOnlyDictionary<string, bool> CurrentChoices(IReadOnlyList<OptionalMod> remote);

    Task SetEnabledAsync(string modId, bool enabled, CancellationToken ct = default);

    /// <summary>Marks every current id as seen, so they stop showing the "new" badge.</summary>
    Task MarkAllSeenAsync(IReadOnlyList<OptionalMod> remote, CancellationToken ct = default);
}
