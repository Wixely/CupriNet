using System.Net;
using System.Net.Sockets;
using System.Text;
using CupriNet.Hosting;
using CupriNet.Traversal;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// LAN genesis: two nodes on the same network pair with no link and no NAT. Covers pairing with a discovered peer
/// through the transport-agnostic seam, and the full announce→discover→pair path over loopback.
/// </summary>
public class CupriNodeLanTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task ConjoinDiscovered_PairsWithoutAnIntonation()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var a = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "lan.test", EnableOverlayGossip = false }, ct);
        await using var b = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "lan.test", EnableOverlayGossip = false }, ct);

        var acceptB = b.AcceptAsync(ct); // B's ordinary TCP listener handles the responder side

        // A "discovers" B (here constructed directly; end-to-end discovery is exercised below).
        var discoveredB = new DiscoveredNode(b.Identity.Sigil, b.Identity.PublicKey.ToArray(), new IPEndPoint(IPAddress.Loopback, b.LocalEndPoint.Port));
        var pairA = await a.ConjoinDiscoveredAsync(discoveredB, now, ct);
        var pairB = await acceptB;

        Assert.Equal(b.Identity.Sigil, pairA.PeerSigil);
        Assert.Equal(a.Identity.Sigil, pairB.PeerSigil);

        await pairA.Vessel.SendAsync(3, "lan-hello"u8.ToArray(), ct);
        var frame = await pairB.Vessel.ReceiveAsync(ct);
        Assert.Equal("lan-hello", Encoding.UTF8.GetString(frame!.Value.Payload));
    }

    [Fact]
    public async Task Announce_Discover_ThenPair_OverLoopback()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        // Distinct discovery ports on loopback, each announcing at the other (deterministic stand-in for broadcast).
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        CupriNodeOptions Opts(int myPort, int peerPort) => new()
        {
            Concordium = "lan.test",
            EnableOverlayGossip = false,
            EnableLanDiscovery = true,
            LanDiscoveryPort = myPort,
            LanAnnounceIntervalSeconds = 1,
            LanAnnounceTargets = [new IPEndPoint(IPAddress.Loopback, peerPort)],
        };

        await using var a = await CupriNode.CreateAsync(Opts(portA, portB), ct);
        await using var b = await CupriNode.CreateAsync(Opts(portB, portA), ct);
        var acceptB = b.AcceptAsync(ct);

        // Wait for A to hear B's announcement.
        DiscoveredNode? discoveredB = null;
        while (discoveredB is null)
        {
            ct.ThrowIfCancellationRequested();
            discoveredB = a.DiscoveredPeers.FirstOrDefault(d => d.Sigil == b.Identity.Sigil);
            if (discoveredB is null) await Task.Delay(100, ct);
        }

        Assert.Equal(b.Identity.Sigil, discoveredB.Sigil);
        Assert.Equal(b.LocalEndPoint.Port, discoveredB.Endpoint.Port); // advertised TCP port, dialable

        // Pair with the discovered peer — no link was ever exchanged.
        var pairA = await a.ConjoinDiscoveredAsync(discoveredB, now, ct);
        var pairB = await acceptB;
        Assert.Equal(b.Identity.Sigil, pairA.PeerSigil);
        Assert.Equal(a.Identity.Sigil, pairB.PeerSigil);
    }
}
