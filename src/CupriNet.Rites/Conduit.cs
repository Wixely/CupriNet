using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Marks;
using CupriNet.Vessel;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Rites;

/// <summary>Rite-reserved bits of <see cref="ConduitFrame.Flags"/>. Applications own everything else.</summary>
public static class ConduitFlags
{
    /// <summary>
    /// The rite has ended this conduit; <see cref="ConduitFrame.Payload"/> carries a short UTF-8 reason. Sent when a
    /// Shrine hosts no conduit, refuses the protocol, or its handler stops — the counterpart of the Auspice
    /// <c>Sealed</c> frame, so a peer is told rather than left waiting on a stream that will never answer.
    /// </summary>
    public const uint Sealed = 0x8000_0000;

    /// <summary>The bits the rite reserves. An application must keep its own flags clear of these.</summary>
    public const uint Reserved = Sealed;
}

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

    /// <summary>True when the peer ended the conduit at the rite (see <see cref="ConduitFlags.Sealed"/>).</summary>
    public bool IsSealed => (Flags & ConduitFlags.Sealed) != 0;

    /// <summary>The reason carried by a sealed frame, or an empty string.</summary>
    public string SealReason => IsSealed ? System.Text.Encoding.UTF8.GetString(Payload) : string.Empty;
}

/// <summary>Canonical serialization for <see cref="ConduitFrame"/>.</summary>
public static class ConduitCodec
{
    /// <summary>
    /// The largest payload a Conduit frame may carry on a <b>Pilgrimage</b> (192 KiB) — same number, same cause as
    /// <see cref="AuspiceCodec.MaxPayloadBytes"/>: a WebRTC DataChannel message tops out at 256 KiB and is
    /// <em>rejected</em> above it, so a browser-reachable rite must fail here, with a reason, rather than deep in the
    /// transport. Past it, chunk — or carry the bytes as a Relic, which is built for exactly that.
    /// </summary>
    public const int MaxPayloadBytes = 192 * 1024;

    /// <summary>The largest an encoded Pilgrimage frame may grow to <em>after</em> padding, still under the ceiling.</summary>
    public const int MaxPaddedBytes = 224 * 1024;

    /// <summary>
    /// The ceiling on an <b>Arcanum</b> (channel) conduit: what a Vessel frame allows, so a payload that encoded
    /// before this check existed still encodes. No browser is on that path.
    /// </summary>
    public const int MaxChannelPayloadBytes = FrameCodec.DefaultMaxFrameSize;

    public static byte[] Encode(ConduitFrame frame, int maxPayloadBytes = MaxChannelPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Payload.Length > maxPayloadBytes)
            throw new CodexFormatException(
                $"A Conduit payload of {frame.Payload.Length} bytes exceeds the {maxPayloadBytes}-byte limit " +
                "for this session. Chunk it, or carry bulk content as a Relic.");

        var w = new CodexWriter();
        w.WriteUInt32(frame.ProtocolId);
        w.WriteUInt32(frame.SchemaVersion);
        w.WriteUInt32(frame.Flags);
        w.WriteBytes(frame.Payload);
        AuthorEnvelope.Write(w, frame.AuthorSealPublicKey, frame.AuthorSignature);
        return w.ToArray();
    }

    public static ConduitFrame Decode(ReadOnlySpan<byte> data, int maxPayloadBytes = MaxChannelPayloadBytes)
    {
        var r = new CodexReader(data);
        var protocolId = r.ReadUInt32();
        var schemaVersion = r.ReadUInt32();
        var flags = r.ReadUInt32();
        var payload = r.ReadBytes().ToArray();
        if (payload.Length > maxPayloadBytes)
            throw new CodexFormatException(
                $"A Conduit payload of {payload.Length} bytes exceeds the {maxPayloadBytes}-byte limit.");

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

    /// <summary>
    /// Encodes with a leading CupriMark version byte. Used only on the Pilgrimage path, which is new wire: the Arcanum
    /// conduit predates the catalogue and inherits its compatibility from the Consecration handshake, so its bytes are
    /// left exactly as they were.
    /// </summary>
    public static byte[] EncodeVersioned(ConduitFrame frame, int maxPayloadBytes = MaxPayloadBytes)
    {
        var body = Encode(frame, maxPayloadBytes);
        var wire = new byte[1 + body.Length];
        wire[0] = checked((byte)CupriMarks.Supported(CupriMarks.Conduit).Max);
        body.CopyTo(wire, 1);
        return wire;
    }

    /// <summary>Decodes a frame written by <see cref="EncodeVersioned"/>, rejecting a version this build does not accept.</summary>
    public static ConduitFrame DecodeVersioned(ReadOnlySpan<byte> data, int maxPayloadBytes = MaxPayloadBytes)
    {
        if (data.Length < 1)
            throw new CodexFormatException("A Conduit frame is empty.");

        var version = data[0];
        if (!CupriMarks.Accepts(CupriMarks.Conduit, version))
            throw new CodexFormatException($"Unsupported Conduit version {version}.");

        return Decode(data[1..], maxPayloadBytes);
    }
}

