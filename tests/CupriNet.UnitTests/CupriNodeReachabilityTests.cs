using System.Net;
using CupriNet.Core;
using CupriNet.Hosting;
using Xunit;

namespace CupriNet.UnitTests;

public class CupriNodeReachabilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Pairing_RecordsReflexiveObservation_OnBothNodes()
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

        // The reflexive exchange runs during pairing, so each node learned how the peer sees it.
        Assert.True(joiner.ReflexiveObserver.Count >= 1);
        Assert.True(host.ReflexiveObserver.Count >= 1);
    }

    [Fact]
    public async Task Intone_AdvertisesMappedBeacon_OnceQuorumAgrees()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var node = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);

        // Before any observations, only the local Host beacon is advertised.
        Assert.DoesNotContain(node.Intone(TimeSpan.FromHours(2), Now).Beacons, b => b.Kind == EndpointKind.Mapped);

        // Two peers observe the same public address -> the node becomes confident and advertises it.
        node.ReflexiveObserver.Add(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51000));
        node.ReflexiveObserver.Add(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51000));

        var intonation = node.Intone(TimeSpan.FromHours(2), Now);
        Assert.Contains(intonation.Beacons, b => b.Kind == EndpointKind.Mapped && b.Host == "203.0.113.7" && b.Port == 51000);
        Assert.Contains(intonation.Beacons, b => b.Kind == EndpointKind.Host); // Host beacon still present
    }
}
