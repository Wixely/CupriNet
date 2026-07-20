using System.Net;
using CupriNet.Core;
using CupriNet.Traversal;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class ReflexiveEndpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Exchange_LearnsOwnObservedEndpoint()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        await using VesselSession client = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        await using VesselSession server = await acceptTask;
        await using var _l = listener;

        var initiate = ReflexiveExchange.ExchangeAsync(client, initiator: true, cancellationToken: ct);
        var accept = ReflexiveExchange.ExchangeAsync(server, initiator: false, cancellationToken: ct);

        var clientReflexive = await initiate;
        var serverReflexive = await accept;

        // Each side learns exactly the local socket endpoint the peer observed (address normalised to IPv4).
        Assert.Equal(Normalize(client.LocalEndPoint!), clientReflexive);
        Assert.Equal(Normalize(server.LocalEndPoint!), serverReflexive);
    }

    private static IPEndPoint Normalize(EndPoint endPoint)
    {
        var ip = (IPEndPoint)endPoint;
        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        return new IPEndPoint(address, ip.Port);
    }

    [Fact]
    public void Encode_Decode_RoundTrips_IPv4_And_IPv6()
    {
        var v4 = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51000);
        Assert.Equal(v4, ReflexiveExchange.Decode(ReflexiveExchange.Encode(v4)));

        var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 443);
        Assert.Equal(v6, ReflexiveExchange.Decode(ReflexiveExchange.Encode(v6)));
    }

    [Fact]
    public void Observer_ReportsPublicEndpoint_OnceQuorumAgrees()
    {
        var observer = new ReflexiveObserver();
        Assert.Null(observer.Best(minimumAgree: 2)); // nothing yet

        observer.Add(new IPEndPoint(IPAddress.Parse("198.51.100.9"), 40000));
        Assert.Null(observer.Best(minimumAgree: 2)); // one observation is not enough

        observer.Add(new IPEndPoint(IPAddress.Parse("198.51.100.9"), 40000));
        observer.Add(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 40000)); // a minority/odd observation

        var best = observer.Best(minimumAgree: 2);
        Assert.NotNull(best);
        Assert.Equal(IPAddress.Parse("198.51.100.9"), best.Address); // the agreed public address wins

        var beacon = observer.MappedBeacon(minimumAgree: 2);
        Assert.NotNull(beacon);
        Assert.Equal(EndpointKind.Mapped, beacon.Kind);
        Assert.Equal("198.51.100.9", beacon.Host);
    }
}