/// <summary>
/// How much a Conduit message's length is rounded up before it goes on the wire — a traffic-analysis defence, and the
/// same policy the Auspice applies for the same reason (see <see cref="AuspicePadding"/>): a duplex session leaks size
/// and timing at least as richly as a live feed does. Padding is applied to the encoded frame <em>before</em> sealing,
/// so it is encrypted with everything else, and the decoder never reads it — every field is length-prefixed, so
/// trailing bytes belong to none of them.
/// </summary>
public sealed class ConduitPadding
{
    /// <summary>The default block size, matching <see cref="AuspicePadding.DefaultBlockBytes"/>.</summary>
    public const int DefaultBlockBytes = 512;

    private readonly int _block;
    private readonly bool _powerOfTwo;

    private ConduitPadding(int block, bool powerOfTwo)
    {
        _block = block;
        _powerOfTwo = powerOfTwo;
    }

    /// <summary>No padding — every message is its exact length. What an Arcanum conduit does unless asked otherwise.</summary>
    public static ConduitPadding None { get; } = new(0, false);

    /// <summary>Rounds every message up to a multiple of <paramref name="blockBytes"/>.</summary>
    public static ConduitPadding Blocks(int blockBytes)
    {
        if (blockBytes is < 1 or > ConduitCodec.MaxPaddedBytes)
            throw new ArgumentOutOfRangeException(nameof(blockBytes));
        return new ConduitPadding(blockBytes, powerOfTwo: false);
    }

    /// <summary>Rounds up to the next power of two, collapsing the whole size range into ~10 buckets, at up to 2x bandwidth.</summary>
    public static ConduitPadding PowersOfTwo { get; } = new(0, powerOfTwo: true);

    /// <summary>The default policy: <see cref="DefaultBlockBytes"/>-byte blocks. What a Pilgrimage conduit uses.</summary>
    public static ConduitPadding Default { get; } = Blocks(DefaultBlockBytes);

    /// <summary>The length <paramref name="length"/> is padded up to, never beyond <paramref name="ceiling"/>.</summary>
    public int Quantise(int length, int ceiling) => PaddingMath.Quantise(length, _block, _powerOfTwo, ceiling);
}

/// <summary>Carries the Conduit (data) rite as frames over a Vessel stream.</summary>
public sealed class ConduitSession
{
    /// <summary>Logical stream for generic data (0 Conjunction, 1 peer exchange, 2 Consecration, 3 Epistles).</summary>
    public const ushort DataStream = 4;

    /// <summary>Domain tag for the authenticated-authorship envelope (see <see cref="RiteAuthor"/>).</summary>
    private const string AuthorDomain = "conduit";

