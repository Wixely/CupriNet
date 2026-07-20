using CupriNet.Alembic;
using CupriNet.Alembic.Simulacrum;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class NodeIdentityTests
{
    private static ICryptoSuite Suite() => new SimulacrumSuite(InsecureConsent.IUnderstandThisProvidesNoSecurity());

    [Fact]
    public void Generate_ProducesSigilFromSealPublicKey()
    {
        var suite = Suite();
        var identity = NodeIdentity.Generate(suite);

        Assert.False(identity.Sigil.IsEmpty);
        Assert.Equal(
            CupriNet.Abstractions.Sigil.FromSealPublicKey(identity.PublicKey.Span),
            identity.Sigil);
    }

    [Fact]
    public void DistinctIdentities_HaveDistinctSigils()
    {
        var suite = Suite();
        var a = NodeIdentity.Generate(suite);
        var b = NodeIdentity.Generate(suite);
        Assert.NotEqual(a.Sigil, b.Sigil);
    }

    [Fact]
    public void FromSeal_RehydratesSameSigil()
    {
        var suite = Suite();
        var original = NodeIdentity.Generate(suite);
        var rehydrated = NodeIdentity.FromSeal(original.Seal);
        Assert.Equal(original.Sigil, rehydrated.Sigil);
    }
}
