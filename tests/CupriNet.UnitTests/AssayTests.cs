using CupriNet.Abstractions;
using CupriNet.Codex;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The Assay: the security suite. These tests only pass with the real (secure) suite. Where relevant
/// they assert the exact flip from the Simulacrum's pinned insecurity — proving the Tempering makes the
/// cryptography do real work, not just that the happy path still runs.
/// </summary>
public class AssayTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static Intonation ValidIntonation(Alembic.ICryptoSuite suite, out byte[] document)
    {
        var identity = NodeIdentity.Generate(suite);
        var intonation = IntonationMint.Intone(identity, suite, new IntonationOptions
        {
            Network = Network,
            Beacons = [new Beacon(EndpointKind.Host, "127.0.0.1", 43820)],
        }, Now);
        document = IntonationCodec.Encode(intonation);
        return intonation;
    }

    // Tamper a byte inside the inviter public key: it is part of the signed body but does not affect the
    // version/network checks that run before signature verification.
    private static byte[] TamperSignedBody(byte[] document)
    {
        var (intonation, body) = IntonationCodec.Decode(document);
        body[20] ^= 0xFF;
        var w = new CodexWriter();
        w.WriteBytes(body);
        w.WriteBytes(intonation.Signature);
        return w.ToArray();
    }

    [Fact]
    public void SecureSuite_RejectsTamperedIntonation_ThatSimulacrumAccepts()
    {
        // Simulacrum: the tamper is NOT caught (pinned insecurity).
        var sim = CryptoSuites.Simulacrum();
        ValidIntonation(sim, out var simDoc);
        var simResult = IntonationValidator.ValidateDocument(TamperSignedBody(simDoc), Network, sim, Now);
        Assert.NotEqual(IntonationStatus.BadSignature, simResult.Status);

        // BouncyCastle: the same tamper IS caught.
        var secure = CryptoSuites.Secure();
        ValidIntonation(secure, out var secureDoc);
        var secureResult = IntonationValidator.ValidateDocument(TamperSignedBody(secureDoc), Network, secure, Now);
        Assert.Equal(IntonationStatus.BadSignature, secureResult.Status);
    }

    [Fact]
    public void Ed25519_RejectsWrongMessage_WrongKey_AndForgedSignature()
    {
        var suite = CryptoSuites.Secure();
        var seal = suite.GenerateSeal();
        var signer = suite.CreateSigner(seal.PrivateKey);
        var message = "authentic"u8.ToArray();
        var signature = signer.Sign(message);

        Assert.True(suite.Verifier.Verify(message, signature, signer.PublicKey.Span));
        Assert.False(suite.Verifier.Verify("tampered"u8, signature, signer.PublicKey.Span));

        var otherKey = suite.GenerateSeal();
        var otherSigner = suite.CreateSigner(otherKey.PrivateKey);
        Assert.False(suite.Verifier.Verify(message, signature, otherSigner.PublicKey.Span));

        var forged = new byte[signature.Length]; // all-zero forged signature
        Assert.False(suite.Verifier.Verify(message, forged, signer.PublicKey.Span));
    }

    [Fact]
    public void Aead_ActuallyEncrypts_AndDetectsTampering()
    {
        var suite = CryptoSuites.Secure();
        var aead = suite.Aead;
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(aead.KeySize);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(aead.NonceSize);
        byte[] plaintext = [10, 20, 30, 40, 50, 60];

        var sealed_ = aead.Seal(key, nonce, plaintext, associatedData: []);

        // Real encryption: the ciphertext region does not equal the plaintext.
        Assert.False(sealed_.AsSpan(0, plaintext.Length).SequenceEqual(plaintext));

        // Correct key/nonce round-trips.
        Assert.Equal(plaintext, aead.Open(key, nonce, sealed_, []));

        // Tampering any byte breaks authentication.
        var tampered = (byte[])sealed_.Clone();
        tampered[0] ^= 0xFF;
        Assert.Null(aead.Open(key, nonce, tampered, []));

        // Wrong key fails authentication.
        var wrongKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(aead.KeySize);
        Assert.Null(aead.Open(wrongKey, nonce, sealed_, []));
    }

    [Fact]
    public void GuiseBinding_ForgedByAnotherSeal_FailsVerification()
    {
        var suite = CryptoSuites.Secure();
        var me = NodeIdentity.Generate(suite);
        var peer = NodeIdentity.Generate(suite);
        var attacker = NodeIdentity.Generate(suite);

        // A binding actually signed by 'me' verifies against my Seal...
        var record = Relationship.Establish(me, peer.Sigil, peer.PublicKey.ToArray(), suite, Now);
        Assert.True(GuiseBinding.Verify(suite, me.PublicKey.Span, peer.Sigil, record.GuisePublicKey, record.GuiseBindingSignature));

        // ...but does NOT verify against the attacker's Seal (they cannot claim my Guise binding).
        Assert.False(GuiseBinding.Verify(suite, attacker.PublicKey.Span, peer.Sigil, record.GuisePublicKey, record.GuiseBindingSignature));
    }

    [Fact]
    public void Argon2id_IsDeterministic_AndSaltSensitive()
    {
        var suite = CryptoSuites.Secure();
        var password = "Dungeons&Dragons"u8.ToArray();
        byte[] saltA = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        byte[] saltB = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);

        var a1 = suite.Passwords.Harden(password, saltA, 32);
        var a2 = suite.Passwords.Harden(password, saltA, 32);
        var b = suite.Passwords.Harden(password, saltB, 32);

        Assert.Equal(32, a1.Length);
        Assert.Equal(a1, a2);            // deterministic for a given (password, salt)
        Assert.NotEqual(a1, b);          // different salt -> different key
    }
}
