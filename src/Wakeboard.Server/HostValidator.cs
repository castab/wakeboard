using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Wakeboard;

public static partial class HostValidator
{
    [GeneratedRegex("^[A-Za-z0-9_](?:[A-Za-z0-9._-]*[A-Za-z0-9_])?$")]
    private static partial Regex HostnamePattern();

    public static (string Name, string Mac, string InterfaceId, string CheckTarget) Validate(HostInput input)
    {
        var name = (input.Name ?? "").Trim();
        var interfaceId = (input.PreferredInterfaceId ?? "").Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("Name must be between 1 and 100 characters.");
        if (interfaceId.Length is < 1 or > 200) throw new ArgumentException("Select a network adapter.");
        return (name, NormalizeMac(input.MacAddress ?? ""), interfaceId, ValidateCheckTarget(input.CheckTarget));
    }

    public static string NormalizeMac(string value)
    {
        var compact = new string(value.Where(character => character is not ':' and not '-' and not '.' && !char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();
        if (compact.Length != 12 || !compact.All(Uri.IsHexDigit)) throw new ArgumentException("Enter a valid six-byte MAC address.");
        return string.Join(':', Enumerable.Range(0, 6).Select(index => compact.Substring(index * 2, 2)));
    }

    public static string ValidateCheckTarget(string? value)
    {
        var target = (value ?? "").Trim();
        if (target.Length == 0) return "";
        if (target.Length > 253) throw new ArgumentException("The ping target is too long.");
        if (IPAddress.TryParse(target, out _)) return target;
        if (!HostnamePattern().IsMatch(target)) throw new ArgumentException("Enter an IP address or hostname without a protocol or path.");
        return target;
    }
}
