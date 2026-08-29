using System.Net;
using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Rites;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

public sealed partial class CupriNode
{
    private static readonly IReadOnlyDictionary<string, IAuspiceSource> NoFeeds =
        new Dictionary<string, IAuspiceSource>();

    private sealed record HostedShrine(
        Signet Signet,
        IOracleHandler Handler,
        IReadOnlyDictionary<string, IAuspiceSource> Feeds,
        IRelicSource? Relics,
        IConduitHandler? Conduit,
        bool Advertise);

    // Every Shrine this node hosts, newest last. One endpoint can serve several because the visitor names which it
    // wants inside the Noise-encrypted binding (blinded), so the host chooses which Signet to present after the
    // handshake rather than before it — no cleartext selector, and the network learns neither that a connection is a
    // site visit nor which site.
    private readonly List<HostedShrine> _shrines = [];

    /// <summary>True when this node hosts at least one Shrine.</summary>
    internal bool HostsAnyShrine => _shrines.Count > 0;

    /// <summary>True when <paramref name="signet"/> is one of the Shrines this node serves.</summary>
    internal bool HostsShrine(Signet signet) => _shrines.Any(s => s.Signet.Sigil == signet.Sigil);

    /// <summary>The first Shrine hosted — what a visitor that names no target is served.</summary>
    private HostedShrine? PrimaryShrine => _shrines.Count > 0 ? _shrines[0] : null;

    /// <summary>
    /// Hosts a Shrine (an L2 site) under <paramref name="signet"/>, serving content through <paramref name="handler"/>
    /// and optional live feeds through <paramref name="feeds"/> (keyed by feed name). The Signet's
    /// <see cref="Signet.Address"/> (<c>cupri1…</c>) IS the site URL; a Pilgrim that dials it pins the Signet during the
    /// Pilgrimage, so the connection is self-authenticating — only the holder of the Signet key can answer, and the
    /// node's overlay Sigil is never presented (hosting a Shrine leaks no L1 identity). v1 hosts one Shrine per node,
    /// served to browser Pilgrims over the node's WebRTC endpoint.
    /// <para>
    /// <paramref name="advertiseInLink"/> stamps the Shrine's Signet into every Intonation this node mints, so
    /// "here is my link" doubles as "here is my site". It is <b>off by default</b> because it ties the Shrine to this
    /// node's overlay Sigil — right for a public or branded host, wrong for anonymous hosting.
    /// </para>
    /// </summary>
    public void HostShrine(Signet signet, IOracleHandler handler, bool advertiseInLink = false)
        => HostShrine(signet, handler, feeds: null, advertiseInLink);

    /// <summary>
    /// As <see cref="HostShrine(Signet, IOracleHandler, bool)"/>, plus named live <paramref name="feeds"/> the Shrine
    /// emanates over the Auspice rite (see <see cref="IAuspiceSource"/>).
    /// </summary>
    public void HostShrine(
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        bool advertiseInLink = false)
        => HostShrine(signet, handler, feeds, relics: null, advertiseInLink);

    /// <summary>
    /// As <see cref="HostShrine(Signet, IOracleHandler, IReadOnlyDictionary{string, IAuspiceSource}, bool)"/>, plus
    /// named bulk <paramref name="relics"/> the Shrine serves over the Relic rite — chunked and hash-verified, the
    /// only way content past the message ceiling (an app blob, an asset pack) reaches a Pilgrim
    /// (see <see cref="IRelicSource"/> / <see cref="StaticRelicSource"/>).
    /// </summary>
    public void HostShrine(
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        IRelicSource? relics,
        bool advertiseInLink = false)
        => HostShrine(signet, handler, feeds, relics, conduit: null, advertiseInLink);

    /// <summary>
    /// As <see cref="HostShrine(Signet, IOracleHandler, IReadOnlyDictionary{string, IAuspiceSource}, IRelicSource, bool)"/>,
    /// plus a raw duplex <paramref name="conduit"/> — the seam an application's own framed protocol sits on when it is
    /// neither request/response nor snapshot-then-updates (see <see cref="IConduitHandler"/>). One conduit per visit;
    /// <see cref="ConduitFrame.ProtocolId"/> discriminates protocols within it.
    /// </summary>
    public void HostShrine(
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        IRelicSource? relics,
        IConduitHandler? conduit,
        bool advertiseInLink = false)
    {
        ArgumentNullException.ThrowIfNull(signet);
        ArgumentNullException.ThrowIfNull(handler);

        // Hosting a second Signet adds a Shrine; hosting the same one again replaces it. One endpoint, many sites.
        var hosted = new HostedShrine(signet, handler, feeds ?? NoFeeds, relics, conduit, advertiseInLink);
        var existing = _shrines.FindIndex(s => s.Signet.Sigil == signet.Sigil);
        if (existing >= 0)
            _shrines[existing] = hosted;
        else
            _shrines.Add(hosted);
    }

