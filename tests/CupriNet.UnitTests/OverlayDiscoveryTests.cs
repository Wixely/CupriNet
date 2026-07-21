using CupriNet.Arcanum;
using CupriNet.Concordance;
using CupriNet.Core;
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
    public async Task JoinViaLink_BootstrapsOverlay_ThenDiscoversAChannel()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var inviter = await NewNodeAsync(ct);
        await using var joiner = await NewNodeAsync(ct);

        // First contact is a single link — no manual Constellation seeding.
        var uri = inviter.IntoneUri(TimeSpan.FromHours(2), now);
        Assert.True(IntonationUri.TryParse(uri, out var intonation, out _));
        var acceptTask = inviter.AcceptAsync(ct);
        await using (await joiner.ConjoinAsync(intonation, now, ct))
        await using (await acceptTask)
        {
            // Bootstrap: pairing over the link seeded each node into the other's Constellation.
            Assert.NotNull(joiner.Constellation.Get(inviter.Identity.Sigil));
            Assert.NotNull(inviter.Constellation.Get(joiner.Identity.Sigil));
        }

        // Both keep serving overlay control.
        _ = Task.Run(async () => { try { await inviter.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await joiner.AcceptAsync(ct); } catch { } });

        // The inviter advertises a channel; the joiner — knowing only the Watchword and the one peer it
        // met via the link — discovers it over the overlay.
        var watchword = Watchword.Generate("SecretRoom");
        Assert.True(await inviter.PublishChannelAsync(watchword, now, ct) >= 1);
        var providers = await joiner.FindChannelProvidersAsync(watchword, now, ct);
        Assert.Contains(providers, d => d.ProviderSigil == inviter.Identity.Sigil);
    }

    [Fact]
    public void ControlRateLimiter_AllowsUpToTheCap_ThenDrops_UntilTheWindowResets()
    {
        var limiter = new ControlRateLimiter(maxPerWindow: 3, windowMs: 1000);
        Assert.True(limiter.Allow(0));
        Assert.True(limiter.Allow(100));
        Assert.True(limiter.Allow(200));   // three within the window
        Assert.False(limiter.Allow(300));  // fourth — over the cap
        Assert.False(limiter.Allow(999));
        Assert.True(limiter.Allow(1001));  // a new window resets the count
    }

    [Fact]
    public async Task Gossip_LearnsNewNodes_FromKnownOnes()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        // Drive gossip by hand (auto loop off) so the round is deterministic.
        static Task<CupriNode> Node(CancellationToken ct) => CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "overlay.test", EnableReflexiveDiscovery = false, EnableOverlayGossip = false }, ct);

        await using var a = await Node(ct);
        await using var b = await Node(ct);
        await using var c = await Node(ct);

        // A knows B; B knows C. A has never heard of C.
        Assert.True(a.AdmitPeer(b.SelfRecord(now), now));
        Assert.True(b.AdmitPeer(c.SelfRecord(now), now));
        Assert.Null(a.Constellation.Get(c.Identity.Sigil));

        _ = Task.Run(async () => { try { await a.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await b.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await c.AcceptAsync(ct); } catch { } });

        // One round: A pulls a peer sample from B and learns C — the map grows.
        var learned = await a.GossipOnceAsync(fanout: 4, sampleSize: 16, ct);

        Assert.True(learned >= 1);
        Assert.NotNull(a.Constellation.Get(c.Identity.Sigil));
    }

    [Fact]
    public void PeerControlBudget_CapsConnections_AndSharesTheRateAcrossThem()
    {
        var budget = new PeerControlBudget(maxRequestsPerWindow: 3, windowMs: 1000);

        // Per-peer connection cap.
        Assert.True(budget.TryOpenConnection(2));
        Assert.True(budget.TryOpenConnection(2));
        Assert.False(budget.TryOpenConnection(2)); // at cap
        Assert.Equal(1, budget.CloseConnection());  // one released
        Assert.True(budget.TryOpenConnection(2));    // slot free again

        // The request rate is shared across the peer's connections, not per-connection.
        Assert.True(budget.Allow(0));
        Assert.True(budget.Allow(0));
        Assert.True(budget.Allow(0));
        Assert.False(budget.Allow(0));   // fourth in the window — over the shared cap
        Assert.True(budget.Allow(1001)); // new window resets it
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
