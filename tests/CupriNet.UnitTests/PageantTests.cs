using CupriNet.Abstractions;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Concordance;
using CupriNet.Hosting;
using CupriNet.Persistence;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Pageants: negotiated fake groups (decoy cliques). These cover the deterministic shared schedule (all members
/// must compute the identical turn-taking — the basis for relay-free clique correlation), the codec/store
/// round-trip that keeps a fake group stable across restarts, real-node clique formation, and re-negotiation of a
/// slot when a member leaves.
/// </summary>
public class PageantTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    // Initiator: no auto loop (create + heal driven by hand), and it doesn't accept others' invites.
    private static Task<CupriNode> InitiatorAsync(int size, CancellationToken ct) => CupriNode.CreateAsync(
        new CupriNodeOptions
        {
            Concordium = "pageant.test",
            EnableReflexiveDiscovery = false,
            EnableOverlayGossip = false,
            EnablePageants = false,
            MaxPageantsAsMember = 0, // no auto loop, no inbound memberships — fully manual
            PageantSize = size,
        }, ct);

    // Member: accepts invites and self-heals its joined groups on its own loop.
    private static Task<CupriNode> MemberAsync(CancellationToken ct) => CupriNode.CreateAsync(
        new CupriNodeOptions
        {
            Concordium = "pageant.test",
            EnableReflexiveDiscovery = false,
            EnableOverlayGossip = false,
            EnablePageants = false,
            MaxPageantsAsMember = 4,
        }, ct);

    private static async Task WaitUntilAsync(Func<bool> condition, Func<Task> tick, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await tick().ConfigureAwait(false);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    [Fact]
    public void PageantSchedule_SameSeed_ProducesIdenticalTurnTaking()
    {
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)(i * 7 + 1);

        var a = new PageantSchedule(seed, memberCount: 4);
        var b = new PageantSchedule(seed, memberCount: 4);

        for (var i = 0; i < 500; i++)
        {
            var ua = a.Next(512);
            var ub = b.Next(512);
            Assert.Equal(ub.Gap, ua.Gap);       // every member computes the same schedule...
            Assert.Equal(ub.Speaker, ua.Speaker); // ...so turn-taking correlates across the clique
            Assert.Equal(ub.Size, ua.Size);
            Assert.InRange(ua.Speaker, 0, 3);
            Assert.InRange(ua.Size, 16, 512);
        }
    }

    [Fact]
    public void PageantSchedule_DifferentSeed_Diverges()
    {
        var s1 = new byte[32];
        var s2 = new byte[32];
        s2[0] = 1;
        var a = new PageantSchedule(s1, 4);
        var b = new PageantSchedule(s2, 4);

        var same = 0;
        for (var i = 0; i < 200; i++)
            if (a.Next().Speaker == b.Next().Speaker)
                same++;
        Assert.True(same < 200, "different seeds must not produce identical schedules");
    }

    [Fact]
    public async Task PageantCodec_AndStore_RoundTrip()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;
        var suite = new BouncyCastleSuite();

        await using var a = await MemberAsync(ct);
        await using var b = await MemberAsync(ct);
        await using var c = await MemberAsync(ct);
        var roster = new List<PeerRecord> { a.SelfRecord(now), b.SelfRecord(now), c.SelfRecord(now) };

        var pageant = new Pageant
        {
            Id = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16],
            Seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            Epoch = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()),
            Roster = roster,
        };

        var decoded = PageantCodec.Decode(PageantCodec.Encode(pageant), suite);
        Assert.NotNull(decoded);
        Assert.Equal(pageant.Id, decoded!.Id);
        Assert.Equal(pageant.Seed, decoded.Seed);
        Assert.Equal(pageant.Epoch, decoded.Epoch);
        Assert.Equal(roster.Select(r => r.Sigil), decoded.Roster.Select(r => r.Sigil));

        var store = new InMemorySecretStore();
        await PageantStore.SaveAsync(store, [new StoredPageant(true, pageant)], ct);
        var loaded = await PageantStore.LoadAsync(store, suite, ct);
        Assert.Single(loaded);
        Assert.True(loaded[0].IsInitiator);
        Assert.Equal(pageant.Id, loaded[0].Pageant.Id);
        Assert.Equal(roster.Select(r => r.Sigil), loaded[0].Pageant.Roster.Select(r => r.Sigil));
    }

    [Fact]
    public async Task Pageants_FormAFullCliqueMesh_OverRealNodes()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var a = await InitiatorAsync(size: 3, ct);
        await using var b = await MemberAsync(ct);
        await using var c = await MemberAsync(ct);

        Assert.True(a.AdmitPeer(b.SelfRecord(now), now));
        Assert.True(a.AdmitPeer(c.SelfRecord(now), now));
        _ = Task.Run(async () => { try { await a.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await b.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await c.AcceptAsync(ct); } catch { } });

        await a.FormPageantForTestAsync(ct); // A mints the group, invites B and C

        // Poll (forcing heals) until every node holds edges to the other two — a full clique, not a star.
        await WaitUntilAsync(
            () => a.PageantEdgeTotal == 2 && b.PageantEdgeTotal == 2 && c.PageantEdgeTotal == 2,
            async () =>
            {
                await a.PageantOnceAsync(ct);
                await b.PageantOnceAsync(ct);
                await c.PageantOnceAsync(ct);
            }, ct);

        Assert.Equal(2, a.PageantEdgeTotal);
        Assert.Equal(2, b.PageantEdgeTotal);
        Assert.Equal(2, c.PageantEdgeTotal);
        Assert.Equal(2, await a.PageantProbeAsync(bytes: 64, ct)); // A's two clique edges carry cover end to end
    }

    [Fact]
    public async Task Pageants_ReNegotiateASlot_WhenAMemberLeaves()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        // A 2-member group isolates the re-negotiation (slot-replacement) path; clique formation is covered above.
        // (A larger group can't be tested on loopback: the /24 diversity quota caps same-subnet peers, so there is
        // no room for a replacement that isn't already in the roster.)
        await using var a = await InitiatorAsync(size: 2, ct);
        var b = await MemberAsync(ct);
        await using var d = await MemberAsync(ct);

        Assert.True(a.AdmitPeer(b.SelfRecord(now), now));
        _ = Task.Run(async () => { try { await a.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await b.AcceptAsync(ct); } catch { } });
        _ = Task.Run(async () => { try { await d.AcceptAsync(ct); } catch { } });

        await a.FormPageantForTestAsync(ct);
        await WaitUntilAsync(() => a.PageantEdgeTotal == 1, () => a.PageantOnceAsync(ct), ct);

        var bSigil = b.Identity.Sigil;
        var dSigil = d.Identity.Sigil;

        // B leaves the network; D becomes available (its /24 slot is now free) as a replacement.
        await b.DisposeAsync();
        Assert.True(a.AdmitPeer(d.SelfRecord(now), now));

        // Heal repeatedly: after the miss threshold A re-negotiates B's slot for D (same ordinal → schedule intact).
        for (var i = 0; i < 60 && !(a.FirstPageantRoster().Contains(dSigil) && !a.FirstPageantRoster().Contains(bSigil)); i++)
        {
            await a.PageantOnceAsync(ct);
            await Task.Delay(100, ct);
        }

        var roster = a.FirstPageantRoster();
        Assert.Contains(dSigil, roster);
        Assert.DoesNotContain(bSigil, roster);
    }
}