    /// <summary>Every <c>cupri1…</c> address this node hosts, in the order they were hosted.</summary>
    public IReadOnlyList<string> ShrineAddresses => _shrines.Select(s => s.Signet.Address).ToList();

    /// <summary>
    /// Resolves the Shrine a visitor asked for from its blinded target tag, or null if this node hosts none matching.
    /// Recomputes the tag per hosted Signet and compares in fixed time — the host never sees the address itself, only
    /// whether one of its own produces the same value for this session.
    /// </summary>
    private HostedShrine? ResolveShrine(ReadOnlyMemory<byte> targetTag, byte[] handshakeHash)
    {
        // No tag: a Pilgrim that predates multi-Shrine, or an ordinary pairing. Serve the first Shrine, which is the
        // only one such a visitor could have meant.
        if (targetTag.Length == 0)
            return PrimaryShrine;

        // Every Signet is derived and compared, and the loop always runs to the end. Returning on the first match
        // would make a hit at index 0 cheaper than one at index 20, so a visitor holding a single valid address could
        // time a known-good visit against an unknown one and estimate how many sites this endpoint hosts. That is a
        // weaker leak than learning *which* site, but co-hosted sites not being obviously co-hosted is much of the
        // point of serving several from one endpoint.
        HostedShrine? match = null;
        foreach (var hosted in _shrines)
        {
            var mine = NoiseConjunction.ShrineTargetTag(Suite, handshakeHash, hosted.Signet.Sigil);
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(mine, targetTag.Span))
                match = hosted;
        }

