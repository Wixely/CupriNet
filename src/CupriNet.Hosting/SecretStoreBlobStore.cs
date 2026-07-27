using CupriNet.Abstractions;

namespace CupriNet.Hosting;

/// <summary>
/// A synchronous opaque-blob key/value store backed by CupriNet's <see cref="ISecretStore"/> — the shape a Tor
/// stack's state store (entry guards, caches) needs. Using this instead of a plaintext file keeps Tor's durable
/// state (notably the entry-guard set, whose loss on restart is a deanonymization risk) inside our encrypted
/// store. Wrap it in the Tor library's own state-store interface in the concrete transport binding.
/// </summary>
/// <remarks>
/// The bridge is sync-over-async because Tor state-store APIs are synchronous; it is only ever called from the
/// transport's own worker threads (no captured UI/single-thread context), where blocking is safe.
/// </remarks>
public sealed class SecretStoreBlobStore(ISecretStore store, string prefix = "tor/")
{
    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public byte[]? Read(string key)
        => _store.LoadAsync(prefix + key).AsTask().GetAwaiter().GetResult();

    public void Write(string key, ReadOnlyMemory<byte> value)
        => _store.StoreAsync(prefix + key, value).AsTask().GetAwaiter().GetResult();

    public void Delete(string key)
        => _store.DeleteAsync(prefix + key).AsTask().GetAwaiter().GetResult();
}
