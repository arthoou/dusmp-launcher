using System.Text.Json;
using System.Text.Json.Serialization;
using DusmpLauncher.Core;
using DusmpLauncher.Models;

namespace DusmpLauncher.Modpack;

/// <summary>
/// Translation keys for everything this module can refuse. They are keys and not
/// sentences because the player reads them through
/// <see cref="Localization.ILanguageManager"/>; the accompanying detail string is
/// technical and goes to the log pane untranslated.
/// </summary>
public static class ManifestErrorKeys
{
    public const string EmptyPath = "error.manifest.emptyPath";
    public const string AbsolutePath = "error.manifest.absolutePath";
    public const string PathTraversal = "error.manifest.pathTraversal";
    public const string DuplicatePath = "error.manifest.duplicatePath";
    public const string InvalidHash = "error.manifest.invalidHash";
    public const string NegativeSize = "error.manifest.negativeSize";
    public const string MissingMinecraftVersion = "error.manifest.missingMinecraftVersion";
    public const string UnknownLoader = "error.manifest.unknownLoader";
    public const string LoaderMismatch = "error.manifest.loaderMismatch";
}

/// <summary>
/// Thrown when a manifest cannot be acted on. Carries the translation key
/// separately from the technical detail so the bridge can answer with
/// <see cref="Bridge.BridgeResponse.Failure"/> without ever showing the player a
/// raw exception message.
/// </summary>
public sealed class ManifestValidationException : Exception
{
    public ManifestValidationException(string errorKey, string detail)
        : base($"{errorKey} ({detail})")
    {
        ErrorKey = errorKey;
        Detail = detail;
    }

    /// <summary>Translation key describing the failure.</summary>
    public string ErrorKey { get; }

    /// <summary>Untranslated technical detail: the offending path, hash or loader name.</summary>
    public string Detail { get; }
}

/// <summary>
/// Reads, writes and vets manifests. The cache matters because the launcher must
/// still be able to verify an installation when the API is unreachable: without a
/// remembered manifest an offline start could not tell a complete install from a
/// broken one.
/// </summary>
public sealed class ManifestManager : IManifestManager
{
    /// <summary>
    /// Loaders the launcher knows how to install. Anything else is rejected at the
    /// manifest boundary rather than failing later inside the game installer, where
    /// the error would be untranslatable.
    /// </summary>
    private static readonly string[] SupportedLoaders = { "vanilla", "forge", "neoforge", "fabric", "quilt" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly LauncherPaths _paths;

    /// <summary>Serialises cache access: startup reads while a version check may be writing.</summary>
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public ManifestManager(LauncherPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <inheritdoc />
    public async Task<ModpackManifest?> LoadCachedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var file = _paths.ManifestCacheFile;
        if (!File.Exists(file))
            return null;

        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(file))
                return null;

            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);

            var manifest = await JsonSerializer
                .DeserializeAsync<ModpackManifest>(stream, JsonOptions, ct)
                .ConfigureAwait(false);

            if (manifest is null)
                return null;

