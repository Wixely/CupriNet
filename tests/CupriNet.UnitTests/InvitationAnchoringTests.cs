using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Concordance;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Invitation-anchoring: overlay routing prefers peers we reached through a real relationship (Intonation
/// or Consecration) over anonymous gossiped strangers, so a flood of cheap Sybils cannot eclipse a lookup
/// or evict our trusted contacts.
/// </summary>
public class InvitationAnchoringTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static PeerRecord Record(int i, ICryptoSuite suite)
        => PeerRecordSigner.Create(
            NodeIdentity.Generate(suite),
            [new Beacon(EndpointKind.Host, $"10.{i / 256}.{i % 256}.1", 43820)],
            1, PeerCapabilities.None, suite, Now);

    [Fact]
    public void Constellation_MarkAnchored_PromotesToKindred_AndReportsAnchored()
    {
        var suite = CryptoSuites.Simulacrum();
        var c = new Constellation(new ConstellationOptions { MaxPerSlash24 = 10_000 });

        var stranger = Record(1, suite);
        c.Admit(stranger, PeerBucket.Strangers, Now);
        Assert.False(c.IsAnchored(stranger.Sigil));

        c.MarkAnchored(stranger.Sigil);
        Assert.True(c.IsAnchored(stranger.Sigil));
        Assert.Equal(PeerBucket.Kindred, c.Get(stranger.Sigil)!.Bucket);

        // A peer held in a non-expendable bucket counts as anchored without an explicit mark.
        var wayfarer = Record(2, suite);
        c.Admit(wayfarer, PeerBucket.Wayfarers, Now);
        Assert.True(c.IsAnchored(wayfarer.Sigil));

        // We can anchor a peer we have no full record for (e.g. one we only Consecrated with).
        var bareSigil = Sigil.FromSealPublicKey(NodeIdentity.Generate(suite).Seal.PublicKey);
        c.MarkAnchored(bareSigil);
        Assert.True(c.IsAnchored(bareSigil));
    }

    [Fact]
    public void Constellation_AnchoredPeer_SurvivesStrangerFlood()
    {
        var suite = CryptoSuites.Simulacrum();
        var c = new Constellation(new ConstellationOptions { MaxRecords = 4, MaxPerSlash24 = 10_000 });

        var trusted = Record(0, suite);
        c.Admit(trusted, PeerBucket.Strangers, Now);
        c.MarkAnchored(trusted.Sigil); // now Kindred

        // Flood strangers well past the cap; eviction takes Strangers first, never our anchored peer.
        for (var i = 1; i <= 50; i++)
            c.Admit(Record(i, suite), PeerBucket.Strangers, Now);

        Assert.NotNull(c.Get(trusted.Sigil));
        Assert.True(c.IsAnchored(trusted.Sigil));
    }

    [Fact]
    public async Task Divination_WithoutAnchoring_NeverQueriesTheFarTrustedPeer()
    {
        var (target, seeds, anchored, oracle, asked) = BuildEclipseScenario();
        var options = new DivinationOptions { Alpha = 3, MaxQueries = 24, ReferralsPerResponse = 8, ResultLimit = 5 };

        await Divination.FindAsync(target, seeds, oracle, options);

        // The trusted peer is the farthest from the target, so a distance-only search never consults it.
        Assert.DoesNotContain(anchored.Sigil, asked);
    }

    [Fact]
    public async Task Divination_WithAnchoring_StillConsultsTheTrustedPeer_ResistingEclipse()
    {
        var (target, seeds, anchored, oracle, asked) = BuildEclipseScenario();
        var options = new DivinationOptions { Alpha = 3, MaxQueries = 24, ReferralsPerResponse = 8, ResultLimit = 5 };

        await Divination.FindAsync(target, seeds, oracle, options, isAnchored: s => s == anchored.Sigil);

        // Anchoring reserves a slot for the trusted peer even though a swarm of Sybils sits nearer the target.
        Assert.Contains(anchored.Sigil, asked);
    }

    /// <summary>
    /// A swarm of Sybil "strangers" packed nearer the target than one anchored peer, with more Sybils than
    /// the query Ward — so a distance-only search exhausts its budget on Sybils and never reaches the peer.
    /// </summary>
    private static (RoutingKey Target, List<PeerRecord> Seeds, PeerRecord Anchored, AuguryFunc Oracle, HashSet<Sigil> Asked) BuildEclipseScenario()
    {
        var suite = CryptoSuites.Simulacrum();
        const int n = 40;
        var records = new List<PeerRecord>(n);
        for (var i = 0; i < n; i++)
            records.Add(Record(i, suite));

        var target = RoutingKey.FromToken([7, 7, 7, 7]);
        var byDistance = records
            .OrderBy(r => RoutingKey.FromSealPublicKey(r.SealPublicKey).DistanceTo(target))
            .ToList();
        var anchored = byDistance[^1]; // the farthest peer is our one trusted contact

        var asked = new HashSet<Sigil>();
        AuguryFunc oracle = (peer, _, _) =>
        {
            asked.Add(peer.Sigil);
            return Task.FromResult<IReadOnlyList<PeerRecord>>([]); // all peers are already seeds
        };

        return (target, records, anchored, oracle, asked);
    }
}
