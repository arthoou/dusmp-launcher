using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DusmpLauncher.Models;

namespace DusmpLauncher.Accounts;

/// <summary>
/// One account the player has signed in with, as the drawer shows it.
/// </summary>
/// <remarks>
/// Never holds a token. A Microsoft account's refresh token stays in the secure
/// store under <see cref="TokenKey"/>; this file is plain JSON the player can
/// read, so anything secret would be a leak waiting to happen.
/// </remarks>
public sealed record StoredAccount
{
    /// <summary>Stable id: the UUID for Microsoft, the lowercased name offline.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("type")] public AccountType Type { get; init; }
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("uuid")] public string? Uuid { get; init; }
    [JsonPropertyName("skinUrl")] public string? SkinUrl { get; init; }
    [JsonPropertyName("addedAt")] public DateTimeOffset AddedAt { get; init; }

    /// <summary>Where this account's refresh token lives in the secure store.</summary>
    [JsonIgnore] public string TokenKey => $"microsoft.refreshToken.{Id}";
}

/// <summary>
/// The list of signed-in accounts, and which one is active.
///
/// The launcher used to keep exactly one account in its config, so signing in
/// again simply overwrote the previous one. The account drawer needs them all,
/// and the player needs to be able to switch without signing in from scratch
/// every time, which is what the per-account token key buys.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _file;

    /// <summary>Serialises writes: two sign-ins finishing at once must not lose one.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<StoredAccount>? _accounts;
    private string? _activeId;

    public AccountStore(string file) => _file = file;

    /// <summary>Every account, oldest first, so the list does not reshuffle itself.</summary>
    public async Task<IReadOnlyList<StoredAccount>> ListAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        return _accounts!;
    }

    public async Task<StoredAccount?> GetActiveAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        return _accounts!.FirstOrDefault(x => x.Id == _activeId) ?? _accounts!.FirstOrDefault();
    }

    /// <summary>
    /// Records an account and makes it active. Signing in again with the same
    /// account updates the name and skin instead of adding a duplicate.
    /// </summary>
    public async Task<StoredAccount> UpsertAsync(PlayerProfile profile, CancellationToken ct = default)
    {
        var id = IdFor(profile);
        if (id.Length == 0) throw new Core.LauncherException("error.auth.failed", "An account needs a name or a uuid.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(ct).ConfigureAwait(false);
            var existing = _accounts!.FirstOrDefault(x => x.Id == id);
            var account = new StoredAccount
            {
                Id = id,
                Type = profile.Type,
                Username = profile.Username,
                Uuid = profile.Uuid,
                SkinUrl = profile.SkinUrl,
                AddedAt = existing?.AddedAt ?? DateTimeOffset.UtcNow,
            };

            if (existing is not null) _accounts!.Remove(existing);
            _accounts!.Add(account);
            _accounts!.Sort((a, b) => a.AddedAt.CompareTo(b.AddedAt));
            _activeId = id;
            await SaveAsync(ct).ConfigureAwait(false);
            return account;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Makes an account active. Returns null when the id is unknown.</summary>
    public async Task<StoredAccount?> SelectAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(ct).ConfigureAwait(false);
            var account = _accounts!.FirstOrDefault(x => x.Id == id);
            if (account is null) return null;
            _activeId = id;
            await SaveAsync(ct).ConfigureAwait(false);
            return account;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Forgets an account. Returns the one that is active afterwards, which is
    /// null once the last account is gone — the caller then sends the player
    /// back to the sign-in screen.
    /// </summary>
    public async Task<StoredAccount?> RemoveAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(ct).ConfigureAwait(false);
            var account = _accounts!.FirstOrDefault(x => x.Id == id);
            if (account is not null) _accounts!.Remove(account);
            if (_activeId == id) _activeId = _accounts!.LastOrDefault()?.Id;
            await SaveAsync(ct).ConfigureAwait(false);
            return _accounts!.FirstOrDefault(x => x.Id == _activeId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The id an account is filed under, derived so it is stable across sign-ins.</summary>
    public static string IdFor(PlayerProfile profile) => profile.Type == AccountType.Microsoft
        ? (profile.Uuid ?? "").Trim().ToLowerInvariant()
        : (profile.Username ?? "").Trim().ToLowerInvariant();

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_accounts is not null) return;

        try
        {
            if (File.Exists(_file))
            {
                var body = await File.ReadAllTextAsync(_file, ct).ConfigureAwait(false);
                var file = JsonSerializer.Deserialize<AccountsFile>(body, Json);
                _accounts = file?.Accounts?.Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList() ?? new();
                _activeId = file?.ActiveId;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged list must not lock the player out; they sign in again.
            Console.Error.WriteLine($"account list unreadable, starting empty: {ex.GetType().Name}");
        }

        _accounts = new();
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var payload = JsonSerializer.Serialize(new AccountsFile { ActiveId = _activeId, Accounts = _accounts! }, Json);
            var temporary = _file + ".tmp";
            await File.WriteAllTextAsync(temporary, payload, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not save the account list: {ex.GetType().Name}");
        }
    }

    private sealed class AccountsFile
    {
        [JsonPropertyName("activeId")] public string? ActiveId { get; set; }
        [JsonPropertyName("accounts")] public List<StoredAccount> Accounts { get; set; } = new();
    }
}
