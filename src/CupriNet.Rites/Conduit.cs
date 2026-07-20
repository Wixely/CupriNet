using CupriNet.Alembic;
using CupriNet.Codex;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Rites;

/// <summary>
/// A generic, application-negotiated data frame. Applications agree on a ProtocolId and SchemaVersion and
/// exchange opaque payloads; CupriNet only frames and seals them. Ordering and backpressure are inherited
/// from the Vessel stream — never buffer unbounded data above this layer.
/// </summary>
public sealed record ConduitFrame
{
    public required uint ProtocolId { get; init; }
    public required uint SchemaVersion { get; init; }
    public required uint Flags { get; init; }
    public required byte[] Payload { get; init; }
}

/// <summary>Canonical serialization for <see cref="ConduitFrame"/>.</summary>
public static class ConduitCodec
{
    public static byte[] Encode(ConduitFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var w = new CodexWriter();
        w.WriteUInt32(frame.ProtocolId);
        w.WriteUInt32(frame.SchemaVersion);
        w.WriteUInt32(frame.Flags);
        w.WriteBytes(frame.Payload);
        return w.ToArray();
    }

    public static ConduitFrame Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        return new ConduitFrame
        {
            ProtocolId = r.ReadUInt32(),
            SchemaVersion = r.ReadUInt32(),
            Flags = r.ReadUInt32(),
            Payload = r.ReadBytes().ToArray(),
        };
    }
}

/// <summary>Carries the Conduit (data) rite as Veil-sealed frames over a Vessel stream.</summary>
public sealed class ConduitSession
{
    /// <summary>Logical stream for generic data (0 Conjunction, 1 peer exchange, 2 Consecration, 3 Epistles).</summary>
    public const ushort DataStream = 4;

    private readonly VesselSession _vessel;
    private readonly VeilCipher _veil;
    private readonly ushort _stream;

    public ConduitSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, ushort stream = DataStream)
    {
        _vessel = vessel ?? throw new ArgumentNullException(nameof(vessel));
        _veil = new VeilCipher(sessionKey, suite);
        _stream = stream;
    }

    public Task SendAsync(ConduitFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _vessel.SendAsync(_stream, _veil.Seal(ConduitCodec.Encode(frame)), cancellationToken).AsTask();
    }

    public async Task<ConduitFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var frame = await _vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return null;
        if (frame.Value.StreamId != _stream)
            throw new EpistleException($"Unexpected frame on stream {frame.Value.StreamId}.");

        var plaintext = _veil.Open(frame.Value.Payload)
                        ?? throw new EpistleException("Conduit content failed authentication (Veil).");
        return ConduitCodec.Decode(plaintext);
    }
}
