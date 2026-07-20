using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;

namespace CupriNet.Core;

/// <summary>Canonical serialization of a Seal key pair.</summary>
public static class SealCodec
{
    public static byte[] Encode(SealKeyPair seal)
    {
        var w = new CodexWriter();
        w.WriteBytes(seal.PrivateKey);
        w.WriteBytes(seal.PublicKey);
        return w.ToArray();
    }

    public static SealKeyPair Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        var privateKey = r.ReadBytes().ToArray();
        var publicKey = r.ReadBytes().ToArray();
        return new SealKeyPair(privateKey, publicKey);
    }
}

/// <summary>
/// Persists and restores this node's long-term identity (Seal) through an <see cref="ISecretStore"/>.
/// The private key is only ever handed to the store, which protects it at rest.
/// </summary>
public sealed class IdentityStore(ISecretStore store)
{
    private const string Key = "identity/seal";

    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Loads the persisted identity, or generates and persists a fresh one on first run.</summary>
    public async ValueTask<NodeIdentity> LoadOrCreateAsync(ICryptoSuite suite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suite);

        var existing = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var identity = NodeIdentity.Generate(suite);
        await SaveAsync(identity, cancellationToken).ConfigureAwait(false);
        return identity;
    }

    public async ValueTask<NodeIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await _store.LoadAsync(Key, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : NodeIdentity.FromSeal(SealCodec.Decode(bytes));
    }

    public ValueTask SaveAsync(NodeIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _store.StoreAsync(Key, SealCodec.Encode(identity.Seal), cancellationToken);
    }
}
