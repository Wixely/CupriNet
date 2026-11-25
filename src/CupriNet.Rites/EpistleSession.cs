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

/// <summary>
/// An acknowledgement (Attestation) arrived for a MessageId. When the attesting member signed it, the envelope
/// names them: on a channel where every peer is the member it speaks for, that is the peer; where an application
/// relays, it is whoever first acknowledged, whichever peer handed it on.
/// </summary>
public sealed record AttestationReceived(byte[] MessageId, byte[]? AuthorSealPublicKey = null, byte[]? AuthorSignature = null) : EpistleEvent;

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

    /// <summary>Its own domain, so a signature over a message can never stand as an acknowledgement, or the reverse.</summary>
    private const string AttestationDomain = "epistle-attestation";

    private readonly IStreamChannel _channel;
    private readonly VeilCipher _veil;
    private readonly ICryptoSuite _suite;
    private readonly RiteIdentity? _author;
    private readonly bool _requireAuthor;

    public EpistleSession(IStreamChannel channel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, VeilRole role,
        RiteIdentity? author = null, bool requireAuthor = false)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
        _veil = new VeilCipher(sessionKey, suite, "epistle", role);
        _author = author;
        _requireAuthor = requireAuthor;
    }

    /// <summary>Convenience: bind to a single Vessel stream directly (single-stream use only).</summary>
    public EpistleSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, VeilRole role,
        RiteIdentity? author = null, bool requireAuthor = false, ushort stream = ContentStream)
        : this(new VesselStreamChannel(vessel, stream), sessionKey, suite, role, author, requireAuthor)
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

    /// <summary>Acknowledges a message, signed as this session's author when it has one — as a message would be.</summary>
    public Task SendAttestationAsync(ReadOnlySpan<byte> messageId, CancellationToken cancellationToken = default)
    {
        if (messageId.Length != Epistle.MessageIdSize)
            throw new ArgumentException($"An Attestation names a MessageId, which is {Epistle.MessageIdSize} bytes.", nameof(messageId));
        var id = messageId.ToArray();
        var (key, signature) = _author is null
            ? ((byte[]?)null, (byte[]?)null)
            : RiteAuthor.Sign(AttestationDomain, EncodeAttestation(id, null, null), _author, _suite);
        return SendFrameAsync(FrameType.Attestation, EncodeAttestation(id, key, signature), cancellationToken);
    }

    /// <summary>
    /// Hands on an acknowledgement received from another member, unchanged, so its signature still names whoever
    /// made it. The library never does this itself — peers speak for themselves — but an application that relays can.
    /// </summary>
    public Task SendAttestationAsync(AttestationReceived attestation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        if (attestation.AuthorSealPublicKey is null || attestation.AuthorSignature is null)
            return SendAttestationAsync(attestation.MessageId, cancellationToken);
        if (attestation.MessageId is not { Length: Epistle.MessageIdSize })
            throw new ArgumentException($"An Attestation names a MessageId, which is {Epistle.MessageIdSize} bytes.", nameof(attestation));
        return SendFrameAsync(FrameType.Attestation,
            EncodeAttestation(attestation.MessageId, attestation.AuthorSealPublicKey, attestation.AuthorSignature), cancellationToken);
    }

    private static byte[] EncodeAttestation(byte[] messageId, byte[]? authorKey, byte[]? authorSignature)
    {
        var w = new CodexWriter();
        w.WriteBytes(messageId);
        AuthorEnvelope.Write(w, authorKey, authorSignature);
        return w.ToArray();
    }

    /// <summary>Decodes an acknowledgement and holds it to the same author policy as a message.</summary>
    private AttestationReceived ReadAttestation(byte[] plaintext)
    {
        var r = new CodexReader(plaintext);
        var messageId = r.ReadBytes().ToArray();
        if (messageId.Length != Epistle.MessageIdSize)
            throw new EpistleException($"An Attestation must name a {Epistle.MessageIdSize}-byte MessageId.");
        var (key, signature) = AuthorEnvelope.Read(ref r);
        if (!RiteAuthor.Verify(AttestationDomain, EncodeAttestation(messageId, null, null), key, signature, _suite, _requireAuthor))
            throw new EpistleException("Attestation author authentication failed.");
        return new AttestationReceived(messageId, key, signature);
    }

    /// <summary>Reads the next content event, or null when the peer closes the connection.</summary>
    public async Task<EpistleEvent?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;

        var reader = new CodexReader(payload);
        var type = (FrameType)reader.ReadByte();
        var sealedBody = reader.ReadBytes();

        // The type byte travels outside the seal, so it is bound in as associated data: change it and the frame fails.
        var plaintext = _veil.Open(sealedBody, [(byte)type])
                        ?? throw new EpistleException("Channel content failed authentication (Veil).");

        return type switch
        {
            FrameType.Message => new MessageReceived(VerifyAuthor(EpistleCodec.Decode(plaintext))),
            FrameType.Attestation => ReadAttestation(plaintext),
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
        w.WriteBytes(_veil.Seal(body, [(byte)type]));
        await _channel.SendAsync(w.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
