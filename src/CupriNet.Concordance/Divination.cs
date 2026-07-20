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
    public static async Task<DivinationResult> FindAsync(
        RoutingKey target, IReadOnlyList<PeerRecord> seeds, AuguryFunc askAugury,
        DivinationOptions? options = null, CancellationToken cancellationToken = default)
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
            var batch = known.Values
                .Where(r => !queried.Contains(r.Sigil))
                .OrderBy(r => RoutingKey.FromSealPublicKey(r.SealPublicKey).DistanceTo(target))
                .Take(options.Alpha)
                .ToList();

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
