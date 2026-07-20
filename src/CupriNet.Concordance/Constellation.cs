using CupriNet.Abstractions;

namespace CupriNet.Concordance;

/// <summary>The bucket a peer occupies within the Constellation.</summary>
public enum PeerBucket
{
    /// <summary>Nodes with an authenticated pairwise relationship.</summary>
    Kindred,

    /// <summary>Recently reachable nodes useful for referrals.</summary>
    Wayfarers,

    /// <summary>Newly learned, untrusted candidates.</summary>
    Strangers,

    /// <summary>Nodes known to advertise particular Arcana.</summary>
    Devotees,

    /// <summary>Invalid, abusive, or repeatedly unreachable nodes.</summary>
    Excommunicate,
}

/// <summary>The outcome of offering a record to the Constellation.</summary>
public enum AdmissionResult
{
    Admitted,
    Updated,
    RejectedStale,
    RejectedDiversity,
    RejectedFull,
}

/// <summary>A peer's slot in the Constellation: its record plus locally-maintained, non-authoritative metadata.</summary>
public sealed class ConstellationEntry
{
    internal ConstellationEntry(PeerRecord record, PeerBucket bucket, DateTimeOffset lastSeen, string? source, uint? slash24)
    {
        Record = record;
        Bucket = bucket;
        LastSeen = lastSeen;
        Source = source;
        Slash24 = slash24;
    }

    public PeerRecord Record { get; internal set; }
    public PeerBucket Bucket { get; internal set; }
    public int Standing { get; internal set; }
    public int Taint { get; internal set; }
    public DateTimeOffset LastSeen { get; internal set; }
    public string? Source { get; }
    public uint? Slash24 { get; internal set; }

    public Sigil Sigil => Record.Sigil;
}

/// <summary>Tunable bounds (Wards) for the Constellation.</summary>
public sealed record ConstellationOptions
{
    public int MaxRecords { get; init; } = 2000;
    public int MaxPerSlash24 { get; init; } = 2;
    public int TaintQuarantineThreshold { get; init; } = 5;

    /// <summary>Cap on anchored (trusted-relationship) Sigils remembered for invitation-anchoring.</summary>
    public int MaxAnchored { get; init; } = 4096;
}

/// <summary>
/// A node's bounded, bucketed view of the network. Enforces the abuse Wards: dedup by Sigil (newer
/// sequence wins), a hard record cap with worst-first eviction, and a per-/24 diversity quota
/// (Temperance). Local Standing/Taint never propagate as authoritative fact; enough Taint quarantines
/// a peer (Excommunication). This type stores only records the caller has already validated.
/// </summary>
public sealed class Constellation
{
    private readonly Dictionary<Sigil, ConstellationEntry> _entries = new();
    private readonly HashSet<Sigil> _anchored = new();
    private readonly ConstellationOptions _options;

    public Constellation(ConstellationOptions? options = null)
        => _options = options ?? new ConstellationOptions();

    public int Count => _entries.Count;

    public IEnumerable<ConstellationEntry> Entries => _entries.Values;

    public int CountInBucket(PeerBucket bucket)
    {
        var count = 0;
        foreach (var entry in _entries.Values)
        {
            if (entry.Bucket == bucket)
                count++;
        }

        return count;
    }

    public ConstellationEntry? Get(Sigil sigil) => _entries.GetValueOrDefault(sigil);

    /// <summary>Offers a (pre-validated) record for admission or update.</summary>
    public AdmissionResult Admit(PeerRecord record, PeerBucket bucket, DateTimeOffset now, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var sigil = record.Sigil;

        if (_entries.TryGetValue(sigil, out var existing))
        {
            if (record.SequenceNumber <= existing.Record.SequenceNumber)
                return AdmissionResult.RejectedStale;

            existing.Record = record;
            existing.LastSeen = now;
            existing.Slash24 = NetworkDiversity.Slash24(record.Endpoints);
            return AdmissionResult.Updated;
        }

        var slash24 = NetworkDiversity.Slash24(record.Endpoints);
        if (slash24 is { } prefix && CountInSlash24(prefix) >= _options.MaxPerSlash24)
            return AdmissionResult.RejectedDiversity;

        if (_entries.Count >= _options.MaxRecords && !TryEvictOne())
            return AdmissionResult.RejectedFull;

        _entries[sigil] = new ConstellationEntry(record, bucket, now, source, slash24);
        return AdmissionResult.Admitted;
    }

    /// <summary>Rewards good behaviour (successful contact or referral).</summary>
    public void Reward(Sigil sigil, int amount = 1)
    {
        if (_entries.TryGetValue(sigil, out var entry))
            entry.Standing += amount;
    }

    /// <summary>Records misbehaviour; crossing the threshold quarantines the peer (Excommunication).</summary>
    public void Taint(Sigil sigil, int amount = 1)
    {
        if (!_entries.TryGetValue(sigil, out var entry))
            return;

        entry.Taint += amount;
        if (entry.Taint >= _options.TaintQuarantineThreshold)
            entry.Bucket = PeerBucket.Excommunicate;
    }

