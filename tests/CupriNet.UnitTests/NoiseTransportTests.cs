using System.Net;
using System.Text;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Noise;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class NoiseTransportTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static async Task<(VesselSession Client, VesselSession Server, VesselListener Listener)> ConnectedPairAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var client = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var server = await acceptTask;
        return (client, server, listener);
    }

    [Fact]
    public void NoiseVessel_EncryptsFramePayloads()
    {
        var suite = CryptoSuites.Secure();
        var (initiatorTransport, responderTransport) = InProcessHandshake(suite);

        var capture = new CapturingVessel();
        var noiseVessel = new NoiseVessel(capture, initiatorTransport);

        noiseVessel.SendAsync(7, "hello"u8.ToArray()).AsTask().GetAwaiter().GetResult();

        var (streamId, wire) = capture.Sent.Single();
        Assert.Equal((ushort)7, streamId);                          // stream id preserved
        Assert.False(wire.AsSpan().StartsWith("hello"u8));          // payload is ciphertext
        Assert.True(wire.Length > 5);                                // + AEAD tag
        Assert.Equal("hello", Encoding.UTF8.GetString(responderTransport.Decrypt(wire)));
    }

    [Fact]
    public async Task NoiseConjunction_MutuallyAuthenticates_AndEncryptsTransport()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();

        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);

        var (client, server, listener) = await ConnectedPairAsync(ct);
        await using var _c = client;
        await using var _s = server;
        await using var _l = listener;

        var initiate = NoiseConjunction.InitiateAsync(client, joiner, Network, suite, expectedPeer: host.Sigil, cancellationToken: ct);
        var accept = NoiseConjunction.AcceptAsync(server, host, Network, suite, ct);

        var joinerResult = await initiate;
        var hostResult = await accept;

        Assert.Equal(host.Sigil, joinerResult.PeerSigil);
        Assert.Equal(joiner.Sigil, hostResult.PeerSigil);

        // The returned vessels transparently encrypt traffic.
        await joinerResult.Vessel.SendAsync(5, "ping"u8.ToArray(), ct);
        var frame = await hostResult.Vessel.ReceiveAsync(ct);
        Assert.NotNull(frame);
        Assert.Equal((ushort)5, frame.Value.StreamId);
        Assert.Equal("ping", Encoding.UTF8.GetString(frame.Value.Payload));
    }

    [Fact]
    public async Task NoiseConjunction_WrongExpectedPeer_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();

        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);
        var imposter = NodeIdentity.Generate(suite).Sigil;

        var (client, server, listener) = await ConnectedPairAsync(ct);
        await using var _c = client;
        await using var _s = server;
        await using var _l = listener;

        var initiate = NoiseConjunction.InitiateAsync(client, joiner, Network, suite, expectedPeer: imposter, cancellationToken: ct);
        var accept = NoiseConjunction.AcceptAsync(server, host, Network, suite, ct);

        await Assert.ThrowsAsync<NoiseConjunctionException>(async () => await initiate);
        try { await accept; } catch { /* peer completes; not under test */ }
    }

    [Fact]
    public async Task NoiseConjunction_NetworkMismatch_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();

        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);

        var (client, server, listener) = await ConnectedPairAsync(ct);
        await using var _c = client;
        await using var _s = server;
        await using var _l = listener;

        var initiate = NoiseConjunction.InitiateAsync(client, joiner, new Concordium("net.a"), suite, cancellationToken: ct);
        var accept = NoiseConjunction.AcceptAsync(server, host, new Concordium("net.b"), suite, ct);

        await Assert.ThrowsAsync<NoiseConjunctionException>(async () => await initiate);
        try { await accept; } catch { /* both sides reject the mismatch */ }
    }

    private static (NoiseTransport Initiator, NoiseTransport Responder) InProcessHandshake(ICryptoSuite suite)
    {
        var initiatorStatic = suite.Agreement.Generate();
        var responderStatic = suite.Agreement.Generate();
        var initiator = new NoiseHandshakeState(suite, true, (initiatorStatic.PrivateKey, initiatorStatic.PublicKey));
        var responder = new NoiseHandshakeState(suite, false, (responderStatic.PrivateKey, responderStatic.PublicKey));

        responder.ReadMessage(initiator.WriteMessage());
        initiator.ReadMessage(responder.WriteMessage());
        responder.ReadMessage(initiator.WriteMessage());

        return (initiator.Split(), responder.Split());
    }

    private sealed class CapturingVessel : IVessel
    {
        public List<(ushort StreamId, byte[] Payload)> Sent { get; } = [];

        public EndPoint? RemoteEndPoint => null;

        public ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((streamId, payload.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask<VesselFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
