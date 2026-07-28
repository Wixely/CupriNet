using System.Collections.Concurrent;
using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Concordance;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// Pageants: negotiated fake groups (decoy cliques). This node forms full-mesh decoy groups whose members all run
/// one shared <see cref="PageantSchedule"/>, so the group's turn-taking correlates across the clique like a real
/// group channel — the topology tell an Effigy star cannot reproduce — while every member still only sends on its
/// own direct edges (nothing relayed). Groups are persisted (stable across restarts) and self-heal: dropped edges
/// are re-dialed, and members that have permanently left are re-negotiated out for fresh peers in the same slot.
/// </summary>
public sealed partial class CupriNode
{
    private const int PageantReplaceThreshold = 3; // consecutive heals with no edge before a slot is re-negotiated

    private readonly object _pageantGate = new();
    private readonly List<PageantSession> _pageants = [];

    /// <summary>Pageants this node currently participates in. Exposed for tests.</summary>
    internal int PageantSessionCount { get { lock (_pageantGate) return _pageants.Count; } }

    /// <summary>Total live clique edges across all Pageants. Exposed for tests.</summary>
    internal int PageantEdgeTotal { get { lock (_pageantGate) return _pageants.Sum(p => p.Edges.Count); } }

    /// <summary>The roster Sigils of the first Pageant (test helper for verifying re-negotiation).</summary>
    internal IReadOnlyList<Sigil> FirstPageantRoster()
    {
        lock (_pageantGate)
            return _pageants.Count == 0 ? [] : _pageants[0].Definition.Roster.Select(r => r.Sigil).ToList();
    }

    internal void StartPageants()
    {
        // Not run in Tor-only mode: fake-group cover traffic over Tor is high-bandwidth for little gain (see StartHotFuzz).
        if (_options.Power == PowerProfile.Unmetered
            && (_options.EnablePageants || _options.MaxPageantsAsMember > 0)
            && _options.Mode != ReachabilityMode.TorOnly)
            _ = PageantLoopAsync(_lifetime.Token);
    }

    /// <summary>Rehydrates persisted Pageants (warm start) and resumes driving + healing them.</summary>
    internal async Task LoadPageantsAsync(ISecretStore store, CancellationToken cancellationToken)
    {
        foreach (var stored in await PageantStore.LoadAsync(store, Suite, cancellationToken).ConfigureAwait(false))
        {
            var ordinal = stored.Pageant.OrdinalOf(Identity.Sigil);
            if (ordinal < 0)
                continue;
            var session = new PageantSession(stored.Pageant, ordinal, stored.IsInitiator, _lifetime.Token);
            lock (_pageantGate)
                _pageants.Add(session);
            StartPageantDriver(session);
        }
    }

