using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Vessel;
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
/// Carries the Epistolary rite over a stream channel: Veil-sealed Messages and Attestations. This type
/// only frames, seals, and parses — reliability (the Vigil) and idempotency (the deduper) are layered
/// around it by the caller. Use a <see cref="VesselMux"/> stream to run alongside other rites concurrently.
/// </summary>
public sealed class EpistleSession
{
    /// <summary>Default logical stream for channel content (0 Conjunction, 1 peer exchange, 2 Consecration).</summary>
    public const ushort ContentStream = 3;

    private enum FrameType : byte
    {
        Message = 1,
        Attestation = 2,
    }

    private readonly IStreamChannel _channel;
    private readonly VeilCipher _veil;

    public EpistleSession(IStreamChannel channel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _veil = new VeilCipher(sessionKey, suite);
    }

    /// <summary>Convenience: bind to a single Vessel stream directly (single-stream use only).</summary>
    public EpistleSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, ushort stream = ContentStream)
        : this(new VesselStreamChannel(vessel, stream), sessionKey, suite)
    {
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
        var payload = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;

        var reader = new CodexReader(payload);
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
        await _channel.SendAsync(w.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
