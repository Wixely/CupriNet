using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Concordance;

namespace CupriNet.Hosting;

/// <summary>
/// A Pageant: a negotiated <em>fake group</em> — a decoy clique. Its members form a full mesh of decoy channel
/// edges and all run one shared conversation schedule derived from <see cref="Seed"/>, so turn-taking correlates
/// across the whole clique (a reply-burst follows a post-burst) exactly like a real group channel — yet every
/// member only ever sends on its own direct edges, so nothing is relayed (Sequestration holds). The roster is a
/// list of signed peer records; a member's ordinal is its index in that shared, ordered roster.
/// </summary>
public sealed record Pageant
{
    /// <summary>Opaque group identifier (also the key an edge cites so the far side can bind it).</summary>
    public required byte[] Id { get; init; }

    /// <summary>The shared seed every member feeds into <see cref="PageantSchedule"/> to compute the same schedule.</summary>
    public required byte[] Seed { get; init; }

    /// <summary>The shared clock anchor; the schedule's utterance times are offsets from this.</summary>
    public required DateTimeOffset Epoch { get; init; }

    /// <summary>The ordered member roster. A member's ordinal (its speaker index) is its position here.</summary>
    public required IReadOnlyList<PeerRecord> Roster { get; init; }

    /// <summary>This node's speaker ordinal, or -1 if it is not in the roster.</summary>
    public int OrdinalOf(Sigil sigil)
    {
        for (var i = 0; i < Roster.Count; i++)
            if (Roster[i].Sigil == sigil)
                return i;
        return -1;
    }
}

/// <summary>Canonical serialization for a <see cref="Pageant"/> (used for both invites and the warm-cache store).</summary>
public static class PageantCodec
{
    /// <summary>Cap on a Pageant's roster — bounds decode work and per-node fan-out cost (which is quadratic).</summary>
    public const int MaxRoster = 8;

    public static byte[] Encode(Pageant pageant)
    {
        ArgumentNullException.ThrowIfNull(pageant);
        var take = pageant.Roster.Count > MaxRoster ? pageant.Roster.Take(MaxRoster).ToList() : pageant.Roster;
        var w = new CodexWriter();
        w.WriteBytes(pageant.Id);
        w.WriteBytes(pageant.Seed);
        w.WriteUInt64((ulong)pageant.Epoch.ToUnixTimeMilliseconds());
        w.WriteVarUInt((ulong)take.Count);
        foreach (var record in take)
            w.WriteBytes(PeerRecordCodec.Encode(record));
        return w.ToArray();
    }

    /// <summary>Decodes a Pageant, re-verifying every roster record's signature. Returns null if malformed.</summary>
    public static Pageant? Decode(ReadOnlySpan<byte> bytes, ICryptoSuite suite)
    {
        try
        {
            var r = new CodexReader(bytes);
            var id = r.ReadBytes().ToArray();
            var seed = r.ReadBytes().ToArray();
            var epoch = DateTimeOffset.FromUnixTimeMilliseconds((long)r.ReadUInt64());
            var count = Math.Min(r.ReadVarUInt(), (ulong)MaxRoster);

            var roster = new List<PeerRecord>((int)count);
            for (var i = 0UL; i < count; i++)
            {
                var record = PeerRecordCodec.Decode(r.ReadBytes()).Record;
                if (!PeerRecordSigner.Verify(record, suite))
                    return null; // a roster with an unverifiable member is rejected wholesale
                roster.Add(record);
            }

            if (id.Length == 0 || seed.Length == 0 || roster.Count == 0)
                return null;
            return new Pageant { Id = id, Seed = seed, Epoch = epoch, Roster = roster };
        }
        catch { return null; }
    }
}

/// <summary>A persisted Pageant plus whether this node initiated it (initiators drive re-negotiation).</summary>
public sealed record StoredPageant(bool IsInitiator, Pageant Pageant);

/// <summary>
/// Local warm-cache of the Pageants this node participates in, so a fake group is <em>stable across restarts</em>
/// (a decoy group that reshuffled every launch would itself be a tell). Persisted only when overlay persistence
/// is on; on reload the node re-establishes the clique and, for members that have since left, re-negotiates.
/// </summary>
public static class PageantStore
{
    private const string StoreKey = "overlay/pageants";
    private const byte Version = 1;
    public const int MaxPageants = 16;

    public static async Task SaveAsync(ISecretStore store, IReadOnlyList<StoredPageant> pageants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pageants);
        var take = pageants.Count > MaxPageants ? pageants.Take(MaxPageants).ToList() : pageants;

