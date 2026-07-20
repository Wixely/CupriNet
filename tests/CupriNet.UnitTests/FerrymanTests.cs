using System.Net;
using System.Text;
using CupriNet.Traversal;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class FerrymanTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void Layer1_Streams_AreRelayable_Layer2_AreNot()
    {
        Assert.True(Ferryman.IsRelayable(0));  // handshake
        Assert.True(Ferryman.IsRelayable(1));  // peer exchange
        Assert.True(Ferryman.IsRelayable(5));  // reflexion
        Assert.False(Ferryman.IsRelayable(2)); // Consecration
        Assert.False(Ferryman.IsRelayable(3)); // Epistles
        Assert.False(Ferryman.IsRelayable(4)); // Conduits
    }

    [Fact]
    public async Task Relays_Layer1_BothWays_ButRefuses_Layer2()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        // Two client legs into the Ferryman: A <-> relay <-> B.
        await using var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var port = listener.LocalEndPoint.Port;

        var acceptA = listener.AcceptAsync(ct);
        await using VesselSession a = await TcpVessel.ConnectAsync("127.0.0.1", port, cancellationToken: ct);
        await using VesselSession relayA = await acceptA;

        var acceptB = listener.AcceptAsync(ct);
        await using VesselSession b = await TcpVessel.ConnectAsync("127.0.0.1", port, cancellationToken: ct);
        await using VesselSession relayB = await acceptB;

        var counters = new RelayCounters();
        var bridge = Ferryman.BridgeAsync(relayA, relayB, counters, ct);

        // A -> B on a Layer-1 stream (peer exchange) is forwarded.
        await a.SendAsync(1, "peer-data"u8.ToArray(), ct);
        var got1 = await b.ReceiveAsync(ct);
        Assert.Equal((ushort)1, got1!.Value.StreamId);
        Assert.Equal("peer-data", Encoding.UTF8.GetString(got1.Value.Payload));

        // B -> A on the reflexion stream is forwarded (reverse direction).
        await b.SendAsync(5, "reflex"u8.ToArray(), ct);
        var got5 = await a.ReceiveAsync(ct);
        Assert.Equal((ushort)5, got5!.Value.StreamId);

        // A -> B on a Layer-2 stream (Epistles) is dropped; a following L1 frame still arrives.
        await a.SendAsync(3, "channel-content"u8.ToArray(), ct);
        await a.SendAsync(1, "after"u8.ToArray(), ct);
        var next = await b.ReceiveAsync(ct);
        Assert.Equal((ushort)1, next!.Value.StreamId);           // the L2 frame was skipped
        Assert.Equal("after", Encoding.UTF8.GetString(next.Value.Payload));
        Assert.True(counters.DroppedL2 >= 1);
        Assert.True(counters.ForwardedL1 >= 3);

        // Closing one leg tears the bridge down.
        await a.DisposeAsync();
        await bridge;
    }
}
