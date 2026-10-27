using System.Security.Cryptography;
using System.Text;
using DusmpLauncher.Models;

namespace DusmpLauncher.Authentication;

public sealed class OfflineAuth : IOfflineAuth
{
    public string? ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length is < 3 or > 16) return "error.auth.offlineNameLength";
        return username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? null : "error.auth.offlineNameCharacters";
    }

    public GameSession CreateSession(string username)
    {
        var error = ValidateUsername(username);
        if (error is not null) throw new Core.LauncherException(error);
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var id = new Guid(bytes).ToString("N");
        var profile = new PlayerProfile { Type = AccountType.Offline, Username = username, Uuid = id, AvatarUrl = "app://launcher/assets/icon.png" };
        return new(profile, AuthConstants.OfflineAccessToken, id, username, null);
    }
}