    private async Task PageantLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await PageantOnceAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* transient — keep the groups we have */ }

            var jitter = TimeSpan.FromMilliseconds(8000 + Random.Shared.Next(0, 8000));
            try { await Task.Delay(jitter, cancellationToken).ConfigureAwait(false); }
            catch { break; }
        }
    }

    /// <summary>
    /// One Pageant reconcile: (as initiator) create new groups up to the target count, then heal every group —
    /// re-dial missing edges and re-negotiate slots whose member has left. Public so tests can drive it.
    /// </summary>
    public async Task PageantOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_options.EnablePageants)
        {
            var deficit = _options.PageantCount - InitiatedCount();
            for (var i = 0; i < deficit; i++)
                await TryCreatePageantAsync(cancellationToken).ConfigureAwait(false);
        }

        PageantSession[] sessions;
        lock (_pageantGate)
            sessions = [.. _pageants];
        foreach (var session in sessions)
            await HealPageantAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private int InitiatedCount()
    {
        lock (_pageantGate)
            return _pageants.Count(p => p.IsInitiator);
    }

    private int MemberCount()
    {
        lock (_pageantGate)
            return _pageants.Count(p => !p.IsInitiator);
    }

    // ---- Formation (initiator) -----------------------------------------------------------------

    private async Task TryCreatePageantAsync(CancellationToken cancellationToken)
    {
        var want = Math.Clamp(_options.PageantSize, 2, PageantCodec.MaxRoster) - 1; // members besides ourselves
        HashSet<Sigil> exclude;
        lock (_pageantGate)
            exclude = [.. _pageants.SelectMany(p => p.Definition.Roster.Select(r => r.Sigil))];
        var partners = SelectEffigyPartners(want, exclude);
        if (partners.Count == 0)
            return; // no peers to form a group with yet

        var now = DateTimeOffset.UtcNow;
        var roster = new List<PeerRecord> { SelfRecord(now) };
        roster.AddRange(partners);

        var pageant = new Pageant
        {
            Id = RandomNumberGenerator.GetBytes(16),
            Seed = RandomNumberGenerator.GetBytes(32),
            Epoch = now,
            Roster = roster,
        };
        var session = new PageantSession(pageant, ordinal: 0, isInitiator: true, _lifetime.Token);
        lock (_pageantGate)
            _pageants.Add(session);

        await InvitePartnersAsync(session, partners, cancellationToken).ConfigureAwait(false);
        StartPageantDriver(session);
        await FormEdgesAsync(session, cancellationToken).ConfigureAwait(false);
        await PersistPageantsAsync().ConfigureAwait(false);
    }

    private async Task InvitePartnersAsync(PageantSession session, IEnumerable<PeerRecord> partners, CancellationToken cancellationToken)
    {
        var invite = OverlayControl.PageantInviteRequest(session.Definition);
        foreach (var partner in partners)
        {
            if (partner.Sigil == Identity.Sigil)
                continue;
            try
            {
                var conn = await GetControlAsync(partner, cancellationToken).ConfigureAwait(false);
                _ = await conn.RoundtripAsync(invite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* couldn't reach an invitee — a heal round re-negotiates the slot if it stays empty */ }
        }
    }

    // ---- Membership (invited) ------------------------------------------------------------------

    /// <summary>Handles an inbound PAGEANT invite (from the control plane): join a new group or update an existing one.</summary>
    internal bool AcceptPageantInvite(Pageant pageant)
    {
        if (_options.Power != PowerProfile.Unmetered)
            return false;
        var ordinal = pageant.OrdinalOf(Identity.Sigil);
        if (ordinal < 0)
            return false; // not addressed to us

        var existing = FindPageantById(pageant.Id);
        if (existing is not null)
        {
            _ = UpdatePageantRosterAsync(existing, pageant);
            return true;
        }
        if (MemberCount() >= _options.MaxPageantsAsMember)
            return false; // at our conscription cap

        _ = JoinPageantAsync(pageant, ordinal);
        return true;
    }

    private async Task JoinPageantAsync(Pageant pageant, int ordinal)
    {
        var session = new PageantSession(pageant, ordinal, isInitiator: false, _lifetime.Token);
        lock (_pageantGate)
            _pageants.Add(session);
        StartPageantDriver(session);
        await FormEdgesAsync(session, _lifetime.Token).ConfigureAwait(false);
        await PersistPageantsAsync().ConfigureAwait(false);
    }

    private async Task UpdatePageantRosterAsync(PageantSession session, Pageant updated)
    {
        // Roster size is the schedule's basis; a change to it would desync the clique, so only same-size updates
        // (a member replaced in the same slot) are honored.
        if (updated.Roster.Count != session.Definition.Roster.Count)
            return;
        var ordinal = updated.OrdinalOf(Identity.Sigil);
        if (ordinal < 0 || ordinal != session.MyOrdinal)
            return;

        session.Definition = updated;
        // Prune edges to members no longer in the roster.
        var members = updated.Roster.Select(r => r.Sigil).ToHashSet();
        foreach (var (sigil, edge) in session.Edges.ToArray())
            if (!members.Contains(sigil) && session.Edges.TryRemove(sigil, out _))
                await edge.DisposeAsync().ConfigureAwait(false);

        await FormEdgesAsync(session, _lifetime.Token).ConfigureAwait(false);
        await PersistPageantsAsync().ConfigureAwait(false);
    }

    // ---- Edges (the clique mesh) ---------------------------------------------------------------

    /// <summary>Dials every roster member we are responsible for (lower Sigil dials higher), opening any missing edges.</summary>
    private async Task FormEdgesAsync(PageantSession session, CancellationToken cancellationToken)
    {
        foreach (var member in session.Definition.Roster)
        {
            var sigil = member.Sigil;
            if (sigil == Identity.Sigil || session.Edges.ContainsKey(sigil))
                continue;
            if (SigilCompare(Identity.Sigil, sigil) >= 0)
                continue; // the higher-Sigil side dials — the other member will connect to us

            try
            {
                var vessel = await DialPageantEdgeAsync(member, session.Definition.Id, cancellationToken).ConfigureAwait(false);
                var edge = new Effigy(sigil, vessel, session.Token);
                if (session.Edges.TryAdd(sigil, edge))
                    _ = DrainPageantEdgeAsync(session, edge);
                else
                    await edge.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* unreachable this round — the next heal retries, and re-negotiates if it stays dead */ }
        }
    }

    private async Task<IVessel> DialPageantEdgeAsync(PeerRecord peer, byte[] pageantId, CancellationToken cancellationToken)
    {
        var beacon = peer.Endpoints.FirstOrDefault(b => b.Kind is EndpointKind.Host or EndpointKind.Mapped or EndpointKind.Manual)
                     ?? throw new CupriNodeException("Peer has no dialable beacon.");

        var vessel = await TcpVessel.ConnectAsync(beacon.Host, beacon.Port, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.SolveAsync(vessel, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.InitiateAsync(vessel, Identity, Network, Suite, expectedPeer: peer.Sigil, cancellationToken: timed.Token).ConfigureAwait(false);
            await DeclareSessionKindAsync(conjunction.Vessel, OverlayControl.KindPageant, timed.Token).ConfigureAwait(false);
            await conjunction.Vessel.SendAsync(OverlayControl.EffigyStream, pageantId, timed.Token).ConfigureAwait(false); // first frame binds the edge
            return conjunction.Vessel;
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The responder side of a Pageant edge: read the cited Pageant id, bind the edge, then drain it.</summary>
    private async Task BindPageantEdgeAsync(IVessel vessel, Sigil peerSigil, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null || frame.Value.StreamId != OverlayControl.EffigyStream)
            {
                await vessel.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var session = FindPageantById(frame.Value.Payload);
            if (session is null || session.Definition.OrdinalOf(peerSigil) < 0)
            {
                await vessel.DisposeAsync().ConfigureAwait(false); // unknown group or not a member — refuse
                return;
            }

            var edge = new Effigy(peerSigil, vessel, session.Token);
            if (!session.Edges.TryAdd(peerSigil, edge))
            {
                await edge.DisposeAsync().ConfigureAwait(false); // already have this edge
                return;
            }
            await DrainPageantEdgeAsync(session, edge).ConfigureAwait(false);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DrainPageantEdgeAsync(PageantSession session, Effigy edge)
    {
        try
        {
            while (!edge.Token.IsCancellationRequested)
                if (await edge.Vessel.ReceiveAsync(edge.Token).ConfigureAwait(false) is null)
                    break;
        }
        catch { }
        finally
        {
            session.Edges.TryRemove(edge.Partner, out _);
            await edge.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- The shared-seed driver (turn-taking, relay-free) --------------------------------------

    private void StartPageantDriver(PageantSession session)
    {
        _ = Task.Run(async () =>
        {
            var schedule = new PageantSchedule(session.Definition.Seed, session.Definition.Roster.Count);
            var cumulative = TimeSpan.Zero;
            var spins = 0;
            try
            {
                while (!session.Token.IsCancellationRequested)
                {
                    var (gap, speaker, size) = schedule.Next(_options.EffigyMaxMessageBytes);
                    cumulative += gap;
                    var wait = session.Definition.Epoch + cumulative - DateTimeOffset.UtcNow;

                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, session.Token).ConfigureAwait(false);
                    }
                    else if (wait < TimeSpan.FromSeconds(-30))
                    {
                        // Catching up to a shared clock we joined late — advance without emitting stale bursts.
                        if (++spins % 500 == 0)
                            await Task.Yield();
                        continue;
                    }

                    // Only speak on our own turns; each speaker fans out directly to its edges (no relaying).
                    if (speaker == session.MyOrdinal)
                    {
                        var edges = session.Edges.Values.ToArray();
                        if (edges.Length > 0)
                            await Task.WhenAll(edges.Select(e => SendPageantCoverAsync(session, e, size, session.Token))).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }, session.Token);
    }

    private async Task SendPageantCoverAsync(PageantSession session, Effigy edge, int bytes, CancellationToken cancellationToken)
    {
        try
        {
            await edge.Vessel.SendAsync(OverlayControl.EffigyStream, new byte[Math.Max(1, bytes)], cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref edge.SentFrames);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            if (session.Edges.TryRemove(edge.Partner, out _))
                await edge.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- Self-healing + re-negotiation ---------------------------------------------------------

    private async Task HealPageantAsync(PageantSession session, CancellationToken cancellationToken)
    {
        await FormEdgesAsync(session, cancellationToken).ConfigureAwait(false); // re-dial transiently-dropped edges

        if (!session.IsInitiator)
            return; // only the initiator owns re-negotiation of dead slots

        // Track how many consecutive heals each member has had no live edge; replace persistent absentees.
        var replacements = new List<(int Ordinal, PeerRecord NewMember)>();
        HashSet<Sigil> exclude;
        lock (_pageantGate)
            exclude = [.. _pageants.SelectMany(p => p.Definition.Roster.Select(r => r.Sigil))];

        for (var i = 0; i < session.Definition.Roster.Count; i++)
        {
            var member = session.Definition.Roster[i];
            if (member.Sigil == Identity.Sigil)
                continue;
            if (session.Edges.ContainsKey(member.Sigil))
            {
                session.MissStreak[member.Sigil] = 0;
                continue;
            }
            var streak = session.MissStreak.GetValueOrDefault(member.Sigil) + 1;
            session.MissStreak[member.Sigil] = streak;
            if (streak < PageantReplaceThreshold)
                continue;

            var replacement = SelectEffigyPartners(1, exclude).FirstOrDefault();
            if (replacement is null)
                continue; // no one to swap in yet — try again next heal
            replacements.Add((i, replacement));
            exclude.Add(replacement.Sigil);
            session.MissStreak.Remove(member.Sigil);
        }

        if (replacements.Count == 0)
            return;

        // Swap absentees out of their slots (same ordinal → schedule unchanged), then re-invite the whole group.
        var roster = session.Definition.Roster.ToList();
        var departed = new List<Sigil>();
        foreach (var (ordinal, newMember) in replacements)
        {
            departed.Add(roster[ordinal].Sigil);
            roster[ordinal] = newMember;
        }
        session.Definition = session.Definition with { Roster = roster };

        foreach (var sigil in departed)
            if (session.Edges.TryRemove(sigil, out var edge))
                await edge.DisposeAsync().ConfigureAwait(false);

        await InvitePartnersAsync(session, roster.Where(r => r.Sigil != Identity.Sigil), cancellationToken).ConfigureAwait(false);
        await FormEdgesAsync(session, cancellationToken).ConfigureAwait(false);
        await PersistPageantsAsync().ConfigureAwait(false);
    }

    // ---- Persistence + teardown ----------------------------------------------------------------

    private async Task PersistPageantsAsync()
    {
        if (!_options.PersistOverlay)
            return;
        List<StoredPageant> snapshot;
        lock (_pageantGate)
            snapshot = _pageants.Select(p => new StoredPageant(p.IsInitiator, p.Definition)).ToList();
        try { await PageantStore.SaveAsync(_secretStore, snapshot).ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    private async ValueTask DisposePageantsAsync()
    {
        PageantSession[] sessions;
        lock (_pageantGate)
        {
            sessions = [.. _pageants];
            _pageants.Clear();
        }
        foreach (var session in sessions)
            await session.DisposeAsync().ConfigureAwait(false);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>Forms one Pageant now, regardless of the <see cref="CupriNodeOptions.EnablePageants"/> flag. For tests.</summary>
    internal Task FormPageantForTestAsync(CancellationToken cancellationToken = default) => TryCreatePageantAsync(cancellationToken);

    /// <summary>Sends one cover frame over every live Pageant edge; returns how many landed. For tests.</summary>
    internal async Task<int> PageantProbeAsync(int bytes, CancellationToken cancellationToken = default)
    {
        (PageantSession Session, Effigy Edge)[] all;
        lock (_pageantGate)
            all = _pageants.SelectMany(s => s.Edges.Values.Select(e => (s, e))).ToArray();
        var before = all.Sum(x => Interlocked.Read(ref x.Edge.SentFrames));
        await Task.WhenAll(all.Select(x => SendPageantCoverAsync(x.Session, x.Edge, bytes, cancellationToken))).ConfigureAwait(false);
        var after = all.Sum(x => Interlocked.Read(ref x.Edge.SentFrames));
        return (int)(after - before);
    }

    private PageantSession? FindPageantById(ReadOnlySpan<byte> id)
    {
        lock (_pageantGate)
            foreach (var session in _pageants)
                if (session.Definition.Id.AsSpan().SequenceEqual(id))
                    return session;
        return null;
    }

    private static int SigilCompare(Sigil a, Sigil b) => a.Span.SequenceCompareTo(b.Span);

    /// <summary>One fake-group clique this node is part of: the shared definition, its live edges, and its lifetime.</summary>
    private sealed class PageantSession : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;

        public PageantSession(Pageant definition, int ordinal, bool isInitiator, CancellationToken parent)
        {
            Definition = definition;
            MyOrdinal = ordinal;
            IsInitiator = isInitiator;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        public Pageant Definition { get; set; }
        public int MyOrdinal { get; }
        public bool IsInitiator { get; }
        public ConcurrentDictionary<Sigil, Effigy> Edges { get; } = new();
        public Dictionary<Sigil, int> MissStreak { get; } = new();
        public CancellationToken Token => _cts.Token;

        public async ValueTask DisposeAsync()
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); } catch { }
            _cts.Dispose();
            foreach (var edge in Edges.Values)
                await edge.DisposeAsync().ConfigureAwait(false);
            Edges.Clear();
        }
    }
}
