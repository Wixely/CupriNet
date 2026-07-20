using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Vessel;
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

    private readonly IStreamChannel _channel;
    private readonly VeilCipher _veil;

    public ConduitSession(IStreamChannel channel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _veil = new VeilCipher(sessionKey, suite);
    }

    /// <summary>Convenience: bind to a single Vessel stream directly (single-stream use only).</summary>
    public ConduitSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, ushort stream = DataStream)
        : this(new VesselStreamChannel(vessel, stream), sessionKey, suite)
    {
    }

    public Task SendAsync(ConduitFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _channel.SendAsync(_veil.Seal(ConduitCodec.Encode(frame)), cancellationToken).AsTask();
    }

    public async Task<ConduitFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;

        var plaintext = _veil.Open(payload)
                        ?? throw new EpistleException("Conduit content failed authentication (Veil).");
        return ConduitCodec.Decode(plaintext);
    }
}
