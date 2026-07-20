using System.Net;
using System.Net.Sockets;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Core;

namespace CupriNet.Traversal;

/// <summary>A peer discovered on the local network: its identity plus a directly-dialable endpoint.</summary>
public sealed record DiscoveredNode(Sigil Sigil, byte[] SealPublicKey, IPEndPoint Endpoint)
{
    /// <summary>The reachable endpoint as a Host beacon.</summary>
    public Beacon ToBeacon() => new(EndpointKind.Host, Endpoint.Address.ToString(), Endpoint.Port);
}

/// <summary>
/// UDP-based local-network discovery. A node announces a signed <see cref="LanPresence"/> to one or more
/// targets (a broadcast/multicast address in production, or specific peers in tests); receivers validate
/// the signature and network, ignore their own announcements, and learn a dialable endpoint from the
/// datagram's source address plus the advertised listen port. Discovery is Layer-1 only.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    private readonly NodeIdentity _identity;
    private readonly Concordium _network;
    private readonly ICryptoSuite _suite;
    private readonly IReadOnlyList<IPEndPoint> _announceTargets;
    private readonly UdpClient _udp;

    public LanDiscovery(NodeIdentity identity, Concordium network, ICryptoSuite suite, IPEndPoint bindEndpoint, IReadOnlyList<IPEndPoint> announceTargets)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _network = network;
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
        _announceTargets = announceTargets ?? throw new ArgumentNullException(nameof(announceTargets));
        ArgumentNullException.ThrowIfNull(bindEndpoint);

        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(bindEndpoint);
    }

    /// <summary>The bound local endpoint (reflects the OS-assigned port when 0 was requested).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>Announces this node's presence, advertising the TCP port it accepts Vessels on.</summary>
    public async Task AnnounceAsync(int listenPort, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var presence = LanPresenceSigner.Create(_identity, _network, listenPort, _suite, now);
        var datagram = LanPresenceCodec.Encode(presence);
        foreach (var target in _announceTargets)
            await _udp.SendAsync(datagram, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Receives the next valid presence from another node on this network.</summary>
    public async Task<DiscoveredNode> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            LanPresence presence;
            try
            {
                (presence, _) = LanPresenceCodec.Decode(result.Buffer);
            }
            catch (CodexFormatException)
            {
                continue; // malformed datagram
            }

            if (presence.Version != LanPresenceCodec.CurrentVersion)
                continue;
            if (presence.Network != _network)
                continue;
            if (presence.SealPublicKey.Length == 0 || !LanPresenceSigner.Verify(presence, _suite))
                continue;

            var sigil = presence.Sigil;
            if (sigil == _identity.Sigil)
                continue; // ignore our own announcements

            var endpoint = new IPEndPoint(result.RemoteEndPoint.Address, presence.ListenPort);
            return new DiscoveredNode(sigil, presence.SealPublicKey, endpoint);
        }
    }

    public void Dispose() => _udp.Dispose();
}
