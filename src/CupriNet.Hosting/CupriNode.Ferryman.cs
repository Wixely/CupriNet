using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Traversal;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// Ferryman: brokered NAT hole punching. A reachable relay (this node, when <see cref="CupriNodeOptions.EnableFerryman"/>)
/// shuttles two NAT'd peers' punch candidates + a session nonce, then drops out — signaling only, never L2 content.
/// It learns neither peer's Sigil: both connect with a throwaway identity, the requester names the target only by
/// a handle, and the target proves it owns that handle with a rendezvous key it made for this relay alone. What the
/// relay does see is both peers' addresses, and that the two were introduced. The actual connection reuses the
/// same UDP punch + Noise pairing as the mutual-link path; the relay just replaces the out-of-band signaling.
/// See <c>design/ferryman.md</c>.
/// </summary>
public sealed partial class CupriNode
{
    private readonly ConcurrentDictionary<string, FerrymanReservation> _reservations = new();
    private int _ferrymanSessions;

    private sealed record FerrymanReservation(IVessel Vessel, IReadOnlyList<IPEndPoint> Candidates, SemaphoreSlim SendGate);

    // ---- Relay side ----------------------------------------------------------------------------

    /// <summary>Serves an inbound Ferryman session: a RESERVE (a target parks a handle) or a RENDEZVOUS (a requester brokers a punch).</summary>
    private async Task ServeFerrymanAsync(NoiseVessel vessel, CancellationToken cancellationToken)
    {
        // Global concurrent-session cap: a Ward against a public relay being swamped (the requester uses an
        // ephemeral identity, so per-Sigil budgeting is pointless; this bounds total sessions instead).
        if (Interlocked.Increment(ref _ferrymanSessions) > Math.Max(16, _options.MaxFerrymanReservations * 2))
        {
            Interlocked.Decrement(ref _ferrymanSessions);
            await vessel.DisposeAsync().ConfigureAwait(false);
            return;
        }

        string? reservedKey = null;
        try
        {
            var first = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (first is null || first.Value.StreamId != OverlayControl.Stream || first.Value.Payload.Length == 0)
                return;
            var payload = first.Value.Payload;

            if (FerrymanProtocol.TypeOf(payload) == FerrymanProtocol.MsgReserve
                && FerrymanProtocol.TryReadReserve(payload, out var handle, out var candidates, out var rendezvousKey, out var signature))
            {
                // Anti-squat: the reserver must PROVE it holds the key whose hash is the handle. Anti-replay: the
                // proof is over THIS connection's handshake hash, so a reservation lifted from one connection — by
                // a relay that once saw it, say — is worthless on any other.
                var subject = FerrymanProtocol.ReserveSubject(vessel.HandshakeHash.Span, handle, candidates);
                var authentic = Suite.Verifier.Verify(subject, signature, rendezvousKey)
                                && FerrymanProtocol.Handle(rendezvousKey).AsSpan().SequenceEqual(handle);
                reservedKey = Convert.ToHexStringLower(handle);
                if (!authentic
                    || (!_reservations.ContainsKey(reservedKey) && _reservations.Count >= _options.MaxFerrymanReservations))
                {
                    await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Reserved(FerrymanProtocol.StatusNotReserved), cancellationToken).ConfigureAwait(false);
                    reservedKey = null;
                    return;
                }
                // Only the key-holder can produce a valid RESERVE for this handle, so overwriting is safe (it's a refresh).
                _reservations[reservedKey] = new FerrymanReservation(vessel, candidates, new SemaphoreSlim(1, 1));
                await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Reserved(FerrymanProtocol.StatusOk), cancellationToken).ConfigureAwait(false);

                // Hold the session open: notifications are pushed on this vessel; it ends when the target disconnects.
                while (await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                }
                return;
            }

