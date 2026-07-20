using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc8032;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;
using BcX25519Agreement = Org.BouncyCastle.Crypto.Agreement.X25519Agreement;

namespace CupriNet.Alembic.BouncyCastle;

/// <summary>
/// The secure crypto suite: a 100% managed, MIT-compatible implementation over BouncyCastle. This is
/// what replaces the Simulacrum at the Tempering (Phase 1b). It provides real Ed25519 signatures,
/// ChaCha20-Poly1305 authenticated encryption, and Argon2id password hardening; hashing and HKDF reuse
/// the BCL primitives from CupriNet.Alembic. Wire shapes (32-byte keys, 12-byte nonce, 16-byte tag,
/// 64-byte signature) match the Simulacrum, so swapping suites changes no frame layouts.
/// </summary>
public sealed class BouncyCastleSuite : ICryptoSuite
{
    private readonly BclHashProvider _hash = new();
    private readonly BclHkdf _kdf = new();

    public string Name => "BouncyCastle";

    public bool IsSecure => true;

    public IAead Aead { get; } = new ChaCha20Poly1305Aead();

    public IHashProvider Hash => _hash;

    public IKdf Kdf => _kdf;

    public IPasswordHardener Passwords { get; } = new Argon2idHardener();

    public IVerifier Verifier { get; } = new Ed25519Verifier();

    public IKeyAgreement Agreement { get; } = new X25519KeyAgreement();

    public ISigner CreateSigner(ReadOnlySpan<byte> privateSeal)
    {
        if (privateSeal.Length != Ed25519.SecretKeySize)
            throw new ArgumentException($"Private Seal must be {Ed25519.SecretKeySize} bytes.", nameof(privateSeal));
        return new Ed25519SignerImpl(privateSeal.ToArray());
    }

    public SealKeyPair GenerateSeal()
    {
        var privateKey = RandomNumberGenerator.GetBytes(Ed25519.SecretKeySize);
        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
        return new SealKeyPair(privateKey, publicKey);
    }

    private sealed class Ed25519SignerImpl : ISigner
    {
        private readonly byte[] _privateKey;

        public Ed25519SignerImpl(byte[] privateKey)
        {
            _privateKey = privateKey;
            var publicKey = new byte[Ed25519.PublicKeySize];
            Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
            PublicKey = publicKey;
        }

        public ReadOnlyMemory<byte> PublicKey { get; }

        public byte[] Sign(ReadOnlySpan<byte> message)
        {
            var m = message.ToArray();
            var signature = new byte[Ed25519.SignatureSize];
            Ed25519.Sign(_privateKey, 0, m, 0, m.Length, signature, 0);
            return signature;
        }
    }

    private sealed class Ed25519Verifier : IVerifier
    {
        public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
        {
            if (signature.Length != Ed25519.SignatureSize || publicKey.Length != Ed25519.PublicKeySize)
                return false;
            var m = message.ToArray();
            return Ed25519.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, m, 0, m.Length);
        }
    }

    private sealed class ChaCha20Poly1305Aead : IAead
    {
        public int KeySize => 32;
        public int NonceSize => 12;
        public int TagSize => 16;

        public byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
        {
            var cipher = new BcChaCha20Poly1305();
            cipher.Init(true, new ParametersWithIV(new KeyParameter(key.ToArray()), nonce.ToArray()));
            if (!associatedData.IsEmpty)
            {
                var aad = associatedData.ToArray();
                cipher.ProcessAadBytes(aad, 0, aad.Length);
            }

            var input = plaintext.ToArray();
            var output = new byte[cipher.GetOutputSize(input.Length)];
            var written = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            written += cipher.DoFinal(output, written);
            return Trim(output, written);
        }

        public byte[]? Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> associatedData)
        {
            if (ciphertextAndTag.Length < TagSize)
                return null;

            var cipher = new BcChaCha20Poly1305();
            cipher.Init(false, new ParametersWithIV(new KeyParameter(key.ToArray()), nonce.ToArray()));
            if (!associatedData.IsEmpty)
            {
                var aad = associatedData.ToArray();
                cipher.ProcessAadBytes(aad, 0, aad.Length);
            }

            var input = ciphertextAndTag.ToArray();
            var output = new byte[cipher.GetOutputSize(input.Length)];
            try
            {
                var written = cipher.ProcessBytes(input, 0, input.Length, output, 0);
                written += cipher.DoFinal(output, written);
                return Trim(output, written);
            }
            catch (InvalidCipherTextException)
            {
                return null;
            }
        }

        private static byte[] Trim(byte[] buffer, int length)
        {
            if (length == buffer.Length)
                return buffer;
            var result = new byte[length];
            Array.Copy(buffer, result, length);
            return result;
        }
    }

    private sealed class X25519KeyAgreement : IKeyAgreement
    {
        public int PublicKeySize => 32;

        public (byte[] PrivateKey, byte[] PublicKey) Generate()
        {
            var privateKey = new X25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
            return (privateKey.GetEncoded(), privateKey.GeneratePublicKey().GetEncoded());
        }

        public byte[] DerivePublicKey(ReadOnlySpan<byte> privateKey)
            => new X25519PrivateKeyParameters(privateKey.ToArray(), 0).GeneratePublicKey().GetEncoded();

        public byte[] Agree(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey)
        {
            var agreement = new BcX25519Agreement();
            agreement.Init(new X25519PrivateKeyParameters(privateKey.ToArray(), 0));
            var secret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicKey.ToArray(), 0), secret, 0);
            return secret;
        }
    }

    private sealed class Argon2idHardener : IPasswordHardener
    {
        // RFC 9106-style parameters: memory-hard. Tunable per device profile later.
        private const int MemoryKb = 65536; // 64 MiB
        private const int Iterations = 3;
        private const int Parallelism = 1;

        public byte[] Harden(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

            var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
                .WithVersion(Argon2Parameters.Version13)
                .WithMemoryAsKB(MemoryKb)
                .WithIterations(Iterations)
                .WithParallelism(Parallelism)
                .WithSalt(salt.ToArray())
                .Build();

            var generator = new Argon2BytesGenerator();
            generator.Init(parameters);

            var output = new byte[length];
            generator.GenerateBytes(password.ToArray(), output, 0, length);
            return output;
        }
    }
}
