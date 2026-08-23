using CupriNet.Codex;
using CupriNet.Marks;

namespace CupriNet.Rites;

/// <summary>Thrown when a relic fetch fails: the Shrine refused it, a frame was out of order, or a chunk failed verification.</summary>
public sealed class RelicException(string message) : Exception(message);

/// <summary>
/// The bulk-content sibling of the <see cref="OracleSession"/> rite: a Pilgrim fetches a named <b>relic</b> — a large
/// artifact a Shrine keeps, an app blob, an image set, anything past the message ceiling — as a Reliquary transfer
/// over the Pilgrimage: first the <see cref="ReliquaryManifest"/>, then each chunk as its own request/response, every
/// chunk hash-verified against the manifest and the whole file verified before the bytes are handed back.
///
/// <para><b>Why chunks and not one response.</b> Every rite here sends whole messages, and the browser transport caps
/// a message at 256 KiB — so a multi-MB body is not slow, it is <em>impossible</em>. The Reliquary already knows how
/// to cut content into verifiable chunks; this rite carries those chunks over the Shrine session. And because every
/// chunk is verified against the manifest as it arrives, a client can prove a blob's integrity <em>before</em>
/// executing it — a property one opaque response could never have.</para>
///
/// <para><b>Why a rite of its own.</b> Like the Auspice, it runs on its own logical stream, so a page fetch over the
/// Oracle and a live feed both proceed while a bulk transfer is in flight — a download must never head-of-line-block
/// the site.</para>
/// </summary>
public enum RelicFrameKind : byte
{
    /// <summary>Pilgrim → Shrine: send the manifest of the named relic.</summary>
    RequestManifest = 1,

    /// <summary>Pilgrim → Shrine: send one chunk of one file of the named relic.</summary>
    RequestChunk = 2,

    /// <summary>Shrine → Pilgrim: the relic's encoded <see cref="ReliquaryManifest"/>.</summary>
    Manifest = 3,

    /// <summary>Shrine → Pilgrim: the requested chunk's bytes.</summary>
    Chunk = 4,

    /// <summary>Shrine → Pilgrim: the request is refused (unknown relic, bad index, source failure) — a short reason.</summary>
    Refused = 5,
}

/// <summary>One Relic frame: a kind, the relic it concerns, which chunk of which file, and the payload.</summary>
public sealed record RelicFrame
{
    public required RelicFrameKind Kind { get; init; }

    /// <summary>The relic's name — a guarded relative path (see <see cref="FilePathGuard"/>), compared ordinally.</summary>
    public required string Name { get; init; }

    /// <summary>Which file of the manifest a chunk frame concerns (0 for manifest and refusal frames).</summary>
    public int FileIndex { get; init; }

    /// <summary>Which chunk of that file (0 for manifest and refusal frames).</summary>
    public int ChunkIndex { get; init; }

    /// <summary>The encoded manifest, the chunk bytes, or a UTF-8 reason for <see cref="RelicFrameKind.Refused"/>.</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>The payload decoded as UTF-8 text.</summary>
    public string AsText() => System.Text.Encoding.UTF8.GetString(Payload);
}

/// <summary>Canonical serialization for <see cref="RelicFrame"/>, with the payload ceiling enforced both ways.</summary>
public static class RelicCodec
{
    /// <summary>
    /// The largest payload a single Relic frame may carry (192 KiB) — one chunk, one encoded manifest, or one reason.
    /// Same number, same cause as <see cref="AuspiceCodec.MaxPayloadBytes"/>: a WebRTC DataChannel message tops out at
    /// 256 KiB (CupriWebRTC's <c>SctpAssociation.MaxMessageBytes</c>), and the remaining 64 KiB is headroom for rite
    /// framing, the vessel header and Noise/DTLS overhead. Enforced when sending <em>and</em> when decoding.
    ///
    /// <para>Because a manifest must also fit in one frame, a relic tops out around <b>1.1 GiB</b> at the default
    /// chunking (~6100 chunk hashes × 32 bytes fills the frame). That is far past anything a Shrine should serve to a
    /// browser tab; if it ever binds, a paged manifest is a compatible v2.</para>
    /// </summary>
    public const int MaxPayloadBytes = 192 * 1024;

    /// <summary>Maximum length of a relic name, a Ward against an unbounded allocation from a hostile peer.</summary>
    public const int MaxNameLength = 256;

    /// <summary>
    /// The chunk size relics are cut to unless the author says otherwise: the frame ceiling itself. Chunks travel as
    /// strictly sequential request/responses, so the transfer is RTT-bound — the biggest chunk the transport carries
    /// is the fastest one.
    /// </summary>
    public const int DefaultChunkBytes = MaxPayloadBytes;

    public static byte[] Encode(RelicFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrEmpty(frame.Name) || frame.Name.Length > MaxNameLength)
            throw new ArgumentOutOfRangeException(nameof(frame), $"A relic name must be 1..{MaxNameLength} characters.");
        if (frame.FileIndex < 0 || frame.ChunkIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frame), "Relic frame indices must be non-negative.");
        if (frame.Payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                $"A Relic payload of {frame.Payload.Length} bytes exceeds the {MaxPayloadBytes}-byte limit " +
                "(the browser DataChannel ceiling, less framing headroom). Cut the content into smaller chunks.");

        var w = new CodexWriter();
        w.WriteByte(checked((byte)CupriMarks.Supported(CupriMarks.Relic).Max));
        w.WriteByte((byte)frame.Kind);
        w.WriteString(frame.Name);
        w.WriteVarUInt((ulong)frame.FileIndex);
        w.WriteVarUInt((ulong)frame.ChunkIndex);
        w.WriteBytes(frame.Payload);
        return w.ToArray();
    }

    public static RelicFrame Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);

        // Leads with the Relic version this build speaks, so a peer that predates the rite (or speaks a version we
        // have buried) is a typed refusal rather than a frame nobody can parse. See OracleCodec for why the rites
        // now carry their own version instead of inheriting it from the handshake.
        var version = r.ReadByte();
        if (!CupriMarks.Accepts(CupriMarks.Relic, version))
            throw new CodexFormatException($"Unsupported Relic frame version {version}.");

        var kind = (RelicFrameKind)r.ReadByte();
        if (!Enum.IsDefined(kind))
            throw new CodexFormatException($"Unknown Relic frame kind {(byte)kind}.");

        var name = r.ReadString();
        if (name.Length is 0 or > MaxNameLength)
            throw new CodexFormatException("Relic frame has an invalid relic name.");

        // Bound the indices by the Reliquary's own Wards, so a hostile frame cannot smuggle absurd values
        // into anything that indexes with them.
        var limits = new ReliquaryLimits();
        var fileIndex = r.ReadVarUInt();
        if (fileIndex >= (ulong)limits.MaxFiles)
            throw new CodexFormatException($"Relic frame file index {fileIndex} is out of range.");
        var chunkIndex = r.ReadVarUInt();
        if (chunkIndex >= (ulong)limits.MaxChunksPerFile)
            throw new CodexFormatException($"Relic frame chunk index {chunkIndex} is out of range.");

        var payload = r.ReadBytes();
        if (payload.Length > MaxPayloadBytes)
            throw new CodexFormatException(
                $"A Relic payload of {payload.Length} bytes exceeds the {MaxPayloadBytes}-byte limit.");

        return new RelicFrame
        {
            Kind = kind,
            Name = name,
            FileIndex = (int)fileIndex,
            ChunkIndex = (int)chunkIndex,
            Payload = payload.ToArray(),
        };
    }
}
