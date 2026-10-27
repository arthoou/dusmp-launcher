using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DusmpLauncher.Models;
using DusmpLauncher.Modpack;

namespace DusmpLauncher.Core;

/// <summary>
/// Creates a local, self-contained Modrinth import without editing Modrinth's
/// database or copying files outside the DUSMP manifest. The target launcher owns
/// the import confirmation and installs Minecraft and the declared loader.
/// Format: https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack
/// </summary>
public static class ModrinthPackExporter
{
    public static async Task<string> ExportAsync(
        LauncherPaths paths,
        ModpackManifest manifest,
        IReadOnlyDictionary<string, bool> optional,
        string destinationFile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(optional);
        new ManifestManager(paths).Validate(manifest);
        ct.ThrowIfCancellationRequested();

        var dependencies = BuildDependencies(manifest);
        var destination = Path.GetFullPath(destinationFile);
        if (!string.Equals(Path.GetExtension(destination), ".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("error.external.invalidPath", "A Modrinth import must use the .mrpack extension.");
        EnsureNoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";

        // Empty files is intentional: every selected file is bundled in overrides,
        // so imports do not need access to private DUSMP CDN URLs or download mods twice.
        var index = new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = manifest.Version,
            name = "DUSMP",
            files = Array.Empty<object>(),
            dependencies
        };

        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
                {
                    await using (var metadata = archive.CreateEntry("modrinth.index.json", CompressionLevel.Fastest).Open())
                        await JsonSerializer.SerializeAsync(metadata, index, cancellationToken: ct).ConfigureAwait(false);

                    foreach (var file in manifest.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (file.OptionalId is not null && !optional.GetValueOrDefault(file.OptionalId, true)) continue;
                        var relative = NormalizePath(file.Path);
                        // These directories/files belong to the player, even if an API
                        // manifest accidentally includes one. Never export worlds/accounts.
                        if (!LauncherPaths.IsDeletable(relative)) continue;

                        var source = paths.ResolveInstanceFile(relative);
                        EnsureNoLinks(source);
                        if (!File.Exists(source))
                            throw new LauncherException("error.download.failed", "Missing staged modpack file: " + relative);

                        var compression = Path.GetExtension(relative).ToLowerInvariant() is ".jar" or ".zip" or ".png" or ".ogg"
                            ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var entry = archive.CreateEntry("overrides/" + relative, compression).Open();
                        await CopyVerifiedAsync(input, entry, file, ct).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            EnsureNoLinks(destination);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Dictionary<string, string> BuildDependencies(ModpackManifest manifest)
    {
        var dependencies = new Dictionary<string, string> { ["minecraft"] = manifest.Minecraft.Trim() };
        var loader = manifest.Loader.Trim().ToLowerInvariant();
        var dependency = loader switch
        {
            "vanilla" => null,
            "forge" => "forge",
            "neoforge" => "neoforge",
            "fabric" => "fabric-loader",
            "quilt" => "quilt-loader",
            _ => throw new LauncherException("error.manifest.invalid", "Unsupported loader: " + loader)
        };
        if (dependency is null) return dependencies;
        var version = manifest.LoaderVersion?.Trim();
        if (string.IsNullOrEmpty(version))
            throw new LauncherException("error.manifest.loaderVersionMissing", loader);
        // Forge metadata sometimes uses minecraft-loader; .mrpack needs just loader.
        if (loader == "forge" && version.StartsWith(manifest.Minecraft.Trim() + "-", StringComparison.Ordinal))
            version = version[(manifest.Minecraft.Trim().Length + 1)..];
        if (string.IsNullOrWhiteSpace(version))
            throw new LauncherException("error.manifest.loaderVersionMissing", loader);
        dependencies[dependency] = version;
        return dependencies;
    }

    private static async Task CopyVerifiedAsync(Stream input, Stream output, ModFile file, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long bytes = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytes += read;
            }

            // writeOnce files may have been edited after their first installation;
            // preserve that staged content, just as synchronization does.
            if (!file.WriteOnce && (bytes != file.Size || !string.Equals(
                    Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new LauncherException("error.download.failed", "Staged file changed during export: " + file.Path);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string NormalizePath(string path)
    {
        var relative = path.Replace('\\', '/');
        if (relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':') || part.Contains('\0')))
            throw new LauncherException("error.external.invalidPath", path);
        return relative;
    }

    private static void EnsureNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (Path.GetDirectoryName(current) is { } parent)
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new LauncherException("error.external.invalidPath", "DUSMP cannot export through symbolic links: " + current);
            current = parent;
        }
    }
}
