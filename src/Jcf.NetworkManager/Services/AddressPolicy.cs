using System.Net;
using System.Net.Sockets;

namespace Jcf.NetworkManager.Services;

public static class AddressPolicy
{
    public static bool IsAllowed(string? address, IReadOnlyList<string> permittedCidrs)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast)) return false;
        return permittedCidrs.Any(cidr => MatchesCidr(ip, cidr));
    }

    /// <summary>True when the value is a well-formed IPv4 CIDR such as 10.250.0.0/16.</summary>
    public static bool IsValidCidr(string? cidr)
    {
        var bits = cidr?.Split('/');
        return bits is { Length: 2 } && IPAddress.TryParse(bits[0], out var prefix) &&
               prefix.AddressFamily == AddressFamily.InterNetwork && bits[0].Count(c => c == '.') == 3 &&
               int.TryParse(bits[1], out var count) && count is >= 0 and <= 32;
    }

    private static bool MatchesCidr(IPAddress address, string cidr)
    {
        var bits = cidr.Split('/');
        if (bits.Length != 2 || !IPAddress.TryParse(bits[0], out var prefix) ||
            prefix.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(bits[1], out var count) || count < 0 || count > 32) return false;
        var a = address.GetAddressBytes();
        var p = prefix.GetAddressBytes();
        int full = count / 8, extra = count % 8;
        for (var i = 0; i < full; i++) if (a[i] != p[i]) return false;
        return extra == 0 || (a[full] & (0xff << (8 - extra))) == (p[full] & (0xff << (8 - extra)));
    }
}
