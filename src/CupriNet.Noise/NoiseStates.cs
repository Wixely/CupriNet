using System.Buffers.Binary;
using System.Text;
using CupriNet.Alembic;

namespace CupriNet.Noise;

/// <summary>Thrown when a Noise handshake or transport operation fails.</summary>
public sealed class NoiseException(string message) : Exception(message);

/// <summary>
/// A Noise CipherState: an AEAD key plus a 64-bit message counter. Nonces are the ChaCha20-Poly1305
/// construction: 4 zero bytes followed by the little-endian counter. Before a key is set, encrypt/decrypt
/// are the identity (plaintext passes through), as the spec requires.
/// </summary>
public sealed class NoiseCipherState
{
    private readonly IAead _aead;
    private byte[]? _key;
    private ulong _nonce;

    internal NoiseCipherState(IAead aead) => _aead = aead;

    public bool HasKey => _key is not null;

    internal void InitializeKey(byte[] key)
    {
        _key = key;
        _nonce = 0;
    }

    public byte[] EncryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        if (_key is null)
            return plaintext.ToArray();
        var result = _aead.Seal(_key, Nonce(_nonce), plaintext, associatedData);
        _nonce++;
        return result;
    }

    public byte[] DecryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        if (_key is null)
            return ciphertext.ToArray();
        var result = _aead.Open(_key, Nonce(_nonce), ciphertext, associatedData)
                     ?? throw new NoiseException("Noise decryption failed (authentication).");
        _nonce++;
        return result;
    }

    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12]; // first 4 bytes are zero
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }
}

/// <summary>
/// A Noise SymmetricState: the chaining key and running transcript hash, plus the current CipherState.
/// Implements MixKey/MixHash/EncryptAndHash/DecryptAndHash/Split over the suite's HKDF, SHA-256, and AEAD.
/// </summary>
internal sealed class NoiseSymmetricState
{
    private const int HashLen = 32;

    private readonly ICryptoSuite _suite;
    private readonly NoiseCipherState _cipher;
    private byte[] _chainingKey;
    private byte[] _hash;

    public NoiseSymmetricState(ICryptoSuite suite, string protocolName)
    {
        _suite = suite;
        _cipher = new NoiseCipherState(suite.Aead);

        var name = Encoding.ASCII.GetBytes(protocolName);
        if (name.Length <= HashLen)
        {
            _hash = new byte[HashLen];
            name.CopyTo(_hash, 0);
        }
        else
        {
            _hash = suite.Hash.Sha256(name);
        }

        _chainingKey = (byte[])_hash.Clone();
    }

    public bool HasKey => _cipher.HasKey;

    public byte[] HandshakeHash => _hash;

    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var output = Hkdf(_chainingKey, inputKeyMaterial, 2);
        _chainingKey = output[0];
        _cipher.InitializeKey(output[1]);
    }

    public void MixHash(ReadOnlySpan<byte> data)
    {
        var buffer = new byte[_hash.Length + data.Length];
        _hash.CopyTo(buffer, 0);
        data.CopyTo(buffer.AsSpan(_hash.Length));
        _hash = _suite.Hash.Sha256(buffer);
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = _cipher.EncryptWithAd(_hash, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var plaintext = _cipher.DecryptWithAd(_hash, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    public (NoiseCipherState First, NoiseCipherState Second) Split()
    {
        var output = Hkdf(_chainingKey, ReadOnlySpan<byte>.Empty, 2);
        var first = new NoiseCipherState(_suite.Aead);
        var second = new NoiseCipherState(_suite.Aead);
        first.InitializeKey(output[0]);
        second.InitializeKey(output[1]);
        return (first, second);
    }

    // Noise HKDF(chaining_key, input_key_material, num_outputs): salt = chaining_key, ikm = input,
    // info = empty; take the first num_outputs 32-byte blocks.
    private byte[][] Hkdf(byte[] chainingKey, ReadOnlySpan<byte> inputKeyMaterial, int outputs)
    {
        var okm = _suite.Kdf.DeriveKey(inputKeyMaterial, chainingKey, ReadOnlySpan<byte>.Empty, outputs * HashLen);
        var result = new byte[outputs][];
        for (var i = 0; i < outputs; i++)
            result[i] = okm.AsSpan(i * HashLen, HashLen).ToArray();
        return result;
    }
}
