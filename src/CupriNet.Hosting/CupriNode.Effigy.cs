using CupriNet.Abstractions;
using CupriNet.Concordance;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// Effigies: decoy <em>channel</em> sessions. Hot fuzz shapes L1 control links; an Effigy goes a layer further and
/// mimics a real L2 channel session — a direct, chat-shaped connection to a cooperating partner over a throwaway
/// coordinate that is never published, persisted, or joinable. On the wire (encrypted frames on the channel
/// stream) it is indistinguishable from a genuine channel; only the encrypted session-kind marker tells the two
/// endpoints to run cover traffic. So a real channel session hides among decoys that carry human-like
/// conversation traffic.
/// <para>
/// Two forms: independent one-to-one Effigies (each with its own conversation shape), and one <em>coordinated
/// cohort</em> whose members burst together — reproducing the fan-out of a real group channel (a mesh of direct
/// Vessels that light up at once when you post), which independent decoys cannot imitate.
/// </para>
/// </summary>
public sealed partial class CupriNode
{
    private readonly object _effigyGate = new();
    private readonly List<Effigy> _effigies = [];       // independent one-to-one decoy sessions
    private readonly List<Effigy> _effigyCohort = [];   // one coordinated fan-out cohort (group cover)
    private int _cohortDriverRunning;

    /// <summary>Independent Effigy sessions currently held. Exposed for tests.</summary>
    internal int EffigyCountActive { get { lock (_effigyGate) return _effigies.Count; } }

    /// <summary>Members of the coordinated Effigy cohort currently held. Exposed for tests.</summary>
    internal int EffigyCohortSize { get { lock (_effigyGate) return _effigyCohort.Count; } }

    /// <summary>
    /// Starts the Effigy maintenance loop if enabled. Off unless <see cref="CupriNodeOptions.EnableEffigies"/>
    /// (Effigies push continuous cover traffic — real bandwidth) and only on an unmetered power profile.
    /// </summary>
    internal void StartEffigies()
    {
        // Off in Tor-only mode unless the app opts in (AllowCoverTrafficOverTor): L2-shaped cover over Tor is
        // high-bandwidth for little gain (see StartHotFuzz).
        if (_options is { EnableEffigies: true, Power: PowerProfile.Unmetered }
            && (_options.EffigyCount > 0 || _options.EffigyGroupSize > 0)
            && (_options.Mode != ReachabilityMode.TorOnly || _options.AllowCoverTrafficOverTor))
            _ = EffigyLoopAsync(_lifetime.Token);
    }

