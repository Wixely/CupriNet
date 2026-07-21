using CupriNet.Hosting;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Effigies: decoy channel sessions that carry chat-shaped cover traffic so a real channel session blends among
/// them. These cover the pure conversation-shape distribution, the establishment of independent decoy sessions
/// over real loopback nodes, and the coordinated cohort whose members fan out together (group-channel cover).
/// </summary>
public class EffigyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static Task<CupriNode> PartnerAsync(CancellationToken ct) => CupriNode.CreateAsync(
        new CupriNodeOptions { Concordium = "effigy.test", EnableReflexiveDiscovery = false, EnableOverlayGossip = false }, ct);

    private static Task<CupriNode> DecoyNodeAsync(int count, int groupSize, CancellationToken ct) => CupriNode.CreateAsync(
        new CupriNodeOptions
        {
            Concordium = "effigy.test",
            EnableReflexiveDiscovery = false,
            EnableOverlayGossip = false,
            EnableEffigies = false, // auto loop off — we drive EffigyOnceAsync by hand for determinism
            EffigyCount = count,
            EffigyGroupSize = groupSize,
        }, ct);

    [Fact]
    public void ConversationShaper_ProducesBurstyIrregularTraffic()
    {
        var rng = new Random(7);
        var shaper = new ConversationShaper();
        var sawTurnCadence = false;   // a short, within-turn gap
        var sawIdleGap = false;       // a long, between-turn gap

        for (var i = 0; i < 2000; i++)
        {
            var (delay, bytes) = shaper.Next(rng, maxBytes: 512);
            Assert.InRange(bytes, 16, 512);
            Assert.InRange(delay, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(90000));
            if (delay < TimeSpan.FromSeconds(3)) sawTurnCadence = true;
            if (delay >= TimeSpan.FromSeconds(8)) sawIdleGap = true;
        }

        // It alternates between turns and idle — not a single fixed cadence (which would itself be a fingerprint).
        Assert.True(sawTurnCadence, "expected some short within-turn cadences");
        Assert.True(sawIdleGap, "expected some long idle gaps");
    }

    [Fact]
    public async Task Effigies_EstablishIndependentDecoySessions_AndCarryCover()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var a = await DecoyNodeAsync(count: 2, groupSize: 0, ct);
        await using var b = await PartnerAsync(ct);
        await using var c = await PartnerAsync(ct);

        Assert.True(a.AdmitPeer(b.SelfRecord(now), now));
        Assert.True(a.AdmitPeer(c.SelfRecord(now), now));
        _ = Task.Run(async () => { try { await b.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await c.AcceptAsync(ct); } catch { } });

        await a.EffigyOnceAsync(ct);

        Assert.Equal(2, a.EffigyCountActive);
        // Each decoy carries a chat-shaped frame end to end (the partner is serving + draining it).
        Assert.Equal(2, await a.EffigyProbeAsync(bytes: 96, ct));
    }

    [Fact]
    public async Task Effigies_CoordinatedCohort_FansOutToAllMembersAtOnce()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        // A coordinated cohort of 2 (no independent singles): the two members must burst together, the way a
        // real 2-member group channel would light up both direct Vessels when a member posts.
        await using var a = await DecoyNodeAsync(count: 0, groupSize: 2, ct);
        await using var b = await PartnerAsync(ct);
        await using var c = await PartnerAsync(ct);

        Assert.True(a.AdmitPeer(b.SelfRecord(now), now));
        Assert.True(a.AdmitPeer(c.SelfRecord(now), now));
        _ = Task.Run(async () => { try { await b.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await c.AcceptAsync(ct); } catch { } });

        await a.EffigyOnceAsync(ct);

        Assert.Equal(0, a.EffigyCountActive);
        Assert.Equal(2, a.EffigyCohortSize);
        // One fan-out reaches both cohort members simultaneously — the coordinated multi-Vessel burst.
        Assert.Equal(2, await a.EffigyProbeAsync(bytes: 96, ct));
    }
}
