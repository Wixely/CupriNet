using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Arcanum;
using Xunit;

namespace CupriNet.UnitTests;

public class OwnershipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly byte[] Channel = Enumerable.Repeat((byte)0xAB, 32).ToArray();

    private static ICryptoSuite Suite() => CryptoSuites.Secure(); // real Ed25519; ownership is all signatures

    [Fact]
    public void Descriptor_VerifiesGenuine_RejectsTampered()
    {
        var suite = Suite();
        var owner = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner, Channel, ArcanumEntry.Sanction, policyVersion: 1, suite, Now);

        Assert.True(Ownership.VerifyDescriptor(descriptor, suite));
        Assert.False(Ownership.VerifyDescriptor(descriptor with { PolicyVersion = 2 }, suite));
    }

    [Fact]
    public void Investiture_Verifies_ForIssuerChannelAndWindow()
    {
        var suite = Suite();
        var owner = suite.GenerateSeal();
        var member = suite.GenerateSeal();

        var inv = Ownership.Invest(owner, Channel, member.PublicKey, ArcanumRole.Member, Now, Now.AddDays(30), serialNumber: 1, suite);

        Assert.True(Ownership.VerifyInvestiture(inv, owner.PublicKey, Channel, suite, Now));
        Assert.False(Ownership.VerifyInvestiture(inv, owner.PublicKey, Channel, suite, Now.AddDays(31)));   // expired
        Assert.False(Ownership.VerifyInvestiture(inv, owner.PublicKey, Channel, suite, Now.AddDays(-1)));   // not yet valid
        Assert.False(Ownership.VerifyInvestiture(inv, suite.GenerateSeal().PublicKey, Channel, suite, Now)); // wrong issuer
        Assert.False(Ownership.VerifyInvestiture(inv, owner.PublicKey, new byte[32], suite, Now));           // wrong channel
    }

    [Fact]
    public void Descriptor_RejectsUnknownVersion_EvenWhenValidlySigned()
    {
        var suite = Suite();
        var owner = suite.GenerateSeal();
        var v1 = Ownership.CreateDescriptor(owner, Channel, ArcanumEntry.Sanction, policyVersion: 1, suite, Now);
        Assert.True(Ownership.VerifyDescriptor(v1, suite));

        // Re-signed at a version the CupriMark catalogue doesn't know: the acceptance gate refuses it even
        // though the signature itself is valid (isolating the version check from the signature check).
        var draft = v1 with { Version = (byte)(v1.Version + 1), Signature = [] };
        var signed = draft with { Signature = suite.CreateSigner(owner.PrivateKey).Sign(Ownership.DescriptorBody(draft)) };
        Assert.False(Ownership.VerifyDescriptor(signed, suite));
    }

    [Fact]
    public void Investiture_RejectsUnknownVersion_EvenWhenValidlySigned()
    {
        var suite = Suite();
        var owner = suite.GenerateSeal();
        var member = suite.GenerateSeal();
        var v1 = Ownership.Invest(owner, Channel, member.PublicKey, ArcanumRole.Member, Now, Now.AddDays(30), serialNumber: 1, suite);
        Assert.True(Ownership.VerifyInvestiture(v1, owner.PublicKey, Channel, suite, Now));

        var draft = v1 with { Version = (byte)(v1.Version + 1), Signature = [] };
        var signed = draft with { Signature = suite.CreateSigner(owner.PrivateKey).Sign(Ownership.InvestitureBody(draft)) };
        Assert.False(Ownership.VerifyInvestiture(signed, owner.PublicKey, Channel, suite, Now));
    }

    [Fact]
    public void Ascension_TransfersOwnership_AcrossReigns()
    {
        var suite = Suite();
        var owner0 = suite.GenerateSeal();
        var owner1 = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner0, Channel, ArcanumEntry.Sanction, policyVersion: 1, suite, Now);

        var link = Ownership.Ascend(owner0, Channel, newReign: 1, owner1.PublicKey, Ownership.HashOf(descriptor, suite), suite);
        var state = Ownership.Resolve(descriptor, [link], suite);

        Assert.Equal(1UL, state.Reign);
        Assert.Equal(owner1.PublicKey, state.CurrentOwnerPublicKey);

        // Reign 0 (no links) resolves to the founding owner.
        Assert.Equal(owner0.PublicKey, Ownership.Resolve(descriptor, [], suite).CurrentOwnerPublicKey);
    }

    [Fact]
    public void Resolve_RejectsBrokenChain_BadSignature_AndNonMonotonicReign()
    {
        var suite = Suite();
        var owner0 = suite.GenerateSeal();
        var owner1 = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner0, Channel, ArcanumEntry.Sealed, 1, suite, Now);
        var descriptorHash = Ownership.HashOf(descriptor, suite);

        // Wrong previous-hash breaks the chain.
        var badChain = Ownership.Ascend(owner0, Channel, 1, owner1.PublicKey, new byte[32], suite);
        Assert.Throws<OwnershipException>(() => Ownership.Resolve(descriptor, [badChain], suite));

        // Signed by someone who is not the reigning owner.
        var imposter = suite.GenerateSeal();
        var forged = Ownership.Ascend(imposter, Channel, 1, owner1.PublicKey, descriptorHash, suite);
        Assert.Throws<OwnershipException>(() => Ownership.Resolve(descriptor, [forged], suite));

        // Non-monotonic reign (jumps to 2).
        var skipped = Ownership.Ascend(owner0, Channel, 2, owner1.PublicKey, descriptorHash, suite);
        Assert.Throws<OwnershipException>(() => Ownership.Resolve(descriptor, [skipped], suite));
    }

    [Fact]
    public void ConflictingTransfers_AreASchism_NotResolvedByTimestamp()
    {
        var suite = Suite();
        var owner0 = suite.GenerateSeal();
        var heirX = suite.GenerateSeal();
        var heirY = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner0, Channel, ArcanumEntry.Sanction, 1, suite, Now);
        var descriptorHash = Ownership.HashOf(descriptor, suite);

        // The founding owner signs two conflicting transfers at Reign 1.
        var toX = Ownership.Ascend(owner0, Channel, 1, heirX.PublicKey, descriptorHash, suite);
        var toY = Ownership.Ascend(owner0, Channel, 1, heirY.PublicKey, descriptorHash, suite);

        // Each branch is individually valid...
        Assert.Equal(heirX.PublicKey, Ownership.Resolve(descriptor, [toX], suite).CurrentOwnerPublicKey);
        Assert.Equal(heirY.PublicKey, Ownership.Resolve(descriptor, [toY], suite).CurrentOwnerPublicKey);

        // ...and together they are a Schism, which the system reports rather than resolving.
        Assert.True(Ownership.IsSchism(descriptor, [toX], [toY], suite));
    }

    [Fact]
    public void Investiture_ByNewOwner_VerifiesAfterAscension()
    {
        var suite = Suite();
        var owner0 = suite.GenerateSeal();
        var owner1 = suite.GenerateSeal();
        var member = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner0, Channel, ArcanumEntry.Sanction, 1, suite, Now);
        var link = Ownership.Ascend(owner0, Channel, 1, owner1.PublicKey, Ownership.HashOf(descriptor, suite), suite);
        var head = Ownership.Resolve(descriptor, [link], suite);

        // The new reigning owner can issue a valid Investiture.
        var inv = Ownership.Invest(owner1, Channel, member.PublicKey, ArcanumRole.Member | ArcanumRole.Steward, Now, Now.AddDays(7), 1, suite);
        Assert.True(Ownership.VerifyInvestiture(inv, head.CurrentOwnerPublicKey, Channel, suite, Now));
    }
}
