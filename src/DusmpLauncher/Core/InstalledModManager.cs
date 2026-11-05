using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

/// <summary>
/// Reads mod metadata without loading code and manages local JARs by renaming
/// them. Disabling a mod never rewrites its contents or removes the file.
/// </summary>
public sealed class InstalledModManager
{
    private const string DisabledSuffix = ".disabled";
    private const string FallbackIcon = "app://launcher/assets/mod-generic.svg";
    private const int MetadataLimit = 256 * 1024;
    private const int IconLimit = 2 * 1024 * 1024;
    private readonly string _modsDirectory;

    public InstalledModManager(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        _modsDirectory = Path.GetFullPath(modsDirectory);
    }

    public Task<IReadOnlyList<InstalledMod>> ListAsync(CancellationToken ct = default) => Task.Run<IReadOnlyList<InstalledMod>>(() =>
    {
        EnsureNoLinks(_modsDirectory);
        if (!Directory.Exists(_modsDirectory)) return Array.Empty<InstalledMod>();
        var mods = new List<InstalledMod>();
        foreach (var file in Directory.EnumerateFiles(_modsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsModFile(Path.GetFileName(file))) continue;
            EnsureNoLinks(file);
            mods.Add(ReadMod(file, ct));
        }
        return mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }, ct);

    public Task SetEnabledAsync(string fileName, bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var source = ResolveMod(fileName);
        var canonical = CanonicalName(fileName);
        var destination = ResolveMod(enabled ? canonical : canonical + DisabledSuffix);
        if (!File.Exists(source))
        {
            if (File.Exists(destination)) return Task.CompletedTask;
            throw new LauncherException("error.mods.missing", fileName);
        }
        if (source.Equals(destination, PathComparison)) return Task.CompletedTask;
        if (File.Exists(destination)) throw new LauncherException("error.mods.exists", Path.GetFileName(destination));
        File.Move(source, destination, overwrite: false);
        return Task.CompletedTask;
    }

