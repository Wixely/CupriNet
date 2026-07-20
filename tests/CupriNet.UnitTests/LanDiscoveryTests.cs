using System.Net;
using CupriNet.Abstractions;
using CupriNet.Core;
using CupriNet.Traversal;
using Xunit;

namespace CupriNet.UnitTests;

public class LanDiscoveryTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void Presence_Codec_RoundTrips_AndVerifies()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var presence = LanPresenceSigner.Create(identity, Network, listenPort: 43820, suite, Now);

        var (decoded, _) = LanPresenceCodec.Decode(LanPresenceCodec.Encode(presence));
        Assert.Equal(identity.Sigil, decoded.Sigil);
        Assert.Equal(43820, decoded.ListenPort);
        Assert.Equal(Network, decoded.Network);

        Assert.True(LanPresenceSigner.Verify(decoded, suite));
        Assert.False(LanPresenceSigner.Verify(decoded with { ListenPort = 1 }, suite));
    }

    [Fact]
    public async Task Announce_IsDiscovered_WithDialableEndpoint()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();

        var announcerIdentity = NodeIdentity.Generate(suite);
        var listenerIdentity = NodeIdentity.Generate(suite);

        using var listener = new LanDiscovery(listenerIdentity, Network, suite, new IPEndPoint(IPAddress.Loopback, 0), []);
        var listenerEndpoint = listener.LocalEndPoint;

        using var announcer = new LanDiscovery(announcerIdentity, Network, suite, new IPEndPoint(IPAddress.Loopback, 0), [listenerEndpoint]);

        const int advertisedTcpPort = 43820;
        await announcer.AnnounceAsync(advertisedTcpPort, Now, ct);

        var discovered = await listener.ReceiveAsync(ct);
        Assert.Equal(announcerIdentity.Sigil, discovered.Sigil);
        Assert.Equal(IPAddress.Loopback, discovered.Endpoint.Address);
        Assert.Equal(advertisedTcpPort, discovered.Endpoint.Port); // dialable: source IP + advertised port
        Assert.Equal(EndpointKind.Host, discovered.ToBeacon().Kind);
    }

    [Fact]
    public async Task Discovery_IgnoresOwnAnnouncements_And_ForeignNetworks()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();

        var listenerIdentity = NodeIdentity.Generate(suite);
        var peerIdentity = NodeIdentity.Generate(suite);

        using var listener = new LanDiscovery(listenerIdentity, Network, suite, new IPEndPoint(IPAddress.Loopback, 0), []);
        var listenerEndpoint = listener.LocalEndPoint;

        // A self-announcer (same identity as the listener) and a foreign-network peer both send first...
        using var selfEcho = new LanDiscovery(listenerIdentity, Network, suite, new IPEndPoint(IPAddress.Loopback, 0), [listenerEndpoint]);
        using var foreign = new LanDiscovery(peerIdentity, new Concordium("other.net"), suite, new IPEndPoint(IPAddress.Loopback, 0), [listenerEndpoint]);
        using var genuine = new LanDiscovery(peerIdentity, Network, suite, new IPEndPoint(IPAddress.Loopback, 0), [listenerEndpoint]);

        await selfEcho.AnnounceAsync(1111, Now, ct);
        await foreign.AnnounceAsync(2222, Now, ct);
        await genuine.AnnounceAsync(3333, Now, ct);

        // ...only the genuine, same-network, non-self announcement is returned.
        var discovered = await listener.ReceiveAsync(ct);
        Assert.Equal(peerIdentity.Sigil, discovered.Sigil);
        Assert.Equal(3333, discovered.Endpoint.Port);
    }
}
