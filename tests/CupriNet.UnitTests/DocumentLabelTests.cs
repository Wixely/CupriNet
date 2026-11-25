using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Arcanum;
using CupriNet.Concordance;
using CupriNet.Core;
using CupriNet.Traversal;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Every signed document is signed under a label that names what it is. A signature over the bare body — which is
/// what every one of them used to be, and so what a signature lifted from any other kind of document would amount
/// to — is refused, for each kind in turn.
/// </summary>
public class DocumentLabelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly Concordium Network = new("example.chat");

    [Fact]
    public void TheSigningInput_IsTheLabel_ANul_ThenTheBody()
    {
        Assert.Equal(
            new byte[] { (byte)'a', (byte)'/', (byte)'b', 0, 1, 2, 3 },
            SealDomain.Input("a/b", [1, 2, 3]));

        // The NUL ends the label: a longer label is never the same bytes as a shorter one plus the start of a body.
        Assert.NotEqual(SealDomain.Input("ab", [1]), SealDomain.Input("a", [(byte)'b', 1]));
        Assert.Equal("cuprinet/decree/v2", SealDomain.Label("decree", 2));
    }

    [Fact]
    public void ASignature_MadeUnderOneLabel_DoesNotVerifyUnderAnother()
    {
        var suite = CryptoSuites.Secure();
        var seal = suite.GenerateSeal();
        byte[] body = [1, 2, 3, 4];

        var asDecree = suite.CreateSigner(seal.PrivateKey).Sign(SealDomain.Input(SealDomain.Label("decree", 2), body));

        Assert.True(suite.Verifier.Verify(SealDomain.Input(SealDomain.Label("decree", 2), body), asDecree, seal.PublicKey));
        Assert.False(suite.Verifier.Verify(SealDomain.Input(SealDomain.Label("lan-presence", 2), body), asDecree, seal.PublicKey));
        Assert.False(suite.Verifier.Verify(SealDomain.Input(SealDomain.Label("decree", 3), body), asDecree, seal.PublicKey));
        Assert.False(suite.Verifier.Verify(body, asDecree, seal.PublicKey));
    }

    [Fact]
    public void ALink_SignedOverItsBareBody_IsRefused_AndSoIsTheOldVersion()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var link = IntonationMint.Intone(identity, suite, new IntonationOptions { Network = Network, Beacons = [] }, Now);
        Assert.Equal(2, link.Version);
        Assert.True(IntonationValidator.ValidateDocument(IntonationCodec.Encode(link), Network, suite, Now).IsValid);

        var bare = link with { Signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(IntonationCodec.EncodeBody(link)) };
        Assert.Equal(IntonationStatus.BadSignature, IntonationValidator.ValidateDocument(IntonationCodec.Encode(bare), Network, suite, Now).Status);

        // A version 1 link — however it is signed — is from before the label and is not accepted at all.
        var old = link with { Version = 1, SignedBody = null };
        old = old with { Signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(IntonationCodec.EncodeBody(old)) };
        Assert.Equal(IntonationStatus.UnsupportedVersion, IntonationValidator.ValidateDocument(IntonationCodec.Encode(old), Network, suite, Now).Status);
    }

    [Fact]
    public void AnOverlayRecord_SignedOverItsBareBody_IsRefused()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var record = PeerRecordSigner.Create(identity, [new Beacon(EndpointKind.Host, "203.0.113.7", 4000)], 1, PeerCapabilities.ChannelProvider, suite, Now);
        Assert.True(PeerRecordSigner.Verify(record, suite));

        var bare = record with { Signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(PeerRecordCodec.EncodeBody(record)) };
        Assert.False(PeerRecordSigner.Verify(bare, suite));
    }

    [Fact]
    public void AnAdvert_SignedOverItsBareBody_IsRefused()
    {
        var suite = CryptoSuites.Secure();
        var provider = NodeIdentity.Generate(suite);
        var decree = DecreeSigner.Publish(provider, ArcanumKeys.Derive(Watchword.Generate("Labels"), suite), [], TimeSpan.FromHours(1), 1, suite, Now);
        Assert.Equal(2, decree.Version);
        Assert.True(DecreeSigner.Verify(decree, suite));

        var bare = decree with { Signature = suite.CreateSigner(provider.Seal.PrivateKey).Sign(DecreeCodec.EncodeBody(decree)) };
        Assert.False(DecreeSigner.Verify(bare, suite));
    }

    [Fact]
    public void ALanAnnouncement_SignedOverItsBareBody_IsRefused()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var presence = LanPresenceSigner.Create(identity, Network, 43820, suite, Now);
        Assert.Equal(2, presence.Version);
        Assert.True(LanPresenceSigner.Verify(presence, suite));

        var bare = presence with { Signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(LanPresenceCodec.EncodeBody(presence)) };
        Assert.False(LanPresenceSigner.Verify(bare, suite));
    }

    [Fact]
    public void TheOwnershipDocuments_SignedOverTheirBareBodies_AreRefused()
    {
        var suite = CryptoSuites.Secure();
        var owner = suite.GenerateSeal();
        var heir = suite.GenerateSeal();
        var member = suite.GenerateSeal();
        byte[] channelId = [1, 2, 3];
        var signer = suite.CreateSigner(owner.PrivateKey);

        var descriptor = Ownership.CreateDescriptor(owner, channelId, ArcanumEntry.Sealed, 1, suite, Now);
        Assert.True(Ownership.VerifyDescriptor(descriptor, suite));
        Assert.False(Ownership.VerifyDescriptor(descriptor with { Signature = signer.Sign(Ownership.DescriptorBody(descriptor)) }, suite));

        var investiture = Ownership.Invest(owner, channelId, member.PublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 1, suite);
        Assert.True(Ownership.VerifyInvestiture(investiture, owner.PublicKey, channelId, suite, Now));
        Assert.False(Ownership.VerifyInvestiture(investiture with { Signature = signer.Sign(Ownership.InvestitureBody(investiture)) }, owner.PublicKey, channelId, suite, Now));

        var ascension = Ownership.Ascend(owner, channelId, 1, heir.PublicKey, Ownership.HashOf(descriptor, suite), suite);
        Assert.Equal(heir.PublicKey, Ownership.Resolve(descriptor, [ascension], suite).CurrentOwnerPublicKey);
        Assert.Throws<OwnershipException>(() => Ownership.Resolve(
            descriptor, [ascension with { Signature = signer.Sign(Ownership.AscensionBody(ascension)) }], suite));
    }
}
