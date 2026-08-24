using System.Net;

namespace Wakeboard;

public static class PasskeyPolicy
{
    public static bool IsEligibleOrigin(HttpRequest request)
    {
        var host = request.Host.Host;
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _)) return false;
        if (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(request.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    public static string DeriveRpId(HttpRequest request) => request.Host.Host;
}
