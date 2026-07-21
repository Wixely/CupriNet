using CupriNet.Hosting;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Hot fuzz: long-lived decoy control connections held open and kept warm with padded cover traffic, so a real
/// channel session blends into a population of equally enduring, equally chatty decoys. These exercise the pure
/// hold-time distribution, the padded PING codec, and the companion maintenance loop over real loopback nodes.
/// </summary>
public class HotFuzzTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static Task<CupriNode> NodeAsync(int degree, CancellationToken ct) => CupriNode.CreateAsync(
        new CupriNodeOptions
        {
            Concordium = "hotfuzz.test",
            EnableReflexiveDiscovery = false,
            EnableOverlayGossip = false, // drive rounds by hand so the test is deterministic (also gates the auto loop off)
            HotFuzzDegree = degree,
        }, ct);

    [Fact]
    public void NextHold_OrdinaryDraw_StaysInTheBand()
    {
        var rng = new Random(1);
        var min = TimeSpan.FromMinutes(2);
        var max = TimeSpan.FromMinutes(30);
        for (var i = 0; i < 1000; i++)
        {
            var hold = HotFuzz.NextHold(rng, min, max, longHoldProbability: 0.0, TimeSpan.FromHours(4));
            Assert.InRange(hold, min, max);
        }
    }

    [Fact]
    public void NextHold_LongDraw_LandsInTheHeavyTail()
    {
        var rng = new Random(2);
        var max = TimeSpan.FromMinutes(30);
        var longHold = TimeSpan.FromHours(4);
        for (var i = 0; i < 1000; i++)
        {
            var hold = HotFuzz.NextHold(rng, TimeSpan.FromMinutes(2), max, longHoldProbability: 1.0, longHold);
            Assert.InRange(hold, max, longHold);
        }
    }

    [Fact]
    public void PingCodec_CarriesRequestedPadding()
    {
        var request = OverlayControl.PingRequest(downPad: 256, upPad: new byte[10]);
        Assert.Equal(OverlayControl.OpPing, request[0]);

        var response = OverlayControl.PingResponse(256);
        Assert.Equal(OverlayControl.StatusOk, response[0]);
        Assert.Equal(1 + 256, response.Length); // status byte + the requested padding

        // Padding is capped so a heartbeat can never be turned into an amplifier.
        Assert.Equal(1 + OverlayControl.MaxPingPad, OverlayControl.PingResponse(OverlayControl.MaxPingPad * 100).Length);
    }

    [Fact]
    public async Task HotFuzz_HoldsCompanions_UpToDegree_OverRealNodes()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var a = await NodeAsync(degree: 2, ct);
        await using var b = await NodeAsync(degree: 2, ct);
        await using var c = await NodeAsync(degree: 2, ct);

        // A knows B and C; B and C serve overlay control in the background.
        Assert.True(a.AdmitPeer(b.SelfRecord(now), now));
        Assert.True(a.AdmitPeer(c.SelfRecord(now), now));
        _ = Task.Run(async () => { try { await b.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await c.AcceptAsync(ct); } catch { } });

        await a.HotFuzzOnceAsync(ct);

        // Both known nodes become held companions, each warmed with a padded heartbeat (no exception thrown).
        Assert.Equal(2, a.HotFuzzCompanionCount);
    }

    [Fact]
    public async Task HotFuzz_RotatesExpiredCompanions_AndRefills()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        // Zero hold: every companion expires immediately, so each round must prune the old set and refill it.
        await using var a = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "hotfuzz.test",
            EnableReflexiveDiscovery = false,
            EnableOverlayGossip = false,
            HotFuzzDegree = 2,
            HotFuzzMinHold = TimeSpan.Zero,
            HotFuzzMaxHold = TimeSpan.Zero,
            HotFuzzLongHoldProbability = 0.0,
        }, ct);
        await using var b = await NodeAsync(degree: 2, ct);
        await using var c = await NodeAsync(degree: 2, ct);

        Assert.True(a.AdmitPeer(b.SelfRecord(now), now));
        Assert.True(a.AdmitPeer(c.SelfRecord(now), now));
        _ = Task.Run(async () => { try { await b.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await c.AcceptAsync(ct); } catch { } });

        await a.HotFuzzOnceAsync(ct);
        Assert.Equal(2, a.HotFuzzCompanionCount);

        // Second round: the first set has expired — it is rotated out and a fresh set dialed back to degree.
        await a.HotFuzzOnceAsync(ct);
        Assert.Equal(2, a.HotFuzzCompanionCount);
    }
}
