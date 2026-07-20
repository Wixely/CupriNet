using CupriNet.Abstractions;
using CupriNet.Arcanum;
using CupriNet.Concordance;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// End-to-end Phase 3 discovery over the simulated overlay: a provider advertises a channel by routing
/// its Decree to nodes near the channel's Ascendant, and a different node holding the same Watchword
/// finds that provider by routing to the same Ascendant and looking up the current Glyph — with no
/// central directory and no node forwarding channel traffic.
/// </summary>
public class ChannelDiscoverySimulationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static Watchword Fixed(string name, string salt = "AAAAAAAAAAAAAAAAAAAAAA")
    {
        Assert.True(Watchword.TryParse($"{name}#{salt}", out var w));
        return w;
    }

    [Fact]
    public async Task Provider_IsDiscovered_ByAnotherWatchwordHolder()
    {
        var suite = CryptoSuites.Simulacrum();
        const int n = 50;
        const int randomEdges = 5;

        var identities = new NodeIdentity[n];
        var records = new PeerRecord[n];
        var views = new Constellation[n];
        var decreeStores = new DecreeStore[n];
        for (var i = 0; i < n; i++)
        {
            identities[i] = NodeIdentity.Generate(suite);
            records[i] = PeerRecordSigner.Create(identities[i], [new Beacon(EndpointKind.Host, $"10.{i / 256}.{i % 256}.1", 43820)], 1, PeerCapabilities.None, suite, Now);
            views[i] = new Constellation(new ConstellationOptions { MaxPerSlash24 = 10_000, MaxRecords = 10_000 });
            decreeStores[i] = new DecreeStore();
        }

        var rng = new Random(4242);
        for (var i = 0; i < n; i++)
        {
            views[i].Admit(records[(i + 1) % n], PeerBucket.Wayfarers, Now); // ring => connected
            for (var e = 0; e < randomEdges; e++)
            {
                var j = rng.Next(n);
                if (j != i)
                    views[i].Admit(records[j], PeerBucket.Strangers, Now);
            }
        }

        var index = new Dictionary<Sigil, int>();
        for (var i = 0; i < n; i++)
            index[identities[i].Sigil] = i;

        AuguryFunc augury = (peer, target, _) => Task.FromResult(views[index[peer.Sigil]].ClosestTo(target, 8));
        DecreePublishFunc publish = (holder, decree, _) =>
        {
            decreeStores[index[holder.Sigil]].Publish(decree, Now);
            return Task.CompletedTask;
        };
        DecreeLookupFunc lookup = (holder, window, _) =>
            Task.FromResult(decreeStores[index[holder.Sigil]].LookupWindow(window, Now));

        var keys = ArcanumKeys.Derive(Fixed("Dungeons&Dragons"), suite);
        var options = new DivinationOptions { Alpha = 3, MaxQueries = 200, ReferralsPerResponse = 8, ResultLimit = 8 };

        // Provider (node 5) advertises the channel near the Ascendant.
        const int provider = 5;
        var decree = DecreeSigner.Publish(identities[provider], keys, [new Beacon(EndpointKind.Host, "203.0.113.9", 43820)], TimeSpan.FromMinutes(20), 1, suite, Now);
        var holders = await ChannelDirectory.PublishAsync(keys.Ascendant, decree, views[provider].Sample(1000), augury, publish, options);
        Assert.True(holders > 0);

        // Searcher (node 20), holding the same Watchword, discovers the provider.
        const int searcher = 20;
        var found = await ChannelDirectory.FindProvidersAsync(keys, Now, suite, views[searcher].Sample(1000), augury, lookup, options);

        Assert.Contains(found, d => d.ProviderSigil == identities[provider].Sigil);
    }

    [Fact]
    public async Task ForeignChannelDecrees_AreFilteredOut()
    {
        var suite = CryptoSuites.Simulacrum();
        var mine = ArcanumKeys.Derive(Fixed("Gaming"), suite);
        var foreign = ArcanumKeys.Derive(Fixed("Politics"), suite);
        var provider = NodeIdentity.Generate(suite);
        var holder = PeerRecordSigner.Create(NodeIdentity.Generate(suite), [], 1, PeerCapabilities.None, suite, Now);

        // The single reachable holder only knows a Decree for a DIFFERENT channel.
        var foreignDecree = DecreeSigner.Publish(provider, foreign, [], TimeSpan.FromMinutes(20), 1, suite, Now);

        AuguryFunc augury = (_, _, _) => Task.FromResult<IReadOnlyList<PeerRecord>>([]);
        DecreeLookupFunc lookup = (_, _, _) => Task.FromResult<IReadOnlyList<Decree>>([foreignDecree]);

        var found = await ChannelDirectory.FindProvidersAsync(
            mine, Now, suite, seeds: [holder], augury, lookup,
            new DivinationOptions { MaxQueries = 4, ResultLimit = 4 });

        Assert.Empty(found); // a foreign channel's Decree never matches mine
    }
}
