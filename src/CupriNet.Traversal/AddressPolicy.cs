using System.Net;
using System.Net.Sockets;

namespace CupriNet.Traversal;

/// <summary>
/// A single address range, parsed from CIDR (<c>10.0.0.0/8</c>, <c>2001:db8::/32</c>), a dotted netmask
/// (<c>10.0.0.0/255.0.0.0</c>), or a bare IP (<c>192.168.0.1</c> → a host route). IPv4 and IPv6.
/// </summary>
public sealed class SubnetRange
{
    private readonly byte[] _network;
    private readonly int _prefixBits;
    private readonly AddressFamily _family;

    private SubnetRange(IPAddress network, int prefixBits)
    {
        _family = network.AddressFamily;
        _prefixBits = prefixBits;
        _network = Masked(network.GetAddressBytes(), prefixBits); // canonical network address
    }

    public static bool TryParse(string? text, out SubnetRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();

        var slash = text.IndexOf('/');
        var ipPart = slash < 0 ? text : text[..slash];
        var suffix = slash < 0 ? null : text[(slash + 1)..];

        if (!IPAddress.TryParse(ipPart, out var ip))
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        var maxBits = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;

        int prefix;
        if (suffix is null)
        {
            prefix = maxBits; // a bare IP is a single-host route
        }
        else if (int.TryParse(suffix, out prefix))
        {
            if (prefix < 0 || prefix > maxBits)
                return false;
        }
        else if (IPAddress.TryParse(suffix, out var mask) && mask.AddressFamily == ip.AddressFamily
                 && TryNetmaskToPrefix(mask.GetAddressBytes(), out prefix))
        {
            // dotted-netmask form, e.g. 10.0.0.0/255.0.0.0
        }
        else
        {
            return false;
        }

        range = new SubnetRange(ip, prefix);
        return true;
    }

    /// <summary>True if <paramref name="address"/> falls within this range.</summary>
    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != _family)
            return false;

        var bytes = address.GetAddressBytes();
        var fullBytes = _prefixBits / 8;
        for (var i = 0; i < fullBytes; i++)
            if (bytes[i] != _network[i])
                return false;

        var remaining = _prefixBits % 8;
        if (remaining > 0)
        {
            var mask = (byte)(0xFF << (8 - remaining));
            if ((bytes[fullBytes] & mask) != (_network[fullBytes] & mask))
                return false;
        }
        return true;
    }

    private static byte[] Masked(byte[] address, int prefixBits)
    {
        var result = (byte[])address.Clone();
        for (var i = 0; i < result.Length; i++)
        {
            var bit = i * 8;
            if (bit >= prefixBits) { result[i] = 0; continue; }
            var take = Math.Min(8, prefixBits - bit);
            if (take < 8)
                result[i] &= (byte)(0xFF << (8 - take));
        }
        return result;
    }

    private static bool TryNetmaskToPrefix(byte[] mask, out int prefix)
    {
        prefix = 0;
        var seenZero = false;
        foreach (var b in mask)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var set = (b & (1 << bit)) != 0;
                if (set)
                {
                    if (seenZero) return false; // non-contiguous mask (e.g. 255.0.255.0) — invalid
                    prefix++;
                }
                else
                {
                    seenZero = true;
                }
            }
        }
        return true;
    }
}

/// <summary>
/// An allow/deny policy over IP address ranges, used to fence a node to specific subnets — a private CupriNet,
/// or LAN-only, etc. Rules: <b>a match in the allow list always wins</b> over the deny list; then a deny match
/// blocks; otherwise the address is allowed (default-allow). So "LAN + WAN" is the empty policy; a private net is
/// <c>deny 0.0.0.0/0</c> (and <c>::/0</c>) plus <c>allow &lt;your subnets&gt;</c>. IP-based, so it does not apply
/// to Tor (onion) peers.
/// </summary>
public sealed class AddressPolicy
{
    /// <summary>An empty policy — allows everything (no filtering).</summary>
    public static readonly AddressPolicy Unrestricted = new([], []);

    private readonly SubnetRange[] _allow;
    private readonly SubnetRange[] _deny;

    private AddressPolicy(SubnetRange[] allow, SubnetRange[] deny)
    {
        _allow = allow;
        _deny = deny;
    }

    /// <summary>True when neither list has any entry, so the policy is a no-op.</summary>
    public bool IsEmpty => _allow.Length == 0 && _deny.Length == 0;

    /// <summary>Parses allow/deny CIDR-or-netmask strings. Throws <see cref="FormatException"/> on any bad entry
    /// so a misconfiguration fails loudly rather than silently permitting traffic.</summary>
    public static AddressPolicy Parse(IEnumerable<string>? allow, IEnumerable<string>? deny)
        => new(ParseAll(allow, nameof(allow)), ParseAll(deny, nameof(deny)));

    private static SubnetRange[] ParseAll(IEnumerable<string>? entries, string which)
    {
        if (entries is null)
            return [];
        var ranges = new List<SubnetRange>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (!SubnetRange.TryParse(entry, out var range))
                throw new FormatException($"Invalid {which} subnet '{entry}'. Use CIDR (10.0.0.0/8), a netmask (10.0.0.0/255.0.0.0), or a bare IP.");
            ranges.Add(range!);
        }
        return ranges.ToArray();
    }

    /// <summary>Whether an address is permitted: allow-list match wins; else a deny-list match blocks; else allowed.</summary>
    public bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        foreach (var range in _allow)
            if (range.Contains(address))
                return true; // whitelist always beats blacklist
        foreach (var range in _deny)
            if (range.Contains(address))
                return false;
        return true; // default allow
    }
}
