using System.Collections.Concurrent;
using System.Net;
using CupriNet.Abstractions;
using CupriNet.Concordance;
using CupriNet.Core;

namespace CupriNet.Hosting;

/// <summary>
/// Hot fuzz: a set of long-lived decoy control connections held open to random overlay nodes and kept warm with
/// padded cover traffic. Ordinary gossip (see <see cref="GossipOnceAsync"/>) fuzzes connection <em>events</em> —
/// who we dial and when. Hot fuzz fuzzes connection <em>lifetime and volume</em>: because we always hold several
/// enduring, chatty connections to random nodes, an observer cannot pick out a real channel session (also
/// enduring and chatty) from the decoys by how long it lasts or how much it carries.
/// </summary>
public sealed partial class CupriNode
{
    /// <summary>The current companions: overlay Sigil → the peer we hold and when its randomized hold expires.</summary>
    private readonly ConcurrentDictionary<Sigil, HotFuzzCompanion> _companions = new();

    private sealed record HotFuzzCompanion(PeerRecord Peer, DateTimeOffset ExpiresAt);

    /// <summary>Number of decoy companions currently held. Exposed for tests.</summary>
    internal int HotFuzzCompanionCount => _companions.Count;

    /// <summary>
    /// Starts the hot-fuzz maintenance loop if enabled. It runs only alongside gossip (it shares the control
    /// plane and the map it builds) and only on an unmetered power profile (holding sockets open is costly on
    /// battery/metered links).
    /// </summary>
    internal void StartHotFuzz()
    {
        if (_options is { EnableHotFuzz: true, EnableOverlayGossip: true, Power: PowerProfile.Unmetered }
            && _options.HotFuzzDegree > 0)
            _ = HotFuzzLoopAsync(_lifetime.Token);
    }

