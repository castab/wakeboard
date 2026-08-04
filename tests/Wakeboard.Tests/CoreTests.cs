using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Wakeboard;
using Xunit;

namespace Wakeboard.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"wakeboard-{Guid.NewGuid():N}");

    [Fact]
    public void PasswordHashAndSessionAreAuthenticated()
    {
        var settings = new AppSettings { PasswordHash = SettingsStore.HashPassword("correct horse", 100_000), SessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) };
        Assert.Equal(3000, settings.Port);
        Assert.True(SettingsStore.VerifyPassword("correct horse", settings.PasswordHash));
        Assert.False(SettingsStore.VerifyPassword("wrong", settings.PasswordHash));
        var auth = new AuthService(settings);
        var now = DateTimeOffset.UtcNow;
        var token = auth.CreateSession(now);
        Assert.True(auth.VerifySession(token, now.AddMinutes(1)));
        Assert.False(auth.VerifySession(token + "x", now.AddMinutes(1)));
        Assert.False(auth.VerifySession(token, now.AddHours(13)));
    }

    [Fact]
    public void OriginValidationSupportsAnHttpsProxyHostname()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("wakeboard.example.ts.net");
        context.Request.Headers.Origin = "https://wakeboard.example.ts.net";

        Assert.True(AuthService.HasValidOrigin(context.Request));

        context.Request.Headers.Origin = "https://attacker.example";
        Assert.False(AuthService.HasValidOrigin(context.Request));
    }

    [Theory]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("aabb.ccdd.eeff", "AA:BB:CC:DD:EE:FF")]
    public void MacAddressesAreNormalized(string input, string expected) => Assert.Equal(expected, HostValidator.NormalizeMac(input));

    [Fact]
    public void MagicPacketAndBroadcastAreCorrect()
    {
        var mac = Convert.FromHexString("AABBCCDDEEFF");
        var packet = NetworkService.BuildMagicPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), value => Assert.Equal(0xFF, value));
        for (var index = 1; index <= 16; index++) Assert.Equal(mac, packet.Skip(index * 6).Take(6));
        Assert.Equal("192.168.4.255", NetworkService.CalculateBroadcast(IPAddress.Parse("192.168.4.22"), IPAddress.Parse("255.255.255.0")).ToString());
    }

    [Fact]
    public async Task StorePreservesConcurrentChangesAndLegacyTargets()
    {
        var paths = new AppPaths(root);
        var store = new ConfigStore(paths);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => store.CreateAsync(new HostInput(
            $"Host {index}", $"02:00:00:00:00:{index:X2}", "adapter-id", index == 0 ? null : $"host-{index}.local"))));
        var hosts = await store.ListAsync();
        Assert.Equal(10, hosts.Count);
        Assert.Equal("", hosts.Single(host => host.Name == "Host 0").CheckTarget);
        Assert.True(File.Exists(paths.ConfigFile));
    }

    [Fact]
    public async Task StoreRefusesCorruptData()
    {
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.ConfigFile, "not-json");
        await Assert.ThrowsAnyAsync<Exception>(() => new ConfigStore(paths).ReadAsync());
        Assert.Equal("not-json", await File.ReadAllTextAsync(paths.ConfigFile));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
