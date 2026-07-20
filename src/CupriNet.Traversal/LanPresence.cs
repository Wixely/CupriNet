using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Core;

namespace CupriNet.Traversal;

/// <summary>
/// A signed presence announcement broadcast on the local network so peers can discover each other with
/// no Intonation. It advertises the node's identity, network, and listen port; the observed source
/// address (from the datagram) supplies the reachable IP.
/// </summary>
public sealed record LanPresence
{
    public required byte Version { get; init; }
    public required Concordium Network { get; init; }
    public required byte[] SealPublicKey { get; init; }
    public required int ListenPort { get; init; }
    public required long IssuedAtUnix { get; init; }
    public required byte[] Signature { get; init; }

    public Sigil Sigil => Sigil.FromSealPublicKey(SealPublicKey);
}

/// <summary>Canonical serialization for <see cref="LanPresence"/> (signed body + signature envelope).</summary>
public static class LanPresenceCodec
{
    public const byte CurrentVersion = 1;

    public static byte[] EncodeBody(LanPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        var w = new CodexWriter();
        w.WriteByte(presence.Version);
        w.WriteString(presence.Network.Value);
        w.WriteBytes(presence.SealPublicKey);
        w.WriteVarUInt((ulong)(uint)presence.ListenPort);
        w.WriteUInt64((ulong)presence.IssuedAtUnix);
        return w.ToArray();
    }

    public static byte[] Encode(LanPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        var w = new CodexWriter();
        w.WriteBytes(EncodeBody(presence));
        w.WriteBytes(presence.Signature);
        return w.ToArray();
    }

    public static (LanPresence Presence, byte[] SignedBody) Decode(ReadOnlySpan<byte> document)
    {
        var reader = new CodexReader(document);
        var body = reader.ReadBytes().ToArray();
        var signature = reader.ReadBytes().ToArray();
        return (DecodeBody(body) with { Signature = signature }, body);
    }

    private static LanPresence DecodeBody(ReadOnlySpan<byte> body)
    {
        var r = new CodexReader(body);
        var version = r.ReadByte();
        var network = new Concordium(r.ReadString());
        var sealKey = r.ReadBytes().ToArray();
        if (sealKey.Length is 0 or > 64)
            throw new CodexFormatException("Invalid Seal public key length.");
        var listenPort = (int)(uint)r.ReadVarUInt();
        var issuedAt = (long)r.ReadUInt64();
        return new LanPresence
        {
            Version = version,
            Network = network,
            SealPublicKey = sealKey,
            ListenPort = listenPort,
            IssuedAtUnix = issuedAt,
            Signature = [],
        };
    }
}

/// <summary>Creates and verifies signed LAN presence announcements.</summary>
public static class LanPresenceSigner
{
    public static LanPresence Create(NodeIdentity identity, Concordium network, int listenPort, ICryptoSuite suite, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(suite);

        var draft = new LanPresence
        {
            Version = LanPresenceCodec.CurrentVersion,
            Network = network,
            SealPublicKey = identity.PublicKey.ToArray(),
            ListenPort = listenPort,
            IssuedAtUnix = now.ToUnixTimeSeconds(),
            Signature = [],
        };
        var signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(LanPresenceCodec.EncodeBody(draft));
        return draft with { Signature = signature };
    }

    public static bool Verify(LanPresence presence, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(suite);
        return suite.Verifier.Verify(LanPresenceCodec.EncodeBody(presence), presence.Signature, presence.SealPublicKey);
    }
}
