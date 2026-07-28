using System.Net;
using System.Net.Sockets;

namespace CupriNet.Traversal;

/// <summary>Address classification shared by the reachability features (reflexive discovery, NAT-PMP mapping).</summary>
public static class NetworkReachability
{
    /// <summary>
    /// True only for addresses that could plausibly be a real, dialable <em>public</em> endpoint — i.e. not
    /// loopback, private (RFC 1918), link-local, CGNAT (100.64/10), multicast, unspecified, or IPv6 unique-local.
    /// Used to reject a peer-reported reflexive address or a gateway-reported NAT-PMP address that is unroutable
    /// (a poisoning / misconfiguration vector), before it is ever advertised as a Mapped beacon.
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] >= 224) return false;          // 0/8, 10/8, multicast/reserved 224+
            if (b[0] == 127) return false;                                     // loopback
            if (b[0] == 169 && b[1] == 254) return false;                      // link-local 169.254/16
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;         // 172.16/12
            if (b[0] == 192 && b[1] == 168) return false;                      // 192.168/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;        // CGNAT 100.64/10
            if (b[0] == 255) return false;                                     // broadcast
            return true;
        }

        // IPv6: reject unspecified, link-local (fe80::/10), unique-local (fc00::/7), and multicast (ff00::/8).
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any))
            return false;
        var v6 = address.GetAddressBytes();
        if ((v6[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique-local
        return true;
    }
}
