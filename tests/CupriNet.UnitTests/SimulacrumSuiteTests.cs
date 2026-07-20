using CupriNet.Alembic;
using CupriNet.Alembic.Simulacrum;
using Xunit;

namespace CupriNet.UnitTests;

public class SimulacrumSuiteTests
{
    private static SimulacrumSuite CreateSuite()
        => new(InsecureConsent.IUnderstandThisProvidesNoSecurity());

    [Fact]
    public void Construction_RequiresConsent()
    {
        Assert.Throws<ArgumentNullException>(() => new SimulacrumSuite(null!));
    }

    [Fact]
    public void Suite_IsMarkedInsecure()
    {
        var suite = CreateSuite();
        Assert.False(suite.IsSecure);
        Assert.Equal("Simulacrum", suite.Name);
    }

    [Fact]
    public void Aead_RoundTrips_AsPassthrough()
    {
        var suite = CreateSuite();
        var aead = suite.Aead;
        var key = new byte[aead.KeySize];
        var nonce = new byte[aead.NonceSize];
        byte[] plaintext = [1, 2, 3, 4, 5];

        var sealed_ = aead.Seal(key, nonce, plaintext, associatedData: []);
        Assert.Equal(plaintext.Length + aead.TagSize, sealed_.Length);

        var opened = aead.Open(key, nonce, sealed_, associatedData: []);
        Assert.NotNull(opened);
        Assert.True(opened.SequenceEqual(plaintext));
    }

    [Fact]
    public void Aead_DoesNotActuallyEncrypt_PlaintextIsVisible()
    {
        // Documents the insecurity: the plaintext appears verbatim in the "ciphertext".
        var suite = CreateSuite();
        var aead = suite.Aead;
        byte[] plaintext = [42, 43, 44];
        var sealed_ = aead.Seal(new byte[aead.KeySize], new byte[aead.NonceSize], plaintext, []);
        Assert.True(sealed_.AsSpan(0, plaintext.Length).SequenceEqual(plaintext));
    }

    [Fact]
    public void Signer_And_Verifier_AlwaysAccept()
    {
        var suite = CreateSuite();
        var seal = suite.GenerateSeal();
        var signer = suite.CreateSigner(seal.PrivateKey);

        var signature = signer.Sign("hello"u8);
        Assert.Equal(SimulacrumSuite.SignatureSize, signature.Length);

        // The credulous verifier accepts anything — this is what the secure suite must later reject.
        Assert.True(suite.Verifier.Verify("hello"u8, signature, signer.PublicKey.Span));
        Assert.True(suite.Verifier.Verify("tampered"u8, signature, signer.PublicKey.Span));
    }

    [Fact]
    public void Hash_IsRealSha256()
    {
        var suite = CreateSuite();
        // Known-answer: SHA-256("abc").
        var digest = suite.Hash.Sha256("abc"u8);
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Convert.ToHexStringLower(digest));
    }

    [Fact]
    public void Kdf_IsDeterministic()
    {
        var suite = CreateSuite();
        var a = suite.Kdf.DeriveKey("ikm"u8, "salt"u8, "info"u8, 32);
        var b = suite.Kdf.DeriveKey("ikm"u8, "salt"u8, "info"u8, 32);
        Assert.Equal(32, a.Length);
        Assert.True(a.SequenceEqual(b));
    }
}
