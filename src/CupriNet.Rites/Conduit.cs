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

    /// <summary>The author's Seal public key, when the frame carries an authenticated-authorship envelope.</summary>
    public byte[]? AuthorSealPublicKey { get; init; }

    /// <summary>The author's Ed25519 signature over the frame content (see <see cref="RiteAuthor"/>).</summary>
    public byte[]? AuthorSignature { get; init; }
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
        AuthorEnvelope.Write(w, frame.AuthorSealPublicKey, frame.AuthorSignature);
        return w.ToArray();
    }

    public static ConduitFrame Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        var protocolId = r.ReadUInt32();
        var schemaVersion = r.ReadUInt32();
        var flags = r.ReadUInt32();
        var payload = r.ReadBytes().ToArray();
        var (authorKey, authorSig) = AuthorEnvelope.Read(ref r);
        return new ConduitFrame
        {
            ProtocolId = protocolId,
            SchemaVersion = schemaVersion,
            Flags = flags,
            Payload = payload,
            AuthorSealPublicKey = authorKey,
            AuthorSignature = authorSig,
        };
    }
}

/// <summary>Carries the Conduit (data) rite as Veil-sealed frames over a Vessel stream.</summary>
public sealed class ConduitSession
{
    /// <summary>Logical stream for generic data (0 Conjunction, 1 peer exchange, 2 Consecration, 3 Epistles).</summary>
    public const ushort DataStream = 4;

    /// <summary>Domain tag for the authenticated-authorship envelope (see <see cref="RiteAuthor"/>).</summary>
    private const string AuthorDomain = "conduit";

    private readonly IStreamChannel _channel;
    private readonly VeilCipher _veil;
    private readonly ICryptoSuite _suite;
    private readonly RiteIdentity? _author;
    private readonly bool _requireAuthor;

    public ConduitSession(IStreamChannel channel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite,
        RiteIdentity? author = null, bool requireAuthor = false)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
        _veil = new VeilCipher(sessionKey, suite);
        _author = author;
        _requireAuthor = requireAuthor;
    }

    /// <summary>Convenience: bind to a single Vessel stream directly (single-stream use only).</summary>
    public ConduitSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite,
        RiteIdentity? author = null, bool requireAuthor = false, ushort stream = DataStream)
        : this(new VesselStreamChannel(vessel, stream), sessionKey, suite, author, requireAuthor)
    {
    }

    public Task SendAsync(ConduitFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.AuthorSealPublicKey is null && _author is not null)
        {
            var content = ConduitCodec.Encode(frame);
            var (key, sig) = RiteAuthor.Sign(AuthorDomain, content, _author, _suite);
            frame = frame with { AuthorSealPublicKey = key, AuthorSignature = sig };
        }

        return _channel.SendAsync(_veil.Seal(ConduitCodec.Encode(frame)), cancellationToken).AsTask();
    }

    public async Task<ConduitFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;

        var plaintext = _veil.Open(payload)
                        ?? throw new EpistleException("Conduit content failed authentication (Veil).");
        var frame = ConduitCodec.Decode(plaintext);
        var content = ConduitCodec.Encode(frame with { AuthorSealPublicKey = null, AuthorSignature = null });
        if (!RiteAuthor.Verify(AuthorDomain, content, frame.AuthorSealPublicKey, frame.AuthorSignature, _suite, _requireAuthor))
            throw new EpistleException("Conduit author authentication failed.");
        return frame;
    }
}
