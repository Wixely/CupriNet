using System.Security.Cryptography;

namespace CupriNet.Alembic.Simulacrum;

/// <summary>
/// A hollow imitation of a crypto suite — the "seam-first, insecure-first" development cipher.
/// AEAD is passthrough (no confidentiality), signatures are empty and verification always succeeds.
/// Its wire shapes (nonce, tag, signature sizes) match the real suite so the secure suite can be
/// swapped in later without changing frame layouts. Hashing and HKDF are genuine (they are not the
/// security boundary). NEVER usable in a secure context: <see cref="ICryptoSuite.IsSecure"/> is false.
/// </summary>
public sealed class SimulacrumSuite : ICryptoSuite
{
    /// <summary>Ed25519-shaped signature length, so the signature slot matches the secure suite.</summary>
    public const int SignatureSize = 64;

    /// <summary>Ed25519-shaped public key length.</summary>
    public const int PublicKeySize = 32;

    /// <summary>X25519/Ed25519-shaped private key length.</summary>
    public const int PrivateKeySize = 32;

    private readonly BclHashProvider _hash = new();
    private readonly BclHkdf _kdf = new();

    public SimulacrumSuite(InsecureConsent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
    }

    public string Name => "Simulacrum";

    public bool IsSecure => false;

    public IAead Aead { get; } = new PassthroughAead();

    public IHashProvider Hash => _hash;

    public IKdf Kdf => _kdf;

    public IPasswordHardener Passwords { get; } = new WeakHardener();

    public IVerifier Verifier { get; } = new CredulousVerifier();

    public IKeyAgreement Agreement { get; } = new UnsupportedAgreement();

    public ISigner CreateSigner(ReadOnlySpan<byte> privateSeal)
    {
        if (privateSeal.Length != PrivateKeySize)
            throw new ArgumentException($"Private Seal must be {PrivateKeySize} bytes.", nameof(privateSeal));
        return new EmptySigner(DerivePublicKey(privateSeal));
    }

    public SealKeyPair GenerateSeal()
    {
        var privateKey = RandomNumberGenerator.GetBytes(PrivateKeySize);
        return new SealKeyPair(privateKey, DerivePublicKey(privateKey));
    }

    // A deterministic stand-in for a real public key: the SHA-256 of the private key.
    private static byte[] DerivePublicKey(ReadOnlySpan<byte> privateKey) => SHA256.HashData(privateKey);

    private sealed class PassthroughAead : IAead
    {
        public int KeySize => 32;
        public int NonceSize => 12;
        public int TagSize => 16;

        public byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
        {
            // No encryption: copy plaintext and append a zero tag to preserve the ciphertext+tag shape.
            var output = new byte[plaintext.Length + TagSize];
            plaintext.CopyTo(output);
            return output;
        }

        public byte[]? Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> associatedData)
        {
            // No authentication: strip the tag and return the "plaintext" unchecked.
            if (ciphertextAndTag.Length < TagSize)
                return null;
            return ciphertextAndTag[..^TagSize].ToArray();
        }
    }

    private sealed class EmptySigner(byte[] publicKey) : ISigner
    {
        public ReadOnlyMemory<byte> PublicKey { get; } = publicKey;

        public byte[] Sign(ReadOnlySpan<byte> message) => new byte[SignatureSize];
    }

    private sealed class CredulousVerifier : IVerifier
    {
        public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey) => true;
    }

    private sealed class UnsupportedAgreement : IKeyAgreement
    {
        private const string Message = "The Simulacrum does not provide key agreement; Noise requires the secure suite.";

        public int PublicKeySize => 32;

        public (byte[] PrivateKey, byte[] PublicKey) Generate() => throw new NotSupportedException(Message);

        public byte[] DerivePublicKey(ReadOnlySpan<byte> privateKey) => throw new NotSupportedException(Message);

        public byte[] Agree(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey) => throw new NotSupportedException(Message);
    }

    private sealed class WeakHardener : IPasswordHardener
    {
        // Deterministic and fast — explicitly NOT memory-hard. A stand-in for Argon2id only.
        public byte[] Harden(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            var seed = new byte[password.Length + salt.Length];
            password.CopyTo(seed);
            salt.CopyTo(seed.AsSpan(password.Length));
            var ikm = SHA256.HashData(seed);
            var output = new byte[length];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, info: "cuprinet/simulacrum/harden"u8);
            return output;
        }
    }
}
