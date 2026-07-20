using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;

namespace CupriNet.Core;

/// <summary>
/// A persisted pairwise trust record: the authenticated peer identity, this node's Guise for the
/// relationship, and the Seal-signed binding proving the Guise belongs to this node.
/// </summary>
public sealed record RelationshipRecord
{
    public required Sigil PeerSigil { get; init; }
    public required byte[] PeerSealPublicKey { get; init; }
    public required byte[] GuisePrivateKey { get; init; }
    public required byte[] GuisePublicKey { get; init; }
    public required byte[] GuiseBindingSignature { get; init; }
    public required long EstablishedUnix { get; init; }
}

/// <summary>Creates pairwise relationship records after a successful Conjunction.</summary>
public static class Relationship
{
    /// <summary>
    /// Establishes a fresh relationship with an authenticated peer: mints a Guise and its Seal binding.
    /// </summary>
    public static RelationshipRecord Establish(NodeIdentity identity, Sigil peerSigil, byte[] peerSealPublicKey, ICryptoSuite suite, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(peerSealPublicKey);
        ArgumentNullException.ThrowIfNull(suite);

        var keyPair = suite.GenerateSeal();
        var guise = new GuiseKeyPair(keyPair.PrivateKey, keyPair.PublicKey);
        var binding = GuiseBinding.Create(identity, peerSigil, guise.PublicKey, suite);

        return new RelationshipRecord
        {
            PeerSigil = peerSigil,
            PeerSealPublicKey = peerSealPublicKey,
            GuisePrivateKey = guise.PrivateKey,
            GuisePublicKey = guise.PublicKey,
            GuiseBindingSignature = binding,
            EstablishedUnix = now.ToUnixTimeSeconds(),
        };
    }
}

/// <summary>Canonical serialization for <see cref="RelationshipRecord"/>.</summary>
public static class RelationshipCodec
{
    public static byte[] Encode(RelationshipRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var w = new CodexWriter();
        w.WriteBytes(record.PeerSigil.Span);
        w.WriteBytes(record.PeerSealPublicKey);
        w.WriteBytes(record.GuisePrivateKey);
        w.WriteBytes(record.GuisePublicKey);
        w.WriteBytes(record.GuiseBindingSignature);
        w.WriteUInt64((ulong)record.EstablishedUnix);
        return w.ToArray();
    }

    public static RelationshipRecord Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        return new RelationshipRecord
        {
            PeerSigil = new Sigil(r.ReadBytes()),
            PeerSealPublicKey = r.ReadBytes().ToArray(),
            GuisePrivateKey = r.ReadBytes().ToArray(),
            GuisePublicKey = r.ReadBytes().ToArray(),
            GuiseBindingSignature = r.ReadBytes().ToArray(),
            EstablishedUnix = (long)r.ReadUInt64(),
        };
    }
}

/// <summary>Persists pairwise relationship records through an <see cref="ISecretStore"/>, keyed by peer Sigil.</summary>
public sealed class RelationshipStore(ISecretStore store)
{
    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask SaveAsync(RelationshipRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _store.StoreAsync(KeyFor(record.PeerSigil), RelationshipCodec.Encode(record), cancellationToken);
    }

    public async ValueTask<RelationshipRecord?> LoadAsync(Sigil peerSigil, CancellationToken cancellationToken = default)
    {
        var bytes = await _store.LoadAsync(KeyFor(peerSigil), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : RelationshipCodec.Decode(bytes);
    }

    public ValueTask DeleteAsync(Sigil peerSigil, CancellationToken cancellationToken = default)
        => _store.DeleteAsync(KeyFor(peerSigil), cancellationToken);

    private static string KeyFor(Sigil peerSigil) => "relationship/" + Convert.ToHexStringLower(peerSigil.Span);
}
