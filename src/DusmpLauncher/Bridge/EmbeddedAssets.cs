using System.Reflection;

namespace DusmpLauncher.Bridge;

/// <summary>
/// Serves the UI out of the assembly over a custom scheme. Going through
/// All assets share app://launcher/ so relative URLs use the same origin
/// in both WebKitGTK and WebView2.
/// </summary>
public sealed class EmbeddedAssets
{
    /// <summary>Scheme the UI is served under. Must match the URLs in index.html.</summary>
    public const string Scheme = "app";
    public const string Host = "launcher";

    private const string ResourcePrefix = "DusmpLauncher.UI.";

    private readonly Assembly _assembly = typeof(EmbeddedAssets).Assembly;
    private readonly Dictionary<string, string> _resourceByPath;

    /// <summary>Set to a directory to read the UI from disk instead, for live editing.</summary>
    private readonly string? _overrideDirectory;

    public EmbeddedAssets()
    {
        _overrideDirectory = ResolveOverrideDirectory();

        // Embedded resource names replace directory separators with dots, which
        // makes "css/app.css" and "css.app.css" indistinguishable by name alone.
        // Build the map once, keyed by the URL path the page will actually ask for.
        _resourceByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;

            var tail = name[ResourcePrefix.Length..];
            // Only the final segment keeps its dot: "css.app.css" -> "css/app.css".
            var lastDot = tail.LastIndexOf('.');
            if (lastDot <= 0)
                continue;

            var path = tail[..lastDot].Replace('.', '/') + tail[lastDot..];
            _resourceByPath[path] = name;
        }
    }

    /// <summary>The URL the window opens with.</summary>
    public static Uri EntryPoint { get; } = new($"{Scheme}://{Host}/index.html");

    /// <summary>
    /// Photino's custom-scheme callback. Returning null makes the WebView show
    /// its own failure page, which is what we want for a genuinely missing asset
    /// — it is a packaging bug, not something to paper over at runtime.
    /// </summary>
    public Stream? Resolve(object sender, string scheme, string url, out string contentType)
    {
        var path = NormalizePath(url);
        contentType = GuessContentType(path);
        if (path.Length == 0)
            return null;

        if (_overrideDirectory is not null)
        {
            var onDisk = Path.Combine(_overrideDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(onDisk))
                return File.OpenRead(onDisk);
        }

        if (_resourceByPath.TryGetValue(path, out var resourceName))
            return _assembly.GetManifestResourceStream(resourceName);

        Console.Error.WriteLine($"UI asset not found: {path}");
        return null;
    }

    /// <summary>Strips the scheme, host and query to leave a resource path.</summary>
    private static string NormalizePath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Scheme || uri.Host != Host)
            return "";

        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (path.Length == 0)
            return "index.html";
        // Also constrain the optional on-disk UI override to its own directory.
        if (path.Contains('\\') || path.Contains('\0') || path.Contains(':')
            || path.Split('/').Any(segment => segment is ".." or "."))
            return "";
        return path;
    }

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Honours <c>DUSMP_UI_DIR</c> so the UI can be edited without rebuilding.
    /// Ignored when the directory does not exist, so a stale env var cannot
    /// silently blank the interface.
    /// </summary>
    private static string? ResolveOverrideDirectory()
    {
        var dir = Environment.GetEnvironmentVariable("DUSMP_UI_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        var full = Path.GetFullPath(dir);
        return Directory.Exists(full) ? full : null;
    }
}
