using System.Text.Json;

namespace DusmpLauncher.Core;

/// <summary>
/// Installs Fabric and Quilt.
///
/// Neither has a CmlLib installer package, but neither needs one: both publish a
/// ready-made version profile, the same JSON shape Minecraft's own launcher
/// consumes. Writing that file into <c>versions/</c> is the whole installation —
/// it declares <c>inheritsFrom</c>, and CmlLib then resolves the parent version,
/// downloads the libraries and builds the launch arguments exactly as it does
/// for a vanilla version.
///
/// Deliberately not a maven-index lookup: the loader build named in the manifest
/// is requested directly, so this keeps working when a provider's version index
/// is incomplete — which is exactly what broke NeoForge installs.
/// </summary>
public sealed class FabricFamilyInstaller
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/versions/loader";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3/versions/loader";

    private readonly HttpClient _http;
    private readonly string _versionsDirectory;

    public FabricFamilyInstaller(HttpClient http, string versionsDirectory)
    {
        _http = http;
        _versionsDirectory = versionsDirectory;
    }

    /// <summary>
    /// Makes sure the loader version exists on disk and returns its version id.
    /// <paramref name="loaderVersion"/> may be null, in which case the newest
    /// build the provider offers for that Minecraft version is used.
    /// </summary>
    public async Task<string> InstallAsync(bool quilt, string minecraftVersion, string? loaderVersion, CancellationToken ct)
    {
        var meta = quilt ? QuiltMeta : FabricMeta;
        var resolved = loaderVersion ?? await ResolveLatestAsync(meta, minecraftVersion, ct).ConfigureAwait(false);

        var profileUrl = $"{meta}/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(resolved)}/profile/json";
        using var response = await _http.GetAsync(profileUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GameLaunchException(
                "error.loader.versionMissing",
                $"{(quilt ? "Quilt" : "Fabric")} {resolved} for Minecraft {minecraftVersion} returned HTTP {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        string versionId;
        try
        {
            using var document = JsonDocument.Parse(json);
            versionId = document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? ""
                : "";
        }
        catch (JsonException ex)
        {
            throw new GameLaunchException("error.loader.versionMissing", $"The loader profile was not valid JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(versionId))
            throw new GameLaunchException("error.loader.versionMissing", "The loader profile declared no version id.");

        // Written atomically: a half-written profile would be picked up on the
        // next start and fail in a way that looks like a corrupt installation.
        var directory = Path.Combine(_versionsDirectory, versionId);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, versionId + ".json");
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, json, ct).ConfigureAwait(false);
        File.Move(temporary, target, overwrite: true);

        return versionId;
    }

    /// <summary>Newest loader build the provider lists for this Minecraft version.</summary>
    private async Task<string> ResolveLatestAsync(string meta, string minecraftVersion, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"{meta}/{Uri.EscapeDataString(minecraftVersion)}", ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new GameLaunchException("error.loader.versionMissing", $"Loader list returned HTTP {(int)response.StatusCode}.");

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        // The list is newest first; take the first stable build, falling back to
        // the first entry when the provider marks none as stable.
        string? firstAny = null;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out var loader) ||
                !loader.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String)
                continue;

            var value = version.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            firstAny ??= value;

            if (loader.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.True)
                return value;
        }

        return firstAny ?? throw new GameLaunchException(
            "error.loader.versionMissing", $"No loader build is published for Minecraft {minecraftVersion}.");
    }
}