    private readonly IStreamChannel _channel;
    private readonly VeilCipher? _veil;
    private readonly ICryptoSuite? _suite;
    private readonly RiteIdentity? _author;
    private readonly bool _requireAuthor;
    private readonly ConduitPadding _padding;
    private readonly int _maxPayloadBytes;
    private readonly bool _versioned;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ConduitSession(IStreamChannel channel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite,
        RiteIdentity? author = null, bool requireAuthor = false,
        ConduitPadding? padding = null, int maxPayloadBytes = ConduitCodec.MaxChannelPayloadBytes)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
        _veil = new VeilCipher(sessionKey, suite);
        _author = author;
        _requireAuthor = requireAuthor;
        _padding = padding ?? ConduitPadding.None;
        _maxPayloadBytes = maxPayloadBytes;
        _versioned = false;                 // the pre-catalogue Arcanum wire, unchanged
    }

    /// <summary>Convenience: bind to a single Vessel stream directly (single-stream use only).</summary>
    public ConduitSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite,
        RiteIdentity? author = null, bool requireAuthor = false, ushort stream = DataStream)
        : this(new VesselStreamChannel(vessel, stream), sessionKey, suite, author, requireAuthor)
    {
    }

    /// <summary>
    /// Binds over an <em>already-encrypted</em> transport (the Pilgrimage's Noise vessel): frames carry no Veil layer,
    /// because the vessel is itself the confidentiality + authenticity boundary — the same choice the Oracle, Auspice
    /// and Relic make there.
    ///
    /// <para>This constructor takes <b>no author identity</b>, deliberately. Authenticated authorship is right in an
    /// Arcanum channel, where members have durable identities; a Pilgrim is meant to be unlinkable, and signing frames
    /// under a durable key on a visit would quietly undo that. Padding is on by default and the payload ceiling is the
    /// browser-reachable 192 KiB, for the same reasons the sibling rites carry them.</para>
    /// </summary>
    public ConduitSession(IStreamChannel channel, ConduitPadding? padding = null,
        int maxPayloadBytes = ConduitCodec.MaxPayloadBytes)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _veil = null;
        _suite = null;
        _author = null;
        _requireAuthor = false;
        _padding = padding ?? ConduitPadding.Default;
        _maxPayloadBytes = maxPayloadBytes;
        _versioned = true;                  // new wire, so it carries its CupriMark component
    }

    /// <summary>The largest payload this session will send or accept.</summary>
    public int MaxPayloadBytes => _maxPayloadBytes;

    /// <summary>
    /// Sends a frame. Sends are serialised, so several tasks may share one conduit — the transports differ on whether
    /// they guarantee that themselves (a TCP Vessel serialises writes; a DataChannel relies on message boundaries),
    /// and a rite should not make a caller reason about which one it is on.
    /// </summary>
    public async Task SendAsync(ConduitFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.AuthorSealPublicKey is null && _author is not null && _suite is not null)
        {
            var content = ConduitCodec.Encode(frame, _maxPayloadBytes);
            var (key, sig) = RiteAuthor.Sign(AuthorDomain, content, _author, _suite);
            frame = frame with { AuthorSealPublicKey = key, AuthorSignature = sig };
        }

        // Encode (which enforces the ceiling) before any I/O, then pad, then seal — so the padding is encrypted too.
        var body = Pad(_versioned
            ? ConduitCodec.EncodeVersioned(frame, _maxPayloadBytes)
            : ConduitCodec.Encode(frame, _maxPayloadBytes));
        var wire = _veil is null ? body : _veil.Seal(body);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.SendAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Ends the conduit at the rite, telling the peer why (see <see cref="ConduitFlags.Sealed"/>).</summary>
    public Task SendSealedAsync(string reason, CancellationToken cancellationToken = default)
        => SendAsync(new ConduitFrame
        {
            ProtocolId = 0,
            SchemaVersion = 0,
            Flags = ConduitFlags.Sealed,
            Payload = System.Text.Encoding.UTF8.GetBytes(reason ?? string.Empty),
        }, cancellationToken);

    /// <summary>Reads the next frame, or null when the peer closed the connection.</summary>
    public async Task<ConduitFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;

        var plaintext = _veil is null
            ? payload
            : _veil.Open(payload) ?? throw new EpistleException("Conduit content failed authentication (Veil).");

        var frame = _versioned
            ? ConduitCodec.DecodeVersioned(plaintext, _maxPayloadBytes)
            : ConduitCodec.Decode(plaintext, _maxPayloadBytes);

        if (_suite is not null)
        {
            var content = ConduitCodec.Encode(
                frame with { AuthorSealPublicKey = null, AuthorSignature = null }, _maxPayloadBytes);
            if (!RiteAuthor.Verify(AuthorDomain, content, frame.AuthorSealPublicKey, frame.AuthorSignature, _suite, _requireAuthor))
                throw new EpistleException("Conduit author authentication failed.");
        }

        return frame;
    }

    /// <summary>
    /// Grows an encoded frame to its quantised length. The padding sits inside what is subsequently sealed, and the
    /// decoder never reads it: every field is length-prefixed, so trailing bytes belong to none of them.
    /// </summary>
    private byte[] Pad(byte[] body)
    {
        var target = _padding.Quantise(body.Length, ConduitCodec.MaxPaddedBytes);
        if (target <= body.Length)
            return body;

        var padded = new byte[target];
        body.CopyTo(padded, 0);
        return padded;
    }
}
