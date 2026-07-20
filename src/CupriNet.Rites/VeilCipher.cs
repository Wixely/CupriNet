using System.Security.Cryptography;
using CupriNet.Alembic;

namespace CupriNet.Rites;

/// <summary>
/// Encrypts and authenticates channel content under a Consecration session (Veil) key. Each message
/// gets a fresh random nonce, and the output is <c>nonce || ciphertext+tag</c>. This is the inner
/// confidentiality layer: content is protected by the channel session key, independent of (and in
/// addition to) any transport protection on the Vessel.
/// </summary>
public sealed class VeilCipher
{
    private readonly IAead _aead;
    private readonly byte[] _sessionKey;

    public VeilCipher(ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        _aead = suite.Aead;
        if (sessionKey.Length != _aead.KeySize)
            throw new ArgumentException($"Session key must be {_aead.KeySize} bytes for {suite.Name}.", nameof(sessionKey));
        _sessionKey = sessionKey.ToArray();
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(_aead.NonceSize);
        var sealed_ = _aead.Seal(_sessionKey, nonce, plaintext, associatedData: []);
        var output = new byte[nonce.Length + sealed_.Length];
        nonce.CopyTo(output, 0);
        sealed_.CopyTo(output, nonce.Length);
        return output;
    }

    public byte[]? Open(ReadOnlySpan<byte> sealedMessage)
    {
        if (sealedMessage.Length < _aead.NonceSize + _aead.TagSize)
            return null;
        var nonce = sealedMessage[.._aead.NonceSize];
        var ciphertextAndTag = sealedMessage[_aead.NonceSize..];
        return _aead.Open(_sessionKey, nonce, ciphertextAndTag, associatedData: []);
    }
}
