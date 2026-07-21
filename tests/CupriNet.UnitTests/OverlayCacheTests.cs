using CupriNet.Alembic;
using CupriNet.Concordance;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The opt-in local overlay cache: persisting known nodes lets a node warm-start (reconnect directly, no
/// cold-start discovery hops); leaving it off keeps nothing about the overlay on disk (cold start).
/// </summary>
public class OverlayCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static PeerRecord Record(int i, ICryptoSuite suite)
        => PeerRecordSigner.Create(
            NodeIdentity.Generate(suite),
            [new Beacon(EndpointKind.Host, $"10.0.0.{i}", 43820 + i)],
            1, PeerCapabilities.ChannelProvider, suite, Now);

    [Fact]
    public async Task ConstellationStore_RoundTrips_AndVerifiesSignaturesOnLoad()
    {
        var suite = CryptoSuites.Secure();
        var store = new InMemorySecretStore();
        var constellation = new Constellation(new ConstellationOptions { MaxPerSlash24 = 10_000 });

        var a = Record(1, suite);
        var b = Record(2, suite);
        constellation.Admit(a, PeerBucket.Wayfarers, Now);
        constellation.Admit(b, PeerBucket.Kindred, Now);

        await ConstellationStore.SaveAsync(store, constellation);
        var loaded = await ConstellationStore.LoadAsync(store, suite);

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, r => r.Sigil == a.Sigil);
        Assert.Contains(loaded, r => r.Sigil == b.Sigil);

        // A go-cold action wipes the cache.
        await ConstellationStore.ClearAsync(store);
        Assert.Empty(await ConstellationStore.LoadAsync(store, suite));
    }

    [Fact]
    public async Task Node_WarmStarts_FromCache_ButColdStartLoadsNothing()
    {
        var suite = CryptoSuites.Secure();
        var store = new InMemorySecretStore();
        var r1 = Record(1, suite);
        var r2 = Record(2, suite);

        CupriNodeOptions Options(bool persist) => new()
        {
            Concordium = "warm.test",
            Suite = suite,
            SecretStore = store,
            PersistOverlay = persist,
            EnableReflexiveDiscovery = false,
        };

        // First run: learn some nodes and persist them.
        await using (var node = await CupriNode.CreateAsync(Options(persist: true)))
        {
            Assert.True(node.AdmitPeer(r1, Now));
            Assert.True(node.AdmitPeer(r2, Now));
            await node.SaveOverlayStateAsync();
        }

        // A later warm start rehydrates the overlay — no cold start.
        await using (var warm = await CupriNode.CreateAsync(Options(persist: true)))
        {
            Assert.NotNull(warm.Constellation.Get(r1.Sigil));
            Assert.NotNull(warm.Constellation.Get(r2.Sigil));
        }

        // With persistence off, the very same store yields a cold start (nothing loaded).
        await using (var cold = await CupriNode.CreateAsync(Options(persist: false)))
        {
            Assert.Null(cold.Constellation.Get(r1.Sigil));
            Assert.Equal(0, cold.Constellation.Count);
        }
    }
}
