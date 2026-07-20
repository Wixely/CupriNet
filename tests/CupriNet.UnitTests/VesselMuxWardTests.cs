using System.Net;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>The VesselMux Wards: a hostile peer cannot force an unbounded number of per-stream queues.</summary>
public class VesselMuxWardTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<(VesselSession A, VesselSession B, VesselListener Listener)> PairAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var a = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var b = await acceptTask;
        return (a, b, listener);
    }

    [Fact]
    public async Task Mux_DeliversOnDistinctStreams_UnderCap()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (va, vb, listener) = await PairAsync(ct);
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        await using var mux = new VesselMux(vb, ownsVessel: false, maxConcurrentStreams: 4);
        var s10 = mux.Stream(10);
        var s11 = mux.Stream(11);

        await va.SendAsync(10, new byte[] { 1 }, ct);
        await va.SendAsync(11, new byte[] { 2 }, ct);

        Assert.Equal(new byte[] { 1 }, await s10.ReceiveAsync(ct));
        Assert.Equal(new byte[] { 2 }, await s11.ReceiveAsync(ct));
    }

    [Fact]
    public async Task Mux_ConcurrentStreamWard_DropsInboundBeyondCap()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (va, vb, listener) = await PairAsync(ct);
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        // Cap of 1: the first inbound stream is admitted, any further distinct stream is dropped.
        await using var mux = new VesselMux(vb, ownsVessel: false, maxConcurrentStreams: 1);
        var admitted = mux.Stream(20);

        await va.SendAsync(20, new byte[] { 1 }, ct);
        Assert.Equal(new byte[] { 1 }, await admitted.ReceiveAsync(ct)); // admitted (and forces the pump past it)

        await va.SendAsync(21, new byte[] { 2 }, ct);
        using var shortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shortCts.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await mux.Stream(21).ReceiveAsync(shortCts.Token));
    }
}
