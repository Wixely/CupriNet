using CupriNet.Arcanum;
using CupriNet.Concordance;
using CupriNet.Hosting;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Overlay discovery running over the REAL network (not the simulation harness): three actual CupriNode
/// instances over loopback TCP. One publishes a channel advertisement into the overlay; another, knowing
/// only opaque routing coordinates, finds it by routing a Divination to the channel's Ascendant and looking
/// up Decrees by the current Glyph window — all over live, Noise-encrypted control connections.
/// </summary>
public class OverlayDiscoveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static Task<CupriNode> NewNodeAsync(CancellationToken ct)
        => CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "overlay.test", EnableReflexiveDiscovery = false }, ct);

    [Fact]
    public async Task PublishAndFind_OverRealConnections_LocatesTheProvider()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow; // align with the store's real-time expiry/glyph epoch

        await using var a = await NewNodeAsync(ct);
        await using var b = await NewNodeAsync(ct);
        await using var c = await NewNodeAsync(ct);

        // Bootstrap: give each node the others' signed records so lookups have somewhere to start.
        // (In production this comes from the Intonation Litany + peer-exchange gossip.)
        var records = new[] { (a, a.SelfRecord(now)), (b, b.SelfRecord(now)), (c, c.SelfRecord(now)) };
        foreach (var (node, _) in records)
            foreach (var (other, record) in records)
                if (!ReferenceEquals(node, other))
                    Assert.True(node.AdmitPeer(record, now));

        // Each node serves overlay control by running its accept loop (returns only on channel peers,
        // of which there are none here — so these run as background servers).
        var servers = new[] { a, b, c }.Select(n => Task.Run(async () =>
        {
            try { await n.AcceptAsync(ct); } catch { /* cancelled on teardown */ }
        })).ToArray();

        // Node A advertises a channel it provides.
        var watchword = Watchword.Generate("Dungeons&Dragons");
        var holders = await a.PublishChannelAsync(watchword, now, ct);
        Assert.True(holders >= 1, "the advert should have been accepted by at least one holder");

        // Node C — which only knows the Watchword — discovers A as a provider over the network.
        var providers = await c.FindChannelProvidersAsync(watchword, now, ct);

        Assert.Contains(providers, d => d.ProviderSigil == a.Identity.Sigil);
        // The discovered advert carries the provider's dialable beacons, so C could now connect + Consecrate.
        var found = providers.First(d => d.ProviderSigil == a.Identity.Sigil);
        Assert.NotEmpty(found.Endpoints);

        _ = servers; // kept alive by the awaits above; disposed with the nodes
    }

    [Fact]
    public async Task Find_WithNoProviders_ReturnsEmpty()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var a = await NewNodeAsync(ct);
        await using var b = await NewNodeAsync(ct);
        a.AdmitPeer(b.SelfRecord(now), now);
        b.AdmitPeer(a.SelfRecord(now), now);
        _ = Task.Run(async () => { try { await a.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await b.AcceptAsync(ct); } catch { } });

        var providers = await a.FindChannelProvidersAsync(Watchword.Generate("NobodyHome"), now, ct);
        Assert.Empty(providers);
    }
}
