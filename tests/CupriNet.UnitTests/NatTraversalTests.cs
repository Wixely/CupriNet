using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Traversal;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>
/// NAT traversal end to end: a hole punch confirms a path and the reliable-UDP session is carried over the very
/// socket the punch opened — proven by running a real Noise handshake and messages over it. Also the many-peer
/// UDP listener accepting an inbound session. (On loopback there is no NAT, so this exercises the mechanics and
/// the punch→data-path handoff; real NAT traversal is inherently untestable in CI.)
/// </summary>
public class NatTraversalTests
{
    private static readonly Concordium Network = new("nat.test");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static IPEndPoint Loopback0 => new(IPAddress.Loopback, 0);

    private static async Task ProveNoiseSessionAsync(VesselSession a, VesselSession b, CancellationToken ct)
    {
        var suite = CryptoSuites.Secure();
        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);

        var initiate = NoiseConjunction.InitiateAsync(a, joiner, Network, suite, expectedPeer: host.Sigil, cancellationToken: ct);
        var accept = NoiseConjunction.AcceptAsync(b, host, Network, suite, ct);
        var joinerResult = await initiate;
        var hostResult = await accept;

        Assert.Equal(host.Sigil, joinerResult.PeerSigil);
        Assert.Equal(joiner.Sigil, hostResult.PeerSigil);

        await joinerResult.Vessel.SendAsync(5, "channel-over-nat"u8.ToArray(), ct);
        var frame = await hostResult.Vessel.ReceiveAsync(ct);
        Assert.Equal("channel-over-nat", Encoding.UTF8.GetString(frame!.Value.Payload));
    }

    [Fact]
    public async Task HolePunch_ThenChannel_OverTheSameSocket()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var sessionId = RandomNumberGenerator.GetBytes(16);
        var sockA = NatTraversal.BindSocket(Loopback0);
        var sockB = NatTraversal.BindSocket(Loopback0);
        var epA = (IPEndPoint)sockA.LocalEndPoint!;
        var epB = (IPEndPoint)sockB.LocalEndPoint!;

        var interval = TimeSpan.FromMilliseconds(50);
        var punchTimeout = TimeSpan.FromSeconds(10);

        // Both sides punch simultaneously toward each other's candidate, then carry a Vessel over the warm socket.
        var taskA = NatTraversal.PunchAndConnectAsync(sessionId, sockA, [epB], interval, punchTimeout, ct);
        var taskB = NatTraversal.PunchAndConnectAsync(sessionId, sockB, [epA], interval, punchTimeout, ct);

        await using var vesselA = await taskA;
        await using var vesselB = await taskB;

        await ProveNoiseSessionAsync(vesselA, vesselB, ct);
    }

    [Fact]
    public async Task UdpVesselListener_AcceptsInboundSession_AndRunsNoise()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var listener = new UdpVesselListener(Loopback0);
        var listenEp = listener.LocalEndPoint;

        var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        clientSocket.Bind(Loopback0);
        clientSocket.Connect(listenEp);
        await using var clientVessel = UdpVessel.Over(new UdpPacketLink(clientSocket, listenEp));

        var acceptTask = listener.AcceptAsync(ct);

        var suite = CryptoSuites.Secure();
        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);

        // The initiator's first datagram opens the demuxed server-side session.
        var initiate = NoiseConjunction.InitiateAsync(clientVessel, joiner, Network, suite, expectedPeer: host.Sigil, cancellationToken: ct);
        await using var serverVessel = await acceptTask;
        var accept = NoiseConjunction.AcceptAsync(serverVessel, host, Network, suite, ct);

        var joinerResult = await initiate;
        var hostResult = await accept;
        Assert.Equal(host.Sigil, joinerResult.PeerSigil);
        Assert.Equal(joiner.Sigil, hostResult.PeerSigil);

        await hostResult.Vessel.SendAsync(7, "inbound-udp"u8.ToArray(), ct);
        var frame = await joinerResult.Vessel.ReceiveAsync(ct);
        Assert.Equal("inbound-udp", Encoding.UTF8.GetString(frame!.Value.Payload));
    }
}
