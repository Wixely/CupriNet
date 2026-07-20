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

    /// <summary>Domain tag for the authenticated-authorship envelope (see <see cref="RiteAuthor"/>).</summary>
    private const string AuthorDomain = "epistle";

    private readonly IStreamChannel _channel;
    private readonly VeilCipher _veil;
    private readonly ICryptoSuite _suite;
    private readonly RiteIdentity? _author;
    private readonly bool _requireAuthor;

    public EpistleSession(IStreamChannel channel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite,
        RiteIdentity? author = null, bool requireAuthor = false)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
        _veil = new VeilCipher(sessionKey, suite);
        _author = author;
        _requireAuthor = requireAuthor;
    }

    /// <summary>Convenience: bind to a single Vessel stream directly (single-stream use only).</summary>
    public EpistleSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite,
        RiteIdentity? author = null, bool requireAuthor = false, ushort stream = ContentStream)
        : this(new VesselStreamChannel(vessel, stream), sessionKey, suite, author, requireAuthor)
    {
    }

    public Task SendMessageAsync(Epistle epistle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(epistle);
        // Sign our own messages so relaying members cannot forge authorship. A message that already
        // carries an envelope is being relayed — pass it through unchanged so the original author survives.
        if (epistle.AuthorSealPublicKey is null && _author is not null)
        {
            var content = EpistleCodec.Encode(epistle);
            var (key, sig) = RiteAuthor.Sign(AuthorDomain, content, _author, _suite);
            epistle = epistle with { AuthorSealPublicKey = key, AuthorSignature = sig };
        }

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
            FrameType.Message => new MessageReceived(VerifyAuthor(EpistleCodec.Decode(plaintext))),
            FrameType.Attestation => new AttestationReceived(plaintext),
            _ => throw new EpistleException($"Unknown content frame type {(byte)type}."),
        };
    }

    /// <summary>Authenticates the message author (see <see cref="RiteAuthor"/>); throws if it fails policy.</summary>
    private Epistle VerifyAuthor(Epistle epistle)
    {
        var content = EpistleCodec.Encode(epistle with { AuthorSealPublicKey = null, AuthorSignature = null });
        if (!RiteAuthor.Verify(AuthorDomain, content, epistle.AuthorSealPublicKey, epistle.AuthorSignature, _suite, _requireAuthor))
            throw new EpistleException("Epistle author authentication failed.");
        return epistle;
    }

    private async Task SendFrameAsync(FrameType type, byte[] body, CancellationToken cancellationToken)
    {
        var w = new CodexWriter();
        w.WriteByte((byte)type);
        w.WriteBytes(_veil.Seal(body));
        await _channel.SendAsync(w.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