    /// <summary>Moves a peer to a different bucket (e.g. promoting a Stranger to Kindred after pairing).</summary>
    public bool Promote(Sigil sigil, PeerBucket bucket)
    {
        if (!_entries.TryGetValue(sigil, out var entry))
            return false;
        entry.Bucket = bucket;
        return true;
    }

    /// <summary>
    /// Marks a peer as <em>anchored</em> — a real relationship reached via an Intonation or a completed
    /// Consecration, not an anonymous gossiped stranger. Anchored peers are preferred in referral routing
    /// (invitation-anchoring) so a flood of cheap Sybil strangers cannot crowd out trusted contacts or
    /// eclipse a lookup. If the peer is already in the table it is also promoted to Kindred.
    /// </summary>
    public void MarkAnchored(Sigil sigil)
    {
        if (sigil.IsEmpty)
            return;
        if (_anchored.Count >= _options.MaxAnchored && !_anchored.Contains(sigil))
            return; // bounded; a real node has few genuine relationships
        _anchored.Add(sigil);
        if (_entries.TryGetValue(sigil, out var entry) && entry.Bucket is not PeerBucket.Excommunicate)
            entry.Bucket = PeerBucket.Kindred;
    }

    /// <summary>
    /// True if the peer is a trusted relationship: explicitly anchored, or held in a non-expendable bucket
    /// (Kindred / Wayfarers / Devotees) rather than Strangers.
    /// </summary>
    public bool IsAnchored(Sigil sigil)
    {
        if (_anchored.Contains(sigil))
            return true;
        return _entries.TryGetValue(sigil, out var entry)
               && entry.Bucket is PeerBucket.Kindred or PeerBucket.Wayfarers or PeerBucket.Devotees;
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> records for a peer-view exchange, preferring distinct /24s
    /// and higher Standing, and never revealing Excommunicated peers.
    /// </summary>
    public IReadOnlyList<PeerRecord> Sample(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return [];

        var ranked = _entries.Values
            .Where(e => e.Bucket != PeerBucket.Excommunicate)
            .OrderByDescending(e => e.Standing)
            .ThenBy(e => e.Taint)
            .ToList();

        var chosen = new List<PeerRecord>(Math.Min(count, ranked.Count));
        var deferred = new List<PeerRecord>();
        var seenPrefixes = new HashSet<uint>();

        foreach (var entry in ranked)
        {
            if (chosen.Count >= count)
                break;

            if (entry.Slash24 is { } prefix && !seenPrefixes.Add(prefix))
                deferred.Add(entry.Record); // already have this /24 — try later if room remains
            else
                chosen.Add(entry.Record);
        }

        foreach (var record in deferred)
        {
            if (chosen.Count >= count)
                break;
            chosen.Add(record);
        }

        return chosen;
    }

    /// <summary>Returns up to <paramref name="k"/> known peers whose Ascendant is closest to the target
    /// (excluding Excommunicated peers). This is the Augury a node offers in response to a Divination.</summary>
    public IReadOnlyList<PeerRecord> ClosestTo(RoutingKey target, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        if (k == 0)
            return [];

        // Distance stays primary so the requester's lookup still converges; among peers we could refer,
        // prefer anchored (trusted) contacts over strangers as the tiebreak.
        return _entries.Values
            .Where(e => e.Bucket != PeerBucket.Excommunicate)
            .OrderBy(e => RoutingKey.FromSealPublicKey(e.Record.SealPublicKey).DistanceTo(target))
            .ThenBy(e => IsAnchored(e.Sigil) ? 0 : 1)
            .Select(e => e.Record)
            .Take(k)
            .ToList();
    }

    private int CountInSlash24(uint prefix)
    {
        var count = 0;
        foreach (var entry in _entries.Values)
        {
            if (entry.Bucket != PeerBucket.Excommunicate && entry.Slash24 == prefix)
                count++;
        }

        return count;
    }

    private bool TryEvictOne()
    {
        // Evict the least valuable expendable peer: Excommunicated first, then Strangers by highest
        // Taint / lowest Standing / oldest contact. Kindred, Wayfarers, and Devotees are retained.
        ConstellationEntry? victim = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.Bucket is not (PeerBucket.Excommunicate or PeerBucket.Strangers))
                continue;
            if (victim is null || IsWorse(entry, victim))
                victim = entry;
        }

        if (victim is null)
            return false;

        _entries.Remove(victim.Sigil);
        return true;
    }

    private static bool IsWorse(ConstellationEntry candidate, ConstellationEntry current)
    {
        // Excommunicate is always more expendable than a Stranger.
        var candidateExcommunicated = candidate.Bucket == PeerBucket.Excommunicate;
        var currentExcommunicated = current.Bucket == PeerBucket.Excommunicate;
        if (candidateExcommunicated != currentExcommunicated)
            return candidateExcommunicated;

        if (candidate.Taint != current.Taint)
            return candidate.Taint > current.Taint;
        if (candidate.Standing != current.Standing)
            return candidate.Standing < current.Standing;
        return candidate.LastSeen < current.LastSeen;
    }
}
