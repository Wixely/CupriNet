using CupriNet.Abstractions;
using CupriNet.Concordance;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// A small in-memory simulation of the overlay: N virtual nodes, each knowing a few neighbours (a ring
/// for connectivity plus random edges). Divination must converge from a searcher's local view to the
/// exact node closest to a random target key — proving iterative referral routing works across many hops
/// without any node forwarding traffic.
/// </summary>
public class DivinationSimulationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public async Task Divination_ConvergesToTargetNode_AcrossManyHops()
    {
        var suite = CryptoSuites.Simulacrum();
        const int n = 50;
        const int randomEdges = 5;

        var identities = new NodeIdentity[n];
        var records = new PeerRecord[n];
        for (var i = 0; i < n; i++)
        {
            identities[i] = NodeIdentity.Generate(suite);
            records[i] = PeerRecordSigner.Create(
                identities[i], [new Beacon(EndpointKind.Host, $"10.{i / 256}.{i % 256}.1", 43820)],
                1, PeerCapabilities.None, suite, Now);
        }

        var views = new Constellation[n];
        var rng = new Random(20260719); // deterministic topology
        for (var i = 0; i < n; i++)
        {
            views[i] = new Constellation(new ConstellationOptions { MaxPerSlash24 = 10_000, MaxRecords = 10_000 });
            views[i].Admit(records[(i + 1) % n], PeerBucket.Wayfarers, Now); // ring successor => connected
            for (var e = 0; e < randomEdges; e++)
            {
                var j = rng.Next(n);
                if (j != i)
                    views[i].Admit(records[j], PeerBucket.Strangers, Now);
            }
        }

        var indexBySigil = new Dictionary<Sigil, int>();
        for (var i = 0; i < n; i++)
            indexBySigil[identities[i].Sigil] = i;

        // Each queried peer answers with its own Constellation's closest known peers (its Augury).
        AuguryFunc oracle = (peer, target, _) =>
            Task.FromResult(views[indexBySigil[peer.Sigil]].ClosestTo(target, 8));

        const int searcher = 0;
        const int destination = 37;
        var target = RoutingKey.FromSealPublicKey(records[destination].SealPublicKey);
        var seeds = views[searcher].Sample(1000);

        var result = await Divination.FindAsync(target, seeds, oracle, new DivinationOptions
        {
            Alpha = 3,
            MaxQueries = 200,
            ReferralsPerResponse = 8,
            ResultLimit = 5,
        });

        Assert.NotEmpty(result.Closest);
        Assert.Equal(identities[destination].Sigil, result.Closest[0].Sigil); // found the exact target node
        Assert.True(result.PeersQueried >= 2);                                 // genuinely multi-hop
        Assert.True(result.PeersQueried <= 200);                               // respected the Ward
    }

    [Fact]
    public async Task Divination_Terminates_WhenNoPeersReachable()
    {
        var suite = CryptoSuites.Simulacrum();
        var target = RoutingKey.FromToken([9, 9, 9]);

        // No seeds, oracle never called: the lookup must terminate immediately.
        var result = await Divination.FindAsync(target, [], (_, _, _) => Task.FromResult<IReadOnlyList<PeerRecord>>([]));

        Assert.Empty(result.Closest);
        Assert.Equal(0, result.PeersQueried);
    }
}