        return match;
    }

    /// <summary>The <c>cupri1…</c> address of the Shrine this node hosts, or null if it hosts none.</summary>
    public string? ShrineAddress => PrimaryShrine?.Signet.Address;

    // A Shrine gets its own listener rather than sharing the node's L1 port, because the identity to present must be
    // chosen BEFORE the Noise handshake and nothing on the wire says which the caller wants. Demultiplexing the L1
    // port would need the client to signal "I am here for the site" in cleartext — the same pre-Noise selector
    // multi-Shrine hosting refuses, so that the network never learns which site a connection is for. A dedicated port
    // needs no selector at all: everything arriving here is a Pilgrimage, and the Signet is presented unconditionally.
    private VesselListener? _shrineListener;
    private SemaphoreSlim? _pilgrimSlots;

    // Concurrent visits per source address. The global slot count alone leaves the Shrine trivially exhaustible: a
    // visit slot is held for the WHOLE visit, so one address could take every slot and hold them idle for the price
    // of that many Toll solves. The overlay path has held a per-peer budget beside its global cap since the first
    // review; a Pilgrim is anonymous, so the address is what there is to key on.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<System.Net.IPAddress, int> _visitsPerAddress = new();

    private bool TryReserveVisit(IPEndPoint? remote)
    {
        if (remote is null)
            return true;                    // not an IP transport (an in-memory pair in a test); nothing to key on

        var allowed = Math.Max(1, _options.MaxPilgrimagesPerAddress);
        var taken = _visitsPerAddress.AddOrUpdate(remote.Address, 1, (_, current) => current + 1);
        if (taken <= allowed)
            return true;

        ReleaseVisit(remote);
        return false;
    }

    private void ReleaseVisit(IPEndPoint? remote)
    {
        if (remote is null)
            return;

        _visitsPerAddress.AddOrUpdate(remote.Address, 0, (_, current) => current - 1);
        // Drop the entry once it reaches zero so a long-lived node does not accumulate one per address ever seen.
        _visitsPerAddress.TryRemove(new KeyValuePair<System.Net.IPAddress, int>(remote.Address, 0));
    }

    /// <summary>The endpoint this node serves Pilgrims on, or null if <see cref="ListenForPilgrims"/> was never called.</summary>
    public IPEndPoint? ShrineEndPoint => _shrineListener?.LocalEndPoint;

    /// <summary>
    /// Starts serving the hosted Shrine on a listener of its own, and returns the endpoint it bound (pass
    /// <paramref name="port"/> 0 for an OS-assigned one). Every connection arriving here is treated as a Pilgrimage:
    /// the host presents <b>only the Signet</b>, so a Pilgrim that pins the site's address completes and one that
    /// pins this node's Sigil does not — the reverse of the L1 port, where the node presents its Sigil and a visit
    /// pairs as an overlay peer with no Shrine behind it.
    ///
    /// <para>Binds the same <see cref="CupriNodeOptions.ListenAddress"/> as the node, on a different port. A node
    /// serving no Shrine simply does not listen here, so dialling it is refused outright rather than answering with
    /// a session nothing is behind.</para>
    ///
    /// <para>Call <see cref="HostShrine(Signet, IOracleHandler, bool)"/> first — this listener exists to present that
    /// Signet, so there must be one. Visits are bounded by
    /// <see cref="CupriNodeOptions.MaxConcurrentPilgrimages"/>; the listener is closed when the node is disposed.</para>
    /// </summary>
    /// <param name="port">TCP port to bind, or 0 for an OS-assigned one (read it back from the return value).</param>
    public IPEndPoint ListenForPilgrims(int port = 0)
    {
        if (_shrines.Count == 0)
            throw new InvalidOperationException(
                "No Shrine is hosted. This listener presents the Signet as its handshake identity, so there must be " +
                "one to present — call HostShrine before ListenForPilgrims.");
        if (_shrineListener is not null)
            throw new InvalidOperationException($"Already serving Pilgrims on {_shrineListener.LocalEndPoint}.");

        var listener = new VesselListener(new IPEndPoint(_options.ListenAddress, port));
        listener.Start();
        _shrineListener = listener;
        _pilgrimSlots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentPilgrimages));
        _ = PilgrimAcceptLoopAsync(listener, _pilgrimSlots, _lifetime.Token);
        return listener.LocalEndPoint;
    }

    /// <summary>
    /// Accepts visits and hands each to a bounded background worker. The slot is taken BEFORE accepting, so a flood
    /// queues in the kernel backlog rather than stalling the loop — the same shape the L1 dispatch loop uses, except
    /// that a visit slot is held for the whole visit rather than just the handshake, because that is the resource a
    /// site actually spends.
    /// </summary>
    private async Task PilgrimAcceptLoopAsync(VesselListener listener, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await slots.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            IVessel vessel;
            try
            {
                vessel = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { slots.Release(); break; }
            catch { slots.Release(); continue; }          // transient accept error — keep serving

            _ = Task.Run(async () =>
            {
                try { await ServePilgrimAsync(vessel, cancellationToken).ConfigureAwait(false); }
                finally { slots.Release(); }
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Host side of a Pilgrimage for a node that may serve several Shrines: the visitor's blinded target picks which
    /// Signet is presented, and a target matching none of them is refused with a reason rather than answered by the
    /// wrong site or by silence. Serves whatever this node currently hosts, so <see cref="HostShrine(Signet, IOracleHandler, bool)"/>
    /// governs it rather than the call site. On failure the vessel is disposed.
    /// </summary>
    /// <remarks>
    /// <b>No subnet fence is applied here.</b> The caller accepted this vessel, so the caller owns the policy — and
    /// for a transport with no meaningful address (an in-memory pair, a DataChannel) there is nothing to fence on.
    /// <see cref="ListenForPilgrims"/> does apply <c>AddressPolicy</c>, as the overlay accept loop does, so moving
    /// between the two changes what is enforced: check the address yourself if you need the same guard here.
    /// </remarks>
    public async Task AcceptPilgrimageOverVesselAsync(IVessel vessel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        if (_shrines.Count == 0)
            throw new InvalidOperationException("This node hosts no Shrine; call HostShrine first.");

        HostedShrine? chosen = null;
        NoiseConjunctionResult conjunction;
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.IssueAndVerifyAsync(vessel, _tollSecret, vessel.RemoteEndPoint, DateTimeOffset.UtcNow, timed.Token).ConfigureAwait(false);

            // The identity is chosen mid-handshake, once the visitor has said (inside Noise) which Shrine it wants.
            conjunction = await NoiseConjunction.AcceptAsync(
                vessel, NodeIdentity.FromSeal(_shrines[0].Signet.Seal), Network, Suite,
                (tag, handshakeHash) =>
                {
                    chosen = ResolveShrine(tag, handshakeHash);
                    return chosen is null ? null : NodeIdentity.FromSeal(chosen.Signet.Seal);
                },
                timed.Token).ConfigureAwait(false);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (chosen is null)
        {
            await conjunction.Vessel.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await ServeRitesAsync(conjunction.Vessel, chosen.Handler, chosen.Feeds, chosen.Relics, chosen.Conduit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Serves one visit. A failed or refused visit drops its own connection and never affects the loop.</summary>
    private async Task ServePilgrimAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        var remote = vessel.RemoteEndPoint as IPEndPoint;

        // Subnet fence first, before any handshake work — the same guard the L1 path applies.
        if (remote is not null && !IsAddressAllowed(remote.Address))
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (_shrines.Count == 0)
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (!TryReserveVisit(remote))
        {
            // This address already holds its share. Dropping before the handshake keeps the cost of being refused
            // on the caller rather than on us.
            await vessel.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await AcceptPilgrimageOverVesselAsync(vessel, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // AcceptPilgrimageOverVesselAsync owns the vessel and disposes it on the way out; nothing to clean up,
            // and one visitor's failure is never the Shrine's problem.
        }
        finally
        {
            ReleaseVisit(remote);
        }
    }

    private async ValueTask DisposeShrineListenerAsync()
    {
        if (_shrineListener is not null)
        {
            await _shrineListener.DisposeAsync().ConfigureAwait(false);
            _shrineListener = null;
        }

        _pilgrimSlots?.Dispose();
        _pilgrimSlots = null;
    }

    /// <summary>
    /// The Shrine stamped into a link minted without one named — the first that opted in, or none. An Intonation
    /// carries a single Shrine address, so a node hosting several publishes them with one link each, via
    /// <see cref="Intone(TimeSpan, DateTimeOffset, Signet, LinkTransports, byte[])"/>. Flagging several is therefore
    /// not a way to advertise several; it only decides which is the default.
    /// </summary>
    internal Sigil? ShrineToAdvertise =>
        _shrines.FirstOrDefault(s => s.Advertise) is { } advertised ? advertised.Signet.Sigil : null;

    /// <summary>
    /// Host side of a Pilgrimage over an already-connected vessel: the Toll, then a Noise handshake in which the host
    /// presents ONLY the <paramref name="signet"/> (never the node Sigil), then both site rites — the Oracle served by
    /// <paramref name="handler"/> and any live <paramref name="feeds"/> — until the Pilgrim disconnects. No overlay
    /// join: a Pilgrim is a short-lived client. On failure the vessel is disposed.
    /// </summary>
    public Task AcceptPilgrimageOverVesselAsync(
        IVessel vessel, Signet signet, IOracleHandler handler, CancellationToken cancellationToken = default)
        => AcceptPilgrimageOverVesselAsync(vessel, signet, handler, feeds: null, cancellationToken);

    /// <summary>
    /// As <see cref="AcceptPilgrimageOverVesselAsync(IVessel, Signet, IOracleHandler, CancellationToken)"/>, also
    /// serving named live <paramref name="feeds"/> over the Auspice rite alongside the Oracle.
    /// </summary>
    public Task AcceptPilgrimageOverVesselAsync(
        IVessel vessel,
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        CancellationToken cancellationToken = default)
        => AcceptPilgrimageOverVesselAsync(vessel, signet, handler, feeds, relics: null, cancellationToken);

    /// <summary>
    /// As <see cref="AcceptPilgrimageOverVesselAsync(IVessel, Signet, IOracleHandler, IReadOnlyDictionary{string, IAuspiceSource}, CancellationToken)"/>,
    /// also serving named bulk <paramref name="relics"/> over the Relic rite alongside the Oracle and the Auspice.
    /// </summary>
    public Task AcceptPilgrimageOverVesselAsync(
        IVessel vessel,
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        IRelicSource? relics,
        CancellationToken cancellationToken = default)
        => AcceptPilgrimageOverVesselAsync(vessel, signet, handler, feeds, relics, conduit: null, cancellationToken);

    /// <summary>
    /// As <see cref="AcceptPilgrimageOverVesselAsync(IVessel, Signet, IOracleHandler, IReadOnlyDictionary{string, IAuspiceSource}, IRelicSource, CancellationToken)"/>,
    /// also serving a raw duplex <paramref name="conduit"/> alongside the other three rites.
    /// </summary>
    public async Task AcceptPilgrimageOverVesselAsync(
        IVessel vessel,
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        IRelicSource? relics,
        IConduitHandler? conduit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        ArgumentNullException.ThrowIfNull(signet);
        ArgumentNullException.ThrowIfNull(handler);

        NoiseConjunctionResult conjunction;
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.IssueAndVerifyAsync(vessel, _tollSecret, vessel.RemoteEndPoint, DateTimeOffset.UtcNow, timed.Token).ConfigureAwait(false);
            // Present the Signet as this session's identity: the Pilgrim pins it (self-authenticating), and nothing
            // about the node's overlay identity is revealed.
            conjunction = await NoiseConjunction.AcceptAsync(vessel, NodeIdentity.FromSeal(signet.Seal), Network, Suite, timed.Token).ConfigureAwait(false);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await ServeRitesAsync(conjunction.Vessel, handler, feeds ?? NoFeeds, relics, conduit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Serves every rite over an already-authenticated vessel, demultiplexed so a fetch, a live feed, a relic and a
    /// raw session all run concurrently on the one visit. Owns the vessel from here: it is disposed on the way out.
    /// </summary>
    private async Task ServeRitesAsync(
        IVessel vessel,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource> feeds,
        IRelicSource? relics,
        IConduitHandler? conduit,
        CancellationToken cancellationToken)
    {
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A visit holds its slot until it ends, and until now nothing ended it but the peer. That made the Shrine
        // exhaustible by connections that simply say nothing: complete the handshake, then wait. The deadline counts
        // traffic in EITHER direction, so a Pilgrim watching a live feed — which sends once and then only listens —
        // is not mistaken for one that has gone away.
        var tracked = new ActivityTrackingVessel(vessel);
        var mux = new VesselMux(tracked, ownsVessel: false);
        var idle = WatchForIdleAsync(tracked, serving, cancellationToken);
        var loops = new[]
        {
            OracleHost.ServeAsync(new OracleSession(mux.Stream(OracleSession.RequestStream)), handler, serving.Token),
            AuspiceHost.ServeAsync(new AuspiceSession(mux.Stream(AuspiceSession.AuspiceStream)), feeds, serving.Token),
            RelicHost.ServeAsync(new RelicSession(mux.Stream(RelicSession.RelicStream), Suite), relics, serving.Token),
            ConduitHost.ServeAsync(new ConduitSession(mux.Stream(ConduitSession.DataStream)), conduit, serving.Token),
        };

        try
        {
            // Any loop ending means the visit is over (the Pilgrim closed, or a rite failed).
            await Task.WhenAny(loops).ConfigureAwait(false);
        }
        finally
        {
            await serving.CancelAsync().ConfigureAwait(false);
            // Observe every outcome so none faults unobserved; cancellation here is the expected path.
            try { await Task.WhenAll(loops).ConfigureAwait(false); } catch { /* teardown */ }
            try { await idle.ConfigureAwait(false); } catch { /* the watchdog ends with the visit */ }
            await mux.DisposeAsync().ConfigureAwait(false);
            await vessel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends a visit that has gone quiet in both directions for <see cref="CupriNodeOptions.PilgrimageIdleTimeout"/>.
    /// Polls at a fraction of the deadline rather than arming a timer per frame: a visit is cheap to check and this
    /// costs nothing per message.
    /// </summary>
    private async Task WatchForIdleAsync(
        ActivityTrackingVessel tracked, CancellationTokenSource serving, CancellationToken cancellationToken)
    {
        var deadline = _options.PilgrimageIdleTimeout;
        if (deadline <= TimeSpan.Zero)
            return;                                             // disabled

        var tick = TimeSpan.FromMilliseconds(Math.Max(1000, deadline.TotalMilliseconds / 4));
        try
        {
            while (!serving.IsCancellationRequested)
            {
                await Task.Delay(tick, serving.Token).ConfigureAwait(false);
                if (tracked.IdleMilliseconds >= deadline.TotalMilliseconds)
                {
                    await serving.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The visit ended first, which is the ordinary case.
        }
    }

    /// <summary>
    /// Pilgrim side of a Pilgrimage over an already-connected vessel: the Toll, then a Noise handshake that pins
    /// <paramref name="expectedSignet"/> (so a host that does not hold the site's key is rejected), under a throwaway
    /// per-visit identity that reveals nothing durable. Returns a <see cref="ShrineSession"/> carrying both the Oracle
    /// and Auspice rites. No overlay join. On failure the vessel is disposed.
    /// <para>
    /// Convenience over <see cref="Pilgrimage.OverVesselAsync"/>, which needs no node at all — use that one from a
    /// browser, a CLI, or a test, where constructing a node (and its listener) is impossible or unwanted.
    /// </para>
    /// </summary>
    public Task<ShrineSession> PilgrimageOverVesselAsync(IVessel vessel, Sigil expectedSignet, CancellationToken cancellationToken = default)
        => Pilgrimage.OverVesselAsync(vessel, expectedSignet, Network, Suite, _options.EnableToll, cancellationToken);
}
