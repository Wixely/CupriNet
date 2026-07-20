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
    public async Task Mux_ConcurrentStreamWard_AdmitsUpToCap_DropsBeyond()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (va, vb, listener) = await PairAsync(ct);
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        await using var mux = new VesselMux(vb, ownsVessel: false, maxConcurrentStreams: 2);

        // The peer uses three distinct streams, in order.
        await va.SendAsync(10, new byte[] { 1 }, ct);
        await va.SendAsync(11, new byte[] { 2 }, ct);
        await va.SendAsync(12, new byte[] { 3 }, ct);

        // The first two streams are admitted and deliver.
        Assert.Equal(new byte[] { 1 }, await mux.Stream(10).ReceiveAsync(ct));
        Assert.Equal(new byte[] { 2 }, await mux.Stream(11).ReceiveAsync(ct));

        // The third stream is beyond the Ward and was dropped: a fresh reader blocks (times out).
        using var shortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shortCts.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await mux.Stream(12).ReceiveAsync(shortCts.Token));
    }
}
