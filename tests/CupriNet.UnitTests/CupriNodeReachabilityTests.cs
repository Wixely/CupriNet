using System.Net;
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

    private static Sigil Sig(byte seed)
    {
        var bytes = new byte[Sigil.Size];
        Array.Fill(bytes, seed);
        return new Sigil(bytes);
    }
}
