using System.Net;
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
        var address = new IPAddress(r.ReadBytes());
        var port = (int)(uint)r.ReadVarUInt();
        return new IPEndPoint(address, port);
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
/// Aggregates reflexive observations from multiple peers. A node believes its public endpoint once enough
/// independent peers agree on the same address, and offers it as a Mapped beacon.
/// </summary>
public sealed class ReflexiveObserver
{
    private readonly List<IPEndPoint> _observations = [];

    public int Count => _observations.Count;

    public void Add(IPEndPoint observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        _observations.Add(observed);
    }

    /// <summary>The public endpoint the most peers agree on, or null until <paramref name="minimumAgree"/> concur.</summary>
    public IPEndPoint? Best(int minimumAgree = 2)
    {
        if (_observations.Count == 0)
            return null;

        var byAddress = _observations
            .GroupBy(e => e.Address)
            .OrderByDescending(g => g.Count())
            .First();

        if (byAddress.Count() < minimumAgree)
            return null;

        var port = byAddress
            .GroupBy(e => e.Port)
            .OrderByDescending(g => g.Count())
            .First().Key;

        return new IPEndPoint(byAddress.Key, port);
    }

    /// <summary>The agreed public endpoint as a Mapped beacon, or null if not yet confident.</summary>
    public Beacon? MappedBeacon(int minimumAgree = 2)
    {
        var best = Best(minimumAgree);
        return best is null ? null : new Beacon(EndpointKind.Mapped, best.Address.ToString(), best.Port);
    }
}
