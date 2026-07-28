using System.Net;
using System.Net.Sockets;
using CupriNet.Abstractions;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Vessel;

namespace CupriNet.Traversal;

/// <summary>
/// A STUN-like reflexive-endpoint exchange over a paired Vessel: each side tells the other the source
/// endpoint it observed for it. A node that dialled out thereby learns its externally-observed
/// (NAT-mapped) address — which it can advertise as a Mapped beacon so others can reach it.
/// </summary>
public static class ReflexiveExchange
{
    /// <summary>Logical stream reserved for the reflexive-endpoint exchange.</summary>
    public const ushort ReflexionStream = 5;

    /// <summary>
    /// Exchanges observed endpoints and returns how the peer observed <em>this</em> node (its reflexive
    /// endpoint). The initiator sends first to avoid a deadlock.
    /// </summary>
    public static async Task<IPEndPoint> ExchangeAsync(IVessel vessel, bool initiator, ushort stream = ReflexionStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        if (vessel.RemoteEndPoint is not IPEndPoint observedPeer)
            throw new InvalidOperationException("Vessel has no IP remote endpoint to reflect.");

        var myReport = Encode(observedPeer);

        byte[] peerReport;
        if (initiator)
        {
            await vessel.SendAsync(stream, myReport, cancellationToken).ConfigureAwait(false);
            peerReport = await ReceiveAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            peerReport = await ReceiveAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
            await vessel.SendAsync(stream, myReport, cancellationToken).ConfigureAwait(false);
        }

        return Decode(peerReport);
    }

    /// <summary>Encodes an endpoint (address bytes + port) for the exchange. IPv4-mapped-IPv6 is canonicalised to IPv4.</summary>
    public static byte[] Encode(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        var w = new CodexWriter();
        w.WriteBytes(address.GetAddressBytes());
        w.WriteVarUInt((ulong)(uint)endpoint.Port);
        return w.ToArray();
    }

    /// <summary>Decodes an endpoint produced by <see cref="Encode"/>.</summary>
    public static IPEndPoint Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        var addressBytes = r.ReadBytes();
        if (addressBytes.Length is not (4 or 16))
            throw new CodexFormatException("Endpoint address must be 4 (IPv4) or 16 (IPv6) bytes.");
        var address = new IPAddress(addressBytes);
        var portValue = r.ReadVarUInt();
        if (portValue > ushort.MaxValue)
            throw new CodexFormatException("Endpoint port is out of range.");
        return new IPEndPoint(address, (int)portValue);
    }

    private static async Task<byte[]> ReceiveAsync(IVessel vessel, ushort stream, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Vessel closed during the reflexive exchange.");
        if (frame.StreamId != stream)
            throw new InvalidOperationException($"Unexpected frame on stream {frame.StreamId} during the reflexive exchange.");
        return frame.Payload;
    }
}

/// <summary>
/// Aggregates reflexive observations to decide this node's externally-observed (NAT-mapped) address, hardened
/// against a peer that lies about it. Three defenses layer here:
/// <list type="number">
/// <item>Each observation is attributed to the reporting peer's <see cref="Sigil"/> and kept one-per-identity
/// (latest wins) — a peer cannot stuff the ballot by reconnecting.</item>
/// <item>Only endpoints reported by peers we <em>chose to dial</em> should be fed in (the caller passes those);
/// an inbound connector's cheap, free lie never counts.</item>
/// <item>Belief requires a quorum of <em>distinct</em> reporter Sigils spanning <em>distinct</em> reporter /24s,
/// so a Sybil must control identities across multiple netblocks, not just multiple keys.</item>
/// <item>Each vote is <em>weighted by the reporter's standing</em> (supplied by the caller): a brand-new peer
/// counts for little, an established one for more, and a tainted/quarantined one (weight ≤ 0) not at all — so
/// fresh Sybil identities carry less influence than peers the node has a good history with.</item>
/// </list>
/// A report whose observed address is not publicly routable — or that merely echoes the reporter's own address —
/// is rejected outright. A poisoned result only costs reachability (the dial fails and self-corrects); it can
/// never cause impersonation, because every dial re-verifies the peer's Sigil over Noise.
/// </summary>
public sealed class ReflexiveObserver
{
    /// <summary>Maximum distinct reporters retained (bounds memory; oldest identity is evicted first).</summary>
    public const int MaxReporters = 128;

