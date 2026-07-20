using System.Security.Cryptography;
using CupriNet.Codex;

namespace CupriNet.Rites;

/// <summary>
/// A channel message. The MessageId makes delivery idempotent (the receiver dedups on it) and lets acks
/// (Attestations) reference it. Timestamps are the sender's untrusted wall clock — never relied on for
/// ordering or security.
/// </summary>
public sealed record Epistle
{
    public const int MessageIdSize = 16;

    public required byte[] MessageId { get; init; }
    public required long TimestampUnixMs { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Payload { get; init; }
    public byte[]? InReplyTo { get; init; }

    /// <summary>Creates a UTF-8 text Epistle with a fresh MessageId.</summary>
    public static Epistle Text(string text, DateTimeOffset now, byte[]? inReplyTo = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Epistle
        {
            MessageId = RandomNumberGenerator.GetBytes(MessageIdSize),
            TimestampUnixMs = now.ToUnixTimeMilliseconds(),
            ContentType = "text/plain; charset=utf-8",
            Payload = System.Text.Encoding.UTF8.GetBytes(text),
            InReplyTo = inReplyTo,
        };
    }

    /// <summary>The payload decoded as UTF-8 text.</summary>
    public string AsText() => System.Text.Encoding.UTF8.GetString(Payload);
}

/// <summary>Canonical serialization for <see cref="Epistle"/>.</summary>
public static class EpistleCodec
{
    public static byte[] Encode(Epistle epistle)
    {
        ArgumentNullException.ThrowIfNull(epistle);
        var w = new CodexWriter();
        w.WriteBytes(epistle.MessageId);
        w.WriteUInt64((ulong)epistle.TimestampUnixMs);
        w.WriteString(epistle.ContentType);
        w.WriteBytes(epistle.Payload);
        if (epistle.InReplyTo is { } reply)
        {
            w.WriteByte(1);
            w.WriteBytes(reply);
        }
        else
        {
            w.WriteByte(0);
        }

        return w.ToArray();
    }

    public static Epistle Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        var messageId = r.ReadBytes().ToArray();
        var timestamp = (long)r.ReadUInt64();
        var contentType = r.ReadString();
        var payload = r.ReadBytes().ToArray();
        var hasReply = r.ReadByte();
        byte[]? inReplyTo = hasReply switch
        {
            0 => null,
            1 => r.ReadBytes().ToArray(),
            _ => throw new CodexFormatException("Invalid InReplyTo presence flag."),
        };

        return new Epistle
        {
            MessageId = messageId,
            TimestampUnixMs = timestamp,
            ContentType = contentType,
            Payload = payload,
            InReplyTo = inReplyTo,
        };
    }
}
