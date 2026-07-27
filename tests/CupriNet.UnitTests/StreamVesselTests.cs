using System.Net;
using System.Net.Sockets;
using System.Text;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The public <see cref="StreamVessel.Over"/> factory — the seam a stream-based transport (Tor onion stream,
/// WebSocket, TLS) uses to hand CupriNet an <see cref="IVessel"/>. Proven over a real duplex stream pair.
/// </summary>
public class StreamVesselTests
{
    [Fact]
    public async Task Over_WrapsADuplexStream_AsAFramedVessel()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync(ct).AsTask();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        var server = await acceptTask;

        await using var a = StreamVessel.Over(client.GetStream(), onDispose: () => { client.Dispose(); return ValueTask.CompletedTask; });
        await using var b = StreamVessel.Over(server.GetStream(), onDispose: () => { server.Dispose(); return ValueTask.CompletedTask; });

        await a.SendAsync(9, "stream-vessel"u8.ToArray(), ct);
        var frame = await b.ReceiveAsync(ct);
        Assert.Equal((ushort)9, frame!.Value.StreamId);
        Assert.Equal("stream-vessel", Encoding.UTF8.GetString(frame.Value.Payload));

        listener.Stop();
    }
}