            if (FerrymanProtocol.TypeOf(payload) == FerrymanProtocol.MsgRendezvous
                && FerrymanProtocol.TryReadRendezvous(payload, out var wanted, out var requesterCandidates))
            {
                var key = Convert.ToHexStringLower(wanted);
                if (_reservations.TryGetValue(key, out var reservation))
                {
                    var nonce = RandomNumberGenerator.GetBytes(FerrymanProtocol.NonceSize);
                    // Push the requester's candidates to the parked target; serialize sends so concurrent rendezvous
                    // for one target can't interleave frames on its vessel. The relay only forwards signaling.
                    var pushed = false;
                    await reservation.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try { await reservation.Vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Notify(nonce, requesterCandidates), cancellationToken).ConfigureAwait(false); pushed = true; }
                    catch { /* target vanished between lookup and push */ }
                    finally { reservation.SendGate.Release(); }

                    var status = pushed ? FerrymanProtocol.StatusOk : FerrymanProtocol.StatusNotReserved;
                    await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Offer(status, nonce, pushed ? reservation.Candidates : []), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Offer(FerrymanProtocol.StatusNotReserved, new byte[FerrymanProtocol.NonceSize], []), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch { /* connection dropped or malformed — nothing to serve */ }
        finally
        {
            // Only remove the reservation if it's still ours (a fresh RESERVE by the same key may have replaced it).
            if (reservedKey is not null
                && _reservations.TryGetValue(reservedKey, out var mine) && ReferenceEquals(mine.Vessel, vessel))
                _reservations.TryRemove(new KeyValuePair<string, FerrymanReservation>(reservedKey, mine));
            Interlocked.Decrement(ref _ferrymanSessions);
            await vessel.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- Target side (D): keep a reservation live, accept brokered incomings --------------------

    private readonly ConcurrentDictionary<Sigil, SealKeyPair> _rendezvousKeys = new();
    private readonly ConcurrentDictionary<Sigil, RelayRoute> _relayRoutes = new();

    /// <summary>
    /// The relays this node currently holds a reservation with, as routes a peer can use to reach it. Every link
    /// minted while a route is live carries it (see <see cref="Intone(TimeSpan, DateTimeOffset, byte[])"/>).
    /// </summary>
    public IReadOnlyList<RelayRoute> RelayRoutes => [.. _relayRoutes.Values.OrderBy(r => r.Host, StringComparer.Ordinal).ThenBy(r => r.Port)];

    /// <summary>Raised when a reservation is first confirmed and its route becomes available to put in a link.</summary>
    public event Action<RelayRoute>? RelayRouteReady;

    /// <summary>The routes to stamp into a link of the given transport class: none where there is no clearnet path.</summary>
    private IReadOnlyList<RelayRoute> RelayRoutesToAdvertise(LinkTransports transports)
        => _options.Mode == ReachabilityMode.TorOnly || transports == LinkTransports.OnionOnly
            ? []
            : [.. RelayRoutes.Take(RelayRouteCodec.MaxRoutes)];

    /// <summary>
    /// Keeps a reservation live with a Ferryman so peers who cannot dial this node directly can reach it: reserve,
    /// wait for a broker NOTIFY, hole-punch the requester, and surface the paired peer through the normal
    /// <see cref="AcceptAsync"/> queue. Runs until cancelled, re-reserving after each brokered connection (the punch
    /// consumes the socket). While it runs, the relay appears in <see cref="RelayRoutes"/> and in the links this
    /// node mints.
    ///
    /// <para>The relay is not told who this node is. It connects with a throwaway identity and reserves under a
    /// handle derived from a rendezvous key kept for that relay alone — the relay sees the handle, this node's
    /// address, and who asks for it, never its Sigil. <paramref name="approveRelay"/>, when given, is asked once
    /// the relay has proven its identity and before anything is reserved; it is asked again only if the relay at
    /// that address later presents a different key. Refusing ends the call with a <see cref="CupriNodeException"/>.</para>
    /// </summary>
    public async Task MaintainFerrymanReservationAsync(
        Beacon relayBeacon, Func<Sigil, Task<bool>>? approveRelay = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relayBeacon);
        EnsureClearnetPermitted("hold a relay reservation");

        var session = new ReservationSession(relayBeacon, approveRelay);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var approved = true;
                try { approved = await ReserveOnceAsync(session, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch { /* relay down / transient — back off and retry */ }
                if (!approved)
                    throw new CupriNodeException("The relay was not approved.");

                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
                catch { break; }
            }
        }
        finally
        {
            if (session.Route is { } route)
                _relayRoutes.TryRemove(new KeyValuePair<Sigil, RelayRoute>(route.RelaySigil, route));
        }
    }

    /// <summary>What one <see cref="MaintainFerrymanReservationAsync"/> call remembers between reservations.</summary>
    private sealed class ReservationSession(Beacon relay, Func<Sigil, Task<bool>>? approve)
    {
        public Beacon Relay { get; } = relay;
        public Func<Sigil, Task<bool>>? Approve { get; } = approve;
        public Sigil? Approved { get; set; }
        public RelayRoute? Route { get; set; }
    }

    /// <summary>Reserves once and waits for one brokered connection. False means the relay was not approved.</summary>
    private async Task<bool> ReserveOnceAsync(ReservationSession session, CancellationToken cancellationToken)
    {
        var punch = NatTraversal.BindSocket(new IPEndPoint(IPAddress.Any, 0));
        var handedOff = false;
        try
        {
            var candidates = PunchCandidatesFor(((IPEndPoint)punch.LocalEndPoint!).Port);
            var (vessel, relaySigil) = await DialFerrymanAsync(session.Relay, expectedRelay: null, cancellationToken).ConfigureAwait(false);
            try
            {
                // The relay has proven its key. Ask once per key: a relay that turns up with a different one is a
                // different relay as far as trust goes.
                if (session.Approve is not null && session.Approved != relaySigil)
                {
                    if (!await session.Approve(relaySigil).ConfigureAwait(false))
                        return false;
                    session.Approved = relaySigil;
                }

                // Prove we own the handle with the rendezvous key for this relay — not our Seal, which would tell
                // the relay exactly who we are — over this connection's handshake hash, so it holds here only.
                var rendezvous = await RendezvousKeyForAsync(relaySigil, cancellationToken).ConfigureAwait(false);
                var handle = FerrymanProtocol.Handle(rendezvous.PublicKey);
                var subject = FerrymanProtocol.ReserveSubject(vessel.HandshakeHash.Span, handle, candidates);
                var signature = Suite.CreateSigner(rendezvous.PrivateKey).Sign(subject);
                await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Reserve(handle, candidates, rendezvous.PublicKey, signature), cancellationToken).ConfigureAwait(false);

                var ack = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (ack is null || !FerrymanProtocol.TryReadReserved(ack.Value.Payload, out var status) || status != FerrymanProtocol.StatusOk)
                    return true; // reservation refused (relay full / rejected) — the outer loop backs off and retries

                PublishRoute(session, new RelayRoute(relaySigil, session.Relay.Host, session.Relay.Port, handle));

                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    if (frame is null)
                        break;
                    if (frame.Value.StreamId == OverlayControl.Stream
                        && FerrymanProtocol.TryReadNotify(frame.Value.Payload, out var nonce, out var requesterCandidates))
                    {
                        var socket = punch;
                        handedOff = true; // the punch task now owns the socket
                        _ = Task.Run(() => AcceptBrokeredAsync(socket, nonce, requesterCandidates, cancellationToken), cancellationToken);
                        return true; // one brokered connection per socket; the outer loop re-reserves with a fresh one
                    }
                }
                return true;
            }
            finally { await vessel.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            if (!handedOff)
                punch.Dispose();
        }
    }

