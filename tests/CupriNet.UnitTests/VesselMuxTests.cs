using System.Net;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class VesselMuxTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<(VesselSession A, VesselSession B, VesselListener Listener)> ConnectedPairAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var a = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var b = await acceptTask;
        return (a, b, listener);
    }

    [Fact]
    public async Task Mux_RoutesConcurrentStreams_Correctly()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var (va, vb, listener) = await ConnectedPairAsync(ct);
        await using var _l = listener;
        await using var muxA = new VesselMux(va, ownsVessel: true);
        await using var muxB = new VesselMux(vb, ownsVessel: true);

        var a3 = muxA.Stream(3);
        var a4 = muxA.Stream(4);
        var b3 = muxB.Stream(3);
        var b4 = muxB.Stream(4);

        // Two concurrent receivers on B, one per stream.
        var recv3 = b3.ReceiveAsync(ct);
        var recv4 = b4.ReceiveAsync(ct);

        // A sends on stream 4 first, then stream 3 — the mux must route each to its own reader.
        await a4.SendAsync(new byte[] { 44 }, ct);
        await a3.SendAsync(new byte[] { 33 }, ct);

        Assert.Equal(new byte[] { 33 }, await recv3);
        Assert.Equal(new byte[] { 44 }, await recv4);
    }

    [Fact]
    public async Task Mux_ReturnsNull_WhenConnectionCloses()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var (va, vb, listener) = await ConnectedPairAsync(ct);
        await using var _l = listener;
        var muxB = new VesselMux(vb, ownsVessel: true);

        var recv = muxB.Stream(3).ReceiveAsync(ct);
        await va.DisposeAsync(); // close from the other end

        Assert.Null(await recv);
        await muxB.DisposeAsync();
    }
}
