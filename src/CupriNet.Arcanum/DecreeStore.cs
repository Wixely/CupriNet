using CupriNet.Abstractions;

namespace CupriNet.Arcanum;

/// <summary>The outcome of offering a Decree to a store.</summary>
public enum DecreePublishResult
{
    Published,
    Updated,
    Rejected,
}

/// <summary>Bounds (Wards) for a <see cref="DecreeStore"/>.</summary>
public sealed record DecreeStoreOptions
{
    /// <summary>Max distinct providers held for a single Glyph.</summary>
    public int MaxDecreesPerGlyph { get; init; } = 8;

    /// <summary>Max distinct Glyphs tracked before new ones are refused.</summary>
    public int MaxGlyphs { get; init; } = 4096;

    /// <summary>
    /// Longest accepted advertisement lifetime. A provider cannot pin a slot with a far-future expiry;
    /// Decrees claiming to live longer than this from now are refused (a Ward).
    /// </summary>
    public long MaxLifetimeSeconds { get; init; } = 3600; // 1 hour
}

/// <summary>
/// A node's bounded, self-expiring store of channel advertisements, indexed by Glyph. A node near a
/// channel's Ascendant holds its Decrees here; searchers that route to such a node look them up by the
/// current-epoch Glyph. Short TTLs, per-Glyph provider caps, and a global Glyph cap bound the memory a
/// hostile advertiser can consume.
/// </summary>
public sealed class DecreeStore
{
    private readonly Dictionary<string, List<Decree>> _byGlyph = new(StringComparer.Ordinal);
    private readonly DecreeStoreOptions _options;

    public DecreeStore(DecreeStoreOptions? options = null)
        => _options = options ?? new DecreeStoreOptions();

    public int GlyphCount => _byGlyph.Count;

    /// <summary>Stores a Decree, replacing an earlier one from the same provider if newer.</summary>
    public DecreePublishResult Publish(Decree decree, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decree);

        var nowUnix = now.ToUnixTimeSeconds();
        if (nowUnix > decree.ExpiresAtUnix)
            return DecreePublishResult.Rejected;
        if (decree.ExpiresAtUnix > nowUnix + _options.MaxLifetimeSeconds)
            return DecreePublishResult.Rejected; // refuse far-future expiry (memory-pinning Ward)

        var key = Convert.ToHexStringLower(decree.Glyph);
        if (!_byGlyph.TryGetValue(key, out var bucket))
        {
            if (_byGlyph.Count >= _options.MaxGlyphs)
            {
                PruneExpired(now);
                if (_byGlyph.Count >= _options.MaxGlyphs)
                    return DecreePublishResult.Rejected;
            }

            bucket = [];
            _byGlyph[key] = bucket;
        }

        PruneBucket(bucket, now);

        var providerSigil = decree.ProviderSigil;
        var existingIndex = bucket.FindIndex(d => d.ProviderSigil == providerSigil);
        if (existingIndex >= 0)
        {
            if (decree.SequenceNumber <= bucket[existingIndex].SequenceNumber)
                return DecreePublishResult.Rejected;
            bucket[existingIndex] = decree;
            return DecreePublishResult.Updated;
        }

        if (bucket.Count >= _options.MaxDecreesPerGlyph)
        {
            // Full: evict the soonest-to-expire, but only if the newcomer outlasts it.
            var soonest = bucket[0];
            foreach (var d in bucket)
            {
                if (d.ExpiresAtUnix < soonest.ExpiresAtUnix)
                    soonest = d;
            }

            if (decree.ExpiresAtUnix <= soonest.ExpiresAtUnix)
                return DecreePublishResult.Rejected;
            bucket.Remove(soonest);
        }

        bucket.Add(decree);
        return DecreePublishResult.Published;
    }

    /// <summary>Returns the unexpired Decrees advertised for a Glyph.</summary>
    public IReadOnlyList<Decree> Lookup(byte[] glyph, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        if (!_byGlyph.TryGetValue(Convert.ToHexStringLower(glyph), out var bucket))
            return [];

        PruneBucket(bucket, now);
        return bucket.ToList();
    }

    /// <summary>Returns the unexpired Decrees advertised for any Glyph in a window (dedup by provider).</summary>
    public IReadOnlyList<Decree> LookupWindow(IEnumerable<byte[]> glyphs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        var byProvider = new Dictionary<Sigil, Decree>();
        foreach (var glyph in glyphs)
        {
            foreach (var decree in Lookup(glyph, now))
            {
                if (!byProvider.TryGetValue(decree.ProviderSigil, out var existing) || decree.SequenceNumber > existing.SequenceNumber)
                    byProvider[decree.ProviderSigil] = decree;
            }
        }

        return byProvider.Values.ToList();
    }

    /// <summary>Drops all expired Decrees and empty buckets.</summary>
    public void Prune(DateTimeOffset now) => PruneExpired(now);

    private void PruneExpired(DateTimeOffset now)
    {
        var emptyKeys = new List<string>();
        foreach (var (key, bucket) in _byGlyph)
        {
            PruneBucket(bucket, now);
            if (bucket.Count == 0)
                emptyKeys.Add(key);
        }

        foreach (var key in emptyKeys)
            _byGlyph.Remove(key);
    }

    private static void PruneBucket(List<Decree> bucket, DateTimeOffset now)
    {
        var cutoff = now.ToUnixTimeSeconds();
        bucket.RemoveAll(d => cutoff > d.ExpiresAtUnix);
    }
}
