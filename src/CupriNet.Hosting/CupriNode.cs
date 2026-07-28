using System.Net;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Arcanum;
using CupriNet.Concordance;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Persistence;
using CupriNet.Rites;
using CupriNet.Traversal;
using CupriNet.Vessel;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Hosting;

/// <summary>Thrown when a node operation fails (invalid Intonation, no dialable endpoint, etc.).</summary>
public sealed class CupriNodeException(string message) : Exception(message);

/// <summary>
/// The public entry point to CupriNet: one object that ties identity + persistence, the Vessel transport,
/// Conjunction pairing, the Concordance overlay, and the Arcanum/rite layers together. Mint an Intonation
/// to invite a peer, Conjoin to an Intonation to pair, then Consecrate a channel to exchange messages.
/// </summary>
public sealed partial class CupriNode : IAsyncDisposable
{
    /// <summary>Maximum time a transport handshake (Noise + identity binding + reflexion) may take.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly CupriNodeOptions _options;
    private readonly VesselListener _listener;
    private readonly string _advertiseHost;
    private readonly byte[] _tollSecret = Toll.NewSecret();
    private readonly ISecretStore _secretStore;
    private readonly CancellationTokenSource _lifetime = new();

    private CupriNode(CupriNodeOptions options, ICryptoSuite suite, NodeIdentity identity, VesselListener listener, ISecretStore secretStore)
    {
        _options = options;
        Suite = suite;
        Identity = identity;
        _listener = listener;
        _secretStore = secretStore;
        Network = new Concordium(options.Concordium);
        Constellation = new Constellation();
        _advertiseHost = options.ListenAddress.Equals(IPAddress.Any) ? "127.0.0.1" : options.ListenAddress.ToString();
    }

    /// <summary>This node's long-term identity.</summary>
    public NodeIdentity Identity { get; }

    /// <summary>The network this node belongs to.</summary>
    public Concordium Network { get; }

    /// <summary>The crypto suite in use.</summary>
    public ICryptoSuite Suite { get; }

    /// <summary>This node's view of the overlay.</summary>
    public Constellation Constellation { get; }

    /// <summary>Reflexive-endpoint observations gathered from peers during pairing.</summary>
    public ReflexiveObserver ReflexiveObserver { get; } = new();

    /// <summary>The bound local endpoint (reflects the OS-assigned port when 0 was requested).</summary>
    public IPEndPoint LocalEndPoint => _listener.LocalEndPoint;

    /// <summary>Creates and starts a node: sets up crypto and persistence, loads/creates identity, starts listening.</summary>
    public static async Task<CupriNode> CreateAsync(CupriNodeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Concordium);

        // Cold-start decision, baked in: Tor needs entry guards that survive restart (reselecting them each run
        // is a deanonymization risk), so it is incompatible with a cold start (no/in-memory store).
        if (options.OnionTransport is not null && options.SecretStore is null or InMemorySecretStore)
            throw new CupriNodeException(
                "Tor (OnionTransport) requires a durable SecretStore: entry guards must survive restart, and " +
                "reselecting them each run deanonymizes you. Provide a persistent SecretStore, or disable Tor.");

        // Strict Tor-only mode needs an onion transport — there is no clearnet path to fall back to.
        if (options.Mode == ReachabilityMode.TorOnly && options.OnionTransport is null)
            throw new CupriNodeException("ReachabilityMode.TorOnly requires an OnionTransport (there is no clearnet path in this mode).");

        var suite = options.Suite ?? new BouncyCastleSuite();
        var secretStore = options.SecretStore ?? new InMemorySecretStore();
        var identity = await new IdentityStore(secretStore).LoadOrCreateAsync(suite, cancellationToken).ConfigureAwait(false);

        // Tor-only: bind the listener to loopback so inbound arrives ONLY via the local onion reverse-proxy —
        // never directly on a clearnet IP (which would make the node reachable on both, correlating the identity).
        var bindAddress = options.Mode == ReachabilityMode.TorOnly ? IPAddress.Loopback : options.ListenAddress;
        var listener = new VesselListener(new IPEndPoint(bindAddress, options.ListenPort));
        listener.Start();

