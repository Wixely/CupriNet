using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Alembic;

namespace CupriNet.Persistence;

/// <summary>A pass-through protector: stores bytes unprotected. Development / seam-first default only.</summary>
public sealed class NullDataProtector : IDataProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => protectedData.ToArray();
}

/// <summary>
/// Protects data with the Alembic AEAD under a fixed master key. The output is <c>nonce || ciphertext+tag</c>.
/// Under the secure suite this is real authenticated encryption at rest; under the Simulacrum it is a
/// pass-through with the same framing (the protection strength arrives with the Tempering, unchanged code).
/// </summary>
public sealed class AeadDataProtector : IDataProtector
{
    private readonly IAead _aead;
    private readonly byte[] _masterKey;

    public AeadDataProtector(ICryptoSuite suite, ReadOnlyMemory<byte> masterKey)
    {
        ArgumentNullException.ThrowIfNull(suite);
        _aead = suite.Aead;
        if (masterKey.Length != _aead.KeySize)
            throw new ArgumentException($"Master key must be {_aead.KeySize} bytes for {suite.Name}.", nameof(masterKey));
        _masterKey = masterKey.ToArray();
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(_aead.NonceSize);
        var sealed_ = _aead.Seal(_masterKey, nonce, plaintext, associatedData: []);

        var output = new byte[nonce.Length + sealed_.Length];
        nonce.CopyTo(output, 0);
        sealed_.CopyTo(output, nonce.Length);
        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (protectedData.Length < _aead.NonceSize + _aead.TagSize)
            throw new CryptographicException("Protected blob is too short.");

        var nonce = protectedData[.._aead.NonceSize];
        var ciphertextAndTag = protectedData[_aead.NonceSize..];
        return _aead.Open(_masterKey, nonce, ciphertextAndTag, associatedData: [])
               ?? throw new CryptographicException("Protected blob failed authentication.");
    }
}
