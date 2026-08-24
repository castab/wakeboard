using System.Text.Json;

namespace Wakeboard;

public sealed class WakeHost
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string MacAddress { get; set; }
    public required string PreferredInterfaceId { get; set; }
    public string CheckTarget { get; set; } = "";
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<WakeHost> Hosts { get; set; } = [];
}

public sealed record HostInput(string? Name, string? MacAddress, string? PreferredInterfaceId, string? CheckTarget);
public sealed record AdapterAddress(string Address, string SubnetMask, string BroadcastAddress);
public sealed record AdapterInfo(string Id, string Name, string Description, IReadOnlyList<AdapterAddress> Addresses);
public sealed record WakeInput(string? InterfaceId);
public sealed record WakeResponse(string InterfaceId, int PacketsSent, IReadOnlyList<string> Destinations, string Message);
public sealed record LivenessResponse(string Target, bool Reachable, long? RoundTripTimeMs, string? Address, string Message, DateTimeOffset CheckedAt);
public sealed record LoginInput(string? Password);

public sealed class PasskeyCredential
{
    public required string Id { get; set; }
    public required string PublicKey { get; set; }
    public required uint SignCount { get; set; }
    public required string RpId { get; set; }
    public required string Label { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed record PasskeyCredentialView(string Id, string Label, string RpId, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt)
{
    public static PasskeyCredentialView From(PasskeyCredential credential) =>
        new(credential.Id, credential.Label, credential.RpId, credential.CreatedAt, credential.LastUsedAt);
}

public sealed record PasskeyCeremonyResponse(JsonElement Options, string State);
public sealed record PasskeyRegisterVerifyInput(string? State, JsonElement Attestation, string? Label);
public sealed record PasskeyLoginVerifyInput(string? State, JsonElement Assertion);

public sealed class AppSettings
{
    public required string PasswordHash { get; set; }
    public required string SessionSecret { get; set; }
    public int Port { get; set; } = 3000;
    public List<PasskeyCredential> Passkeys { get; set; } = [];
    public string? PasskeyUserHandle { get; set; }
}
