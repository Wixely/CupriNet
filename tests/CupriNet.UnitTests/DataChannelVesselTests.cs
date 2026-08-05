using System.Net;
using System.Text;
using System.Threading.Channels;
using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Proves the WebRTC-transport claim without a WebRTC stack: a <see cref="DataChannelVessel"/> over an in-memory
/// message duplex behaves as a full <see cref="IVessel"/> — multiplexing frames and carrying a real Noise
/// Conjunction end to end. A SIPSorcery DataChannel just has to implement <see cref="IDataChannel"/>.
/// </summary>
public class DataChannelVesselTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Multiplexes_StreamsAndPayloads_InOrder()
    {
        var (a, b) = LoopbackDataChannel.CreatePair();
        await using var va = new DataChannelVessel(a);
        await using var vb = new DataChannelVessel(b);

        await va.SendAsync(2, "hello"u8.ToArray());
        await va.SendAsync(7, "world"u8.ToArray());

        var f1 = await vb.ReceiveAsync();
        var f2 = await vb.ReceiveAsync();

        Assert.Equal((ushort)2, f1!.Value.StreamId);
        Assert.Equal("hello", Encoding.UTF8.GetString(f1.Value.Payload));
        Assert.Equal((ushort)7, f2!.Value.StreamId);
        Assert.Equal("world", Encoding.UTF8.GetString(f2.Value.Payload));
    }

    [Fact]
    public async Task Receive_ReturnsNull_WhenPeerCloses()
    {
        var (a, b) = LoopbackDataChannel.CreatePair();
        await using var vb = new DataChannelVessel(b);

        await new DataChannelVessel(a).DisposeAsync(); // close the far end

        Assert.Null(await vb.ReceiveAsync());
    }

    [Fact]
    public async Task NoiseConjunction_CompletesOverDataChannel()
    {
        // The money test: a DataChannel pair carries a full mutually-authenticated Noise handshake, so everything
        // above the transport (Toll, Consecration, Rites) would work over WebRTC unchanged.
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();

        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);

        var (a, b) = LoopbackDataChannel.CreatePair();
        var clientVessel = new DataChannelVessel(a);
        var serverVessel = new DataChannelVessel(b);

        var initiate = NoiseConjunction.InitiateAsync(clientVessel, joiner, Network, suite, expectedPeer: host.Sigil, cancellationToken: ct);
        var accept = NoiseConjunction.AcceptAsync(serverVessel, host, Network, suite, ct);

        var joinerResult = await initiate;
        var hostResult = await accept;

        Assert.Equal(host.Sigil, joinerResult.PeerSigil);   // mutual authentication over the DataChannel
        Assert.Equal(joiner.Sigil, hostResult.PeerSigil);

        // The returned (Noise-encrypting) vessels carry app frames transparently.
        await joinerResult.Vessel.SendAsync(5, "ping"u8.ToArray(), ct);
        var frame = await hostResult.Vessel.ReceiveAsync(ct);
        Assert.Equal((ushort)5, frame!.Value.StreamId);
        Assert.Equal("ping", Encoding.UTF8.GetString(frame.Value.Payload));

        await joinerResult.Vessel.DisposeAsync();
        await hostResult.Vessel.DisposeAsync();
    }

    /// <summary>An in-memory, reliable, ordered message duplex — the test stand-in for a WebRTC DataChannel.</summary>
    private sealed class LoopbackDataChannel : IDataChannel
    {
        private readonly Channel<byte[]> _inbound;
        private readonly Channel<byte[]> _outbound;

        private LoopbackDataChannel(Channel<byte[]> inbound, Channel<byte[]> outbound)
        {
            _inbound = inbound;
            _outbound = outbound;
        }

        public static (LoopbackDataChannel A, LoopbackDataChannel B) CreatePair()
        {
            var toA = Channel.CreateUnbounded<byte[]>();
            var toB = Channel.CreateUnbounded<byte[]>();
            return (new LoopbackDataChannel(toA, toB), new LoopbackDataChannel(toB, toA));
        }

        public EndPoint? RemoteEndPoint => null;
        public EndPoint? LocalEndPoint => null;

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            _outbound.Writer.TryWrite(message.ToArray()); // copy — the caller may reuse the buffer
            return ValueTask.CompletedTask;
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null; // peer closed
            }
        }

        public ValueTask DisposeAsync()
        {
            _outbound.Writer.TryComplete(); // completes the peer's inbound → its ReceiveAsync returns null
            return ValueTask.CompletedTask;
        }
    }
}
