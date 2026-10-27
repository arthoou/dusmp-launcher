using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DusmpLauncher.Core;

/// <summary>Updates only the DUSMP entry in the official launcher's profile files.</summary>
public static class OfficialLauncherProfile
{
    public const string ProfileKey = "dusmp";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task WriteAsync(string minecraftRoot, string versionId, string gameDirectory,
        string? javaArguments, CancellationToken ct = default, bool microsoftStore = false)
    {
        // The Win32 launcher and the Microsoft Store launcher use different files.
        // If both exist, keep the DUSMP entry usable in either without copying profiles between them.
        var names = new[] { "launcher_profiles.json", "launcher_profiles_microsoft_store.json" };
        var files = names.Select(name => Path.Combine(minecraftRoot, name)).Where(File.Exists).ToList();
        if (files.Count == 0) files.Add(Path.Combine(minecraftRoot, names[microsoftStore ? 1 : 0]));
        var pending = new List<(string File, string? Original, string Updated)>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new LauncherException("error.external.invalidPath", "The launcher profile file is a symbolic link.");
            var original = File.Exists(file) ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : null;
            JsonObject root;
            try
            {
                root = original is null ? new JsonObject() : JsonNode.Parse(original) as JsonObject
                    ?? throw new JsonException("Profile root must be an object.");
                if (root["profiles"] is not null && root["profiles"] is not JsonObject)
                    throw new JsonException("Profiles must be an object.");
            }
            catch (JsonException)
            { throw new LauncherException("error.external.profileUnreadable", "Invalid launcher profiles: " + file); }

            var profiles = root["profiles"] as JsonObject;
            if (profiles is null) { profiles = new JsonObject(); root["profiles"] = profiles; }
            if (profiles[ProfileKey] is not null && profiles[ProfileKey] is not JsonObject)
                throw new LauncherException("error.external.profileUnreadable", "Invalid DUSMP profile: " + file);
            var profile = profiles[ProfileKey] as JsonObject;
            if (profile is null) { profile = new JsonObject(); profiles[ProfileKey] = profile; }
            else if (profile["gameDir"] is not JsonValue gameValue || !gameValue.TryGetValue<string>(out var previousDirectory)
                || !Path.GetFullPath(previousDirectory).Equals(Path.GetFullPath(gameDirectory),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new LauncherException("error.external.instanceExists", "The existing DUSMP profile points at a different game directory: " + file);

            profile["name"] = "DUSMP";
            profile["type"] = "custom";
            profile["lastVersionId"] = versionId;
            profile["gameDir"] = gameDirectory;
            profile["created"] ??= DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            if (!string.IsNullOrWhiteSpace(javaArguments)) profile["javaArgs"] = javaArguments;
            root["version"] ??= 3;
            pending.Add((file, original, root.ToJsonString(Json)));
        }

        Directory.CreateDirectory(minecraftRoot);
        foreach (var (file, original, updated) in pending)
        {
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".dusmp.tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(false), ct).ConfigureAwait(false);
                var current = File.Exists(file) ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : null;
                if (!string.Equals(current, original, StringComparison.Ordinal))
                    throw new LauncherException("error.external.profileUnreadable", "The profile changed during setup. Close the Minecraft Launcher and retry.");
                File.Move(temporary, file, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
