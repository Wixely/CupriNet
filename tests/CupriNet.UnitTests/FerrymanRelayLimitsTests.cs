using System.Net;
using CupriNet.Concordance;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// What a public relay will and will not hold open. Its callers are anonymous by design, so every bound here is on
/// time or on source address: a caller that says nothing is dropped, a reservation that stops refreshing is reaped,
/// one address cannot take every slot, an address no punch could reach is not passed on, and asking it to disturb
/// a target costs work done on the spot.
/// </summary>
public class FerrymanRelayLimitsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task<CupriNode> RelayAsync(CancellationToken ct, TimeSpan? request = null, TimeSpan? idle = null, int perAddress = 16, int tribute = 8) =>
        CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat",
            EnableOverlayGossip = false,
            EnableFerryman = true,
            FerrymanRequestTimeout = request ?? TimeSpan.FromSeconds(10),
            FerrymanReservationIdleTimeout = idle ?? TimeSpan.FromSeconds(90),
            MaxFerrymanSessionsPerAddress = perAddress,
            RequiredFerrymanTributeDifficulty = tribute,
        }, ct);

    private static async Task<NoiseVessel> DialAsync(CupriNode relay, CancellationToken ct)
    {
        var raw = await TcpVessel.ConnectAsync("127.0.0.1", relay.LocalEndPoint.Port, cancellationToken: ct);
        await Toll.SolveAsync(raw, ct);
        var conjunction = await NoiseConjunction.InitiateAsync(raw, NodeIdentity.Generate(relay.Suite), relay.Network, relay.Suite, cancellationToken: ct);
        await conjunction.Vessel.SendAsync(OverlayControl.Stream, new[] { OverlayControl.KindFerryman }, ct);
        return conjunction.Vessel;
    }

    /// <summary>Reserves a fresh handle on <paramref name="vessel"/> and returns it with the relay's answer.</summary>
    private static async Task<(byte[] Handle, byte Status)> ReserveAsync(CupriNode relay, NoiseVessel vessel, CancellationToken ct)
    {
        var rendezvous = relay.Suite.GenerateSeal();
        var handle = FerrymanProtocol.Handle(rendezvous.PublicKey);
        IReadOnlyList<IPEndPoint> candidates = [new IPEndPoint(IPAddress.Loopback, 40000)];
        var signature = relay.Suite.CreateSigner(rendezvous.PrivateKey).Sign(FerrymanProtocol.ReserveSubject(vessel.HandshakeHash.Span, handle, candidates));
        await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Reserve(handle, candidates, rendezvous.PublicKey, signature), ct);
        var ack = await vessel.ReceiveAsync(ct);
        Assert.True(FerrymanProtocol.TryReadReserved(ack!.Value.Payload, out var status));
        return (handle, status);
    }

    /// <summary>True when the relay has hung up: the next receive ends or fails rather than waiting.</summary>
    private static async Task<bool> IsClosedByRelayAsync(NoiseVessel vessel, TimeSpan within, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(within);
        try { return await vessel.ReceiveAsync(wait.Token) is null; }
        catch (OperationCanceledException) { return false; }
        catch { return true; }
    }

    private static readonly IPEndPoint[] RequesterCandidates = [new(IPAddress.Loopback, 40001)];

    /// <summary>Work at <paramref name="difficulty"/> for <paramref name="handle"/>, good on <paramref name="vessel"/> only.</summary>
    private static byte[] TributeFor(NoiseVessel vessel, byte[] handle, int difficulty)
        => Tribute.Solve(FerrymanProtocol.TributeSubject(vessel.HandshakeHash.Span, handle), difficulty);

    /// <summary>Sends a RENDEZVOUS carrying <paramref name="tribute"/> and returns the relay's OFFER.</summary>
    private static async Task<byte[]> RendezvousAsync(NoiseVessel requester, byte[] handle, byte[] tribute, CancellationToken ct)
    {
        await requester.SendAsync(OverlayControl.Stream, FerrymanProtocol.Rendezvous(handle, RequesterCandidates, tribute), ct);
        return (await requester.ReceiveAsync(ct))!.Value.Payload.ToArray();
    }

    /// <summary>A properly paid rendezvous for <paramref name="handle"/>: the status the relay answers with.</summary>
    private static async Task<byte> RendezvousStatusAsync(CupriNode relay, byte[] handle, CancellationToken ct, int tribute = 8)
    {
        await using var requester = await DialAsync(relay, ct);
        var offer = await RendezvousAsync(requester, handle, TributeFor(requester, handle, tribute), ct);
        Assert.True(FerrymanProtocol.TryReadOffer(offer, out var status, out _, out _));
        return status;
    }

    [Fact]
    public async Task ACaller_ThatSaysNothing_IsDropped()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await RelayAsync(ct, request: TimeSpan.FromMilliseconds(300));

        await using var silent = await DialAsync(relay, ct);

        Assert.True(await IsClosedByRelayAsync(silent, TimeSpan.FromSeconds(10), ct));
    }

    [Fact]
    public async Task AReservation_ThatStopsRefreshing_IsReaped()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await RelayAsync(ct, idle: TimeSpan.FromMilliseconds(400));

        await using var target = await DialAsync(relay, ct);
        var (handle, status) = await ReserveAsync(relay, target, ct);
        Assert.Equal(FerrymanProtocol.StatusOk, status);

        Assert.True(await IsClosedByRelayAsync(target, TimeSpan.FromSeconds(10), ct));
        Assert.Equal(FerrymanProtocol.StatusNotReserved, await RendezvousStatusAsync(relay, handle, ct));
    }

    [Fact]
    public async Task AReservation_ThatKeepsRefreshing_IsKept()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await RelayAsync(ct, idle: TimeSpan.FromMilliseconds(1500));

        await using var target = await DialAsync(relay, ct);
        var (handle, status) = await ReserveAsync(relay, target, ct);
        Assert.Equal(FerrymanProtocol.StatusOk, status);

        // Well past the idle timeout, but never silent for anything near it. The gap is a fifteenth of the timeout:
        // at a sixth (600ms against 100ms) a loaded two-core CI runner stalled long enough to be reaped.
        for (var i = 0; i < 35; i++)
        {
            await Task.Delay(100, ct);
            await target.SendAsync(OverlayControl.Stream, FerrymanProtocol.KeepAlive(), ct);
        }

        Assert.Equal(FerrymanProtocol.StatusOk, await RendezvousStatusAsync(relay, handle, ct));
    }

    [Fact]
    public async Task OneAddress_CannotHoldMoreThanItsShareOfSessions()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await RelayAsync(ct, perAddress: 2);

        await using var first = await DialAsync(relay, ct);
        await using var second = await DialAsync(relay, ct);
        Assert.Equal(FerrymanProtocol.StatusOk, (await ReserveAsync(relay, first, ct)).Status);
        Assert.Equal(FerrymanProtocol.StatusOk, (await ReserveAsync(relay, second, ct)).Status);

        // A third from the same address is turned away without being served.
        await using var third = await DialAsync(relay, ct);
        Assert.True(await IsClosedByRelayAsync(third, TimeSpan.FromSeconds(10), ct));

        // Giving a slot back makes room again.
        await first.DisposeAsync();
        for (var attempt = 0; ; attempt++)
        {
            await using var again = await DialAsync(relay, ct);
            try
            {
                if ((await ReserveAsync(relay, again, ct)).Status == FerrymanProtocol.StatusOk)
                    break;
            }
            catch when (attempt < 50) { /* the relay has not noticed the first one leave yet */ }
            await Task.Delay(50, ct);
        }
    }

    [Fact]
    public async Task ARendezvous_ThatUnderpays_IsToldThePrice_AndServedOnceItPaysIt()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await RelayAsync(ct, tribute: 12);

        await using var target = await DialAsync(relay, ct);
        var (handle, _) = await ReserveAsync(relay, target, ct);

        await using var requester = await DialAsync(relay, ct);
        var subject = FerrymanProtocol.TributeSubject(requester.HandshakeHash.Span, handle);
        var worthless = new byte[8];
        while (Tribute.Verify(subject, worthless, 12))
            worthless[0]++;

        // Underpaid: no nonce, no address of the target's — only the price. The target hears nothing.
        var refusal = await RendezvousAsync(requester, handle, worthless, ct);
        Assert.True(FerrymanProtocol.TryReadRequiredTribute(refusal, out var required));
        Assert.Equal(12, required);
        Assert.True(FerrymanProtocol.TryReadOffer(refusal, out var status, out _, out var offered));
        Assert.Equal(FerrymanProtocol.StatusTributeTooLow, status);
        Assert.Empty(offered);

        // Paid, on the same connection: brokered.
        var offer = await RendezvousAsync(requester, handle, Tribute.Solve(subject, required), ct);
        Assert.True(FerrymanProtocol.TryReadOffer(offer, out status, out _, out offered));
        Assert.Equal(FerrymanProtocol.StatusOk, status);
        Assert.NotEmpty(offered);

        var notify = await target.ReceiveAsync(ct);
        Assert.True(FerrymanProtocol.TryReadNotify(notify!.Value.Payload, out _, out _));
    }

    [Fact]
    public async Task ARendezvous_ThatUnderpaysTwice_IsDropped()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await RelayAsync(ct, tribute: 20);

        await using var target = await DialAsync(relay, ct);
        var (handle, _) = await ReserveAsync(relay, target, ct);

        await using var requester = await DialAsync(relay, ct);
        var subject = FerrymanProtocol.TributeSubject(requester.HandshakeHash.Span, handle);
        var worthless = new byte[8];
        while (Tribute.Verify(subject, worthless, 20))
            worthless[0]++;

        Assert.True(FerrymanProtocol.TryReadRequiredTribute(await RendezvousAsync(requester, handle, worthless, ct), out _));
        await requester.SendAsync(OverlayControl.Stream, FerrymanProtocol.Rendezvous(handle, RequesterCandidates, worthless), ct);
        Assert.True(await IsClosedByRelayAsync(requester, TimeSpan.FromSeconds(10), ct));
    }

    [Fact]
    public async Task WorkDoneForOneConnection_BuysNothingOnAnother()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await RelayAsync(ct, tribute: 16);

        await using var target = await DialAsync(relay, ct);
        var (handle, _) = await ReserveAsync(relay, target, ct);

        await using var first = await DialAsync(relay, ct);
        await using var second = await DialAsync(relay, ct);
        var paidOnFirst = TributeFor(first, handle, 16);

        Assert.True(FerrymanProtocol.TryReadRequiredTribute(await RendezvousAsync(second, handle, paidOnFirst, ct), out _));
        Assert.True(FerrymanProtocol.TryReadOffer(await RendezvousAsync(first, handle, paidOnFirst, ct), out var status, out _, out _));
        Assert.Equal(FerrymanProtocol.StatusOk, status);
    }

    [Fact]
    public async Task ARelay_ThatAsksForNoWork_ServesARendezvousWithNone()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await RelayAsync(ct, tribute: 0);

        await using var target = await DialAsync(relay, ct);
        var (handle, _) = await ReserveAsync(relay, target, ct);

        await using var requester = await DialAsync(relay, ct);
        Assert.True(FerrymanProtocol.TryReadOffer(await RendezvousAsync(requester, handle, [], ct), out var status, out _, out _));
        Assert.Equal(FerrymanProtocol.StatusOk, status);
    }

    [Fact]
    public void Candidates_NoPunchCouldReach_AreDropped_ButLoopbackAndPrivateOnesStay()
    {
        IPEndPoint[] offered =
        [
            new(IPAddress.Loopback, 4000),                  // same host — kept on purpose
            new(IPAddress.Parse("192.168.1.20"), 4000),     // same LAN — kept on purpose
            new(IPAddress.Parse("203.0.113.9"), 4000),
            new(IPAddress.Parse("::ffff:203.0.113.9"), 4000), // the same address again, IPv4-mapped
            new(IPAddress.Any, 4000),
            new(IPAddress.IPv6Any, 4000),
            new(IPAddress.Broadcast, 4000),
            new(IPAddress.Parse("224.0.0.251"), 4000),
            new(IPAddress.Parse("ff02::1"), 4000),
        ];

        Assert.Equal(
            [new IPEndPoint(IPAddress.Loopback, 4000), new IPEndPoint(IPAddress.Parse("192.168.1.20"), 4000), new IPEndPoint(IPAddress.Parse("203.0.113.9"), 4000)],
            FerrymanProtocol.Usable(offered));
    }
}
