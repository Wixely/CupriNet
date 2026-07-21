using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>
/// The "KCP route": prove that the reliable-UDP transport (ReliableArq → ArqStream → Vessel) carries the exact
/// same stack the TCP path does — framing, a real Noise handshake, and multiplexed messages — including over a
/// lossy link, and over a real loopback UDP socket. This is the non-Tor, no-native-dependency path to running a
/// channel over a (hole-punched) UDP path.
/// </summary>
public class KcpRouteTests
{
    private static readonly Concordium Network = new("kcp.test");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A paired in-memory datagram channel with optional independent loss in each direction.</summary>
    private sealed class InMemoryPacketLink : IPacketLink
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly double _loss;
        private readonly Random _rng;
        private readonly object _rngLock = new();
        private Channel<byte[]> _peerInbound = null!;

        private InMemoryPacketLink(double loss, int seed) { _loss = loss; _rng = new Random(seed); }

        public static (InMemoryPacketLink A, InMemoryPacketLink B) Pair(double loss = 0.0, int seed = 1)
        {
            var a = new InMemoryPacketLink(loss, seed);
            var b = new InMemoryPacketLink(loss, seed + 1000);
            a._peerInbound = b._inbound;
            b._peerInbound = a._inbound;
            return (a, b);
        }

        public EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 2);

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
        {
            bool drop;
            lock (_rngLock) drop = _rng.NextDouble() < _loss;
            if (!drop)
                _peerInbound.Writer.TryWrite(datagram.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try { return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ArqStream_TransfersBytesReliably_OverALossyLink()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (linkA, linkB) = InMemoryPacketLink.Pair(loss: 0.2, seed: 5);
        await using var a = new ArqStream(linkA);
        await using var b = new ArqStream(linkB);

        var payload = new byte[48 * 1024];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 17 + 3) & 0xFF);

        await a.WriteAsync(payload, ct);

        var received = new byte[payload.Length];
        var read = 0;
        while (read < received.Length)
            read += await b.ReadAsync(received.AsMemory(read), ct);

        Assert.Equal(payload, received);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)] // handshake + traffic must survive a quarter of datagrams being dropped
    public async Task NoiseHandshake_AndMessages_RunOverReliableUdp(double loss)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);

        var (linkA, linkB) = InMemoryPacketLink.Pair(loss, seed: 11);
        var client = UdpVessel.Over(linkA);
        var server = UdpVessel.Over(linkB);

        // Exactly the TCP-path handshake — just over ARQ/UDP vessels instead.
        var initiate = NoiseConjunction.InitiateAsync(client, joiner, Network, suite, expectedPeer: host.Sigil, cancellationToken: ct);
        var accept = NoiseConjunction.AcceptAsync(server, host, Network, suite, ct);
        var joinerResult = await initiate;
        var hostResult = await accept;

        Assert.Equal(host.Sigil, joinerResult.PeerSigil);
        Assert.Equal(joiner.Sigil, hostResult.PeerSigil);

        // Multiplexed, encrypted traffic both ways over the reliable-UDP session.
        await joinerResult.Vessel.SendAsync(5, "ping-over-udp"u8.ToArray(), ct);
        var f1 = await hostResult.Vessel.ReceiveAsync(ct);
        Assert.Equal("ping-over-udp", Encoding.UTF8.GetString(f1!.Value.Payload));

        await hostResult.Vessel.SendAsync(9, "pong-over-udp"u8.ToArray(), ct);
        var f2 = await joinerResult.Vessel.ReceiveAsync(ct);
        Assert.Equal("pong-over-udp", Encoding.UTF8.GetString(f2!.Value.Payload));

        await joinerResult.Vessel.DisposeAsync();
        await hostResult.Vessel.DisposeAsync();
    }

    [Fact]
    public async Task Vessel_RoundTrips_OverRealLoopbackUdp()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var epA = (IPEndPoint)sockA.LocalEndPoint!;
        var epB = (IPEndPoint)sockB.LocalEndPoint!;
        sockA.Connect(epB);
        sockB.Connect(epA);

        await using var a = UdpVessel.Over(new UdpPacketLink(sockA, epB));
        await using var b = UdpVessel.Over(new UdpPacketLink(sockB, epA));

        await a.SendAsync(3, "hello-real-udp"u8.ToArray(), ct);
        var frame = await b.ReceiveAsync(ct);
        Assert.Equal((ushort)3, frame!.Value.StreamId);
        Assert.Equal("hello-real-udp", Encoding.UTF8.GetString(frame.Value.Payload));
    }
}