            // A cache that no longer validates is worth exactly as much as no cache:
            // callers treat null as "fetch it again", which is the right recovery.
            Validate(manifest);
            return manifest;
        }
        catch (Exception ex) when (ex is JsonException
                                      or ManifestValidationException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return null;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveCacheAsync(ModpackManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        // Never cache something the launcher would refuse to act on later.
        Validate(manifest);
        ct.ThrowIfCancellationRequested();

        var file = _paths.ManifestCacheFile;
        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = file + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();

                // Same reason as the managed-file ledger: a truncated cache would be
                // read back as a corrupt manifest on the next offline start.
                File.Move(temp, file, overwrite: true);
            }
            catch
            {
                TryDeleteTemp(temp);
                throw;
            }
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Catches a NeoForge build pinned against the wrong Minecraft version.
    ///
    /// NeoForge numbers itself after the game: Minecraft 1.21.1 takes 21.1.x,
    /// 1.21.2 takes 21.2.x, and plain 1.21 takes 21.0.x. A build from the wrong
    /// series is not published for that version at all, so the install died on a
    /// bare "404 (Not Found)" against a maven URL — a message that named neither
    /// the real problem nor who could fix it. A pack pinned to 21.2.247 on
    /// 1.21.1 is a typo in the panel, and this says so.
    ///
    /// Returns null when the pair is fine, or when either version is not in a
    /// shape this rule covers: a snapshot or an unusual loader string is not
    /// something to refuse on a guess.
    /// </summary>
    private static string? NeoForgeSeriesMismatch(ModpackManifest manifest)
    {
        if (!string.Equals(manifest.Loader?.Trim(), "neoforge", StringComparison.OrdinalIgnoreCase)) return null;
        var build = manifest.LoaderVersion?.Trim();
        if (string.IsNullOrEmpty(build)) return null;

        var game = System.Text.RegularExpressions.Regex.Match(manifest.Minecraft?.Trim() ?? "", @"^1\.(\d+)(?:\.(\d+))?$");
        if (!game.Success) return null;
        var expected = $"{game.Groups[1].Value}.{(game.Groups[2].Success ? game.Groups[2].Value : "0")}";

        var pinned = System.Text.RegularExpressions.Regex.Match(build, @"^(\d+\.\d+)\.");
        if (!pinned.Success || pinned.Groups[1].Value == expected) return null;

        return $"neoforge {build} is not built for Minecraft {manifest.Minecraft}; that version takes {expected}.x.";
    }

    public void Validate(ModpackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.IsNullOrWhiteSpace(manifest.Minecraft))
            throw new ManifestValidationException(
                ManifestErrorKeys.MissingMinecraftVersion, $"manifestVersion={Describe(manifest.Version)}");

        var loader = manifest.Loader?.Trim() ?? "";
        if (!SupportedLoaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
            throw new ManifestValidationException(ManifestErrorKeys.UnknownLoader, Describe(loader));

        if (NeoForgeSeriesMismatch(manifest) is { } mismatch)
            throw new ManifestValidationException(ManifestErrorKeys.LoaderMismatch, mismatch);

        // Case-insensitive: two paths that differ only in case cannot both exist on
        // Windows, so accepting them would make the install unreproducible there.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var files = manifest.Files ?? Array.Empty<ModFile>();
        foreach (var file in files)
        {
            if (file is null)
                throw new ManifestValidationException(ManifestErrorKeys.EmptyPath, "null file entry");

            var raw = file.Path ?? "";
            if (string.IsNullOrWhiteSpace(raw))
                throw new ManifestValidationException(ManifestErrorKeys.EmptyPath, "empty path");

            // Checked on the raw string: normalisation strips leading slashes, which
            // would quietly turn "/etc/passwd" into a path inside the instance.
            if (IsRooted(raw))
                throw new ManifestValidationException(ManifestErrorKeys.AbsolutePath, raw);

            var normalized = ManagedFileIndex.NormalizePath(raw);
            if (normalized.Length == 0 || normalized.EndsWith('/'))
                throw new ManifestValidationException(ManifestErrorKeys.EmptyPath, raw);

            // Blunt on purpose: no real asset name contains a double dot, and this
            // refuses every traversal spelling without reasoning about segments.
            if (normalized.Contains("..", StringComparison.Ordinal))
                throw new ManifestValidationException(ManifestErrorKeys.PathTraversal, raw);

            if (!ManagedFileIndex.IsWellFormedSha256(file.Sha256))
                throw new ManifestValidationException(
                    ManifestErrorKeys.InvalidHash, $"{normalized}: {Describe(file.Sha256)}");

            if (file.Size < 0)
                throw new ManifestValidationException(
                    ManifestErrorKeys.NegativeSize, $"{normalized}: {file.Size}");

            if (!seen.Add(normalized))
                throw new ManifestValidationException(ManifestErrorKeys.DuplicatePath, normalized);
        }
    }

    /// <summary>
    /// True for anything the host would resolve outside the instance folder. Decided
    /// here rather than with <see cref="Path.IsPathRooted(string)"/> so that the same
    /// manifest is rejected on Linux and on Windows — a drive-qualified path is not
    /// "rooted" on Linux, but it is still not a relative instance path.
    /// </summary>
    private static bool IsRooted(string path)
    {
        if (path.Length == 0)
            return false;

        if (path[0] is '/' or '\\')
            return true;

        // "C:mods/x.jar" and "C:\mods\x.jar" alike.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return true;

        return false;
    }

    /// <summary>Renders a possibly empty value for the log pane without printing nothing at all.</summary>
    private static string Describe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<empty>" : value;

    private static void TryDeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing reads the temp file; the next save replaces it.
        }
    }
}