    /// <summary>Records a confirmed route so links carry it, announcing it the first time (or when it changes).</summary>
    private void PublishRoute(ReservationSession session, RelayRoute route)
    {
        var changed = session.Route is null || !session.Route.Equals(route);
        if (changed && session.Route is { } stale)
            _relayRoutes.TryRemove(new KeyValuePair<Sigil, RelayRoute>(stale.RelaySigil, stale));
        session.Route = route;
        _relayRoutes[route.RelaySigil] = route;
        if (changed)
            RelayRouteReady?.Invoke(route);
    }

    /// <summary>
    /// The rendezvous key this node uses with one relay: made on first use, kept in the secret store so the handle
    /// — and with it every link already handed out — survives a restart. One key per relay, so two relays cannot
    /// tell they serve the same node by comparing handles.
    /// </summary>
    private async Task<SealKeyPair> RendezvousKeyForAsync(Sigil relay, CancellationToken cancellationToken)
    {
        if (_rendezvousKeys.TryGetValue(relay, out var cached))
            return cached;

        var storeKey = "ferryman/rendezvous/" + Convert.ToHexStringLower(relay.Span);
        var stored = await _secretStore.LoadAsync(storeKey, cancellationToken).ConfigureAwait(false);
        if (stored is null || !TryDecodeKeyPair(stored, out var pair))
        {
            pair = Suite.GenerateSeal();
            var w = new CodexWriter();
            w.WriteBytes(pair.PrivateKey);
            w.WriteBytes(pair.PublicKey);
            await _secretStore.StoreAsync(storeKey, w.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        return _rendezvousKeys.GetOrAdd(relay, pair);
    }

    private static bool TryDecodeKeyPair(byte[] stored, out SealKeyPair pair)
    {
        pair = default;
        try
        {
            var r = new CodexReader(stored);
            var privateKey = r.ReadBytes().ToArray();
            var publicKey = r.ReadBytes().ToArray();
            if (privateKey.Length == 0 || publicKey.Length == 0)
                return false;
            pair = new SealKeyPair(privateKey, publicKey);
            return true;
        }
        catch (CodexFormatException) { return false; }
    }

    private async Task AcceptBrokeredAsync(Socket socket, byte[] nonce, IReadOnlyList<IPEndPoint> requesterCandidates, CancellationToken cancellationToken)
    {
        try
        {
            var vessel = await NatTraversal.PunchAndConnectAsync(
                nonce, socket, requesterCandidates, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            var peer = await AcceptChannelOverVesselAsync(vessel, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            if (!_accepted.Writer.TryWrite(peer))
                await peer.DisposeAsync().ConfigureAwait(false);
        }
        catch { socket.Dispose(); }
    }

    // ---- Requester side (E): reach a target through its Ferryman --------------------------------

    /// <summary>
    /// Reaches <paramref name="targetSigil"/> — a peer only reachable via a relay — by brokering a hole punch along
    /// <paramref name="route"/>, taken from the target's link. <paramref name="approveRelay"/>, when given, is asked
    /// about the relay named in the route <b>before any connection is made</b>, so a relay the user declines never
    /// learns this node's address. The relay is then dialled with a throwaway identity and pinned to that Sigil,
    /// asked for the route's handle, and — once the punch lands — dropped; the pairing that follows is direct and
    /// pinned to the target. The relay carries no content and never learns either peer's Sigil.
    /// </summary>
    public async Task<PairedPeer> ConjoinViaFerrymanAsync(
        RelayRoute route, Sigil targetSigil, DateTimeOffset now,
        Func<Sigil, Task<bool>>? approveRelay = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        EnsureClearnetPermitted("broker a hole punch through a relay");

        if (approveRelay is not null && !await approveRelay(route.RelaySigil).ConfigureAwait(false))
            throw new CupriNodeException("The relay was not approved.");

        var punch = NatTraversal.BindSocket(new IPEndPoint(IPAddress.Any, 0));
        var consumed = false;
        try
        {
            var candidates = PunchCandidatesFor(((IPEndPoint)punch.LocalEndPoint!).Port);
            var relayDial = new Beacon(EndpointKind.Manual, route.Host, route.Port);
            var (vessel, _) = await DialFerrymanAsync(relayDial, route.RelaySigil, cancellationToken).ConfigureAwait(false);
            try
            {
                await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Rendezvous(route.Handle, candidates), cancellationToken).ConfigureAwait(false);
                var offer = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                            ?? throw new CupriNodeException("The relay closed during rendezvous.");
                if (!FerrymanProtocol.TryReadOffer(offer.Payload, out var status, out var nonce, out var targetCandidates)
                    || status != FerrymanProtocol.StatusOk)
                    throw new CupriNodeException("The target is not reachable through this relay (no reservation).");

                var punched = await NatTraversal.PunchAndConnectAsync(
                    nonce, punch, targetCandidates, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                consumed = true; // the socket is now the data path
                return await ConjoinOverVesselAsync(punched, targetSigil, now, cancellationToken).ConfigureAwait(false);
            }
            finally { await vessel.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            if (!consumed)
                punch.Dispose();
        }
    }

    // ---- Shared helpers ------------------------------------------------------------------------

    /// <summary>
    /// Dials a relay and runs a Noise handshake with an EPHEMERAL identity, then declares the Ferryman session kind.
    /// With <paramref name="expectedRelay"/> the handshake is pinned: only that relay can answer.
    /// </summary>
    private async Task<(NoiseVessel Vessel, Sigil RelaySigil)> DialFerrymanAsync(Beacon relayBeacon, Sigil? expectedRelay, CancellationToken cancellationToken)
    {
        var raw = await DialTcpAsync(relayBeacon, cancellationToken).ConfigureAwait(false);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            var ephemeral = NodeIdentity.Generate(Suite); // the relay never learns our real Sigil
            if (_options.EnableToll)
                await Toll.SolveAsync(raw, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.InitiateAsync(raw, ephemeral, Network, Suite, expectedPeer: expectedRelay, cancellationToken: timed.Token).ConfigureAwait(false);
            await DeclareSessionKindAsync(conjunction.Vessel, OverlayControl.KindFerryman, timed.Token).ConfigureAwait(false);
            return (conjunction.Vessel, conjunction.PeerSigil);
        }
        catch
        {
            await raw.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Punch candidates for a bound UDP socket: loopback (same-host) plus this node's reachable IP beacons, at the punch port.</summary>
    private List<IPEndPoint> PunchCandidatesFor(int punchPort)
    {
        var ips = new List<IPAddress> { IPAddress.Loopback };
        foreach (var beacon in SelfBeacons())
            if (beacon.Kind is EndpointKind.Host or EndpointKind.Mapped or EndpointKind.Manual
                && IPAddress.TryParse(beacon.Host, out var ip))
                ips.Add(ip);
        return ips.Distinct().Select(ip => new IPEndPoint(ip, punchPort)).ToList();
    }
}