        var w = new CodexWriter();
        w.WriteByte(Version);
        w.WriteVarUInt((ulong)take.Count);
        foreach (var entry in take)
        {
            w.WriteByte((byte)(entry.IsInitiator ? 1 : 0));
            w.WriteBytes(PageantCodec.Encode(entry.Pageant));
        }

        await store.StoreAsync(StoreKey, w.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<StoredPageant>> LoadAsync(ISecretStore store, ICryptoSuite suite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(suite);

        var bytes = await store.LoadAsync(StoreKey, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return [];

        var r = new CodexReader(bytes);
        if (r.ReadByte() != Version)
            return [];
        var count = Math.Min(r.ReadVarUInt(), (ulong)MaxPageants);

        var list = new List<StoredPageant>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            bool initiator;
            Pageant? pageant;
            try
            {
                initiator = r.ReadByte() == 1;
                pageant = PageantCodec.Decode(r.ReadBytes(), suite);
            }
            catch { break; }
            if (pageant is not null)
                list.Add(new StoredPageant(initiator, pageant));
        }
        return list;
    }

    public static Task ClearAsync(ISecretStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.DeleteAsync(StoreKey, cancellationToken).AsTask();
    }
}

/// <summary>
/// The shared conversation schedule for a Pageant. Driven by a deterministic PRNG (SplitMix64) seeded from the
/// group seed, so <em>every member computes the identical sequence</em> of (gap, speaker, size) utterances — that
/// agreement is what makes the clique's turn-taking correlate without any node relaying another's traffic. Each
/// member acts only on utterances whose speaker is its own ordinal.
/// </summary>
internal sealed class PageantSchedule
{
    private SplitMix64 _rng;
    private readonly int _memberCount;
    private readonly double[] _weights; // per-member talkativeness, so some speak more than others (realistic)
    private readonly double _totalWeight;
    private int _current;
    private int _burstRemaining;

    public PageantSchedule(ReadOnlySpan<byte> seed, int memberCount)
    {
        _memberCount = Math.Max(1, memberCount);
        _rng = new SplitMix64(FoldSeed(seed));
        _weights = new double[_memberCount];
        var total = 0.0;
        for (var i = 0; i < _memberCount; i++)
        {
            var w = 0.4 + _rng.NextDouble();
            _weights[i] = w;
            total += w;
        }
        _totalWeight = total;
        _current = PickSpeaker();
    }

    /// <summary>The next utterance: the gap to wait, who speaks (ordinal), and the message size in bytes.</summary>
    public (TimeSpan Gap, int Speaker, int Size) Next(int maxBytes = 512)
    {
        TimeSpan gap;
        if (_burstRemaining > 0)
        {
            _burstRemaining--;
            gap = TimeSpan.FromMilliseconds(300 + _rng.NextInt(2700)); // within a turn: typing cadence
        }
        else
        {
            _current = PickSpeaker();
            _burstRemaining = _rng.NextInt(4);                          // 0..3 follow-ups in this turn
            gap = TimeSpan.FromMilliseconds(1500 + _rng.NextInt(18000)); // between turns — livelier than a 1:1 idle
        }
        return (gap, _current, NextSize(maxBytes));
    }

    private int PickSpeaker()
    {
        var r = _rng.NextDouble() * _totalWeight;
        var acc = 0.0;
        for (var i = 0; i < _memberCount; i++)
        {
            acc += _weights[i];
            if (r <= acc)
                return i;
        }
        return _memberCount - 1;
    }

    private int NextSize(int maxBytes)
    {
        const int lo = 16;
        var hi = Math.Max(lo + 1, maxBytes);
        var u = _rng.NextDouble();
        return lo + (int)((hi - lo) * u * u); // skew toward small messages
    }

    private static ulong FoldSeed(ReadOnlySpan<byte> seed)
    {
        var acc = 0xCBF29CE484222325UL; // FNV-1a over the seed bytes → a stable 64-bit PRNG seed
        foreach (var b in seed)
        {
            acc ^= b;
            acc *= 0x100000001B3UL;
        }
        return acc;
    }
}

/// <summary>
/// A tiny, fully-deterministic PRNG (SplitMix64). Used instead of <see cref="System.Random"/> for Pageant
/// schedules because every member — in a different process, on a different machine — must reproduce the exact
/// same stream from the shared seed, which the BCL's Random does not guarantee across runtimes.
/// </summary>
internal struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

    public int NextInt(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(Next() % (ulong)maxExclusive);
}
