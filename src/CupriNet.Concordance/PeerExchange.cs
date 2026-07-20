using CupriNet.Alembic;
using CupriNet.Codex;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Concordance;

/// <summary>Tallies the result of admitting a received peer sample.</summary>
public sealed record PeerExchangeOutcome(int Admitted, int Updated, int Rejected, int Invalid);

/// <summary>
/// The sampled peer-view exchange: two paired nodes gossip a bounded, diverse sample of signed
/// PeerRecords over a Vessel stream. Full contact lists are never exchanged; the sample size is a hard
/// Ward. Received records are signature-checked before admission, and admission still runs every
/// Constellation Ward (dedup, diversity, capacity). Peer exchange is Layer-1 data — it may traverse
/// paired links freely; it never carries Layer-2 (Arcanum) content.
/// </summary>
public static class PeerExchange
{
    /// <summary>Default logical stream for peer exchange (stream 0 is reserved for the Conjunction handshake).</summary>
    public const ushort DefaultStream = 1;

    /// <summary>Hard cap on records offered or accepted in a single exchange.</summary>
    public const int MaxRecordsPerExchange = 32;

    private enum MessageType : byte
    {
        Request = 1,
        Sample = 2,
    }

    /// <summary>Requests a peer sample from the other side of a Vessel.</summary>
    public static async Task RequestAsync(VesselSession vessel, int maxRequested, ushort stream = DefaultStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequested);

        var w = new CodexWriter();
        w.WriteByte((byte)MessageType.Request);
        w.WriteVarUInt((ulong)maxRequested);
        await vessel.SendAsync(stream, w.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serves a single peer-sample request: reads the request and replies with a diverse sample.</summary>
    public static async Task ServeOnceAsync(VesselSession vessel, Constellation constellation, ushort stream = DefaultStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        ArgumentNullException.ThrowIfNull(constellation);

        var payload = await ReceiveOnAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
        var reader = new CodexReader(payload);
        if ((MessageType)reader.ReadByte() != MessageType.Request)
            throw new CodexFormatException("Expected a peer-sample Request.");

        var requested = (int)Math.Min((ulong)MaxRecordsPerExchange, reader.ReadVarUInt());
        var sample = constellation.Sample(requested);

        var w = new CodexWriter();
        w.WriteByte((byte)MessageType.Sample);
        w.WriteVarUInt((ulong)sample.Count);
        foreach (var record in sample)
            w.WriteBytes(PeerRecordCodec.Encode(record));

        await vessel.SendAsync(stream, w.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a peer-sample response (does not validate or admit the records).</summary>
    public static async Task<IReadOnlyList<PeerRecord>> ReadSampleAsync(VesselSession vessel, ushort stream = DefaultStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);

        var payload = await ReceiveOnAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
        var reader = new CodexReader(payload);
        if ((MessageType)reader.ReadByte() != MessageType.Sample)
            throw new CodexFormatException("Expected a peer-sample response.");

        var count = reader.ReadVarUInt();
        if (count > MaxRecordsPerExchange)
            throw new CodexFormatException($"Peer sample of {count} exceeds the maximum of {MaxRecordsPerExchange}.");

        var records = new List<PeerRecord>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            var (record, _) = PeerRecordCodec.Decode(reader.ReadBytes());
            records.Add(record);
        }

        return records;
    }

    /// <summary>Validates and admits received records, returning a tally. Invalid signatures are dropped.</summary>
    public static PeerExchangeOutcome AdmitRecords(Constellation constellation, IReadOnlyList<PeerRecord> records, ICryptoSuite suite, DateTimeOffset now, PeerBucket bucket, string? source)
    {
        ArgumentNullException.ThrowIfNull(constellation);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(suite);

        int admitted = 0, updated = 0, rejected = 0, invalid = 0;
        foreach (var record in records)
        {
            if (!PeerRecordSigner.Verify(record, suite))
            {
                invalid++;
                continue;
            }

            switch (constellation.Admit(record, bucket, now, source))
            {
                case AdmissionResult.Admitted: admitted++; break;
                case AdmissionResult.Updated: updated++; break;
                default: rejected++; break;
            }
        }

        return new PeerExchangeOutcome(admitted, updated, rejected, invalid);
    }

    /// <summary>Requests, reads, validates, and admits a peer sample in one call.</summary>
    public static async Task<PeerExchangeOutcome> PullAsync(
        VesselSession vessel, Constellation constellation, ICryptoSuite suite, DateTimeOffset now,
        int maxRequested = MaxRecordsPerExchange, PeerBucket bucket = PeerBucket.Strangers,
        string? source = null, ushort stream = DefaultStream, CancellationToken cancellationToken = default)
    {
        await RequestAsync(vessel, maxRequested, stream, cancellationToken).ConfigureAwait(false);
        var records = await ReadSampleAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
        return AdmitRecords(constellation, records, suite, now, bucket, source);
    }

    private static async Task<byte[]> ReceiveOnAsync(VesselSession vessel, ushort stream, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new CodexFormatException("Vessel closed during peer exchange.");
        if (frame.StreamId != stream)
            throw new CodexFormatException($"Unexpected frame on stream {frame.StreamId} during peer exchange.");
        return frame.Payload;
    }
}
