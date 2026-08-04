using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Wakeboard;

public sealed class NetworkService
{
    public IReadOnlyList<AdapterInfo> ListAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
            && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
            && adapter.Supports(NetworkInterfaceComponent.IPv4))
        .Select(adapter => new AdapterInfo(adapter.Id, adapter.Name, adapter.Description,
            GetUsableAddresses(adapter).Select(item => new AdapterAddress(item.Address.ToString(), item.Mask.ToString(), CalculateBroadcast(item.Address, item.Mask).ToString())).ToArray()))
        .Where(adapter => adapter.Addresses.Count > 0)
        .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<WakeResponse> WakeAsync(string macAddress, string interfaceId, CancellationToken cancellationToken)
    {
        var mac = Convert.FromHexString(HostValidator.NormalizeMac(macAddress).Replace(":", ""));
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.Id == interfaceId)
            ?? throw new KeyNotFoundException("The selected network adapter was not found.");
        if (adapter.OperationalStatus != OperationalStatus.Up) throw new InvalidOperationException("The selected network adapter is not active.");
        var addresses = GetUsableAddresses(adapter);
        if (addresses.Count == 0) throw new InvalidOperationException("The selected network adapter has no broadcast-capable IPv4 address.");
        var packet = BuildMagicPacket(mac);
        var destinations = new List<string>();
        var sent = 0;
        foreach (var address in addresses)
        {
            var endpoint = new IPEndPoint(CalculateBroadcast(address.Address, address.Mask), 9);
            using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            client.Client.Bind(new IPEndPoint(address.Address, 0));
            destinations.Add(endpoint.ToString());
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await client.SendAsync(packet, endpoint, cancellationToken);
                sent++;
                if (attempt < 2) await Task.Delay(100, cancellationToken);
            }
        }
        return new WakeResponse(interfaceId, sent, destinations, "Wake packets were transmitted successfully.");
    }

    public async Task<LivenessResponse> CheckLivenessAsync(string target, CancellationToken cancellationToken)
    {
        target = HostValidator.ValidateCheckTarget(target);
        if (target.Length == 0) throw new ArgumentException("An IP address or hostname is required.");
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, 1500).WaitAsync(cancellationToken);
            var reachable = reply.Status == IPStatus.Success;
            return new LivenessResponse(target, reachable, reachable ? reply.RoundtripTime : null, reply.Address?.ToString(),
                reachable ? "The host responded to ICMP echo." : $"No ICMP response ({reply.Status}).", DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is PingException or SocketException or ArgumentException)
        {
            return new LivenessResponse(target, false, null, null, $"No ICMP response ({error.Message}).", DateTimeOffset.UtcNow);
        }
    }

    public static byte[] BuildMagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("A MAC address must contain six bytes.");
        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var index = 1; index <= 16; index++) Buffer.BlockCopy(mac, 0, packet, index * 6, 6);
        return packet;
    }

    public static IPAddress CalculateBroadcast(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4) throw new ArgumentException("IPv4 address and mask are required.");
        return new IPAddress(addressBytes.Zip(maskBytes, (value, maskValue) => (byte)(value | ~maskValue)).ToArray());
    }

    private static IReadOnlyList<(IPAddress Address, IPAddress Mask)> GetUsableAddresses(NetworkInterface adapter) => adapter.GetIPProperties().UnicastAddresses
        .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && item.IPv4Mask is not null
            && !IPAddress.IsLoopback(item.Address) && !item.Address.Equals(IPAddress.Any)
            && !item.IPv4Mask.Equals(IPAddress.Any) && !item.IPv4Mask.Equals(IPAddress.Broadcast))
        .Select(item => (item.Address, item.IPv4Mask)).Distinct().ToArray();
}
