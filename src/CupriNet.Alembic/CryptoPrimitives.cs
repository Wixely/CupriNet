namespace CupriNet.Alembic;

/// <summary>An authenticated-encryption primitive (AEAD), e.g. ChaCha20-Poly1305.</summary>
public interface IAead
{
    int KeySize { get; }
    int NonceSize { get; }
    int TagSize { get; }

    /// <summary>Encrypts <paramref name="plaintext"/> and returns ciphertext concatenated with the tag.</summary>
    byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);

    /// <summary>Decrypts and authenticates. Returns plaintext, or <c>null</c> if authentication fails.</summary>
    byte[]? Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> associatedData);
}

/// <summary>Cryptographic hashes (SHA-2 family).</summary>
public interface IHashProvider
{
    byte[] Sha256(ReadOnlySpan<byte> data);
    byte[] Sha512(ReadOnlySpan<byte> data);
}

/// <summary>A key-derivation function (HKDF).</summary>
public interface IKdf
{
    byte[] DeriveKey(ReadOnlySpan<byte> inputKeyMaterial, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length);
}

/// <summary>A memory-hard password hardener (Argon2id in the secure suite; the Crucible).</summary>
public interface IPasswordHardener
{
    byte[] Harden(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int length);
}

/// <summary>Produces detached signatures over a message using a private Seal.</summary>
public interface ISigner
{
    ReadOnlyMemory<byte> PublicKey { get; }
    byte[] Sign(ReadOnlySpan<byte> message);
}

/// <summary>Verifies detached signatures against a public Seal.</summary>
public interface IVerifier
{
    bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey);
}

/// <summary>A long-term identity (Seal) key pair.</summary>
public readonly record struct SealKeyPair(byte[] PrivateKey, byte[] PublicKey);

/// <summary>
/// Diffie–Hellman key agreement (X25519), used by the Noise transport handshake. Insecure development
/// suites may not support this (Noise requires real DH); calls then throw <see cref="NotSupportedException"/>.
/// </summary>
public interface IKeyAgreement
{
    /// <summary>Public-key length in bytes (32 for X25519).</summary>
    int PublicKeySize { get; }

    /// <summary>Generates a fresh agreement key pair.</summary>
    (byte[] PrivateKey, byte[] PublicKey) Generate();

    /// <summary>Derives the public key for a private key.</summary>
    byte[] DerivePublicKey(ReadOnlySpan<byte> privateKey);

    /// <summary>Computes the shared secret between our private key and a peer's public key.</summary>
    byte[] Agree(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey);
}