    private async Task HotFuzzLoopAsync(CancellationToken cancellationToken)
    {
        var baseInterval = _options.HotFuzzHeartbeatInterval;
        if (baseInterval <= TimeSpan.Zero)
            baseInterval = TimeSpan.FromSeconds(20);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Jitter each beat by ±50% so the heartbeat cadence itself isn't a fixed, fingerprintable clock.
            var jitter = 0.5 + Random.Shared.NextDouble();
            var wait = TimeSpan.FromMilliseconds(baseInterval.TotalMilliseconds * jitter);
            try { await Task.Delay(wait, cancellationToken).ConfigureAwait(false); }
            catch { break; }

            try { await HotFuzzOnceAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* transient — keep the companions alive across a bad round */ }
        }
    }

    /// <summary>
    /// One hot-fuzz round: rotate out expired companions, top the set back up to <see
    /// cref="CupriNodeOptions.HotFuzzDegree"/> with fresh, subnet-diverse random nodes, then send a padded
    /// heartbeat over every companion. Public so tests can drive it deterministically (with the auto loop off).
    /// </summary>
    public async Task HotFuzzOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Rotate out companions whose randomized hold has elapsed.
        foreach (var (sigil, companion) in _companions.ToArray())
        {
            if (companion.ExpiresAt <= now && _companions.TryRemove(sigil, out _))
                EvictControl(sigil); // drop the pooled connection; a fresh node takes its place below
        }

        // 2. Top up to the target degree with fresh, diverse random nodes.
        var deficit = _options.HotFuzzDegree - _companions.Count;
        if (deficit > 0)
        {
            foreach (var peer in SelectHotFuzzCandidates(deficit))
            {
                try
                {
                    await GetControlAsync(peer, cancellationToken).ConfigureAwait(false); // dial + pin in the pool
                    var hold = HotFuzz.NextHold(
                        Random.Shared, _options.HotFuzzMinHold, _options.HotFuzzMaxHold,
                        _options.HotFuzzLongHoldProbability, _options.HotFuzzLongHold);
                    _companions[peer.Sigil] = new HotFuzzCompanion(peer, now + hold);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* unreachable — leave the slot open, another node fills it next round */ }
            }
        }

        // 3. Heartbeat every live companion with padded cover traffic.
        await Task.WhenAll(_companions.Values.Select(c => HeartbeatAsync(c.Peer, cancellationToken))).ConfigureAwait(false);
    }

    private async Task HeartbeatAsync(PeerRecord peer, CancellationToken cancellationToken)
    {
        try
        {
            var conn = await GetControlAsync(peer, cancellationToken).ConfigureAwait(false);
            var coop = _options.HotFuzzCooperativePadding;
            var downPad = coop ? _options.HotFuzzPaddingBytes : 0;
            var upPad = coop ? RandomPad(_options.HotFuzzPaddingBytes) : [];
            _ = await conn.RoundtripAsync(OverlayControl.PingRequest(downPad, upPad), cancellationToken).ConfigureAwait(false);
            Constellation.Reward(peer.Sigil); // a companion that answers earns a little standing
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // The companion went away (idle-closed, NAT dropped it, refused). Retire it so the next round refills.
            _companions.TryRemove(peer.Sigil, out _);
            EvictControl(peer.Sigil);
        }
    }

    /// <summary>Random padding of a jittered size in [max/2, max], so heartbeat volume varies round to round.</summary>
    private static byte[] RandomPad(int max)
    {
        if (max <= 0)
            return [];
        var buf = new byte[Random.Shared.Next(max / 2, max + 1)];
        Random.Shared.NextBytes(buf);
        return buf;
    }

    /// <summary>
    /// Picks up to <paramref name="count"/> fresh companions, preferring one node per /16 subnet before doubling
    /// up, so the decoy set is spread across the network and an eclipse attacker can't simply <em>be</em> all our
    /// decoys. Falls back to filling from any known node when diversity runs out (e.g. a loopback test net).
    /// </summary>
    private IReadOnlyList<PeerRecord> SelectHotFuzzCandidates(int count)
    {
        var pool = Constellation.Sample(Math.Max(count * 8, 32))
            .Where(p => p.Sigil != Identity.Sigil && !_companions.ContainsKey(p.Sigil))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        var chosen = new List<PeerRecord>(count);
        var usedSubnets = new HashSet<string>();

        // First pass: one companion per distinct subnet.
        foreach (var peer in pool)
        {
            if (chosen.Count >= count)
                break;
            if (usedSubnets.Add(SubnetKey(peer)))
                chosen.Add(peer);
        }
        // Second pass: fill any remaining slots from whatever's left (same-subnet is fine now).
        foreach (var peer in pool)
        {
            if (chosen.Count >= count)
                break;
            if (!chosen.Contains(peer))
                chosen.Add(peer);
        }
        return chosen;
    }

    /// <summary>A coarse locality key: the /16 of the peer's first dialable IPv4 beacon (or the host string itself).</summary>
    private static string SubnetKey(PeerRecord peer)
    {
        var beacon = peer.Endpoints.FirstOrDefault(b => b.Kind is EndpointKind.Host or EndpointKind.Mapped or EndpointKind.Manual);
        if (beacon is null)
            return peer.Sigil.ToString(); // no beacon — treat as its own locality
        if (!IPAddress.TryParse(beacon.Host, out var ip))
            return beacon.Host; // a hostname — key by name
        var bytes = ip.GetAddressBytes();
        return bytes.Length >= 2 ? $"{bytes[0]}.{bytes[1]}" : beacon.Host;
    }
}

/// <summary>Pure hot-fuzz scheduling helpers (no I/O), split out so the hold-time distribution can be unit-tested.</summary>
internal static class HotFuzz
{
    /// <summary>
    /// Draws a randomized companion hold. Most draws land in the ordinary band <c>[min, max]</c>; with
    /// <paramref name="longHoldProbability"/> the draw instead lands in the heavy tail <c>[max, longHold]</c>.
    /// The tail is what stops an hours-long real session from being the unique connection that never rotates.
    /// </summary>
    public static TimeSpan NextHold(Random rng, TimeSpan min, TimeSpan max, double longHoldProbability, TimeSpan longHold)
    {
        if (max < min)
            max = min;
        double lo, hi;
        if (rng.NextDouble() < longHoldProbability)
        {
            lo = max.TotalMilliseconds;
            hi = Math.Max(longHold.TotalMilliseconds, lo);
        }
        else
        {
            lo = min.TotalMilliseconds;
            hi = max.TotalMilliseconds;
        }
        return TimeSpan.FromMilliseconds(lo + rng.NextDouble() * (hi - lo));
    }
}
