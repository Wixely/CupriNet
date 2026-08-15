using CupriNet.Abstractions;
using CupriNet.Alembic;

namespace CupriNet.Core;

/// <summary>
/// Persists and restores a Shrine's <see cref="Signet"/> through an <see cref="ISecretStore"/>, keyed by a Shrine
/// name so one node can host several Shrines. The private key is only ever handed to the store, which protects it
/// at rest — mirroring <see cref="IdentityStore"/> for the node's own Seal.
/// </summary>
public sealed class SignetStore(ISecretStore store)
{
    /// <summary>The name used when a caller hosts a single unnamed Shrine.</summary>
    public const string DefaultName = "default";

    private const string KeyPrefix = "shrine/signet/";

    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Loads the persisted Signet for <paramref name="name"/>, or generates and persists a fresh one.</summary>
    public async ValueTask<Signet> LoadOrCreateAsync(ICryptoSuite suite, string name = DefaultName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suite);

        var existing = await LoadAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var signet = Signet.Generate(suite);
        await SaveAsync(signet, name, cancellationToken).ConfigureAwait(false);
        return signet;
    }

    public async ValueTask<Signet?> LoadAsync(string name = DefaultName, CancellationToken cancellationToken = default)
    {
        var bytes = await _store.LoadAsync(KeyFor(name), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Signet.FromSeal(SealCodec.Decode(bytes));
    }

    public ValueTask SaveAsync(Signet signet, string name = DefaultName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signet);
        return _store.StoreAsync(KeyFor(name), SealCodec.Encode(signet.Seal), cancellationToken);
    }

    private static string KeyFor(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return KeyPrefix + name;
    }
}
