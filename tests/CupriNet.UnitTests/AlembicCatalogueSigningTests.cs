using System.Text;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Marks;
using Xunit;

namespace CupriNet.UnitTests;

public class AlembicCatalogueSigningTests
{
    [Fact]
    public void SignerAndVerifierBridge_RoundTrips_AndRejectsTamperAndWrongKey()
    {
        var suite = new BouncyCastleSuite();
        var seal = suite.GenerateSeal();
        var signer = new AlembicCatalogueSigner(suite, seal.PrivateKey, seal.PublicKey);
        var verifier = new AlembicCatalogueVerifier(suite);

        var body = Encoding.UTF8.GetBytes("a canonical catalogue body");
        var signature = signer.Sign(body);

        // Correct body + key verifies. This also proves the argument-order remap is right: CupriMark's
        // Verify(body, publicKey, signature) is routed to Alembic's Verify(message, signature, publicKey).
        Assert.True(verifier.Verify(body, signer.PublicKey, signature));

        // A tampered body must fail.
        var tampered = (byte[])body.Clone();
        tampered[0] ^= 0xFF;
        Assert.False(verifier.Verify(tampered, signer.PublicKey, signature));

        // A different key must fail.
        var other = suite.GenerateSeal();
        Assert.False(verifier.Verify(body, other.PublicKey, signature));
    }

    [Fact]
    public void SignerBridge_ExposesTheProvidedPublicKey()
    {
        var suite = new BouncyCastleSuite();
        var seal = suite.GenerateSeal();
        var signer = new AlembicCatalogueSigner(suite, seal.PrivateKey, seal.PublicKey);
        Assert.True(signer.PublicKey.SequenceEqual(seal.PublicKey));
    }
}
