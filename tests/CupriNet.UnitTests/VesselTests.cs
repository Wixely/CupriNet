using System.Net;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

public class VesselTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Loopback_FramedExchange_BothDirections()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var port = listener.LocalEndPoint.Port;

        var acceptTask = listener.AcceptAsync(ct);
        await using var client = await TcpVessel.ConnectAsync("127.0.0.1", port, cancellationToken: ct);
        await using var server = await acceptTask;

        await client.SendAsync(1, new byte[] { 1, 2, 3 }, ct);
        var received = await server.ReceiveAsync(ct);
        Assert.NotNull(received);
        Assert.Equal((ushort)1, received.Value.StreamId);
        Assert.Equal(new byte[] { 1, 2, 3 }, received.Value.Payload);

        await server.SendAsync(7, new byte[] { 9, 9 }, ct);
        var back = await client.ReceiveAsync(ct);
        Assert.NotNull(back);
        Assert.Equal((ushort)7, back.Value.StreamId);
        Assert.Equal(new byte[] { 9, 9 }, back.Value.Payload);
    }

    [Fact]
    public async Task Multiplexing_StreamIdsPreserved_InOrder()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        await using var client = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        await using var server = await acceptTask;

        // Interleave frames across three logical streams over one Vessel.
        await client.SendAsync(10, new byte[] { 1 }, ct);
        await client.SendAsync(20, new byte[] { 2 }, ct);
        await client.SendAsync(10, new byte[] { 3 }, ct);

        var f1 = await server.ReceiveAsync(ct);
        var f2 = await server.ReceiveAsync(ct);
        var f3 = await server.ReceiveAsync(ct);

        Assert.Equal((ushort)10, f1!.Value.StreamId);
        Assert.Equal((ushort)20, f2!.Value.StreamId);
        Assert.Equal((ushort)10, f3!.Value.StreamId);
        Assert.Equal(new byte[] { 1 }, f1.Value.Payload);
        Assert.Equal(new byte[] { 2 }, f2.Value.Payload);
        Assert.Equal(new byte[] { 3 }, f3.Value.Payload);
    }

    [Fact]
    public async Task PeerClose_ReceiveReturnsNull()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var client = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        await using var server = await acceptTask;

        await client.DisposeAsync(); // close from the client side
        var received = await server.ReceiveAsync(ct);
        Assert.Null(received);
    }
}
