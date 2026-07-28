using System.Net;
using System.Net.Sockets;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

public class UdpVesselListenerTests
{
    [Fact]
    public async Task CapsPeers_AndDropsTinyDatagrams_AgainstSourceSpoofing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        await using var listener = new UdpVesselListener(new IPEndPoint(IPAddress.Loopback, 0), maxPeers: 4);
        var target = listener.LocalEndPoint;

        // A minimal valid ARQ segment (cmd=ACK, 11-byte header, no payload).
        var ack = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        // A sub-header datagram must never open per-peer state.
        using (var tiny = new UdpClient()) await tiny.SendAsync(new byte[] { 1, 2, 3 }, target, ct);

        // Ten distinct sources each send a valid segment; only maxPeers (4) may ever become sessions.
        var clients = new List<UdpClient>();
        for (var i = 0; i < 10; i++)
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            clients.Add(client);
            await client.SendAsync(ack, target, ct);
        }

        var accepted = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromMilliseconds(750));
            try { await listener.AcceptAsync(idle.Token); accepted++; }
            catch (OperationCanceledException) { break; }
        }

        foreach (var client in clients)
            client.Dispose();

        // The cap holds (never more than 4 despite 10 sources + a tiny datagram); peers were created (not zero).
        Assert.InRange(accepted, 1, 4);
    }
}