    /// <summary>Ceiling on a single reporter's standing weight, so no one peer can dominate the quorum weight.</summary>
    public const int MaxReporterWeight = 8;

    private readonly record struct Observation(IPEndPoint Observed, string ReporterSubnet, int Weight);

    private readonly Dictionary<Sigil, Observation> _byReporter = new();
    private readonly LinkedList<Sigil> _order = new(); // reporter insertion order, for bounded eviction

    /// <summary>Number of distinct reporters currently held.</summary>
    public int Count => _byReporter.Count;

    /// <summary>
    /// Records that <paramref name="reporter"/> (whose connection we observed arriving from
    /// <paramref name="reporterSource"/>) reported our externally-observed endpoint as <paramref name="observed"/>,
    /// carrying <paramref name="weight"/> — the reporter's standing-derived trust. One live report is kept per
    /// reporter; sanity-failing reports, and reports with non-positive weight (unknown-bad / tainted), are ignored.
    /// </summary>
    public void Observe(Sigil reporter, IPAddress reporterSource, IPEndPoint observed, int weight = 1)
    {
        ArgumentNullException.ThrowIfNull(reporterSource);
        ArgumentNullException.ThrowIfNull(observed);

        if (weight <= 0)
            return;                       // tainted / quarantined reporter — no vote at all

        var address = Canonical(observed.Address);
        var source = Canonical(reporterSource);
        if (!IsPubliclyRoutable(address))
            return;                       // an unroutable "public" address is useless and a poisoning vector
        if (address.Equals(source))
            return;                       // a peer reporting its own address as ours — reject

        var observation = new Observation(new IPEndPoint(address, observed.Port), SubnetKey(source), Math.Min(weight, MaxReporterWeight));
        if (!_byReporter.ContainsKey(reporter))
        {
            if (_byReporter.Count >= MaxReporters)
            {
                var oldest = _order.First!.Value;
                _order.RemoveFirst();
                _byReporter.Remove(oldest);
            }
            _order.AddLast(reporter);
        }
        _byReporter[reporter] = observation; // latest wins — still one vote for this identity
    }

    /// <summary>
    /// The externally-observed endpoint as a Mapped beacon, or null until a quorum agrees on the same address:port:
    /// at least <paramref name="minDistinctReporters"/> distinct reporter Sigils, spanning at least
    /// <paramref name="minDistinctSubnets"/> distinct reporter /24s, whose <em>combined standing weight</em> is at
    /// least <paramref name="minWeight"/>.
    /// </summary>
    public Beacon? MappedBeacon(int minDistinctReporters = 2, int minDistinctSubnets = 2, int minWeight = 2)
    {
        if (_byReporter.Count == 0)
            return null;

        var qualifying = _byReporter.Values
            .GroupBy(o => o.Observed)
            .Select(g => new
            {
                Endpoint = g.Key,
                Reporters = g.Count(), // already one observation per Sigil
                Subnets = g.Select(o => o.ReporterSubnet).Distinct().Count(),
                Weight = g.Sum(o => o.Weight),
            })
            .Where(g => g.Reporters >= minDistinctReporters && g.Subnets >= minDistinctSubnets && g.Weight >= minWeight)
            .OrderByDescending(g => g.Weight)
            .ThenByDescending(g => g.Reporters)
            .FirstOrDefault();

        return qualifying is null
            ? null
            : new Beacon(EndpointKind.Mapped, qualifying.Endpoint.Address.ToString(), qualifying.Endpoint.Port);
    }

    /// <summary>Normalises an IPv4-mapped-IPv6 address to IPv4 so v4 and v6 forms of one address compare equal.</summary>
    private static IPAddress Canonical(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>A grouping key for a reporter's netblock: the /24 for IPv4, the /48 for IPv6.</summary>
    private static string SubnetKey(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var take = bytes.Length == 4 ? 3 : 6; // IPv4 /24, IPv6 /48
        return Convert.ToHexString(bytes, 0, take);
    }

    /// <summary>True only for addresses that could plausibly be a real, dialable public endpoint.</summary>
    private static bool IsPubliclyRoutable(IPAddress address)
    {
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
        if ((v6[0] & 0xFE) == 0xFC) return false;                             // fc00::/7 unique-local
        return true;
    }
}
