using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Traversal;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// Ferryman: brokered NAT hole punching. A reachable relay (this node, when <see cref="CupriNodeOptions.EnableFerryman"/>)
/// shuttles two NAT'd peers' punch candidates + a session nonce, then drops out — signaling only, never L2 content,
/// and it never learns either peer's real Sigil (both connect with an ephemeral key). The actual connection reuses
/// the same UDP punch + Noise pairing as the mutual-link path; the relay just replaces the out-of-band signaling.
/// See <c>design/ferryman.md</c>.
/// </summary>
public sealed partial class CupriNode
{
    private readonly ConcurrentDictionary<string, FerrymanReservation> _reservations = new();
    private int _ferrymanSessions;

    private sealed record FerrymanReservation(IVessel Vessel, IReadOnlyList<IPEndPoint> Candidates, SemaphoreSlim SendGate);

    // ---- Relay side ----------------------------------------------------------------------------

    /// <summary>Serves an inbound Ferryman session: a RESERVE (a target parks a handle) or a RENDEZVOUS (a requester brokers a punch).</summary>
    private async Task ServeFerrymanAsync(IVessel vessel, CancellationToken cancellationToken)
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

            if (payload[0] == FerrymanProtocol.MsgReserve
                && FerrymanProtocol.TryReadReserve(payload, out var handle, out var candidates, out var sealPublicKey, out var signature))
            {
                // Anti-squat: the reserver must PROVE it holds the key whose hash is the handle. Without this,
                // anyone could reserve hash(victimSigil) — a public value — and hijack a target's handle.
                var subject = FerrymanProtocol.ReserveSubject(handle, candidates);
                var authentic = Suite.Verifier.Verify(subject, signature, sealPublicKey)
                                && FerrymanProtocol.Handle(Sigil.FromSealPublicKey(sealPublicKey)).AsSpan().SequenceEqual(handle);
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

                // Hold the connection open so the rendezvous handler can push a NOTIFY; a null frame means the target left.
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    if (frame is null)
                        break;
                }
                return;
            }

            if (payload[0] == FerrymanProtocol.MsgRendezvous
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

    /// <summary>
    /// Keeps a reservation live with a Ferryman so peers who only have a <see cref="EndpointKind.Relay"/> beacon can
    /// reach this node: reserve, wait for a broker NOTIFY, hole-punch the requester, and surface the paired peer
    /// through the normal <see cref="AcceptAsync"/> queue. Runs until cancelled, re-reserving after each brokered
    /// connection (the punch consumes the socket).
    /// </summary>
    public async Task MaintainFerrymanReservationAsync(Beacon relayBeacon, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relayBeacon);
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await ReserveOnceAsync(relayBeacon, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { /* relay down / transient — back off and retry */ }
            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch { break; }
        }
    }

    private async Task ReserveOnceAsync(Beacon relayBeacon, CancellationToken cancellationToken)
    {
        var punch = NatTraversal.BindSocket(new IPEndPoint(IPAddress.Any, 0));
        var handedOff = false;
        try
        {
            var candidates = PunchCandidatesFor(((IPEndPoint)punch.LocalEndPoint!).Port);
            var handle = FerrymanProtocol.Handle(Identity.Sigil);
            var (vessel, _) = await DialFerrymanAsync(relayBeacon, cancellationToken).ConfigureAwait(false);
            try
            {
                // Sign the reservation with our real Seal so the relay can verify we own the handle (anti-squat).
                var subject = FerrymanProtocol.ReserveSubject(handle, candidates);
                var signature = Suite.CreateSigner(Identity.Seal.PrivateKey).Sign(subject);
                await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Reserve(handle, candidates, Identity.Seal.PublicKey, signature), cancellationToken).ConfigureAwait(false);

                var ack = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (ack is null || ack.Value.Payload.Length < 2
                    || ack.Value.Payload[0] != FerrymanProtocol.MsgReserved || ack.Value.Payload[1] != FerrymanProtocol.StatusOk)
                    return; // reservation refused (relay full / rejected) — the outer loop backs off and retries elsewhere

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
                        return; // one brokered connection per socket; the outer loop re-reserves with a fresh one
                    }
                }
            }
            finally { await vessel.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            if (!handedOff)
                punch.Dispose();
        }
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
    /// Reaches <paramref name="targetSigil"/> — a peer only reachable via a relay — by brokering a hole punch through
    /// <paramref name="relayBeacon"/>. Connects to the relay with an ephemeral identity (so the relay never learns
    /// this node's Sigil), optionally lets <paramref name="approveRelay"/> gate the relay by its (TOFU) key, exchanges
    /// candidates, punches, and completes the direct, Sigil-pinned pairing with the target. The relay carries no content.
    /// </summary>
    public async Task<PairedPeer> ConjoinViaFerrymanAsync(
        Beacon relayBeacon, Sigil targetSigil, DateTimeOffset now,
        Func<Sigil, Task<bool>>? approveRelay = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relayBeacon);

        var punch = NatTraversal.BindSocket(new IPEndPoint(IPAddress.Any, 0));
        var consumed = false;
        try
        {
            var candidates = PunchCandidatesFor(((IPEndPoint)punch.LocalEndPoint!).Port);
            var handle = FerrymanProtocol.Handle(targetSigil);
            var (vessel, relaySigil) = await DialFerrymanAsync(relayBeacon, cancellationToken).ConfigureAwait(false);
            try
            {
                if (approveRelay is not null && !await approveRelay(relaySigil).ConfigureAwait(false))
                    throw new CupriNodeException("The relay was not approved.");

                await vessel.SendAsync(OverlayControl.Stream, FerrymanProtocol.Rendezvous(handle, candidates), cancellationToken).ConfigureAwait(false);
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

    /// <summary>Dials a relay and runs a Noise handshake with an EPHEMERAL identity, then declares the Ferryman session kind.</summary>
    private async Task<(IVessel Vessel, Sigil RelaySigil)> DialFerrymanAsync(Beacon relayBeacon, CancellationToken cancellationToken)
    {
        var raw = await DialTcpAsync(relayBeacon, cancellationToken).ConfigureAwait(false);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            var ephemeral = NodeIdentity.Generate(Suite); // the relay never learns our real Sigil
            if (_options.EnableToll)
                await Toll.SolveAsync(raw, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.InitiateAsync(raw, ephemeral, Network, Suite, expectedPeer: null, cancellationToken: timed.Token).ConfigureAwait(false);
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
