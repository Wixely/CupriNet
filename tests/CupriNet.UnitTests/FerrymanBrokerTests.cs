using System.Net;
using CupriNet.Abstractions;
using CupriNet.Core;
using CupriNet.Hosting;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The Ferryman <em>brokering</em> tier: a relay coordinates a hole punch between two peers (signaling only), then
/// drops out. On loopback there is no NAT, so this exercises reservation → rendezvous → punch → direct pairing and
/// the relay handoff; real NAT traversal is inherently untestable in CI (same caveat as <see cref="NatTraversalTests"/>).
/// The complementary L1 stream-relay tier is covered by <see cref="FerrymanTests"/>.
/// </summary>
public class FerrymanBrokerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task<CupriNode> NodeAsync(bool relay, CancellationToken ct) =>
        CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat",
            EnableOverlayGossip = false,
            EnableFerryman = relay,
        }, ct);

    private static Beacon RelayBeacon(CupriNode relay) =>
        new(EndpointKind.Manual, "127.0.0.1", ((IPEndPoint)relay.LocalEndPoint).Port);

    [Fact]
    public async Task BrokeredPunch_PairsRequesterAndTarget_ViaRelay()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var relay = await NodeAsync(relay: true, ct);
        await using var target = await NodeAsync(relay: false, ct);
        await using var requester = await NodeAsync(relay: false, ct);
        var beacon = RelayBeacon(relay);

        // The target keeps a reservation live with the relay so it's reachable by handle.
        using var reserve = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var maintaining = target.MaintainFerrymanReservationAsync(beacon, reserve.Token);

        var accepting = target.AcceptAsync(ct);
        await using var fromRequester = await ConjoinWithRetryAsync(requester, beacon, target.Identity.Sigil, ct);
        await using var fromTarget = await accepting;

        // Both ends completed the DIRECT, mutually-authenticated pairing — the relay only brokered signaling.
        Assert.Equal(target.Identity.Sigil, fromRequester.PeerSigil);
        Assert.Equal(requester.Identity.Sigil, fromTarget.PeerSigil);

        reserve.Cancel();
        try { await maintaining; } catch { /* cancelled */ }
    }

    [Fact]
    public async Task Rendezvous_ToAnUnreservedTarget_Fails()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var relay = await NodeAsync(relay: true, ct);
        await using var requester = await NodeAsync(relay: false, ct);

        // No target ever reserved this Sigil's handle -> the relay has nothing to broker.
        var unknown = NodeIdentity.Generate(requester.Suite).Sigil;
        await Assert.ThrowsAsync<CupriNodeException>(
            async () => await requester.ConjoinViaFerrymanAsync(RelayBeacon(relay), unknown, DateTimeOffset.UtcNow, cancellationToken: ct));
    }

    [Fact]
    public async Task ApproveRelayCallback_SeesTheRelaySigil_AndCanRefuse()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var relay = await NodeAsync(relay: true, ct);
        await using var requester = await NodeAsync(relay: false, ct);

        var seen = default(Sigil);
        await Assert.ThrowsAsync<CupriNodeException>(async () => await requester.ConjoinViaFerrymanAsync(
            RelayBeacon(relay), NodeIdentity.Generate(requester.Suite).Sigil, DateTimeOffset.UtcNow,
            approveRelay: s => { seen = s; return false; }, cancellationToken: ct));

        Assert.Equal(relay.Identity.Sigil, seen); // the callback saw the relay's real Sigil before refusing
    }

    private static async Task<PairedPeer> ConjoinWithRetryAsync(CupriNode requester, Beacon relay, Sigil target, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await requester.ConjoinViaFerrymanAsync(relay, target, DateTimeOffset.UtcNow, cancellationToken: ct); }
            catch (CupriNodeException) when (attempt < 40 && !ct.IsCancellationRequested)
            {
                await Task.Delay(200, ct).ConfigureAwait(false); // reservation not live yet — retry
            }
        }
    }
}
