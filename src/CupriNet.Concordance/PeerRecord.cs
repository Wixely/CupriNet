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

    /// <summary>
    /// Optional self-asserted display name (Moniker) — e.g. "Community Relay". Signed (so a node can only claim one for its
    /// own key) but <b>never verified by CupriNet</b>. It's a display hint; a client believes it only by matching the
    /// node's fingerprint (<see cref="Sigil"/>) against one it trusts.
    /// </summary>
    public string? Moniker { get; init; }

    public required byte[] Signature { get; init; }

    /// <summary>
    /// The exact body bytes the signature was made over, when this value was decoded from the wire (null for one
    /// built locally). When present they are what gets verified and what gets re-encoded, so a document passes
    /// through this node byte-for-byte: fields a newer build appended after the ones this build knows stay under
    /// the signature instead of being dropped and breaking it. They are only used while they still say what the
    /// fields say: change a field with <c>with</c> and the value is encoded afresh, so a tampered copy never
    /// verifies on the strength of the original's bytes.
    /// </summary>
    public byte[]? SignedBody { get; init; }

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

        // Optional trailing Moniker: written only when set, so a record without one is byte-identical to the older
        // format (backward-compatible — old decoders read up to here and stop; see DecodeBody).
        var moniker = Monikers.Normalize(record.Moniker);
        if (moniker is not null)
            w.WriteString(moniker);

        return w.ToArray();
    }

    public static byte[] Encode(PeerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var w = new CodexWriter();
        w.WriteBytes(SignedBody(record));
        w.WriteBytes(record.Signature);
        return w.ToArray();
    }

    public static (PeerRecord Record, byte[] SignedBody) Decode(ReadOnlySpan<byte> document)
    {
        var reader = new CodexReader(document);
        var body = reader.ReadBytes().ToArray();
        var signature = reader.ReadBytes().ToArray();
        return (DecodeBody(body) with { Signature = signature, SignedBody = body }, body);
    }

    /// <summary>The bytes a record's signature covers: the ones it arrived with, or its encoding if built here.</summary>
    public static byte[] SignedBody(PeerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var encoded = EncodeBody(record);
        if (record.SignedBody is not { } received)
            return encoded;
        // Use the received bytes only while the fields are still the ones those bytes decode to.
        try { return EncodeBody(DecodeBody(received)).AsSpan().SequenceEqual(encoded) ? received : encoded; }
        catch (CodexFormatException) { return encoded; }
    }

    private static PeerRecord DecodeBody(ReadOnlySpan<byte> body)
    {
        var r = new CodexReader(body);
        var sealKey = r.ReadBytes().ToArray();
        if (sealKey.Length is 0 or > 64)
            throw new CodexFormatException("Invalid Seal public key length.");
        var endpoints = BeaconCodec.Read(ref r, MaxEndpoints);
        var sequence = r.ReadUInt64();
        var issuedAt = (long)r.ReadUInt64();
        var capabilities = (PeerCapabilities)r.ReadUInt32();

        // Optional trailing Moniker (see EncodeBody): present only if bytes remain. Clamped, never rejected.
        var moniker = r.End ? null : Monikers.Normalize(r.ReadString());

        return new PeerRecord
        {
            SealPublicKey = sealKey,
            Endpoints = endpoints,
            SequenceNumber = sequence,
            IssuedAtUnix = issuedAt,
            Capabilities = capabilities,
            Moniker = moniker,
            Signature = [],
        };
    }
}

/// <summary>Creates and validates signed <see cref="PeerRecord"/>s.</summary>
public static class PeerRecordSigner
{
    /// <summary>Signs a fresh peer record for this node.</summary>
    public static PeerRecord Create(NodeIdentity identity, IReadOnlyList<Beacon> endpoints, ulong sequenceNumber, PeerCapabilities capabilities, ICryptoSuite suite, DateTimeOffset now, string? moniker = null)
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
            Moniker = Monikers.Normalize(moniker),
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
        return suite.Verifier.Verify(PeerRecordCodec.SignedBody(record), record.Signature, record.SealPublicKey);
    }
}
