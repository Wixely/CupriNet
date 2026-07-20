using CupriNet.Abstractions;

namespace CupriNet.Concordance;

/// <summary>
/// Asks one peer for its Augury toward a target: the peers it knows that are closest to the target key.
/// The peer answers with referrals only — it never forwards the caller's request or traffic. Over a real
/// network this is one <c>DIVINE</c>/<c>AUGURY</c> round-trip on a Vessel; in simulation it reads a
/// virtual node's Constellation.
/// </summary>
public delegate Task<IReadOnlyList<PeerRecord>> AuguryFunc(PeerRecord peer, RoutingKey target, CancellationToken cancellationToken);

/// <summary>Bounds (Wards) for an iterative Divination lookup.</summary>
public sealed record DivinationOptions
{
    /// <summary>Number of peers queried concurrently per round.</summary>
    public int Alpha { get; init; } = 3;

    /// <summary>Hard cap on how many peers a single lookup will query.</summary>
    public int MaxQueries { get; init; } = 24;

    /// <summary>Max referrals accepted from any one peer's Augury.</summary>
    public int ReferralsPerResponse { get; init; } = 8;

    /// <summary>How many of the closest results to return.</summary>
    public int ResultLimit { get; init; } = 8;
}

/// <summary>The result of a Divination: the closest peers found and how many were queried.</summary>
public sealed record DivinationResult(IReadOnlyList<PeerRecord> Closest, int PeersQueried);

/// <summary>
/// Iterative, bounded referral routing. Starting from seed peers, repeatedly query the α closest
/// not-yet-asked candidates for their Augury toward the target, merge the referrals, and converge on the
/// nodes nearest the target key. No node ever relays another's request — the searcher contacts each
/// suggested peer directly. Every step is bounded by the Wards in <see cref="DivinationOptions"/>.
/// </summary>
public static class Divination
{
    /// <param name="isAnchored">
    /// Optional invitation-anchoring predicate. When supplied, each query round reserves one of its α
    /// slots for the nearest anchored (trusted-relationship) candidate if the α closest would otherwise be
    /// all strangers. This keeps the lookup converging on distance while guaranteeing we still consult a
    /// peer we actually trust each round — so a flood of cheap Sybil strangers packed around the target
    /// cannot fully eclipse the search. Null (the default) preserves pure distance-ordered behaviour.
    /// </param>
    public static async Task<DivinationResult> FindAsync(
        RoutingKey target, IReadOnlyList<PeerRecord> seeds, AuguryFunc askAugury,
        DivinationOptions? options = null, Func<Sigil, bool>? isAnchored = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(askAugury);
        options ??= new DivinationOptions();

        var known = new Dictionary<Sigil, PeerRecord>();
        foreach (var seed in seeds)
            known.TryAdd(seed.Sigil, seed);

        var queried = new HashSet<Sigil>();

        while (queried.Count < options.MaxQueries)
        {
            var candidates = known.Values
                .Where(r => !queried.Contains(r.Sigil))
                .OrderBy(r => RoutingKey.FromSealPublicKey(r.SealPublicKey).DistanceTo(target))
                .ToList();

            var batch = candidates.Take(options.Alpha).ToList();

            // Invitation-anchoring: if this round's closest α are all strangers, swap the farthest of them
            // for the nearest anchored candidate so an eclipse ring of Sybils can't monopolise the search.
            if (isAnchored is not null && batch.Count == options.Alpha && options.Alpha >= 2 && !batch.Any(r => isAnchored(r.Sigil)))
            {
                var nearestAnchored = candidates.Skip(options.Alpha).FirstOrDefault(r => isAnchored(r.Sigil));
                if (nearestAnchored is not null)
                    batch[^1] = nearestAnchored;
            }

            if (batch.Count == 0)
                break;

            foreach (var peer in batch)
                queried.Add(peer.Sigil);

            var responses = await Task.WhenAll(batch.Select(peer => AskSafelyAsync(askAugury, peer, target, cancellationToken)))
                .ConfigureAwait(false);

            foreach (var referrals in responses)
            {
                var taken = 0;
                foreach (var referral in referrals)
                {
                    if (taken >= options.ReferralsPerResponse)
                        break;
                    taken++;
                    known.TryAdd(referral.Sigil, referral);
                }
            }
        }

        var closest = known.Values
            .OrderBy(r => RoutingKey.FromSealPublicKey(r.SealPublicKey).DistanceTo(target))
            .Take(options.ResultLimit)
            .ToList();

        return new DivinationResult(closest, queried.Count);
    }

    private static async Task<IReadOnlyList<PeerRecord>> AskSafelyAsync(AuguryFunc askAugury, PeerRecord peer, RoutingKey target, CancellationToken cancellationToken)
    {
        try
        {
            return await askAugury(peer, target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // An unreachable or misbehaving peer simply yields no referrals; the lookup routes around it.
            return [];
        }
    }
}
