using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Concordance;

namespace CupriNet.Arcanum;

/// <summary>Stores a Decree at a node reached near a channel's Ascendant.</summary>
public delegate Task DecreePublishFunc(PeerRecord holder, Decree decree, CancellationToken cancellationToken);

/// <summary>Asks a reached node for the Decrees it holds matching a Glyph window.</summary>
public delegate Task<IReadOnlyList<Decree>> DecreeLookupFunc(PeerRecord holder, IReadOnlyList<byte[]> glyphWindow, CancellationToken cancellationToken);

/// <summary>
/// Ties channel advertisement and discovery to the overlay. Both publish and find route by Divination
/// toward the channel's <em>stable</em> Ascendant (which only Watchword-holders can compute), then act on
/// the reached nodes: a provider deposits its Decree; a searcher looks up Decrees by the current Glyph
/// window and keeps only those that verify and match its own channel. No node forwards channel traffic —
/// it only stores or returns opaque advertisements.
/// </summary>
public static class ChannelDirectory
{
    /// <summary>Publishes a Decree to the nodes closest to the channel's Ascendant. Returns the holder count.</summary>
    public static async Task<int> PublishAsync(
        RoutingKey ascendant, Decree decree, IReadOnlyList<PeerRecord> seeds,
        AuguryFunc augury, DecreePublishFunc publish, DivinationOptions? options = null,
        Func<Sigil, bool>? isAnchored = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decree);
        ArgumentNullException.ThrowIfNull(publish);

        var route = await Divination.FindAsync(ascendant, seeds, augury, options, isAnchored, cancellationToken).ConfigureAwait(false);
        var holders = 0;
        foreach (var holder in route.Closest)
        {
            await publish(holder, decree, cancellationToken).ConfigureAwait(false);
            holders++;
        }

        return holders;
    }

    /// <summary>
    /// Finds providers for a channel: routes to its Ascendant, queries reached nodes for the current Glyph
    /// window, and returns the verified, matching Decrees (deduped to the newest per provider).
    /// </summary>
    public static async Task<IReadOnlyList<Decree>> FindProvidersAsync(
        ArcanumKeys keys, DateTimeOffset now, ICryptoSuite suite, IReadOnlyList<PeerRecord> seeds,
        AuguryFunc augury, DecreeLookupFunc lookup, DivinationOptions? options = null,
        long turningSeconds = Glyph.DefaultTurningSeconds, Func<Sigil, bool>? isAnchored = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(lookup);

        var window = Glyph.Window(keys, now, suite, turningSeconds);
        var route = await Divination.FindAsync(keys.Ascendant, seeds, augury, options, isAnchored, cancellationToken).ConfigureAwait(false);

        var byProvider = new Dictionary<Sigil, Decree>();
        foreach (var holder in route.Closest)
        {
            var decrees = await lookup(holder, window, cancellationToken).ConfigureAwait(false);
            foreach (var decree in decrees)
            {
                if (!DecreeSigner.Verify(decree, suite))
                    continue;
                if (!DecreeValidator.Matches(decree, keys, now, suite, turningSeconds))
                    continue;
                if (!byProvider.TryGetValue(decree.ProviderSigil, out var existing) || decree.SequenceNumber > existing.SequenceNumber)
                    byProvider[decree.ProviderSigil] = decree;
            }
        }

        return byProvider.Values.ToList();
    }
}
