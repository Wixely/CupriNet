using System.Net;
using CupriNet.Abstractions;
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
    public void Observer_AdvertisesMapped_OnQuorumOfDistinctPeersAcrossSubnets()
    {
        var observer = new ReflexiveObserver();
        Assert.Null(observer.MappedBeacon()); // nothing yet

        // One peer's report is never enough.
        observer.Observe(Sig(1), IPAddress.Parse("198.51.100.10"), new IPEndPoint(IPAddress.Parse("192.0.2.5"), 41000));
        Assert.Null(observer.MappedBeacon());

        // A second, distinct peer in a different /24 agreeing on the same endpoint reaches quorum.
        observer.Observe(Sig(2), IPAddress.Parse("203.0.113.20"), new IPEndPoint(IPAddress.Parse("192.0.2.5"), 41000));
        var beacon = observer.MappedBeacon();
        Assert.NotNull(beacon);
        Assert.Equal(EndpointKind.Mapped, beacon!.Kind);
        Assert.Equal("192.0.2.5", beacon.Host);
        Assert.Equal(41000, beacon.Port);
    }

    [Fact]
    public void Observer_IgnoresBallotStuffingBySingleIdentity()
    {
        var observer = new ReflexiveObserver();

        // The same Sigil reports the same fake endpoint many times over (e.g. by reconnecting).
        for (var i = 0; i < 10; i++)
            observer.Observe(Sig(7), IPAddress.Parse("198.51.100.10"), new IPEndPoint(IPAddress.Parse("192.0.2.99"), 6000));

        Assert.Equal(1, observer.Count);        // one identity = one live vote
        Assert.Null(observer.MappedBeacon());   // never reaches a 2-identity quorum
    }

    [Fact]
    public void Observer_RequiresSubnetDiversity()
    {
        var observer = new ReflexiveObserver();

        // Two DISTINCT Sigils, but both reporting from the same /24 (a Sybil cluster in one netblock).
        observer.Observe(Sig(1), IPAddress.Parse("198.51.100.10"), new IPEndPoint(IPAddress.Parse("192.0.2.5"), 41000));
        observer.Observe(Sig(2), IPAddress.Parse("198.51.100.11"), new IPEndPoint(IPAddress.Parse("192.0.2.5"), 41000));

        Assert.Null(observer.MappedBeacon());                                   // fails the 2-subnet quorum
        Assert.NotNull(observer.MappedBeacon(minDistinctReporters: 2, minDistinctSubnets: 1)); // relaxed: subnet check off
    }

    [Fact]
    public void Observer_RejectsUnroutableAndSelfReports()
    {
        var observer = new ReflexiveObserver();

        // Private / loopback observed addresses are not advertisable public endpoints.
        observer.Observe(Sig(1), IPAddress.Parse("198.51.100.10"), new IPEndPoint(IPAddress.Parse("10.0.0.5"), 41000));
        observer.Observe(Sig(2), IPAddress.Parse("203.0.113.20"), new IPEndPoint(IPAddress.Parse("127.0.0.1"), 41000));
        // A peer reporting its OWN address as ours.
        observer.Observe(Sig(3), IPAddress.Parse("203.0.113.21"), new IPEndPoint(IPAddress.Parse("203.0.113.21"), 41000));

        Assert.Equal(0, observer.Count);
        Assert.Null(observer.MappedBeacon());
    }

    /// <summary>A distinct 32-byte Sigil seeded from a single byte, for tests.</summary>
    private static Sigil Sig(byte seed)
    {
        var bytes = new byte[Sigil.Size];
        Array.Fill(bytes, seed);
        return new Sigil(bytes);
    }
}
