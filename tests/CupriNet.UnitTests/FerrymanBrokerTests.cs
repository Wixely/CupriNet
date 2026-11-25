using System.Net;
using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The Ferryman <em>brokering</em> tier: a relay coordinates a hole punch between two peers (signaling only), then
/// drops out. On loopback there is no NAT, so this exercises reservation → rendezvous → punch → direct pairing and
/// the relay handoff; real NAT traversal is inherently untestable in CI (same caveat as <see cref="NatTraversalTests"/>).
/// It also pins down what the relay is and is not told: a handle that says nothing about the target's Sigil, proven
/// with a key made for that relay, on a reservation good for one connection only.
/// The complementary L1 stream-relay tier is covered by <see cref="FerrymanTests"/>.
/// </summary>
public class FerrymanBrokerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task<CupriNode> NodeAsync(bool relay, CancellationToken ct, ISecretStore? store = null, int pays = 16, int requires = 16) =>
        CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat",
            EnableOverlayGossip = false,
            EnableFerryman = relay,
            SecretStore = store,
            FerrymanTributeDifficulty = pays,
            RequiredFerrymanTributeDifficulty = requires,
        }, ct);

    private static Beacon RelayBeacon(CupriNode relay) =>
        new(EndpointKind.Manual, "127.0.0.1", ((IPEndPoint)relay.LocalEndPoint).Port);

    /// <summary>Waits until the target's reservation with a relay is confirmed and its route is known.</summary>
    private static async Task<RelayRoute> RouteVia(CupriNode target, CupriNode relay, CancellationToken ct)
    {
        while (true)
        {
            if (target.RelayRoutes.FirstOrDefault(r => r.RelaySigil == relay.Identity.Sigil) is { } route)
                return route;
            await Task.Delay(25, ct);
        }
    }

    [Fact]
    public async Task ARequester_ThatOffersLessWorkThanTheRelayWants_PaysThePriceItIsTold_AndIsBrokered()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var relay = await NodeAsync(relay: true, ct, requires: 14);
        await using var target = await NodeAsync(relay: false, ct);
        await using var requester = await NodeAsync(relay: false, ct, pays: 2);

        using var reserve = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var maintaining = target.MaintainFerrymanReservationAsync(RelayBeacon(relay), cancellationToken: reserve.Token);
        var route = await RouteVia(target, relay, ct);

        var accepting = target.AcceptAsync(ct);
        await using var fromRequester = await requester.ConjoinViaFerrymanAsync(route, target.Identity.Sigil, now, cancellationToken: ct);
        await using var fromTarget = await accepting;
        Assert.Equal(target.Identity.Sigil, fromRequester.PeerSigil);

        reserve.Cancel();
        try { await maintaining; } catch { /* cancelled */ }
    }

    [Fact]
    public async Task BrokeredPunch_PairsRequesterAndTarget_AlongTheRouteInTheTargetsLink()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var relay = await NodeAsync(relay: true, ct);
        await using var target = await NodeAsync(relay: false, ct);
        await using var requester = await NodeAsync(relay: false, ct);

        // The target keeps a reservation live with the relay, and so becomes reachable through it.
        using var reserve = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var maintaining = target.MaintainFerrymanReservationAsync(RelayBeacon(relay), cancellationToken: reserve.Token);
        await RouteVia(target, relay, ct);

        // Everything the requester needs is in the target's link: which relay, and what to ask it for.
        Assert.True(IntonationUri.TryParse(target.IntoneUri(TimeSpan.FromHours(1), now), out var link, out _));
        var route = Assert.Single(link!.RelayRoutes);
        Assert.Equal(relay.Identity.Sigil, route.RelaySigil);

        var accepting = target.AcceptAsync(ct);
        await using var fromRequester = await requester.ConjoinViaFerrymanAsync(route, link.InviterSigil, now, cancellationToken: ct);
        await using var fromTarget = await accepting;

        // Both ends completed the DIRECT, mutually-authenticated pairing — the relay only brokered signaling.
        Assert.Equal(target.Identity.Sigil, fromRequester.PeerSigil);
        Assert.Equal(requester.Identity.Sigil, fromTarget.PeerSigil);

        reserve.Cancel();
        try { await maintaining; } catch { /* cancelled */ }
        Assert.Empty(target.RelayRoutes); // no reservation, no route
    }

    [Fact]
    public async Task OnionOnlyLink_CarriesNoRelayRoute()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var relay = await NodeAsync(relay: true, ct);
        await using var target = await NodeAsync(relay: false, ct);
        using var reserve = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var maintaining = target.MaintainFerrymanReservationAsync(RelayBeacon(relay), cancellationToken: reserve.Token);
        await RouteVia(target, relay, ct);

        // A relay route ends in a clearnet hole punch; an onion-only link promises not to lead to the node's address.
        Assert.Single(target.Intone(TimeSpan.FromHours(1), now, LinkTransports.All).RelayRoutes);
        Assert.Empty(target.Intone(TimeSpan.FromHours(1), now, LinkTransports.OnionOnly).RelayRoutes);

        reserve.Cancel();
        try { await maintaining; } catch { /* cancelled */ }
    }

    [Fact]
    public async Task TheHandle_SaysNothingAboutTheTargetsSigil_AndDiffersPerRelay()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var relayA = await NodeAsync(relay: true, ct);
        await using var relayB = await NodeAsync(relay: true, ct);
        await using var target = await NodeAsync(relay: false, ct);

        using var reserve = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var a = target.MaintainFerrymanReservationAsync(RelayBeacon(relayA), cancellationToken: reserve.Token);
        var b = target.MaintainFerrymanReservationAsync(RelayBeacon(relayB), cancellationToken: reserve.Token);
        var viaA = await RouteVia(target, relayA, ct);
        var viaB = await RouteVia(target, relayB, ct);

        // A relay that knows a Sigil cannot work out the handle it reserved under (the retired scheme hashed the
        // Sigil, so anyone could), nor the Seal key behind it.
        byte[] fromSigil = [.. "cuprinet/ferryman/handle/v1"u8, .. target.Identity.Sigil.Span];
        Assert.NotEqual(SHA256.HashData(fromSigil)[..RelayRoute.HandleSize], viaA.Handle);
        Assert.NotEqual(FerrymanProtocol.Handle(target.Identity.Seal.PublicKey), viaA.Handle);

        // And two relays comparing notes see two unrelated handles.
        Assert.NotEqual(viaA.Handle, viaB.Handle);

        reserve.Cancel();
        try { await Task.WhenAll(a, b); } catch { /* cancelled */ }
    }

    [Fact]
    public async Task TheHandle_SurvivesARestart_SoLinksAlreadyHandedOutStillWork()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await NodeAsync(relay: true, ct);
        var store = new InMemorySecretStore(); // stands in for the node's disk

        async Task<byte[]> ReserveOnceAsync()
        {
            await using var target = await NodeAsync(relay: false, ct, store);
            using var reserve = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var maintaining = target.MaintainFerrymanReservationAsync(RelayBeacon(relay), cancellationToken: reserve.Token);
            var handle = (await RouteVia(target, relay, ct)).Handle;
            reserve.Cancel();
            try { await maintaining; } catch { /* cancelled */ }
            return handle;
        }

        Assert.Equal(await ReserveOnceAsync(), await ReserveOnceAsync());
    }

    [Fact]
    public async Task AReservation_ReplayedOnAnotherConnection_IsRefused()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await NodeAsync(relay: true, ct);
        var suite = relay.Suite;

        async Task<NoiseVessel> DialAsync()
        {
            var raw = await TcpVessel.ConnectAsync("127.0.0.1", relay.LocalEndPoint.Port, cancellationToken: ct);
            await Toll.SolveAsync(raw, ct);
            var conjunction = await NoiseConjunction.InitiateAsync(raw, NodeIdentity.Generate(suite), relay.Network, suite, cancellationToken: ct);
            await conjunction.Vessel.SendAsync(OverlayControl.Stream, new[] { OverlayControl.KindFerryman }, ct);
            return conjunction.Vessel;
        }

        async Task<byte> StatusOfAsync(NoiseVessel vessel, byte[] reserve)
        {
            await vessel.SendAsync(OverlayControl.Stream, reserve, ct);
            var ack = await vessel.ReceiveAsync(ct);
            Assert.True(FerrymanProtocol.TryReadReserved(ack!.Value.Payload, out var status));
            return status;
        }

        // A genuine reservation, made and accepted on one connection…
        var rendezvous = suite.GenerateSeal();
        var handle = FerrymanProtocol.Handle(rendezvous.PublicKey);
        IReadOnlyList<IPEndPoint> candidates = [new IPEndPoint(IPAddress.Loopback, 40000)];

        await using var first = await DialAsync();
        var signature = suite.CreateSigner(rendezvous.PrivateKey).Sign(FerrymanProtocol.ReserveSubject(first.HandshakeHash.Span, handle, candidates));
        var reserve = FerrymanProtocol.Reserve(handle, candidates, rendezvous.PublicKey, signature);
        Assert.Equal(FerrymanProtocol.StatusOk, await StatusOfAsync(first, reserve));

        // …is worthless on any other: whoever saw it cannot park the target's handle elsewhere with stale addresses.
        await using var second = await DialAsync();
        Assert.Equal(FerrymanProtocol.StatusNotReserved, await StatusOfAsync(second, reserve));
    }

    [Fact]
    public async Task Rendezvous_ForAHandleNobodyReserved_Fails()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var relay = await NodeAsync(relay: true, ct);
        await using var requester = await NodeAsync(relay: false, ct);

        var route = new RelayRoute(relay.Identity.Sigil, "127.0.0.1", relay.LocalEndPoint.Port, RandomNumberGenerator.GetBytes(RelayRoute.HandleSize));
        await Assert.ThrowsAsync<CupriNodeException>(async () => await requester.ConjoinViaFerrymanAsync(
            route, NodeIdentity.Generate(requester.Suite).Sigil, DateTimeOffset.UtcNow, cancellationToken: ct));
    }

    [Fact]
    public async Task Requester_IsAskedAboutTheRelay_BeforeAnyConnectionIsMade()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var requester = await NodeAsync(relay: false, ct);

        // Nothing listens at this address. Were the question asked after dialling, the dial would fail first; that
        // it is reached at all shows a declined relay never hears from us.
        var relaySigil = NodeIdentity.Generate(requester.Suite).Sigil;
        var route = new RelayRoute(relaySigil, "127.0.0.1", 1, RandomNumberGenerator.GetBytes(RelayRoute.HandleSize));

        var seen = default(Sigil);
        await Assert.ThrowsAsync<CupriNodeException>(async () => await requester.ConjoinViaFerrymanAsync(
            route, NodeIdentity.Generate(requester.Suite).Sigil, DateTimeOffset.UtcNow,
            approveRelay: s => { seen = s; return Task.FromResult(false); }, cancellationToken: ct));

        Assert.Equal(relaySigil, seen);
    }

    [Fact]
    public async Task Requester_PinsTheRelayNamedInTheRoute()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await NodeAsync(relay: true, ct);
        await using var requester = await NodeAsync(relay: false, ct);

        // The right address, but the route names a different relay: whoever answers there is not the one approved.
        var someoneElse = NodeIdentity.Generate(requester.Suite).Sigil;
        var route = new RelayRoute(someoneElse, "127.0.0.1", relay.LocalEndPoint.Port, RandomNumberGenerator.GetBytes(RelayRoute.HandleSize));

        await Assert.ThrowsAsync<NoiseConjunctionException>(async () => await requester.ConjoinViaFerrymanAsync(
            route, NodeIdentity.Generate(requester.Suite).Sigil, DateTimeOffset.UtcNow, cancellationToken: ct));
    }

    [Fact]
    public async Task Target_IsAskedAboutTheRelay_BeforeItReserves_AndCanRefuse()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var relay = await NodeAsync(relay: true, ct);
        await using var target = await NodeAsync(relay: false, ct);

        var seen = default(Sigil);
        await Assert.ThrowsAsync<CupriNodeException>(() => target.MaintainFerrymanReservationAsync(
            RelayBeacon(relay), s => { seen = s; return Task.FromResult(false); }, ct));

        Assert.Equal(relay.Identity.Sigil, seen); // it saw the relay's proven identity…
        Assert.Empty(target.RelayRoutes);          // …and nothing was reserved
    }

    [Fact]
    public void RelayRoutes_RoundTripInALink_WithOrWithoutTheBlocksBeforeThem()
    {
        var suite = CryptoSuites.Secure();
        var network = new Concordium("example.chat");
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        IReadOnlyList<RelayRoute> routes =
        [
            new(NodeIdentity.Generate(suite).Sigil, "relay.example.net", 43820, RandomNumberGenerator.GetBytes(RelayRoute.HandleSize)),
            new(NodeIdentity.Generate(suite).Sigil, "203.0.113.9", 4000, RandomNumberGenerator.GetBytes(RelayRoute.HandleSize)),
        ];

        foreach (var shrine in new Sigil?[] { null, Signet.Generate(suite).Sigil })
        {
            var minted = IntonationMint.Intone(NodeIdentity.Generate(suite), suite, new IntonationOptions
            {
                Network = network,
                Beacons = [],
                Shrine = shrine,
                RelayRoutes = routes,
            }, now);

            var validation = IntonationValidator.ValidateDocument(IntonationCodec.Encode(minted), network, suite, now);

            Assert.True(validation.IsValid);
            Assert.Equal(routes, validation.Intonation!.RelayRoutes);
            Assert.Equal(shrine, validation.Intonation.Shrine);
            Assert.Null(validation.Intonation.WebRtc);
        }
    }
}