    public async Task<int> ImportAsync(IEnumerable<string> sources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ct.ThrowIfCancellationRequested();
        EnsureNoLinks(_modsDirectory);
        var planned = new List<(string Source, string Target)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Preflight the complete selection so one bad file/collision cannot
        // leave an earlier selected file silently installed.
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source)) throw new LauncherException("error.mods.invalidFile");
            var fullSource = Path.GetFullPath(source);
            var fileName = Path.GetFileName(fullSource);
            if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullSource))
                throw new LauncherException("error.mods.invalidFile", fileName);
            EnsureNoLinks(fullSource);
            var target = ResolveMod(fileName);
            if (!names.Add(fileName) || HasCollision(fileName))
                throw new LauncherException("error.mods.exists", fileName);
            ValidateJar(fullSource, ct);
            planned.Add((fullSource, target));
        }
        if (planned.Count == 0) return 0;
        Directory.CreateDirectory(_modsDirectory);
        EnsureNoLinks(_modsDirectory);
        var staged = new List<(string Temporary, string Target)>();
        try
        {
            foreach (var item in planned)
            {
                ct.ThrowIfCancellationRequested();
                var temporary = ResolveTemporary(".dusmp-import-" + Guid.NewGuid().ToString("N") + ".tmp");
                staged.Add((temporary, item.Target));
                await using (var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
                ValidateJar(temporary, ct);
            }
            ct.ThrowIfCancellationRequested();
            foreach (var item in staged)
            {
                EnsureNoLinks(_modsDirectory);
                if (HasCollision(Path.GetFileName(item.Target)))
                    throw new LauncherException("error.mods.exists", Path.GetFileName(item.Target));
            }
            foreach (var item in staged) File.Move(item.Temporary, item.Target, overwrite: false);
            return planned.Count;
        }
        finally
        {
            foreach (var item in staged)
            {
                // Only delete the unique import staging files owned by this call.
                if (File.Exists(item.Temporary)) File.Delete(item.Temporary);
            }
        }
    }

    private InstalledMod ReadMod(string path, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);
        var fallbackName = Path.GetFileNameWithoutExtension(CanonicalName(fileName));
        var name = fallbackName;
        string? version = null, icon = null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            string? iconPath = null;
            var fabric = FindEntry(archive, "fabric.mod.json");
            var quilt = FindEntry(archive, "quilt.mod.json");
            if (fabric is not null)
            {
                using var metadata = JsonDocument.Parse(ReadBounded(fabric, MetadataLimit, ct));
                name = JsonString(metadata.RootElement, "name") ?? JsonString(metadata.RootElement, "id") ?? name;
                version = JsonString(metadata.RootElement, "version");
                if (metadata.RootElement.TryGetProperty("icon", out var entry)) iconPath = JsonIcon(entry);
            }
            else if (quilt is not null)
            {
                using var metadata = JsonDocument.Parse(ReadBounded(quilt, MetadataLimit, ct));
                var loader = metadata.RootElement.GetProperty("quilt_loader");
                name = JsonString(loader, "id") ?? name;
                version = JsonString(loader, "version");
                if (loader.TryGetProperty("metadata", out var details))
                {
                    name = JsonString(details, "name") ?? name;
                    if (details.TryGetProperty("icon", out var entry)) iconPath = JsonIcon(entry);
                }
            }
            else
            {
                var toml = FindEntry(archive, "META-INF/neoforge.mods.toml") ?? FindEntry(archive, "META-INF/mods.toml");
                if (toml is not null)
                {
                    var text = Encoding.UTF8.GetString(ReadBounded(toml, MetadataLimit, ct));
                    name = TomlString(text, "displayName") ?? TomlString(text, "modId") ?? name;
                    version = TomlString(text, "version");
                    iconPath = TomlString(text, "logoFile");
                }
            }
            if (string.IsNullOrWhiteSpace(version) || version.Contains("${", StringComparison.Ordinal))
            {
                version = null;
                var manifest = FindEntry(archive, "META-INF/MANIFEST.MF");
                if (manifest is not null)
                {
                    var text = Encoding.UTF8.GetString(ReadBounded(manifest, MetadataLimit, ct));
                    version = Regex.Match(text, @"(?m)^Implementation-Version:\s*([^\r\n]+)").Groups[1].Value;
                }
            }
            foreach (var candidate in new[] { iconPath, "icon.png", "logo.png", "pack.png" })
            {
                if (!IsArchivePath(candidate)) continue;
                var entry = FindEntry(archive, candidate!);
                if (entry is null || entry.Length > IconLimit) continue;
                var image = ReadBounded(entry, IconLimit, ct);
                if (!IsPng(image)) continue;
                icon = "data:image/png;base64," + Convert.ToBase64String(image);
                break;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                      or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Broken/unknown metadata must not hide a local file or prevent its
            // owner disabling it. The filename and a neutral icon remain usable.
        }
        return new InstalledMod(fileName, Display(name) ?? fallbackName, Display(version), icon ?? FallbackIcon,
            !fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private bool HasCollision(string canonical)
    {
        if (!Directory.Exists(_modsDirectory)) return false;
        return Directory.EnumerateFiles(_modsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Any(file => file is not null
                && (file.Equals(canonical, StringComparison.OrdinalIgnoreCase)
                    || file.Equals(canonical + DisabledSuffix, StringComparison.OrdinalIgnoreCase)));
    }

    private string ResolveMod(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !IsModFile(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(':') || fileName.Contains('\0')
            || Path.GetFileName(fileName) != fileName)
            throw new LauncherException("error.mods.invalidFile", fileName);
        return ResolveTemporary(fileName);
    }

    private string ResolveTemporary(string name)
    {
        var full = Path.GetFullPath(Path.Combine(_modsDirectory, name));
        if (!full.StartsWith(_modsDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison))
            throw new LauncherException("error.mods.unsafePath", name);
        EnsureNoLinks(full);
        return full;
    }

    private static void EnsureNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new LauncherException("error.mods.unsafePath", current);
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }

    private static void ValidateJar(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0 || archive.Entries.Count > 100000)
                throw new InvalidDataException("The archive has no valid entries.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new LauncherException("error.mods.invalidFile", Path.GetFileName(path), ex);
        }
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, int maximum, CancellationToken ct)
    {
        if (entry.Length > maximum) throw new InvalidDataException("Mod metadata is too large.");
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (output.Length + count > maximum) throw new InvalidDataException("Mod metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name) => archive.GetEntry(name)
        ?? archive.Entries.FirstOrDefault(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static bool IsModFile(string name) => name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
    private static string CanonicalName(string name) => name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
        ? name[..^DisabledSuffix.Length] : name;
    private static string? JsonString(JsonElement value, string property) => value.TryGetProperty(property, out var entry)
        && entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
    private static string? JsonIcon(JsonElement icon) => icon.ValueKind == JsonValueKind.String ? icon.GetString()
        : icon.ValueKind == JsonValueKind.Object ? icon.EnumerateObject()
            .Where(item => item.Value.ValueKind == JsonValueKind.String)
            .OrderByDescending(item => int.TryParse(item.Name, out var size) ? size : 0)
            .Select(item => item.Value.GetString()).FirstOrDefault() : null;
    private static string? TomlString(string text, string key)
    {
        var match = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + @"\s*=\s*(?:""((?:\\.|[^""\r\n])*)""|'([^'\r\n]*)')");
        if (!match.Success) return null;
        if (match.Groups[2].Success) return match.Groups[2].Value;
        try { return JsonSerializer.Deserialize<string>('"' + match.Groups[1].Value + '"'); }
        catch (JsonException) { return match.Groups[1].Value; }
    }
    private static string? Display(string? text) => string.IsNullOrWhiteSpace(text) ? null
        : new string(text.Where(character => !char.IsControl(character)).Take(200).ToArray()).Trim();
    private static bool IsArchivePath(string? path) => !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':')
        && !path.Split('/').Any(part => part is ".." or ".");
    private static bool IsPng(byte[] bytes) => bytes.Length >= 24
        && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)
        && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) is > 0 and <= 4096
        && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) is > 0 and <= 4096;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
