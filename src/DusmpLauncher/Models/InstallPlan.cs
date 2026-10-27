namespace DusmpLauncher.Models;

/// <summary>Why a file needs work. Drives both the UI wording and the logs.</summary>
public enum FileAction
{
    /// <summary>Hash matches the manifest; nothing to do.</summary>
    UpToDate = 0,
    /// <summary>Not on disk.</summary>
    Missing = 1,
    /// <summary>On disk but the hash differs.</summary>
    Corrupt = 2,
    /// <summary>Managed by an earlier manifest and no longer listed: safe to delete.</summary>
    Obsolete = 3,
    /// <summary>Belongs to an optional mod the player turned off: safe to delete.</summary>
    Deselected = 4,
    /// <summary>Write-once file that already exists; the player's edits stay.</summary>
    PreservedByUser = 5,
}

/// <summary>One decided unit of work.</summary>
public sealed record PlannedFile(ModFile File, FileAction Action, string AbsolutePath);

/// <summary>
/// The full diff between the remote manifest and what is on disk. Computed once
/// per verification pass; the downloader only ever consumes this.
/// </summary>
public sealed class InstallPlan
{
    public required ModpackManifest Manifest { get; init; }
    public required IReadOnlyList<PlannedFile> ToDownload { get; init; }
    public required IReadOnlyList<PlannedFile> ToDelete { get; init; }
    public required IReadOnlyList<PlannedFile> UpToDate { get; init; }
    public required IReadOnlyList<PlannedFile> Preserved { get; init; }

    public long BytesToDownload => ToDownload.Sum(f => f.File.Size);
    public bool IsComplete => ToDownload.Count == 0 && ToDelete.Count == 0;
    public int TotalManagedFiles => ToDownload.Count + UpToDate.Count + Preserved.Count;

    public static InstallPlan Empty(ModpackManifest manifest) => new()
    {
        Manifest = manifest,
        ToDownload = Array.Empty<PlannedFile>(),
        ToDelete = Array.Empty<PlannedFile>(),
        UpToDate = Array.Empty<PlannedFile>(),
        Preserved = Array.Empty<PlannedFile>(),
    };
}
