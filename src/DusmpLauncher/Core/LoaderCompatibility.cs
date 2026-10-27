using System.IO.Compression;
using System.Text.RegularExpressions;

namespace DusmpLauncher.Core;

/// <summary>One mod that needs a newer loader than the pack pins.</summary>
public sealed record LoaderRequirement(string ModId, string FileName, string MinimumVersion);

/// <summary>
/// Compares the loader the modpack pins against what its own mods demand.
///
/// This exists because of a failure that is invisible until the game is already
/// starting: a pack pinned to NeoForge 21.1.228 shipping mods that declare
/// "neoforge 21.1.247 or above" loads nothing and drops the player on a red
/// "Error loading mods" screen listing mods they have never heard of. Nothing is
/// wrong with the download — the pinned loader is simply older than the mods.
///
/// Deliberately reports instead of repairing: bumping the loader on our own
/// would silently put the player on a build the server is not running, and the
/// pinned version is the whole point. The fix belongs in the panel, so the
/// message has to name the version to publish.
/// </summary>
public static class LoaderCompatibility
{
    /// <summary>Loader ids a mods.toml can declare a dependency on.</summary>
    private static readonly string[] LoaderModIds = { "neoforge", "forge" };

    /// <summary>
    /// <c>[[dependencies.&lt;mod&gt;]]</c> blocks, as written by both the Forge
    /// and NeoForge templates. Whitespace and key order vary between packs, so
    /// the block is matched whole and its keys read individually.
    /// </summary>
    private static readonly Regex DependencyBlock = new(
        @"\[\[dependencies\.[^\]]+\]\](?<body>(?:(?!\[\[)[\s\S])*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ModIdKey = new(@"modId\s*=\s*""(?<value>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionRangeKey = new(@"versionRange\s*=\s*""(?<value>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>
    /// The mod's own id, which lives in its <c>[[mods]]</c> block. Read from that
    /// block specifically: some packs declare their dependencies first, and
    /// taking the first modId in the file then reports every such mod as
    /// "neoforge" — the id of the thing it depends on.
    /// </summary>
    private static readonly Regex OwnModId = new(
        @"\[\[mods\]\](?<body>(?:(?!\[\[)[\s\S])*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Mods whose declared minimum loader version is newer than
    /// <paramref name="loaderVersion"/>, worst first. Empty when the pack is
    /// consistent, when the loader has no toml format, or when nothing can be
    /// read — this must never be the reason a launch fails.
    /// </summary>
    public static IReadOnlyList<LoaderRequirement> FindTooNew(string modsDirectory, string? loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion) || !Directory.Exists(modsDirectory))
            return Array.Empty<LoaderRequirement>();

        var installed = Parse(loaderVersion);
        if (installed is null) return Array.Empty<LoaderRequirement>();

        var found = new List<LoaderRequirement>();
        foreach (var jar in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var requirement = ReadRequirement(jar);
                if (requirement is null) continue;
                if (Compare(Parse(requirement.MinimumVersion), installed) > 0)
                    found.Add(requirement);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // An unreadable jar is the game's problem to report, not ours.
            }
        }

        return found
            .OrderByDescending(x => Parse(x.MinimumVersion), Comparer<int[]?>.Create(Compare))
            .ToArray();
    }

    private static LoaderRequirement? ReadRequirement(string jar)
    {
        using var archive = ZipFile.OpenRead(jar);
        var entry = archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml");
        if (entry is null) return null;

        using var reader = new StreamReader(entry.Open());
        var toml = reader.ReadToEnd();

        string? best = null;
        foreach (Match block in DependencyBlock.Matches(toml))
        {
            var body = block.Groups["body"].Value;
            var modId = ModIdKey.Match(body).Groups["value"].Value;
            if (!LoaderModIds.Contains(modId, StringComparer.OrdinalIgnoreCase)) continue;

            var lower = LowerBound(VersionRangeKey.Match(body).Groups["value"].Value);
            if (lower is null) continue;
            if (best is null || Compare(Parse(lower), Parse(best)) > 0) best = lower;
        }

        if (best is null) return null;
        var owner = ModIdKey.Match(OwnModId.Match(toml).Groups["body"].Value).Groups["value"].Value;
        return new LoaderRequirement(
            string.IsNullOrWhiteSpace(owner) ? Path.GetFileNameWithoutExtension(jar) : owner,
            Path.GetFileName(jar),
            best);
    }

    /// <summary>
    /// The inclusive lower bound of a Maven range: <c>[21.1.247,)</c> to
    /// <c>21.1.247</c>. An exclusive bound is treated as the same version, which
    /// only ever makes this report slightly less eager.
    /// </summary>
    internal static string? LowerBound(string range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;

        var trimmed = range.Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '[' && trimmed[0] != '(')) return null;

        var body = trimmed[1..].TrimEnd(']', ')');
        var lower = body.Split(',')[0].Trim();
        return lower.Length == 0 ? null : lower;
    }

    /// <summary>Numeric parts of a version, or null when it is not comparable.</summary>
    internal static int[]? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var parts = new List<int>();
        foreach (var piece in version.Split('.', '-', '+'))
        {
            // Stops at the first non-numeric piece: "21.1.247-beta" compares as 21.1.247.
            if (!int.TryParse(piece, out var value)) break;
            parts.Add(value);
        }
        return parts.Count == 0 ? null : parts.ToArray();
    }

    /// <summary>Component-wise comparison; a missing component counts as zero.</summary>
    internal static int Compare(int[]? left, int[]? right)
    {
        if (left is null || right is null) return 0;
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }
}
