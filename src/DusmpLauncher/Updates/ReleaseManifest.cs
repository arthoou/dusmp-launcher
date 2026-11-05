using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DusmpLauncher.Updates;

/// <summary>The signed release describes executable bytes, never extraction or executable commands.</summary>
public sealed class ReleaseManifest
{
    public const string KeyId = "dusmp-release-1";
    public const int MaxManifestBytes = 256 * 1024;
    [JsonRequired] public int SchemaVersion { get; init; }
    [JsonRequired] public string Channel { get; init; } = "";
    [JsonRequired] public string Version { get; init; } = "";
    [JsonRequired] public string MinimumVersion { get; init; } = "";
    [JsonRequired] public bool Mandatory { get; init; }
    [JsonRequired] public DateTimeOffset PublishedAt { get; init; }
    [JsonRequired] public string Notes { get; init; } = "";
    [JsonRequired] public List<ReleaseArtifact> Artifacts { get; init; } = new();
    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported";
    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" : "unsupported";
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };

    public static string PublicKeyPem
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DusmpLauncher.release-public-key.pem")
                ?? throw new InvalidOperationException("The release verification key is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static ReleaseManifest Verify(string envelopeJson, string? publicKeyPem = null)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(envelopeJson) > MaxManifestBytes)
            throw new InvalidDataException("Release manifest is too large.");
        try
        {
            RejectDuplicateKeys(envelopeJson);
            var envelope = JsonSerializer.Deserialize<SignedRelease>(envelopeJson, Json)
                ?? throw new InvalidDataException("Empty signed manifest.");
            if (envelope.SchemaVersion != 1 || envelope.KeyId != KeyId)
                throw new InvalidDataException("Unsupported manifest schema or signing key.");
            var payload = Convert.FromBase64String(envelope.Payload ?? throw new FormatException());
            var signature = Convert.FromBase64String(envelope.Signature ?? throw new FormatException());
            if (Convert.ToBase64String(payload) != envelope.Payload || Convert.ToBase64String(signature) != envelope.Signature)
                throw new FormatException("Noncanonical base64.");
            if (payload.Length == 0 || payload.Length > MaxManifestBytes || signature.Length != 64)
                throw new CryptographicException("Invalid release signature.");
            using var verifier = ECDsa.Create();
            verifier.ImportFromPem(publicKeyPem ?? PublicKeyPem);
            if (verifier.KeySize != 256 || !verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException("Invalid release signature.");
            var payloadJson = new System.Text.UTF8Encoding(false, true).GetString(payload);
            RejectDuplicateKeys(payloadJson);
            var release = JsonSerializer.Deserialize<ReleaseManifest>(payloadJson, Json)
                ?? throw new InvalidDataException("Empty release.");
            release.Validate();
            return release;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or System.Text.DecoderFallbackException)
        {
            throw new InvalidDataException("Malformed release manifest.", ex);
        }
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || Channel != "stable" || ParseVersion(MinimumVersion) > ParseVersion(Version)
            || PublishedAt < DateTimeOffset.Parse("2020-01-01T00:00:00Z") || PublishedAt > DateTimeOffset.UtcNow.AddMinutes(10)
            || Notes is null || Notes.Length > 16384 || Artifacts is null || Artifacts.Count != 2)
            throw new InvalidDataException("Invalid release metadata.");
        var platforms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in Artifacts)
        {
            if (artifact is null || artifact.Os is not ("windows" or "linux") || artifact.Arch != "x64"
                || !platforms.Add(artifact.Os) || artifact.FileName != (artifact.Os == "windows" ? "DusmpLauncher.exe" : "DusmpLauncher"))
                throw new InvalidDataException("Invalid or duplicate release platform.");
            ValidateFile(artifact.Url, artifact.FileName, artifact.Size, artifact.Sha256, artifact.Os);
            if (artifact.Download is { } download)
            {
                if (download.Format != (artifact.Os == "windows" ? "exe" : "tar.gz")
                    || string.IsNullOrEmpty(download.Compatibility) || download.Compatibility.Length > 500
                    || !download.FileName.EndsWith(artifact.Os == "windows" ? ".exe" : ".tar.gz", StringComparison.Ordinal))
                    throw new InvalidDataException("Unsupported distribution format.");
                ValidateFile(download.Url, download.FileName, download.Size, download.Sha256, artifact.Os);
            }
        }
    }

    private void ValidateFile(string url, string name, long size, string hash, string os)
    {
        if (size < 1 || size > 1024L * 1024 * 1024 || !Regex.IsMatch(hash ?? "", "^[a-f0-9]{64}$")
            || !Regex.IsMatch(name ?? "", "^[A-Za-z0-9][A-Za-z0-9._-]{0,120}$"))
            throw new InvalidDataException("Invalid release file metadata.");
        var uri = RequireOfficialUrl(url);
        if (uri.AbsolutePath != $"/launcher/releases/{Version}/{os}-x64/{name}" || uri.Query.Length != 0)
            throw new InvalidDataException("Release file is outside its versioned directory.");
    }

    public ReleaseArtifact Select(string os, string arch) => Artifacts.SingleOrDefault(x => x.Os == os && x.Arch == arch)
        ?? throw new PlatformNotSupportedException("No compatible launcher release is available.");

    public static Uri RequireOfficialUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Host is not ("dusmp.online" or "www.dusmp.online" or "api.dusmp.online") || value != uri.AbsoluteUri)
            throw new InvalidDataException("The update source is not an authorized HTTPS origin.");
        return uri;
    }

    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value ?? "", @"^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$")
            || !System.Version.TryParse(value, out var version) || version.Major > 65535 || version.Minor > 65535 || version.Build > 65535)
            throw new InvalidDataException("Invalid release version.");
        return version;
    }

    public static async Task VerifyFileAsync(string path, ReleaseArtifact artifact, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length != artifact.Size)
            throw new InvalidDataException("The update is incomplete or has an invalid size.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(artifact.Sha256)))
            throw new CryptographicException("The update SHA-256 does not match its signed release.");
        ValidateExecutable(path, artifact.Os);
    }

    public static void ValidateExecutable(string path, string os)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64) throw new InvalidDataException("Invalid executable.");
        var header = reader.ReadBytes(64);
        if (os == "linux")
        {
            if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F'
                || header[4] != 2 || header[5] != 1 || BitConverter.ToUInt16(header, 18) != 62)
                throw new InvalidDataException("The update is not a Linux x64 executable.");
        }
        else if (os == "windows")
        {
            var offset = BitConverter.ToInt32(header, 60);
            if (header[0] != 'M' || header[1] != 'Z' || offset < 64 || offset > stream.Length - 26)
                throw new InvalidDataException("The update is not a Windows executable.");
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
                throw new InvalidDataException("The update is not a Windows x64 executable.");
            stream.Position = offset + 24;
            if (reader.ReadUInt16() != 0x20b) throw new InvalidDataException("The update is not a PE32+ executable.");
        }
        else throw new PlatformNotSupportedException();
    }

    private static void RejectDuplicateKeys(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        static void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate manifest field.");
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Walk(child);
        }
        Walk(doc.RootElement);
    }

    private sealed class SignedRelease
    {
        [JsonRequired] public int SchemaVersion { get; init; }
        [JsonRequired] public string KeyId { get; init; } = "";
        [JsonRequired] public string Payload { get; init; } = "";
        [JsonRequired] public string Signature { get; init; } = "";
    }
}

public sealed class ReleaseArtifact
{
    [JsonRequired] public string Os { get; init; } = "";
    [JsonRequired] public string Arch { get; init; } = "";
    [JsonRequired] public string FileName { get; init; } = "";
    [JsonRequired] public string Url { get; init; } = "";
    [JsonRequired] public long Size { get; init; }
    [JsonRequired] public string Sha256 { get; init; } = "";
    public ReleaseDownload? Download { get; init; }
}

public sealed class ReleaseDownload
{
    [JsonRequired] public string Url { get; init; } = "";
    [JsonRequired] public string FileName { get; init; } = "";
    [JsonRequired] public long Size { get; init; }
    [JsonRequired] public string Sha256 { get; init; } = "";
    [JsonRequired] public string Format { get; init; } = "";
    [JsonRequired] public string Compatibility { get; init; } = "";
}
