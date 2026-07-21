using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;

namespace CupriNet.Concordance;

/// <summary>
/// Local, at-rest cache of the overlay view (the Constellation's signed peer records), for warm-starting the
/// node: on a later run we can reconnect to nodes we already know directly, instead of paying cold-start
/// discovery hops. The records are self-signed, so they are re-verified on load. Whether to persist this at
/// all is the caller's choice — keeping it off (a cold start) leaves nothing about the overlay on disk, which
/// is better for plausible deniability; keeping it on trades that for faster reconnection.
/// </summary>
public static class ConstellationStore
{
    private const string StoreKey = "overlay/constellation";
    private const byte Version = 1;

    /// <summary>Cap on cached records (bounds both what we write and what we admit on load).</summary>
    public const int MaxRecords = 2048;

    /// <summary>Persists the Constellation's non-quarantined records to the secret store.</summary>
    public static async Task SaveAsync(ISecretStore store, Constellation constellation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(constellation);

        var records = constellation.Entries
            .Where(e => e.Bucket != PeerBucket.Excommunicate)
            .Select(e => e.Record)
            .Take(MaxRecords)
            .ToList();

        var w = new CodexWriter();
        w.WriteByte(Version);
        w.WriteVarUInt((ulong)records.Count);
        foreach (var record in records)
            w.WriteBytes(PeerRecordCodec.Encode(record));

        await store.StoreAsync(StoreKey, w.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads cached records, re-verifying each signature. Returns an empty list if there is no cache.</summary>
    public static async Task<IReadOnlyList<PeerRecord>> LoadAsync(ISecretStore store, ICryptoSuite suite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(suite);

        var bytes = await store.LoadAsync(StoreKey, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return [];

        var reader = new CodexReader(bytes);
        if (reader.ReadByte() != Version)
            return [];
        var count = Math.Min(reader.ReadVarUInt(), (ulong)MaxRecords);

        var records = new List<PeerRecord>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            PeerRecord record;
            try { record = PeerRecordCodec.Decode(reader.ReadBytes()).Record; }
            catch { break; }
            if (PeerRecordSigner.Verify(record, suite))
                records.Add(record);
        }
        return records;
    }

    /// <summary>Deletes the cached overlay view (a user "forget the network" / go-cold action).</summary>
    public static Task ClearAsync(ISecretStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.DeleteAsync(StoreKey, cancellationToken).AsTask();
    }
}
