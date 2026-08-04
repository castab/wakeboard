using System.Security.Cryptography;
using System.Text.Json;

namespace Wakeboard;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AppSettings Load(AppPaths paths)
    {
        if (!File.Exists(paths.SettingsFile))
            throw new InvalidOperationException($"Wakeboard is not configured. Run '{Environment.ProcessPath} install' from an elevated terminal.");
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), JsonOptions)
            ?? throw new InvalidDataException("settings.json is empty or invalid.");
        if (settings.Port is < 1 or > 65535 || settings.SessionSecret.Length < 32 || string.IsNullOrWhiteSpace(settings.PasswordHash))
            throw new InvalidDataException("settings.json contains invalid values.");
        return settings;
    }

    public static void Save(AppPaths paths, AppSettings settings)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static string HashPassword(string password, int iterations = 310_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2_sha256:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split(':');
            if (parts.Length != 4 || parts[0] != "pbkdf2_sha256" || !int.TryParse(parts[1], out var iterations) || iterations < 100_000) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}
