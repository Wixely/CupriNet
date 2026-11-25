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

    /// <summary>The author's Seal public key, when the message carries an authenticated-authorship envelope.</summary>
    public byte[]? AuthorSealPublicKey { get; init; }

    /// <summary>The author's Ed25519 signature over the message content (see <see cref="RiteAuthor"/>).</summary>
    public byte[]? AuthorSignature { get; init; }

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
        if (epistle.MessageId is not { Length: Epistle.MessageIdSize })
            throw new ArgumentException($"An Epistle's MessageId is {Epistle.MessageIdSize} bytes.", nameof(epistle));
        if (epistle.InReplyTo is { Length: not Epistle.MessageIdSize })
            throw new ArgumentException($"InReplyTo names a MessageId, which is {Epistle.MessageIdSize} bytes.", nameof(epistle));
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

        AuthorEnvelope.Write(w, epistle.AuthorSealPublicKey, epistle.AuthorSignature);
        return w.ToArray();
    }

    public static Epistle Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        // An id is a fixed size: it keys the receiver's dedup set and the sender's retry table, so a peer does not
        // get to choose how much of either one message occupies.
        var messageId = r.ReadBytes().ToArray();
        if (messageId.Length != Epistle.MessageIdSize)
            throw new CodexFormatException($"An Epistle's MessageId must be {Epistle.MessageIdSize} bytes.");
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
        if (inReplyTo is { Length: not Epistle.MessageIdSize })
            throw new CodexFormatException($"InReplyTo must be a {Epistle.MessageIdSize}-byte MessageId.");
        var (authorKey, authorSig) = AuthorEnvelope.Read(ref r);

        return new Epistle
        {
            MessageId = messageId,
            TimestampUnixMs = timestamp,
            ContentType = contentType,
            Payload = payload,
            InReplyTo = inReplyTo,
            AuthorSealPublicKey = authorKey,
            AuthorSignature = authorSig,
        };
    }
}
