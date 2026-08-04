using System.Security.Cryptography;
using System.Text;

namespace Wakeboard;

public sealed class AuthService(AppSettings settings)
{
    public const string CookieName = "wakeboard_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    public bool VerifyPassword(string password) => SettingsStore.VerifyPassword(password, settings.PasswordHash);

    public string CreateSession(DateTimeOffset? now = null)
    {
        var expires = (now ?? DateTimeOffset.UtcNow).Add(SessionLifetime).ToUnixTimeSeconds().ToString();
        return $"{expires}.{Sign(expires)}";
    }

    public bool VerifySession(string? token, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var expires) || expires <= (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(parts[0]));
        var actual = Encoding.ASCII.GetBytes(parts[1]);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public CookieOptions CookieOptions(bool secure) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = secure,
        Path = "/", MaxAge = SessionLifetime, IsEssential = true
    };

    private string Sign(string value)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(settings.SessionSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool HasValidOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var originValue) || !Uri.TryCreate(originValue.ToString(), UriKind.Absolute, out var origin)) return false;
        return string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase);
    }
}
