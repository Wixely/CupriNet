using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Core;

namespace CupriNet.Concordance;

/// <summary>Capabilities a peer advertises. Purely a hint until independently confirmed.</summary>
[Flags]
public enum PeerCapabilities : uint
{
    None = 0,
    Relay = 1,            // offers Ferryman (L1) relay
    ChannelProvider = 2,  // hosts one or more Arcana
}

/// <summary>
/// A signed, self-describing record of how to reach a peer. A peer signs its own record with its Seal;
/// the Sigil is derived from the embedded Seal public key, so a valid record cannot claim another Sigil.
/// A valid signature proves a key made a claim — never that the peer is honest, reachable, or unique.
/// </summary>
public sealed record PeerRecord
{
    public required byte[] SealPublicKey { get; init; }
    public required IReadOnlyList<Beacon> Endpoints { get; init; }
    public required ulong SequenceNumber { get; init; }
    public required long IssuedAtUnix { get; init; }
    public required PeerCapabilities Capabilities { get; init; }
    public required byte[] Signature { get; init; }

    public Sigil Sigil => Sigil.FromSealPublicKey(SealPublicKey);
}

/// <summary>Canonical serialization for <see cref="PeerRecord"/> (signed body + signature envelope).</summary>
public static class PeerRecordCodec
{
    public const int MaxEndpoints = 8;

    public static byte[] EncodeBody(PeerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var w = new CodexWriter();
        w.WriteBytes(record.SealPublicKey);
        BeaconCodec.Write(w, record.Endpoints, MaxEndpoints);
        w.WriteUInt64(record.SequenceNumber);
        w.WriteUInt64((ulong)record.IssuedAtUnix);
        w.WriteUInt32((uint)record.Capabilities);
        return w.ToArray();
    }

    public static byte[] Encode(PeerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var w = new CodexWriter();
        w.WriteBytes(EncodeBody(record));
        w.WriteBytes(record.Signature);
        return w.ToArray();
    }

    public static (PeerRecord Record, byte[] SignedBody) Decode(ReadOnlySpan<byte> document)
    {
        var reader = new CodexReader(document);
        var body = reader.ReadBytes().ToArray();
        var signature = reader.ReadBytes().ToArray();
        return (DecodeBody(body) with { Signature = signature }, body);
    }

    private static PeerRecord DecodeBody(ReadOnlySpan<byte> body)
    {
        var r = new CodexReader(body);
        var sealKey = r.ReadBytes().ToArray();
        var endpoints = BeaconCodec.Read(ref r, MaxEndpoints);
        var sequence = r.ReadUInt64();
        var issuedAt = (long)r.ReadUInt64();
        var capabilities = (PeerCapabilities)r.ReadUInt32();

        return new PeerRecord
        {
            SealPublicKey = sealKey,
            Endpoints = endpoints,
            SequenceNumber = sequence,
            IssuedAtUnix = issuedAt,
            Capabilities = capabilities,
            Signature = [],
        };
    }
}

/// <summary>Creates and validates signed <see cref="PeerRecord"/>s.</summary>
public static class PeerRecordSigner
{
    /// <summary>Signs a fresh peer record for this node.</summary>
    public static PeerRecord Create(NodeIdentity identity, IReadOnlyList<Beacon> endpoints, ulong sequenceNumber, PeerCapabilities capabilities, ICryptoSuite suite, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(suite);

        var draft = new PeerRecord
        {
            SealPublicKey = identity.PublicKey.ToArray(),
            Endpoints = endpoints,
            SequenceNumber = sequenceNumber,
            IssuedAtUnix = now.ToUnixTimeSeconds(),
            Capabilities = capabilities,
            Signature = [],
        };

        var signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(PeerRecordCodec.EncodeBody(draft));
        return draft with { Signature = signature };
    }

    /// <summary>Verifies a record's self-signature against its embedded Seal public key.</summary>
    public static bool Verify(PeerRecord record, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(suite);
        return suite.Verifier.Verify(PeerRecordCodec.EncodeBody(record), record.Signature, record.SealPublicKey);
    }
}
