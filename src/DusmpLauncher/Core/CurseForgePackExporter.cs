using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

/// <summary>
/// Creates a local CurseForge-format modpack for the app's Import Profile .zip
/// flow. Only selected, launcher-managed manifest files are included; accounts,
/// worlds and other files found alongside them are never enumerated.
/// </summary>
public static class CurseForgePackExporter
{
    private const int BufferBytes = 128 * 1024;

    /// <summary>
    /// Packs staged files as overrides without downloading them a second time.
    /// The archive is for local import, not publication on CurseForge: no
    /// CurseForge project/file IDs are invented for files from another source.
    /// CurseForge may ask the player to include all bundled files on import.
    /// </summary>
    public static async Task<string> ExportAsync(
        LauncherPaths paths, ModpackManifest manifest,
        IReadOnlyDictionary<string, bool> optional, string destinationFile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(optional);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(manifest.Minecraft))
            throw new LauncherException("error.manifest.invalid", "The Minecraft version is missing.");
        var loaderId = GetLoaderId(manifest);
        var selected = new List<(ModFile File, string Relative, string Source)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file.OptionalId is { } id && !optional.GetValueOrDefault(id, true)) continue;
            var relative = NormalizeArchivePath(file.Path);
            if (!LauncherPaths.IsDeletable(relative)) continue;
            if (!names.Add(relative))
                throw new LauncherException("error.manifest.invalid", "Duplicate archive path: " + relative);
            selected.Add((file, relative, paths.ResolveInstanceFile(relative)));
        }

        var destination = Path.GetFullPath(destinationFile);
        var instanceRoot = Path.GetFullPath(paths.Instance).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(instanceRoot, PathComparison))
            throw new LauncherException("error.external.invalidPath", "Export the pack outside the staged Minecraft directory.");
        EnsureNoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
                {
                    var document = new
                    {
                        minecraft = new
                        {
                            version = manifest.Minecraft,
                            modLoaders = loaderId is null
                                ? Array.Empty<object>()
                                : new object[] { new { id = loaderId, primary = true } },
                        },
                        manifestType = "minecraftModpack",
                        manifestVersion = 1,
                        name = "DUSMP",
                        version = manifest.Version,
                        author = "DUSMP Team",
                        files = Array.Empty<object>(),
                        overrides = "overrides",
                    };
                    await using (var metadata = archive.CreateEntry("manifest.json", CompressionLevel.Fastest).Open())
                        await JsonSerializer.SerializeAsync(metadata, document, cancellationToken: ct).ConfigureAwait(false);

                    foreach (var (file, relative, source) in selected)
                    {
                        ct.ThrowIfCancellationRequested();
                        EnsureNoLinks(source);
                        if (!File.Exists(source))
                            throw new LauncherException("error.download.failed", "Missing staged modpack file: " + relative);
                        var compression = Path.GetExtension(relative).ToLowerInvariant() is ".jar" or ".zip" or ".png" or ".ogg"
                            ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var entry = archive.CreateEntry("overrides/" + relative, compression).Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long copied = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), ct).ConfigureAwait(false)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await entry.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            copied += read;
                        }
                        // Write-once configurations may have legitimate player
                        // changes; immutable mods must still match the manifest.
                        if (!file.WriteOnce && (copied != file.Size || !string.Equals(
                                Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase)))
                            throw new LauncherException("error.download.failed", "Staged file changed before export: " + relative);
                    }
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            EnsureNoLinks(destination);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? GetLoaderId(ModpackManifest manifest)
    {
        var loader = (manifest.Loader ?? "vanilla").Trim().ToLowerInvariant();
        if (loader is "vanilla" or "") return null;
        if (loader is not ("forge" or "neoforge" or "fabric" or "quilt"))
            throw new LauncherException("error.manifest.invalid", "Unsupported CurseForge modloader: " + loader);
        if (string.IsNullOrWhiteSpace(manifest.LoaderVersion))
            throw new LauncherException("error.manifest.loaderVersionMissing", loader);

        var version = manifest.LoaderVersion.Trim();
        if (version.StartsWith(loader + "-", StringComparison.OrdinalIgnoreCase)) version = version[(loader.Length + 1)..];
        // Forge's Maven coordinate contains the Minecraft version; the
        // CurseForge loader ID uses only the Forge build number.
        if (loader == "forge" && version.StartsWith(manifest.Minecraft + "-", StringComparison.Ordinal))
            version = version[(manifest.Minecraft.Length + 1)..];
        if (version.Length == 0 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+' or '_')))
            throw new LauncherException("error.manifest.invalid", "Invalid pinned loader version.");
        return loader + "-" + version;
    }

    private static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new LauncherException("error.manifest.invalid", "An archive file has no path.");
        var relative = path.Replace('\\', '/');
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.Any(c => c < ' ' || c is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
                throw new LauncherException("error.manifest.invalid", "Invalid portable archive path: " + path);
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && stem[3] is >= '1' and <= '9'))
                throw new LauncherException("error.manifest.invalid", "Reserved Windows archive path: " + path);
        }
        return relative;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new LauncherException("error.external.invalidPath", "A pack export cannot follow symbolic links: " + current);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