        var node = new CupriNode(options, suite, identity, listener, secretStore);

        // Warm start: rehydrate the overlay from the local cache so we can reconnect to known nodes directly,
        // avoiding cold-start hops. Opt-in — a cold start (this off) keeps nothing about the overlay on disk.
        if (options.PersistOverlay)
        {
            await node.LoadOverlayStateAsync(secretStore, cancellationToken).ConfigureAwait(false);
            await node.LoadPageantsAsync(secretStore, cancellationToken).ConfigureAwait(false);
        }

        node.StartGossip();
        node.StartHotFuzz();
        node.StartEffigies();
        node.StartPageants();
        node.StartLanDiscovery();
        node.StartPortMapping();
        node.StartTor();
        return node;
    }

    /// <summary>Mints a fresh connection URL (Intonation) advertising this node's reachability and seed peers.</summary>
    public Intonation Intone(TimeSpan lifetime, DateTimeOffset now, byte[]? petition = null)
    {
        List<Beacon> beacons;
        if (_options.Mode == ReachabilityMode.TorOnly)
        {
            // Anonymity: advertise ONLY the onion address — never a clearnet beacon that would tie it to an IP.
            beacons = _onionBeacon is not null ? [_onionBeacon] : [];
        }
        else
        {
            beacons = new List<Beacon>(_options.AdvertisedBeacons ?? [new Beacon(EndpointKind.Host, _advertiseHost, LocalEndPoint.Port)]);
            // Include externally-reachable candidates: reflexive address, any NAT-PMP-mapped port, and our onion.
            foreach (var mapped in new[] { ReflexiveObserver.MappedBeacon(), _mappedBeacon, _onionBeacon })
                if (mapped is not null && !beacons.Any(b => b.Kind == mapped.Kind && b.Host == mapped.Host && b.Port == mapped.Port))
                    beacons.Add(mapped);
        }
        var litany = Constellation.Sample(IntonationCodec.MaxLitany).Select(r => r.Sigil).ToList();

        return IntonationMint.Intone(Identity, Suite, new IntonationOptions
        {
            Network = Network,
            Beacons = beacons,
            Litany = litany,
            Lifetime = lifetime,
            Petition = petition,
        }, now);
    }

    /// <summary>Renders an Intonation as its <c>cuprinet://intone/…</c> URL.</summary>
    public string IntoneUri(TimeSpan lifetime, DateTimeOffset now, byte[]? petition = null)
        => IntonationUri.ToUri(Intone(lifetime, now, petition));

    /// <summary>Validates an Intonation, dials one of its beacons, and completes the Conjunction pairing.</summary>
    public async Task<PairedPeer> ConjoinAsync(Intonation intonation, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intonation);

        var validation = IntonationValidator.ValidateDocument(IntonationCodec.Encode(intonation), Network, Suite, now);
        if (!validation.IsValid)
            throw new CupriNodeException($"Intonation is not usable: {validation.Status}.");

        var torOnly = _options.Mode == ReachabilityMode.TorOnly;
        // Tor-only never dials clearnet — a direct connection would expose this identity's IP.
        var clearnet = torOnly ? [] : DialableInPriorityOrder(intonation.Beacons); // Host/Mapped/Manual
        var onion = intonation.Beacons.FirstOrDefault(b => b.Kind == EndpointKind.Onion);

        // Refuse a clearnet-only invitation up front in Tor-only mode, with a clear reason.
        if (torOnly && onion is null)
            throw new CupriNodeException(
                "You are in Tor-only mode, but this invitation is reachable only over clearnet (a direct address). " +
                "Connecting would expose your IP, so it was refused.");

        Exception? last = null;

        // Clearnet candidates first (fast); the onion path is the slower fallback.
        foreach (var beacon in clearnet)
        {
            IVessel vessel;
            try { vessel = await DialTcpAsync(beacon, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { last = ex; continue; } // candidate unreachable — try the next
            try { return await ConjoinOverVesselAsync(vessel, intonation.InviterSigil, now, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { last = ex; } // the seam disposed the vessel; try the next candidate
        }

        // Tor path, if this node has an onion transport and the invitation offers an onion address.
        if (onion is not null && _onion is not null)
        {
            try { return await ConjoinViaOnionAsync(onion.Host, intonation.InviterSigil, now, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { last = ex; }
        }

        // Nothing connected — explain precisely why, so the user knows what to do.
        if (clearnet.Count == 0 && onion is not null && _onion is null)
            throw new CupriNodeException(
                "This invitation is reachable only over Tor (its link contains an onion address), but Tor is not " +
                "enabled on this node. Turn on Tor to connect to this peer.");
        if (clearnet.Count == 0 && onion is null)
            throw new CupriNodeException("Intonation has no dialable beacon.");
        throw new CupriNodeException($"Could not reach the inviter via any candidate: {last?.Message ?? "no reachable beacon"}.");
    }

    /// <summary>Orders a peer's beacons for dialing: LAN/host first (fastest when reachable), then external Mapped, then Manual.</summary>
    private static IReadOnlyList<Beacon> DialableInPriorityOrder(IEnumerable<Beacon> beacons)
    {
        static int Rank(EndpointKind kind) => kind switch
        {
            EndpointKind.Host => 0,
            EndpointKind.Mapped => 1,
            EndpointKind.Manual => 2,
            _ => 3,
        };
        return beacons.Where(b => b.Kind is EndpointKind.Host or EndpointKind.Mapped or EndpointKind.Manual)
            .OrderBy(b => Rank(b.Kind)).ToList();
    }

    /// <summary>Dials a beacon over TCP with a bounded connect timeout, so an unreachable (e.g. off-LAN private) candidate fails fast.</summary>
    private async Task<IVessel> DialTcpAsync(Beacon beacon, CancellationToken cancellationToken)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timed.CancelAfter(_options.CandidateConnectTimeout);
        return await TcpVessel.ConnectAsync(beacon.Host, beacon.Port, cancellationToken: timed.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes an outbound channel pairing over an already-connected transport <see cref="IVessel"/> — the Toll,
    /// the Noise handshake (pinned to <paramref name="expectedPeer"/>), reflexive learning, invitation anchoring,
    /// and the overlay bootstrap — independent of <em>how</em> the Vessel was established (TCP, hole-punched UDP,
    /// LAN, relay, …). Transports need only produce a connected Vessel; this is everything above them, and it is
    /// the single seam every reachability path plugs into. On failure the Vessel is disposed.
    /// </summary>
    public async Task<PairedPeer> ConjoinOverVesselAsync(IVessel vessel, Sigil expectedPeer, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.SolveAsync(vessel, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.InitiateAsync(
                vessel, Identity, Network, Suite, expectedPeer: expectedPeer, cancellationToken: timed.Token).ConfigureAwait(false);
            await DeclareSessionKindAsync(conjunction.Vessel, OverlayControl.KindChannel, timed.Token).ConfigureAwait(false);
            await LearnReflexiveAsync(conjunction.Vessel, conjunction.PeerSigil, initiator: true, cancellationToken).ConfigureAwait(false);
            // We reached this peer through an invitation/known-peer relationship, so anchor it.
            Constellation.MarkAnchored(conjunction.PeerSigil);
            await BootstrapOverlayAsync(conjunction.Vessel, initiator: true, now, cancellationToken).ConfigureAwait(false);
            return new PairedPeer(conjunction.Vessel, conjunction.PeerSigil, conjunction.PeerSealPublicKey, isInitiator: true);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The responder counterpart to <see cref="ConjoinOverVesselAsync"/>: completes an <em>inbound</em> channel
    /// pairing over an already-connected transport Vessel (Toll verification, Noise accept, reflexive learning,
    /// overlay bootstrap). Used when a Vessel was produced out-of-band — e.g. both sides of a hole-punched UDP
    /// path — rather than accepted from the TCP listener. On failure the Vessel is disposed.
    /// </summary>
    public async Task<PairedPeer> AcceptChannelOverVesselAsync(IVessel vessel, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.IssueAndVerifyAsync(vessel, _tollSecret, vessel.RemoteEndPoint, DateTimeOffset.UtcNow, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.AcceptAsync(vessel, Identity, Network, Suite, timed.Token).ConfigureAwait(false);
            var kind = await ReadSessionKindAsync(conjunction.Vessel, timed.Token).ConfigureAwait(false);
            if (kind != OverlayControl.KindChannel)
                throw new CupriNodeException("Expected a channel session over this vessel.");
            await LearnReflexiveAsync(conjunction.Vessel, conjunction.PeerSigil, initiator: false, cancellationToken).ConfigureAwait(false);
            await BootstrapOverlayAsync(conjunction.Vessel, initiator: false, now, cancellationToken).ConfigureAwait(false);
            return new PairedPeer(conjunction.Vessel, conjunction.PeerSigil, conjunction.PeerSealPublicKey, isInitiator: false);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Re-dials a previously trusted peer directly from its cached beacons — no fresh Intonation — pinning
    /// its <see cref="KnownPeer.Sigil"/> so any other identity answering at that address is rejected. Tries
    /// each dialable beacon in turn. This is the fast path back to contacts we have already Consecrated with.
    /// </summary>
    public async Task<PairedPeer> ReconnectAsync(KnownPeer peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        Exception? last = null;

        if (_options.Mode == ReachabilityMode.TorOnly)
        {
            // Onion-only reconnect: dial the peer's .onion, never a clearnet beacon.
            var onion = peer.Beacons.FirstOrDefault(b => b.Kind == EndpointKind.Onion)
                        ?? throw new CupriNodeException("This trusted peer has no onion address; it can't be reached in Tor-only mode.");
            return await ConjoinViaOnionAsync(onion.Host, peer.Sigil, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        var dialable = DialableInPriorityOrder(peer.Beacons);
        if (dialable.Count == 0)
            throw new CupriNodeException("Known peer has no dialable beacon.");

        foreach (var beacon in dialable)
        {
            IVessel vessel;
            try { vessel = await DialTcpAsync(beacon, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { last = ex; continue; }

            // ConjoinOverVesselAsync disposes the vessel on failure, so we just try the next beacon.
            try { return await ConjoinOverVesselAsync(vessel, peer.Sigil, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { last = ex; }
        }

        throw new CupriNodeException($"Could not reconnect to the trusted peer: {last?.Message ?? "all beacons unreachable"}.");
    }

    /// <summary>
    /// Accepts inbound connections and returns the next one that wants a channel. Inbound <em>overlay
    /// control</em> connections (DHT discovery) are served internally and transparently — the caller only
    /// ever receives channel peers, so an app's accept loop doubles as the node's overlay-serving loop.
    /// </summary>
    public async Task<PairedPeer> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vessel = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);

            NoiseConjunctionResult conjunction;
            byte kind;
            try
            {
                using var timed = LinkedHandshakeToken(cancellationToken);
                // Issue and verify the pre-handshake Toll before allocating any Noise state (anti-exhaustion).
                if (_options.EnableToll)
                    await Toll.IssueAndVerifyAsync(vessel, _tollSecret, vessel.RemoteEndPoint, DateTimeOffset.UtcNow, timed.Token).ConfigureAwait(false);
                conjunction = await NoiseConjunction.AcceptAsync(vessel, Identity, Network, Suite, timed.Token).ConfigureAwait(false);
                kind = await ReadSessionKindAsync(conjunction.Vessel, timed.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await vessel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch
            {
                // A single failed handshake must not tear down the accept loop; skip and keep serving.
                await vessel.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (kind == OverlayControl.KindControl)
            {
                var served = conjunction.Vessel;
                var peerSigil = conjunction.PeerSigil;
                var budget = _peerBudgets.GetOrAdd(peerSigil, _ =>
                    new PeerControlBudget(_options.MaxControlRequestsPerWindow, _options.ControlWindowSeconds * 1000L));

                // Ward: global cap, then per-peer connection cap — one peer cannot flood or multiply its budget.
                if (Interlocked.Increment(ref _activeControlConnections) > _options.MaxConcurrentControlConnections
                    || !budget.TryOpenConnection(_options.MaxControlConnectionsPerPeer))
                {
                    Interlocked.Decrement(ref _activeControlConnections);
                    await served.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try { await ServeControlAsync(served, budget, cancellationToken).ConfigureAwait(false); }
                    finally
                    {
                        // Release the peer's slot; drop its budget once it has no more connections (bounds memory).
                        if (budget.CloseConnection() == 0)
                            _peerBudgets.TryRemove(new KeyValuePair<Sigil, PeerControlBudget>(peerSigil, budget));
                        Interlocked.Decrement(ref _activeControlConnections);
                    }
                }, cancellationToken);
                continue;
            }

            if (kind == OverlayControl.KindEffigy)
            {
                // A decoy channel session: serve it internally with cover traffic. It is never a real channel,
                // never surfaced to the app. Counted against the global control cap so it can't be a flood vector.
                var served = conjunction.Vessel;
                if (Interlocked.Increment(ref _activeControlConnections) > _options.MaxConcurrentControlConnections)
                {
                    Interlocked.Decrement(ref _activeControlConnections);
                    await served.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await ServeEffigyAsync(served, cancellationToken).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref _activeControlConnections); }
                }, cancellationToken);
                continue;
            }

            if (kind == OverlayControl.KindPageant)
            {
                // An inbound Pageant (fake-group) clique edge: bind it to the group it cites and drain it.
                var served = conjunction.Vessel;
                var peerSigil = conjunction.PeerSigil;
                if (Interlocked.Increment(ref _activeControlConnections) > _options.MaxConcurrentControlConnections)
                {
                    Interlocked.Decrement(ref _activeControlConnections);
                    await served.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await BindPageantEdgeAsync(served, peerSigil, cancellationToken).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref _activeControlConnections); }
                }, cancellationToken);
                continue;
            }

            await LearnReflexiveAsync(conjunction.Vessel, conjunction.PeerSigil, initiator: false, cancellationToken).ConfigureAwait(false);
            await BootstrapOverlayAsync(conjunction.Vessel, initiator: false, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return new PairedPeer(conjunction.Vessel, conjunction.PeerSigil, conjunction.PeerSealPublicKey, isInitiator: false);
        }
    }

    /// <summary>
    /// Consecrates a channel with a peer using a Watchword, yielding an encrypted channel session.
    /// <para>
    /// Layer separation: pass <see cref="ConsecrateOptions.ChannelIdentity"/> — a channel persona distinct
    /// from this node's overlay identity — to speak and hold membership under the persona, so what the node
    /// SAYS in the channel is unlinkable to the overlay Sigil it cultivates the network under. The overlay
    /// identity is used only for L1 (pairing, routing, anchoring). When no persona is given the overlay
    /// identity is used (no L1/L2 separation).
    /// </para>
    /// <para>
    /// When a gated (Sanction/Sealed) <see cref="ConsecrateOptions.Admission"/> is supplied, each side
    /// additionally proves membership of the <em>persona</em> — either the persona is the owner, or it holds
    /// an owner-signed Investiture for that persona — with a session-bound signature proving possession of
    /// the persona key. Membership is thus anchored to the persona, never to the overlay Sigil.
    /// </para>
    /// </summary>
    public async Task<ArcanumSession> ConsecrateAsync(PairedPeer peer, Watchword watchword, DateTimeOffset now,
        ConsecrateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(watchword);

        // The persona under which we speak and hold membership in this channel (overlay identity if none).
        var persona = options?.ChannelIdentity ?? new RiteIdentity(Identity.Seal.PublicKey, Identity.Seal.PrivateKey);

        var keys = ArcanumKeys.Derive(watchword, Suite);
        using var timed = LinkedTimeout(cancellationToken, _options.ConsecrationTimeout);
        var consecration = peer.IsInitiator
            ? await ConsecrationHandshake.InitiateAsync(peer.Vessel, keys, Identity.Sigil, peer.PeerSigil, now, Suite, cancellationToken: timed.Token).ConfigureAwait(false)
            : await ConsecrationHandshake.AcceptAsync(peer.Vessel, keys, Identity.Sigil, peer.PeerSigil, now, Suite, cancellationToken: timed.Token).ConfigureAwait(false);

        // Enforce owner-gated admission over the (already encrypted) vessel before the session is usable.
        if (options?.Admission is { } admission)
            await EnforceAdmissionAsync(peer, admission, persona, consecration.SessionKey, Ownership.ChannelId(keys), now, timed.Token).ConfigureAwait(false);

        // L1 anchoring uses the OVERLAY Sigil (cultivating the network) — deliberately not the persona.
        Constellation.MarkAnchored(peer.PeerSigil);
        return new ArcanumSession(peer.Vessel, consecration.Epoch, consecration.SessionKey, Suite, persona, _options.RequireSignedAuthors);
    }

    private async Task EnforceAdmissionAsync(PairedPeer peer, ArcanumAdmission admission, RiteIdentity persona, byte[] sessionKey, byte[] channelId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var descriptor = admission.Descriptor;
        if (!Ownership.VerifyDescriptor(descriptor, Suite) || !descriptor.ChannelId.AsSpan().SequenceEqual(channelId))
            throw new CupriNodeException("Channel descriptor is invalid or belongs to a different channel.");

        // Aperture/Anarch are open: the Watchword alone is membership, so no credential exchange.
        if (descriptor.AccessMode is not (ArcanumEntry.Sanction or ArcanumEntry.Sealed))
            return;

        // Bind the possession proof to this session so a captured claim cannot be replayed elsewhere.
        var commitment = AdmissionCommitment(channelId, sessionKey);
        var myClaim = BuildAdmissionClaim(persona, descriptor, admission.MyInvestiture, commitment);
        byte[] peerClaim;
        if (peer.IsInitiator)
        {
            await peer.Vessel.SendAsync(ConsecrationHandshake.ChannelStream, myClaim, cancellationToken).ConfigureAwait(false);
            peerClaim = await ReceiveAdmissionClaimAsync(peer.Vessel, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            peerClaim = await ReceiveAdmissionClaimAsync(peer.Vessel, cancellationToken).ConfigureAwait(false);
            await peer.Vessel.SendAsync(ConsecrationHandshake.ChannelStream, myClaim, cancellationToken).ConfigureAwait(false);
        }

        VerifyAdmissionClaim(peerClaim, descriptor, channelId, commitment, now);
    }

    private byte[] AdmissionCommitment(byte[] channelId, byte[] sessionKey)
    {
        var w = new Codex.CodexWriter();
        w.WriteString("cuprinet/admission/v1");
        w.WriteBytes(channelId);
        w.WriteBytes(sessionKey);
        return Suite.Hash.Sha256(w.ToArray());
    }

    private byte[] BuildAdmissionClaim(RiteIdentity persona, ChannelDescriptor descriptor, Investiture? myInvestiture, byte[] commitment)
    {
        var w = new Codex.CodexWriter();
        w.WriteBytes(persona.SealPublicKey);
        if (persona.SealPublicKey.AsSpan().SequenceEqual(descriptor.OwnerPublicKey))
        {
            w.WriteByte(0); // this persona is the owner
        }
        else
        {
            if (myInvestiture is null)
                throw new CupriNodeException("This channel is owner-gated but no membership Investiture was provided.");
            w.WriteByte(1);
            w.WriteBytes(Ownership.EncodeInvestiture(myInvestiture));
        }

        // Prove possession of the persona key, bound to this session — the membership proof, decoupled
        // from the overlay identity the transport authenticated.
        w.WriteBytes(Suite.CreateSigner(persona.SealPrivateKey).Sign(commitment));
        return w.ToArray();
    }

    private void VerifyAdmissionClaim(byte[] claim, ChannelDescriptor descriptor, byte[] channelId, byte[] commitment, DateTimeOffset now)
    {
        var r = new Codex.CodexReader(claim);
        var personaPublicKey = r.ReadBytes().ToArray();
        if (personaPublicKey.Length is 0 or > 64)
            throw new CupriNodeException("Malformed admission claim.");
        var tag = r.ReadByte();
        Investiture? investiture = tag switch
        {
            0 => null,
            1 => Ownership.DecodeInvestiture(r.ReadBytes()),
            _ => throw new CupriNodeException("Malformed admission claim."),
        };
        var signature = r.ReadBytes().ToArray();

        // Possession + freshness: the peer signed this session's commitment with the persona key it claims.
        if (!Suite.Verifier.Verify(commitment, signature, personaPublicKey))
            throw new CupriNodeException("Admission possession proof failed.");

        if (tag == 0)
        {
            if (!personaPublicKey.AsSpan().SequenceEqual(descriptor.OwnerPublicKey))
                throw new CupriNodeException("Peer claimed channel ownership but its persona is not the owner.");
            return;
        }

        // The Investiture must name the very persona that just proved possession, and be owner-signed.
        if (!investiture!.MemberSealPublicKey.AsSpan().SequenceEqual(personaPublicKey))
            throw new CupriNodeException("Peer's Investiture names a different persona.");
        if (!Ownership.VerifyInvestiture(investiture, descriptor.OwnerPublicKey, channelId, Suite, now))
            throw new CupriNodeException("Peer's Investiture did not verify against the channel owner.");
    }

    private static async Task<byte[]> ReceiveAdmissionClaimAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new CupriNodeException("Vessel closed during admission.");
        if (frame.StreamId != ConsecrationHandshake.ChannelStream)
            throw new CupriNodeException($"Unexpected frame on stream {frame.StreamId} during admission.");
        return frame.Payload;
    }

    private static CancellationTokenSource LinkedHandshakeToken(CancellationToken cancellationToken)
        => LinkedTimeout(cancellationToken, HandshakeTimeout);

    /// <summary>A linked token source that also cancels after <paramref name="timeout"/> — the uniform deadline seam.</summary>
    private static CancellationTokenSource LinkedTimeout(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    private async Task LearnReflexiveAsync(IVessel vessel, Sigil peerSigil, bool initiator, CancellationToken cancellationToken)
    {
        if (!_options.EnableReflexiveDiscovery || _options.Mode == ReachabilityMode.TorOnly)
            return; // Tor-only: no clearnet reflexive address to learn or advertise
        try
        {
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timed.CancelAfter(TimeSpan.FromSeconds(5));
            // Always run the exchange so the peer can learn ITS reflexive address from us. But only trust the
            // report about US when WE initiated — i.e. from a peer we chose to dial. An inbound connector's
            // report is free to forge, so it is deliberately not counted (anti-Sybil, Layer 2).
            var observed = await ReflexiveExchange.ExchangeAsync(vessel, initiator, cancellationToken: timed.Token).ConfigureAwait(false);
            if (initiator && vessel.RemoteEndPoint is System.Net.IPEndPoint reporter)
                ReflexiveObserver.Observe(peerSigil, reporter.Address, observed, ReflexiveWeight(peerSigil));
        }
        catch
        {
            // best-effort: pairing succeeds even when reflexive discovery is unavailable
        }
    }

    /// <summary>
    /// How much a reporter's reflexive vote counts (Layer 4 anti-Sybil): a peer we dialled but don't yet track
    /// counts minimally; one that has earned <see cref="ConstellationEntry.Standing"/> counts more; a quarantined
    /// (Excommunicate) or heavily <see cref="ConstellationEntry.Taint"/>ed one counts for nothing. So a burst of
    /// fresh Sybil identities carries far less weight than peers the node has a good history with.
    /// </summary>
    private int ReflexiveWeight(Sigil reporter)
    {
        var entry = Constellation.Get(reporter);
        if (entry is null)
            return 1; // dialled but untracked — minimal, non-zero trust
        if (entry.Bucket == PeerBucket.Excommunicate)
            return 0; // quarantined for misbehaviour — no vote
        return Math.Clamp(1 + entry.Standing - entry.Taint, 0, ReflexiveObserver.MaxReporterWeight);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        DisposeLan();
        await DisposeTorAsync().ConfigureAwait(false);
        await DisposeOverlayAsync().ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
