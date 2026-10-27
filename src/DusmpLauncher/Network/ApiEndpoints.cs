namespace DusmpLauncher.Network;

/// <summary>
/// The only place launcher API paths are written down. Everything else goes
/// through <see cref="IApiClient"/>, so no URL is ever duplicated in a feature.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>Production base. Overridable with <c>DUSMP_API_BASE</c> for staging.</summary>
    public const string DefaultBaseUrl = "https://api.dusmp.online";

    public const string LauncherVersion = "launcher/version";
    public const string ModpackEditions = "modpack/editions";
    public const string ModpackLatest = "modpack/latest";
    public const string ModpackManifest = "modpack/manifest";
    public const string OptionalMods = "mods/optional";
    public const string Servers = "servers";
    public const string Regions = "regions";
    public const string Status = "status";
    public const string News = "news";
    public const string ClientRegion = "client/region";
    public const string Health = "health";

    /// <summary>Resolves the base URL: env override first, then the default.</summary>
    public static string ResolveBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("DUSMP_API_BASE");
        return string.IsNullOrWhiteSpace(env) ? DefaultBaseUrl : env.TrimEnd('/');
    }

    /// <summary>True when the launcher was told to run entirely on bundled fixtures.</summary>
    public static bool UseFixtures()
    {
        var env = Environment.GetEnvironmentVariable("DUSMP_API_MODE");
        return string.Equals(env, "fixtures", StringComparison.OrdinalIgnoreCase);
    }
}
