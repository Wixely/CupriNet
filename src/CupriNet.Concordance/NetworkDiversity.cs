using System.Net;
using System.Net.Sockets;
using CupriNet.Core;

namespace CupriNet.Concordance;

/// <summary>Derives coarse network-locality keys used to keep the Constellation diverse (Temperance).</summary>
public static class NetworkDiversity
{
    /// <summary>
    /// Returns the IPv4 /24 prefix (top 24 bits) of the first parseable IPv4 endpoint, or null if the
    /// peer advertises no IPv4 literal. Used to cap how many peers from one /24 may occupy the table.
    /// </summary>
    public static uint? Slash24(IEnumerable<Beacon> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (var beacon in endpoints)
        {
            if (IPAddress.TryParse(beacon.Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return (uint)((b[0] << 16) | (b[1] << 8) | b[2]);
            }
        }

        return null;
    }
}
