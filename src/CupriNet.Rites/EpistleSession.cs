using CupriNet.Alembic;
using CupriNet.Codex;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Rites;

/// <summary>Thrown when channel content cannot be authenticated or is malformed.</summary>
public sealed class EpistleException(string message) : Exception(message);

/// <summary>An event read from a channel content stream.</summary>
public abstract record EpistleEvent;

/// <summary>A message arrived.</summary>
public sealed record MessageReceived(Epistle Epistle) : EpistleEvent;

/// <summary>An acknowledgement (Attestation) arrived for a MessageId.</summary>
public sealed record AttestationReceived(byte[] MessageId) : EpistleEvent;

/// <summary>
/// Carries the Epistolary rite over a Vessel: Veil-sealed Messages and Attestations on a dedicated
/// content stream. This type only frames, seals, and parses — reliability (the Vigil) and idempotency
/// (the deduper) are layered around it by the caller.
/// </summary>
public sealed class EpistleSession
{
    /// <summary>Logical stream for channel content (0 Conjunction, 1 peer exchange, 2 Consecration).</summary>
    public const ushort ContentStream = 3;

    private enum FrameType : byte
    {
        Message = 1,
        Attestation = 2,
    }

    private readonly VesselSession _vessel;
    private readonly VeilCipher _veil;
    private readonly ushort _stream;

    public EpistleSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, ushort stream = ContentStream)
    {
        _vessel = vessel ?? throw new ArgumentNullException(nameof(vessel));
        _veil = new VeilCipher(sessionKey, suite);
        _stream = stream;
    }

    public Task SendMessageAsync(Epistle epistle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(epistle);
        return SendFrameAsync(FrameType.Message, EpistleCodec.Encode(epistle), cancellationToken);
    }

    public Task SendAttestationAsync(ReadOnlySpan<byte> messageId, CancellationToken cancellationToken = default)
        => SendFrameAsync(FrameType.Attestation, messageId.ToArray(), cancellationToken);

    /// <summary>Reads the next content event, or null when the peer closes the connection.</summary>
    public async Task<EpistleEvent?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var frame = await _vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return null;
        if (frame.Value.StreamId != _stream)
            throw new EpistleException($"Unexpected frame on stream {frame.Value.StreamId}.");

        var reader = new CodexReader(frame.Value.Payload);
        var type = (FrameType)reader.ReadByte();
        var sealedBody = reader.ReadBytes();

        var plaintext = _veil.Open(sealedBody)
                        ?? throw new EpistleException("Channel content failed authentication (Veil).");

        return type switch
        {
            FrameType.Message => new MessageReceived(EpistleCodec.Decode(plaintext)),
            FrameType.Attestation => new AttestationReceived(plaintext),
            _ => throw new EpistleException($"Unknown content frame type {(byte)type}."),
        };
    }

    private async Task SendFrameAsync(FrameType type, byte[] body, CancellationToken cancellationToken)
    {
        var w = new CodexWriter();
        w.WriteByte((byte)type);
        w.WriteBytes(_veil.Seal(body));
        await _vessel.SendAsync(_stream, w.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
