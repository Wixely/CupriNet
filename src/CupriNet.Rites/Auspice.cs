using CupriNet.Codex;

namespace CupriNet.Rites;

/// <summary>
/// The streaming sibling of the <see cref="OracleSession"/> rite: a Pilgrim <b>attends</b> a named Auspice and the
/// Shrine emanates a <b>snapshot</b> followed by a stream of <b>updates</b> — subscribe-once, push-many, over the same
/// authenticated session as the Oracle. This is how live data reaches a page without polling, a second connection, or
/// any WebSocket/SSE machinery.
///
/// <para><b>Why it lives here and not in a product.</b> The Pilgrim half of this rite runs inside the CupriNet client
/// stack (including where that stack is compiled to WebAssembly for a browser), so the rite has to sit beside Oracle in
/// the core; a downstream host re-exposes it as its own sugar.</para>
///
/// <para><b>Per-message, not a byte stream.</b> Every frame is a whole message, capped at
/// <see cref="AuspiceCodec.MaxPayloadBytes"/>. It carries deltas and state, not bulk content — for anything large use
/// the Reliquary (chunked, hash-verified, resumable).</para>
/// </summary>
public enum AuspiceFrameKind : byte
{
    /// <summary>Pilgrim → Shrine: begin attending a named Auspice.</summary>
    Attend = 1,

    /// <summary>Pilgrim → Shrine: stop attending it.</summary>
    Depart = 2,

    /// <summary>Shrine → Pilgrim: the opening state of the feed, sent once per attendance.</summary>
    Snapshot = 3,

    /// <summary>Shrine → Pilgrim: an incremental update following the snapshot.</summary>
    Update = 4,

    /// <summary>Shrine → Pilgrim: the feed has ended (source completed, failed, or refused).</summary>
    Sealed = 5,
}

/// <summary>One Auspice frame: a kind, the feed it belongs to, and its payload (empty for Attend/Depart).</summary>
public sealed record AuspiceFrame
{
    public required AuspiceFrameKind Kind { get; init; }

    /// <summary>The feed name. Application-defined; compared ordinally.</summary>
    public required string Topic { get; init; }

    /// <summary>The message body — application-defined bytes, or a UTF-8 reason for <see cref="AuspiceFrameKind.Sealed"/>.</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>The payload decoded as UTF-8 text.</summary>
    public string AsText() => System.Text.Encoding.UTF8.GetString(Payload);
}

/// <summary>Canonical serialization for <see cref="AuspiceFrame"/>, with the payload ceiling enforced both ways.</summary>
public static class AuspiceCodec
{
    /// <summary>
    /// The largest payload a single Auspice message may carry (192 KiB).
    ///
    /// <para>This is set by the <b>browser</b>, not by us. A WebRTC DataChannel message cannot exceed the
    /// <c>a=max-message-size</c> an endpoint advertises, and CupriWebRTC's SCTP association fixes that at <b>256 KiB</b>
    /// (<c>SctpAssociation.MaxMessageBytes</c>) — rejecting anything larger rather than truncating it. The remaining
    /// 64 KiB is headroom for rite framing, the vessel header and Noise/DTLS overhead, so an at-limit payload can never
    /// push the wire message over the edge.</para>
    ///
    /// <para>It is enforced when sending <em>and</em> when decoding, so neither side can be pushed past what the
    /// transport will carry. Content larger than this does not belong in a message rite at all — use the Reliquary
    /// (chunked, hash-verified, resumable).</para>
    /// </summary>
    public const int MaxPayloadBytes = 192 * 1024;

    /// <summary>Maximum length of a feed name, a Ward against an unbounded allocation from a hostile peer.</summary>
    public const int MaxTopicLength = 128;

    public static byte[] Encode(AuspiceFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateTopic(frame.Topic);
        if (frame.Payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                $"An Auspice payload of {frame.Payload.Length} bytes exceeds the {MaxPayloadBytes}-byte limit " +
                "(the browser DataChannel ceiling, less framing headroom). Send a delta, or move bulk content to the Reliquary.");

        var w = new CodexWriter();
        w.WriteByte((byte)frame.Kind);
        w.WriteString(frame.Topic);
        w.WriteBytes(frame.Payload);
        return w.ToArray();
    }

    public static AuspiceFrame Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        var kind = (AuspiceFrameKind)r.ReadByte();
        if (!Enum.IsDefined(kind))
            throw new CodexFormatException($"Unknown Auspice frame kind {(byte)kind}.");

        var topic = r.ReadString();
        if (topic.Length is 0 or > MaxTopicLength)
            throw new CodexFormatException("Auspice frame has an invalid feed name.");

        var payload = r.ReadBytes();
        if (payload.Length > MaxPayloadBytes)
            throw new CodexFormatException(
                $"An Auspice payload of {payload.Length} bytes exceeds the {MaxPayloadBytes}-byte limit.");

        return new AuspiceFrame { Kind = kind, Topic = topic, Payload = payload.ToArray() };
    }

    private static void ValidateTopic(string topic)
    {
        if (string.IsNullOrEmpty(topic) || topic.Length > MaxTopicLength)
            throw new ArgumentOutOfRangeException(
                nameof(topic), $"An Auspice feed name must be 1..{MaxTopicLength} characters.");
    }
}