    private async Task EffigyLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await EffigyOnceAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* transient — keep the sessions we have */ }

            try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false); }
            catch { break; }
        }
    }

    /// <summary>
    /// One reconcile round: top the independent Effigies and the coordinated cohort back up to their target
    /// sizes with fresh, subnet-diverse partners. Public so tests can drive it deterministically (auto loop off).
    /// </summary>
    public async Task EffigyOnceAsync(CancellationToken cancellationToken = default)
    {
        var inUse = CurrentPartners();

        var singleDeficit = _options.EffigyCount - EffigyCountActive;
        foreach (var peer in singleDeficit > 0 ? SelectEffigyPartners(singleDeficit, inUse) : [])
        {
            try
            {
                var effigy = await EstablishEffigyAsync(peer, cancellationToken).ConfigureAwait(false);
                lock (_effigyGate) _effigies.Add(effigy);
                inUse.Add(peer.Sigil);
                StartSingleShaper(effigy);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* partner unreachable — another fills the slot next round */ }
        }

        if (_options.EffigyGroupSize > 0)
        {
            var groupDeficit = _options.EffigyGroupSize - EffigyCohortSize;
            foreach (var peer in groupDeficit > 0 ? SelectEffigyPartners(groupDeficit, inUse) : [])
            {
                try
                {
                    var effigy = await EstablishEffigyAsync(peer, cancellationToken).ConfigureAwait(false);
                    lock (_effigyGate) _effigyCohort.Add(effigy);
                    inUse.Add(peer.Sigil);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            EnsureCohortDriver();
        }
    }

    private async Task<Effigy> EstablishEffigyAsync(PeerRecord peer, CancellationToken cancellationToken)
    {
        var vessel = await DialEffigyAsync(peer, cancellationToken).ConfigureAwait(false);
        var effigy = new Effigy(peer.Sigil, vessel, _lifetime.Token);
        _ = DrainEffigyAsync(effigy); // absorb the partner's symmetric cover; retire the session when it closes
        return effigy;
    }

    /// <summary>Dials a partner and opens an Effigy session — identical on the wire to dialing a channel peer.</summary>
    private async Task<IVessel> DialEffigyAsync(PeerRecord peer, CancellationToken cancellationToken)
    {
        // Route over the lane matching our mode (onion in Tor-only), so effigies work — and stay off clearnet — over Tor.
        var vessel = await DialControlVesselAsync(peer, cancellationToken).ConfigureAwait(false);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.SolveAsync(vessel, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.InitiateAsync(vessel, Identity, Network, Suite, expectedPeer: peer.Sigil, cancellationToken: timed.Token).ConfigureAwait(false);
            await DeclareSessionKindAsync(conjunction.Vessel, OverlayControl.KindEffigy, timed.Token).ConfigureAwait(false);
            return conjunction.Vessel;
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The responder side of an Effigy: drain the partner's cover and send our own, so both directions are shaped.</summary>
    private async Task ServeEffigyAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;

        var send = Task.Run(async () =>
        {
            var rng = new Random(Random.Shared.Next());
            var shaper = new ConversationShaper();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var (delay, bytes) = shaper.Next(rng, _options.EffigyMaxMessageBytes);
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    await vessel.SendAsync(OverlayControl.EffigyStream, new byte[Math.Max(1, bytes)], token).ConfigureAwait(false);
                }
            }
            catch { /* cancelled or the session closed */ }
        }, token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (await vessel.ReceiveAsync(token).ConfigureAwait(false) is null)
                    break;
            }
        }
        catch { }
        finally
        {
            linked.Cancel();
            try { await send.ConfigureAwait(false); } catch { }
            await vessel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void StartSingleShaper(Effigy effigy)
    {
        _ = Task.Run(async () =>
        {
            var rng = new Random(Random.Shared.Next());
            var shaper = new ConversationShaper();
            try
            {
                while (!effigy.Token.IsCancellationRequested)
                {
                    var (delay, bytes) = shaper.Next(rng, _options.EffigyMaxMessageBytes);
                    await Task.Delay(delay, effigy.Token).ConfigureAwait(false);
                    await SendCoverAsync(effigy, bytes, effigy.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { RemoveEffigy(effigy); }
        }, effigy.Token);
    }

    /// <summary>
    /// The coordinated cohort's single driver: one conversation shape fans out to <em>all</em> cohort members at
    /// once, reproducing the simultaneous multi-Vessel burst a real group channel emits when a member posts.
    /// </summary>
    private void EnsureCohortDriver()
    {
        if (Interlocked.CompareExchange(ref _cohortDriverRunning, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            var rng = new Random(Random.Shared.Next());
            var shaper = new ConversationShaper();
            try
            {
                while (!_lifetime.Token.IsCancellationRequested)
                {
                    var (delay, bytes) = shaper.Next(rng, _options.EffigyMaxMessageBytes);
                    await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                    Effigy[] members;
                    lock (_effigyGate) members = _effigyCohort.ToArray();
                    if (members.Length == 0)
                        continue;
                    await Task.WhenAll(members.Select(m => SendCoverAsync(m, bytes, _lifetime.Token))).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { Interlocked.Exchange(ref _cohortDriverRunning, 0); }
        }, _lifetime.Token);
    }

    private async Task SendCoverAsync(Effigy effigy, int bytes, CancellationToken cancellationToken)
    {
        try
        {
            await effigy.Vessel.SendAsync(OverlayControl.EffigyStream, new byte[Math.Max(1, bytes)], cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref effigy.SentFrames);
        }
        catch (OperationCanceledException) { throw; }
        catch { RemoveEffigy(effigy); } // the partner went away — drop it so the next reconcile refills
    }

    private async Task DrainEffigyAsync(Effigy effigy)
    {
        try
        {
            while (!effigy.Token.IsCancellationRequested)
            {
                if (await effigy.Vessel.ReceiveAsync(effigy.Token).ConfigureAwait(false) is null)
                    break;
            }
        }
        catch { }
        finally { RemoveEffigy(effigy); }
    }

    /// <summary>Sends one cover frame over every live Effigy (independent + cohort) at once; returns how many landed.
    /// The cohort part is a coordinated fan-out. Exposed for tests (the shapers' first real send is seconds away).</summary>
    internal async Task<int> EffigyProbeAsync(int bytes, CancellationToken cancellationToken = default)
    {
        Effigy[] all;
        lock (_effigyGate)
            all = [.. _effigies, .. _effigyCohort];
        var before = all.Sum(e => Interlocked.Read(ref e.SentFrames));
        await Task.WhenAll(all.Select(e => SendCoverAsync(e, bytes, cancellationToken))).ConfigureAwait(false);
        var after = all.Sum(e => Interlocked.Read(ref e.SentFrames));
        return (int)(after - before);
    }

    private void RemoveEffigy(Effigy effigy)
    {
        bool removed;
        lock (_effigyGate)
            removed = _effigies.Remove(effigy) | _effigyCohort.Remove(effigy);
        if (removed)
            _ = effigy.DisposeAsync();
    }

    private HashSet<Sigil> CurrentPartners()
    {
        lock (_effigyGate)
            return [.. _effigies.Select(e => e.Partner), .. _effigyCohort.Select(e => e.Partner)];
    }

    private IReadOnlyList<PeerRecord> SelectEffigyPartners(int count, HashSet<Sigil> exclude)
    {
        var pool = Constellation.Sample(Math.Max(count * 8, 32))
            .Where(p => p.Sigil != Identity.Sigil && !exclude.Contains(p.Sigil))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        var chosen = new List<PeerRecord>(count);
        var usedSubnets = new HashSet<string>();
        foreach (var peer in pool) // one partner per distinct /16 first, for spread
        {
            if (chosen.Count >= count) break;
            if (usedSubnets.Add(SubnetKey(peer))) chosen.Add(peer);
        }
        foreach (var peer in pool) // then fill from whatever's left
        {
            if (chosen.Count >= count) break;
            if (!chosen.Contains(peer)) chosen.Add(peer);
        }
        return chosen;
    }

    private async ValueTask DisposeEffigiesAsync()
    {
        Effigy[] all;
        lock (_effigyGate)
        {
            all = [.. _effigies, .. _effigyCohort];
            _effigies.Clear();
            _effigyCohort.Clear();
        }
        foreach (var effigy in all)
            await effigy.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>One decoy channel session: the Vessel to a partner, plus a sent-frame counter and its own lifetime.</summary>
    private sealed class Effigy : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        public long SentFrames;

        public Effigy(Sigil partner, IVessel vessel, CancellationToken parent)
        {
            Partner = partner;
            Vessel = vessel;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        public Sigil Partner { get; }
        public IVessel Vessel { get; }
        public CancellationToken Token => _cts.Token;

        public async ValueTask DisposeAsync()
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); } catch { }
            _cts.Dispose();
            await Vessel.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A generative model of one side of a chat: alternating <em>turns</em> (short bursts of messages, sizes skewed
/// small) and <em>idle</em> gaps. Pure (RNG injected) so the shape distribution is unit-testable. Beating a
/// fixed-cadence fingerprint is the whole point — a metronome is itself a signature, so cover must be irregular.
/// </summary>
internal sealed class ConversationShaper
{
    private int _burstRemaining;

    /// <summary>The delay to wait before the next cover message, and that message's size in bytes.</summary>
    public (TimeSpan Delay, int Bytes) Next(Random rng, int maxBytes)
    {
        TimeSpan delay;
        if (_burstRemaining > 0)
        {
            _burstRemaining--;
            delay = TimeSpan.FromMilliseconds(rng.Next(300, 3000)); // within a turn: typing/reading cadence
        }
        else
        {
            _burstRemaining = rng.Next(1, 8);                       // messages in the turn just beginning
            delay = TimeSpan.FromMilliseconds(rng.Next(8000, 90000)); // quiet gap between turns
        }
        return (delay, NextSize(rng, maxBytes));
    }

    private static int NextSize(Random rng, int maxBytes)
    {
        const int lo = 16;
        var hi = Math.Max(lo + 1, maxBytes);
        var u = rng.NextDouble();
        return lo + (int)((hi - lo) * u * u); // square the uniform → skew toward small messages
    }
}
