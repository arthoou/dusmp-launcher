using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DusmpLauncher.Core;

namespace DusmpLauncher.Network;

/// <summary>One skin the account has worn, as Minecraft Services reports it.</summary>
public sealed record SkinEntry(string Id, string Url, string Variant, bool Active);

/// <summary>One cape the account owns.</summary>
public sealed record CapeEntry(string Id, string Url, string Alias, bool Active);

/// <summary>Everything the appearance screen needs in one shot.</summary>
public sealed record AppearanceState(
    string Username,
    string Uuid,
    string Variant,
    IReadOnlyList<SkinEntry> Skins,
    IReadOnlyList<CapeEntry> Capes);

/// <summary>
/// Reads and changes the player's skin and cape through Minecraft Services.
///
/// Everything here needs the Minecraft access token from a completed Microsoft
/// sign-in, so an offline account has no appearance to edit — the caller checks
/// that before getting here and the screen stays read-only.
/// </summary>
public sealed class MinecraftAppearance
{
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string SkinsUrl = ProfileUrl + "/skins";
    private const string ActiveSkinUrl = SkinsUrl + "/active";
    private const string ActiveCapeUrl = ProfileUrl + "/capes/active";

    /// <summary>
    /// A Minecraft skin is a 64x64 (or legacy 64x32) PNG, which is a few
    /// kilobytes. Anything far larger is not a skin, and rejecting it here gives
    /// a clear message instead of an opaque refusal from the API.
    /// </summary>
    private const long MaxSkinBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public MinecraftAppearance(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>The account's current skin, its history and its capes.</summary>
    public async Task<AppearanceState> GetAsync(string accessToken, CancellationToken ct = default)
    {
        var profile = await ReadProfileAsync(accessToken, ct).ConfigureAwait(false);

        var skins = (profile.Skins ?? new List<SkinDto>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new SkinEntry(x.Id ?? x.Url!, Https(x.Url!), Normalise(x.Variant), IsActive(x.State)))
            .ToArray();

        var capes = (profile.Capes ?? new List<CapeDto>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new CapeEntry(x.Id ?? "", Https(x.Url!), string.IsNullOrWhiteSpace(x.Alias) ? x.Id ?? "" : x.Alias!, IsActive(x.State)))
            .ToArray();

        return new AppearanceState(
            profile.Name ?? "",
            profile.Id ?? "",
            skins.FirstOrDefault(x => x.Active)?.Variant ?? "classic",
            skins,
            capes);
    }

    /// <summary>
    /// Uploads a PNG as the active skin. <paramref name="variant"/> is
    /// <c>classic</c> or <c>slim</c>; anything else is treated as classic, since
    /// the API rejects an unknown value outright.
    /// </summary>
    public async Task<AppearanceState> UploadSkinAsync(string accessToken, string filePath, string variant, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new LauncherException("error.skin.fileMissing", filePath);

        var info = new FileInfo(filePath);
        if (info.Length == 0 || info.Length > MaxSkinBytes)
            throw new LauncherException("error.skin.fileTooBig", $"{info.Length} bytes");

        var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        if (!IsPng(bytes))
            throw new LauncherException("error.skin.notPng", Path.GetFileName(filePath));

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Normalise(variant)), "variant");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", Path.GetFileName(filePath));

        // Sources disagree on the verb — the wiki documents POST, working
        // launchers use PUT — and the endpoint answers 401 to every method
        // before it routes, so it cannot be settled without a real token. Try
        // PUT first because that is what a launcher known to work uses, and fall
        // back on the documented POST if the server says the method is wrong.
        var response = await SendSkinAsync(HttpMethod.Put, form, accessToken, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            response.Dispose();
            response = await SendSkinAsync(HttpMethod.Post, form, accessToken, ct).ConfigureAwait(false);
        }

        using (response)
            await EnsureOkAsync(response, "skin upload", ct).ConfigureAwait(false);

        return await GetAsync(accessToken, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendSkinAsync(HttpMethod method, MultipartFormDataContent form, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, SkinsUrl) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Drops the uploaded skin and goes back to the default one.</summary>
    public async Task<AppearanceState> ResetSkinAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ActiveSkinUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureOkAsync(response, "skin reset", ct).ConfigureAwait(false);
        return await GetAsync(accessToken, ct).ConfigureAwait(false);
    }

    /// <summary>Wears a cape, or hides it when <paramref name="capeId"/> is null.</summary>
    public async Task<AppearanceState> SetCapeAsync(string accessToken, string? capeId, CancellationToken ct = default)
    {
        var wear = !string.IsNullOrWhiteSpace(capeId);
        using var request = new HttpRequestMessage(wear ? HttpMethod.Put : HttpMethod.Delete, ActiveCapeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (wear)
            request.Content = JsonContent.Create(new { capeId });

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureOkAsync(response, "cape update", ct).ConfigureAwait(false);
        return await GetAsync(accessToken, ct).ConfigureAwait(false);
    }

    private async Task<ProfileDto> ReadProfileAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureOkAsync(response, "profile", ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ProfileDto>(body, Json)
            ?? throw new LauncherException("error.auth.profile", "Minecraft returned an empty profile.");
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, string step, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = "";
        try { body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }

        var key = (int)response.StatusCode switch
        {
            401 => "error.auth.expired",
            403 => "error.auth.appAccess",
            >= 500 or 429 => "error.auth.connection",
            _ => "error.skin.failed",
        };
        throw new LauncherException(key,
            $"Minecraft {step} returned HTTP {(int)response.StatusCode}."
                + (body.Length == 0 ? "" : $" {body[..Math.Min(200, body.Length)]}"));
    }

    /// <summary>
    /// Texture URLs as the page can actually load them.
    ///
    /// Mojang still hands out <c>http://textures.minecraft.net/...</c> for some
    /// skins and capes. The launcher's page is served over its own scheme and
    /// only trusts https, so a plain-http texture silently renders as nothing.
    /// The same host answers over https, so the scheme is simply upgraded.
    /// </summary>
    public static string Https(string url) =>
        url.StartsWith("http://textures.minecraft.net/", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["http://".Length..]
            : url;

    /// <summary>The API only knows these two; an unknown value is refused outright.</summary>
    private static string Normalise(string? variant) =>
        string.Equals(variant?.Trim(), "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";

    private static bool IsActive(string? state) => string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    /// <summary>The PNG signature, so a renamed jpg fails here rather than at the API.</summary>
    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    private sealed class ProfileDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("skins")] public List<SkinDto>? Skins { get; set; }
        [JsonPropertyName("capes")] public List<CapeDto>? Capes { get; set; }
    }

    private sealed class SkinDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("variant")] public string? Variant { get; set; }
    }

    private sealed class CapeDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("alias")] public string? Alias { get; set; }
    }
}
