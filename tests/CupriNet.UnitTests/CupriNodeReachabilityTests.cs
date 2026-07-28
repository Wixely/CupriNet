using System.Net;
using System.Net.Sockets;
using CupriNet.Abstractions;
using CupriNet.Core;
using CupriNet.Hosting;
using Xunit;

namespace CupriNet.UnitTests;

public class CupriNodeReachabilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Pairing_OverLoopback_LearnsNoMappedBeacon_AndDoesNotBreakPairing()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);

        var uri = host.IntoneUri(TimeSpan.FromHours(2), Now);
        Assert.True(CupriNet.Core.IntonationUri.TryParse(uri, out var intonation, out _));

        var acceptTask = host.AcceptAsync(ct);
        await using var pairedFromJoiner = await joiner.ConjoinAsync(intonation, Now, ct);
        await using var pairedFromHost = await acceptTask;

        // The reflexive exchange runs during pairing but must not break it. Over loopback the observed address
        // is not a routable public endpoint, so the sanity filter drops it — no bogus Mapped beacon is learned.
        // The host, as the responder, records nothing regardless (anti-Sybil Layer 2: only a peer we dialled counts).
        Assert.Equal(0, host.ReflexiveObserver.Count);
        Assert.DoesNotContain(joiner.Intone(TimeSpan.FromHours(2), Now).Beacons, b => b.Kind == EndpointKind.Mapped);
        Assert.DoesNotContain(host.Intone(TimeSpan.FromHours(2), Now).Beacons, b => b.Kind == EndpointKind.Mapped);
    }

    [Fact]
    public async Task Intone_AdvertisesMappedBeacon_OnceQuorumAgrees()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var node = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);

        // Before any observations, only the local Host beacon is advertised.
        Assert.DoesNotContain(node.Intone(TimeSpan.FromHours(2), Now).Beacons, b => b.Kind == EndpointKind.Mapped);

        // Two distinct peers in different /24s observe the same public address -> the node advertises it.
        var observed = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51000);
        node.ReflexiveObserver.Observe(Sig(1), IPAddress.Parse("198.51.100.10"), observed);
        node.ReflexiveObserver.Observe(Sig(2), IPAddress.Parse("192.0.2.30"), observed);

        var intonation = node.Intone(TimeSpan.FromHours(2), Now);
        Assert.Contains(intonation.Beacons, b => b.Kind == EndpointKind.Mapped && b.Host == "203.0.113.7" && b.Port == 51000);
        Assert.Contains(intonation.Beacons, b => b.Kind == EndpointKind.Host); // Host beacon still present
    }

    [Fact]
    public async Task Intone_And_SelfRecord_StripPrivateAddresses_ByDefault()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var lan = new Beacon(EndpointKind.Host, "192.168.0.15", 43820);       // private — a topology leak
        var wan = new Beacon(EndpointKind.Manual, "203.0.113.5", 51000);      // public — safe to advertise

        // Default: private/LAN addresses are stripped from the link (they'd otherwise reach whoever gets it).
        await using var priv = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat", EnableOverlayGossip = false, AdvertisedBeacons = [lan, wan],
        }, ct);
        var link = priv.Intone(TimeSpan.FromHours(1), Now).Beacons;
        Assert.DoesNotContain(link, b => b.Host == "192.168.0.15");
        Assert.Contains(link, b => b.Host == "203.0.113.5");
        // The gossiped self-record must not carry the LAN address either.
        Assert.DoesNotContain(priv.SelfRecord(Now).Endpoints, b => b.Host == "192.168.0.15");

        // Opt-in restores it for a LAN-only / trusted deployment.
        await using var local = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat", EnableOverlayGossip = false, AdvertisedBeacons = [lan, wan],
            AdvertiseLocalAddresses = true,
        }, ct);
        Assert.Contains(local.Intone(TimeSpan.FromHours(1), Now).Beacons, b => b.Host == "192.168.0.15");
    }

    [Fact]
    public async Task SubnetPolicy_BlocksDisallowedPeer_ButWhitelistBeatsBlacklist()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat", EnableOverlayGossip = false }, ct);
        var uri = host.IntoneUri(TimeSpan.FromHours(1), Now); // a loopback (127.0.0.1) beacon
        Assert.True(CupriNet.Core.IntonationUri.TryParse(uri, out var link, out _));

        // Deny everything: the loopback inviter is not dialable -> no candidate.
        await using var blocked = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat", EnableOverlayGossip = false, DeniedSubnets = ["0.0.0.0/0"],
        }, ct);
        await Assert.ThrowsAsync<CupriNodeException>(async () => await blocked.ConjoinAsync(link!, Now, ct));

        // Whitelist loopback (which beats the deny-all): pairing succeeds.
        var acceptTask = host.AcceptAsync(ct);
        await using var allowed = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat", EnableOverlayGossip = false,
            AllowedSubnets = ["127.0.0.0/8"], DeniedSubnets = ["0.0.0.0/0"],
        }, ct);
        await using var pairedFromJoiner = await allowed.ConjoinAsync(link!, Now, ct);
        await using var pairedFromHost = await acceptTask;
        Assert.Equal(allowed.Identity.Sigil, pairedFromHost.PeerSigil);
    }

    private static Sigil Sig(byte seed)
    {
        var bytes = new byte[Sigil.Size];
        Array.Fill(bytes, seed);
        return new Sigil(bytes);
    }

    [Fact]
    public async Task AcceptLoop_IsNotBlockedByASilentPeer()
    {
        // Comfortably under the 30s handshake timeout: if a silent peer could stall the accept loop, a legitimate
        // pairing would be delayed ~30s and this would cancel first.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat", EnableOverlayGossip = false }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat", EnableOverlayGossip = false }, ct);

        // A slow-loris: connect to the host and never speak, occupying an inbound handshake.
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)host.LocalEndPoint).Port, ct);

        // A real peer must still pair promptly despite the silent connection.
        var uri = host.IntoneUri(TimeSpan.FromHours(1), Now);
        Assert.True(CupriNet.Core.IntonationUri.TryParse(uri, out var intonation, out _));

        var acceptTask = host.AcceptAsync(ct);
        await using var fromJoiner = await joiner.ConjoinAsync(intonation, Now, ct);
        await using var fromHost = await acceptTask;

        Assert.Equal(joiner.Identity.Sigil, fromHost.PeerSigil);
    }
}
