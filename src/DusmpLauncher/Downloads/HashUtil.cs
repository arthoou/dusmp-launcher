using System.Buffers;
using System.Security.Cryptography;

namespace DusmpLauncher.Downloads;

/// <summary>
/// SHA-256 of files on disk. Everything here streams through a pooled buffer
/// because the pack contains jars and asset archives that are far too big to
/// hold in memory — on a machine with 8 GB of RAM, reading a 500 MB archive into
/// a byte[] to hash it is the difference between a working launcher and an
/// OutOfMemoryException on the loading screen.
/// </summary>
public static class HashUtil
{
    /// <summary>Length of a SHA-256 written as lowercase hex, which is the only form the manifest uses.</summary>
    public const int Sha256HexLength = 64;

    /// <summary>
    /// Buffer size for the read loop. 128 KiB is large enough that the syscall
    /// cost disappears next to the hashing cost, and small enough to stay in the
    /// ArrayPool's pooled buckets instead of going to the LOH on every file.
    /// </summary>
    public const int DefaultBufferSize = 128 * 1024;

    private const int MinBufferSize = 4 * 1024;
    private const int MaxBufferSize = 1024 * 1024;

    /// <summary>
    /// Hashes a file. Opens with <see cref="FileShare.Read"/> so a verification
    /// pass cannot be broken by another reader, and with no internal FileStream
    /// buffer because the read loop below already buffers.
    /// </summary>
    public static async Task<string> ComputeFileSha256Async(
        string path,
        int bufferSize = DefaultBufferSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 0,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        var stream = new FileStream(path, options);
        await using (stream.ConfigureAwait(false))
        {
            return await ComputeSha256Async(stream, bufferSize, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hashes whatever the stream yields, in lowercase hex. Uses
    /// <see cref="IncrementalHash"/> rather than a one-shot helper so the
    /// cancellation token is honoured between chunks: a Repair on a 2 GB
    /// installation must still abort the moment the player closes the window.
    /// </summary>
    public static async Task<string> ComputeSha256Async(
        Stream stream,
        int bufferSize = DefaultBufferSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(bufferSize, MinBufferSize, MaxBufferSize));
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0)
                    break;

                incremental.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Compares two hex digests. Deliberately refuses anything that is not a
    /// well-formed SHA-256: a manifest entry with an empty or truncated hash
    /// would otherwise compare equal to itself and wave an unverifiable file
    /// through, which is exactly the case integrity checking exists to catch.
    /// </summary>
    public static bool HashesEqual(string? expected, string? actual)
    {
        if (!IsWellFormedSha256(expected) || !IsWellFormedSha256(actual))
            return false;

        // Case-insensitive because upstream tooling emits uppercase hex often
        // enough that rejecting it would be a self-inflicted outage.
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the string is exactly 64 hex digits. Checked before any network
    /// work starts, so a bad manifest fails loudly instead of after a download.
    /// </summary>
    public static bool IsWellFormedSha256(string? hex)
    {
        if (hex is null || hex.Length != Sha256HexLength)
            return false;

        foreach (var c in hex)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
                return false;
        }

        return true;
    }
}
