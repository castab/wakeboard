using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Wakeboard;
using Xunit;

namespace Wakeboard.Tests;

public sealed class PasskeyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"wakeboard-{Guid.NewGuid():N}");

    private static AppSettings NewSettings() => new()
    {
        PasswordHash = SettingsStore.HashPassword("correct horse", 100_000),
        SessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
    };

    [Fact]
    public void LoadDefaultsPasskeysForLegacySettingsFiles()
    {
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.Root);
        var legacyJson = JsonSerializer.Serialize(new
        {
            passwordHash = SettingsStore.HashPassword("correct horse", 100_000),
            sessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            port = 3000,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(paths.SettingsFile, legacyJson);

        var settings = SettingsStore.Load(paths);

        Assert.Empty(settings.Passkeys);
        Assert.Null(settings.PasskeyUserHandle);
    }

    [Fact]
    public void SettingsRoundTripPreservesPasskeys()
    {
        var paths = new AppPaths(root);
        var credential = new PasskeyCredential
        {
            Id = "cred-id", PublicKey = "public-key", SignCount = 7, RpId = "localhost",
            Label = "Windows Hello", CreatedAt = DateTimeOffset.UtcNow, LastUsedAt = DateTimeOffset.UtcNow,
        };
        var settings = NewSettings();
        settings.Passkeys.Add(credential);
        settings.PasskeyUserHandle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        SettingsStore.Save(paths, settings);
        var loaded = SettingsStore.Load(paths);

        var loadedCredential = Assert.Single(loaded.Passkeys);
        Assert.Equal(credential.Id, loadedCredential.Id);
        Assert.Equal(credential.PublicKey, loadedCredential.PublicKey);
        Assert.Equal(credential.SignCount, loadedCredential.SignCount);
        Assert.Equal(credential.RpId, loadedCredential.RpId);
        Assert.Equal(credential.Label, loadedCredential.Label);
        Assert.Equal(credential.CreatedAt, loadedCredential.CreatedAt);
        Assert.Equal(credential.LastUsedAt, loadedCredential.LastUsedAt);
        Assert.Equal(settings.PasskeyUserHandle, loaded.PasskeyUserHandle);
    }

    [Fact]
    public async Task StoreAddsConcurrentPasskeysWithoutLoss()
    {
        var paths = new AppPaths(root);
        SettingsStore.Save(paths, NewSettings());
        var store = new PasskeyStore(paths);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => store.AddAsync(new PasskeyCredential
        {
            Id = $"cred-{index}", PublicKey = "key", SignCount = 0, RpId = "localhost",
            Label = $"Key {index}", CreatedAt = DateTimeOffset.UtcNow,
        })));

        var stored = await store.ListAsync();
        Assert.Equal(10, stored.Count);
    }

    [Fact]
    public async Task StoreUpdatesSignCountAndDeletesCredentials()
    {
        var paths = new AppPaths(root);
        SettingsStore.Save(paths, NewSettings());
        var store = new PasskeyStore(paths);
        await store.AddAsync(new PasskeyCredential { Id = "cred-1", PublicKey = "key", SignCount = 0, RpId = "localhost", Label = "Key", CreatedAt = DateTimeOffset.UtcNow });

        var usedAt = DateTimeOffset.UtcNow;
        await store.UpdateSignCountAsync("cred-1", 5, usedAt);
        var updated = Assert.Single(await store.ListAsync());
        Assert.Equal(5u, updated.SignCount);
        Assert.Equal(usedAt, updated.LastUsedAt);

        await store.DeleteAsync("cred-1");
        Assert.Empty(await store.ListAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.DeleteAsync("cred-1"));
    }

    [Fact]
    public void SignStateRoundTripsAndRejectsTamperingExpiryAndWrongPurpose()
    {
        var auth = new AuthService(NewSettings());
        var now = DateTimeOffset.UtcNow;
        var token = auth.SignState("passkey-register", "{\"challenge\":\"abc\"}", TimeSpan.FromMinutes(2), now);

        Assert.True(auth.TryVerifyState("passkey-register", token, out var payload, now.AddSeconds(30)));
        Assert.Equal("{\"challenge\":\"abc\"}", payload);

        Assert.False(auth.TryVerifyState("passkey-register", token, out _, now.AddMinutes(3)));
        Assert.False(auth.TryVerifyState("passkey-register", token + "x", out _, now));
        Assert.False(auth.TryVerifyState("passkey-login", token, out _, now));
    }

    [Theory]
    [InlineData("http", "localhost", true)]
    [InlineData("http", "127.0.0.1", false)]
    [InlineData("http", "wakeboard-pc", false)]
    [InlineData("https", "wakeboard.example.ts.net", true)]
    [InlineData("https", "127.0.0.1", false)]
    public void OriginEligibilityMatchesExpectedTable(string scheme, string host, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        Assert.Equal(expected, PasskeyPolicy.IsEligibleOrigin(context.Request));
    }

    [Fact]
    public void DeriveRpIdStripsPort()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost", 3000);
        Assert.Equal("localhost", PasskeyPolicy.DeriveRpId(context.Request));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
