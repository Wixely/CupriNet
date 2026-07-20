namespace CupriNet.Abstractions;

/// <summary>
/// Protects and unprotects bytes at rest. Implementations range from a no-op (development) to
/// AEAD-under-a-master-key. Kept separate from <see cref="ISecretStore"/> so the storage medium and the
/// protection scheme vary independently.
/// </summary>
public interface IDataProtector
{
    /// <summary>Wraps plaintext for storage.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>Unwraps previously protected bytes. Throws if the data is corrupt or cannot be authenticated.</summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}
