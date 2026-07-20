namespace CupriNet.Abstractions;

/// <summary>
/// Abstraction over platform-protected secret storage (Archivane). Concrete implementations
/// back onto DPAPI, Keychain, Keystore, or an encrypted file fallback.
/// </summary>
public interface ISecretStore
{
    ValueTask StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default);

    ValueTask<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default);
}
