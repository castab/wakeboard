using System.Text.Json;

namespace Wakeboard;

public sealed class ConfigStore(AppPaths paths)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<AppConfig> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.ConfigFile)) return new AppConfig();
        await using var stream = new FileStream(paths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("config.json is empty or invalid.");
        Validate(config);
        return config;
    }

    public async Task<IReadOnlyList<WakeHost>> ListAsync(CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).Hosts;

    public async Task<WakeHost?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).Hosts.FirstOrDefault(host => host.Id == id);

    public async Task<WakeHost> CreateAsync(HostInput input, CancellationToken cancellationToken = default)
    {
        var validated = HostValidator.Validate(input);
        var now = DateTimeOffset.UtcNow;
        var host = new WakeHost
        {
            Id = Guid.NewGuid().ToString(), Name = validated.Name, MacAddress = validated.Mac,
            PreferredInterfaceId = validated.InterfaceId, CheckTarget = validated.CheckTarget,
            CreatedAt = now, UpdatedAt = now
        };
        await UpdateAsync(config =>
        {
            if (config.Hosts.Any(item => item.MacAddress == host.MacAddress)) throw new ArgumentException("That MAC address already exists.");
            config.Hosts.Add(host);
        }, cancellationToken);
        return host;
    }

    public async Task<WakeHost> UpdateHostAsync(string id, HostInput input, CancellationToken cancellationToken = default)
    {
        var validated = HostValidator.Validate(input);
        WakeHost? updated = null;
        await UpdateAsync(config =>
        {
            var index = config.Hosts.FindIndex(host => host.Id == id);
            if (index < 0) throw new KeyNotFoundException("Host not found.");
            if (config.Hosts.Any(host => host.Id != id && host.MacAddress == validated.Mac)) throw new ArgumentException("That MAC address already exists.");
            var original = config.Hosts[index];
            updated = new WakeHost
            {
                Id = original.Id, Name = validated.Name, MacAddress = validated.Mac,
                PreferredInterfaceId = validated.InterfaceId, CheckTarget = validated.CheckTarget,
                CreatedAt = original.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow
            };
            config.Hosts[index] = updated;
        }, cancellationToken);
        return updated!;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(config =>
        {
            var index = config.Hosts.FindIndex(host => host.Id == id);
            if (index < 0) throw new KeyNotFoundException("Host not found.");
            config.Hosts.RemoveAt(index);
        }, cancellationToken);
    }

    private async Task UpdateAsync(Action<AppConfig> mutation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadAsync(cancellationToken);
            mutation(config);
            Validate(config);
            await WriteAtomicAsync(config, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task WriteAtomicAsync(AppConfig config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var temporary = Path.Combine(paths.DataDirectory, $"config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, paths.ConfigFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Validate(AppConfig config)
    {
        if (config.Version != 1 || config.Hosts is null) throw new InvalidDataException("Unsupported or invalid configuration schema.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in config.Hosts)
        {
            if (string.IsNullOrWhiteSpace(host.Id) || !ids.Add(host.Id) || string.IsNullOrWhiteSpace(host.Name) || host.Name.Length > 100)
                throw new InvalidDataException("Configuration contains an invalid host record.");
            try
            {
                host.MacAddress = HostValidator.NormalizeMac(host.MacAddress);
                host.CheckTarget = HostValidator.ValidateCheckTarget(host.CheckTarget);
            }
            catch (ArgumentException error) { throw new InvalidDataException($"Host '{host.Id}' is invalid: {error.Message}", error); }
            if (string.IsNullOrWhiteSpace(host.PreferredInterfaceId) || host.CreatedAt == default || host.UpdatedAt == default)
                throw new InvalidDataException($"Host '{host.Id}' is invalid.");
        }
    }
}
