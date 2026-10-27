using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DusmpLauncher.Modpack;

/// <summary>
/// One line of the ledger: a file the launcher itself wrote, plus the cheap stat
/// fields that let the next start decide whether it still has to be re-hashed.
/// </summary>
public sealed record ManagedFileEntry
{
    /// <summary>Instance-relative path with forward slashes, exactly as the manifest spelled it.</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = "";

    /// <summary>
    /// Lowercase hex SHA-256 of the content the launcher put there. Compared
    /// against the new manifest so a version bump on the same path is detected
    /// without reading a single byte of the file.
    /// </summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";

    [JsonPropertyName("size")] public long Size { get; init; }

    /// <summary>
    /// Last-write time in UTC ticks, read back at commit time. Size plus mtime is
    /// the "nothing touched this since we wrote it" test; it can only be fooled by
    /// an edit that preserves both, which is why Repair rehashes unconditionally.
    /// </summary>
    [JsonPropertyName("mtimeTicks")] public long LastWriteTicksUtc { get; init; }

    /// <summary>
    /// True when the manifest handed this file to the player (write-once). The
    /// content may legitimately differ from <see cref="Sha256"/> now, so a
    /// verification pass must never trust the hash of such an entry.
    /// </summary>
    [JsonPropertyName("writeOnce")] public bool WriteOnce { get; init; }

    /// <summary>Optional mod this file came from, or null when it is mandatory.</summary>
    [JsonPropertyName("optionalId")] public string? OptionalId { get; init; }
}

/// <summary>
/// The ledger of files the launcher wrote, persisted at
/// <see cref="Core.LauncherPaths.ManagedIndexFile"/>. It exists for two reasons:
/// it makes a warm verification pass cheap (size and mtime instead of a full
/// re-hash), and it is the only record of which files on disk are ours — so
/// removing a file the manifest dropped never turns into walking the instance
/// folder and deleting whatever looks unfamiliar.
/// </summary>
public sealed class ManagedFileIndex
{
    /// <summary>Bumped whenever the on-disk shape changes in a way an older build cannot read.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>1 MiB reads: large enough that hashing a 200 MB mod is I/O bound, small enough to pool.</summary>
    private const int HashBufferBytes = 1 << 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Keyed case-insensitively: Windows cannot hold two paths that differ only in
    /// case, so treating them as one entry keeps the ledger consistent with what
    /// the file system can actually represent.
    /// </summary>
    private readonly Dictionary<string, ManagedFileEntry> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modpack version the ledger describes. Diagnostics only — the manifest decides truth.</summary>
    public string? ModpackVersion { get; set; }

    /// <summary>When the ledger was last written, for the log pane.</summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    public int Count => _files.Count;

    /// <summary>Every recorded file. Enumerated for the obsolete-file pass.</summary>
    public IEnumerable<ManagedFileEntry> Entries => _files.Values;

    public bool TryGet(string relativePath, [MaybeNullWhen(false)] out ManagedFileEntry entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(relativePath))
            return false;

        return _files.TryGetValue(NormalizePath(relativePath), out entry);
    }

    /// <summary>Adds or replaces an entry, normalising its path so lookups cannot miss.</summary>
    public void Set(ManagedFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var normalized = NormalizePath(entry.Path);
        if (normalized.Length == 0)
            throw new ArgumentException("Managed file entry has no path.", nameof(entry));

        _files[normalized] = entry.Path == normalized ? entry : entry with { Path = normalized };
    }

    public bool Remove(string relativePath)
        => !string.IsNullOrEmpty(relativePath) && _files.Remove(NormalizePath(relativePath));

    public void Clear() => _files.Clear();

    /// <summary>
    /// Canonical form of a manifest path: forward slashes, no leading slash. Every
    /// comparison in this module goes through here, so a manifest that spells a
    /// path with backslashes still matches the ledger written on the other OS.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        return normalized;
    }

    /// <summary>
    /// True when the file on disk still has the size and mtime the ledger recorded,
    /// which is the condition under which the recorded hash may be trusted.
    /// </summary>
    public static bool MatchesDisk(ManagedFileEntry entry, long size, long lastWriteTicksUtc)
        => entry.Size == size && entry.LastWriteTicksUtc == lastWriteTicksUtc;

    /// <summary>
    /// Hash comparison used everywhere in this module. Case-insensitive because a
    /// server emitting uppercase hex is a formatting difference, not a mismatch.
    /// </summary>
    public static bool HashEquals(string? left, string? right)
        => !string.IsNullOrEmpty(left)
           && !string.IsNullOrEmpty(right)
           && left.Length == right.Length
           && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>64 hex characters and nothing else — anything shorter cannot be a SHA-256.</summary>
    public static bool IsWellFormedSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the ledger. A missing, unreadable, corrupt or future-schema file comes
    /// back as an empty ledger instead of an exception: an empty ledger only costs a
    /// full re-hash, whereas failing here would strand an otherwise fine install.
    /// </summary>
    public static async Task<ManagedFileIndex> LoadAsync(string file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var index = new ManagedFileIndex();
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            return index;

        Document? document;
        try
        {
            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return index;
        }

        if (document is null || document.SchemaVersion > CurrentSchemaVersion)
            return index;

        index.ModpackVersion = document.ModpackVersion;
        index.UpdatedAt = document.UpdatedAt;

        foreach (var entry in document.Files)
        {
            if (entry is null)
                continue;

            var normalized = NormalizePath(entry.Path);

            // A malformed line drives deletions if kept, so drop it rather than act on it.
            if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
                continue;
            if (!IsWellFormedSha256(entry.Sha256) || entry.Size < 0)
                continue;

            index._files[normalized] = entry with { Path = normalized, Sha256 = entry.Sha256.ToLowerInvariant() };
        }

        return index;
    }

    /// <summary>
    /// Writes the ledger to a temporary file and renames it over the real one, so a
    /// crash mid-write leaves the previous ledger intact. A half-written ledger
    /// would look like "almost nothing is managed", which loses obsolete-file
    /// tracking and forces a full re-hash on the next start.
    /// </summary>
    public async Task SaveAsync(string file, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file))
            throw new ArgumentException("Managed index path is empty.", nameof(file));

        ct.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var written = DateTimeOffset.UtcNow;
        var document = new Document
        {
            SchemaVersion = CurrentSchemaVersion,
            ModpackVersion = ModpackVersion,
            UpdatedAt = written,
            // Sorted so a diff of two ledgers is readable when support asks for one.
            Files = _files.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList(),
        };

        var temp = file + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                // Force the bytes out before the rename: the rename is only atomic
                // with respect to content that already reached the device.
                stream.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }

        UpdatedAt = written;
    }

    /// <summary>
    /// SHA-256 of a file, streamed. Kept here because the ledger is what decides
    /// when this cost has to be paid, and every consumer must hash identically.
    /// </summary>
    public static async Task<string> ComputeSha256Async(string absolutePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var buffer = ArrayPool<byte>.Shared.Rent(HashBufferBytes);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(
                absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp is harmless: the next successful save overwrites it.
        }
    }

    /// <summary>On-disk envelope. Separate from the class so the public API is not a DTO.</summary>
    private sealed class Document
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonPropertyName("modpackVersion")] public string? ModpackVersion { get; set; }
        [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
        [JsonPropertyName("files")] public List<ManagedFileEntry> Files { get; set; } = new();
    }
}
