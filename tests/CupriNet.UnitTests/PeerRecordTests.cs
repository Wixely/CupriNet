using CupriNet.Concordance;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class PeerRecordTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public void Create_RoundTripsThroughCodec()
    {
        var suite = CryptoSuites.Simulacrum();
        var identity = NodeIdentity.Generate(suite);
        var record = PeerRecordSigner.Create(
            identity,
            [new Beacon(EndpointKind.Host, "10.0.0.5", 43820)],
            sequenceNumber: 7,
            PeerCapabilities.Relay | PeerCapabilities.ChannelProvider,
            suite, Now);

        var (decoded, _) = PeerRecordCodec.Decode(PeerRecordCodec.Encode(record));

        Assert.Equal(record.Sigil, decoded.Sigil);
        Assert.Equal(record.SequenceNumber, decoded.SequenceNumber);
        Assert.Equal(record.Capabilities, decoded.Capabilities);
        Assert.Equal(record.Endpoints, decoded.Endpoints);
        Assert.Equal(record.Signature, decoded.Signature);
    }

    [Fact]
    public void Moniker_RoundTrips_AndIsNullWhenUnset()
    {
        var suite = CryptoSuites.Simulacrum();
        var identity = NodeIdentity.Generate(suite);

        var named = PeerRecordSigner.Create(identity, [], 1, PeerCapabilities.None, suite, Now, moniker: "Wikipedia");
        var (namedBack, _) = PeerRecordCodec.Decode(PeerRecordCodec.Encode(named));
        Assert.Equal("Wikipedia", namedBack.Moniker);

        var plain = PeerRecordSigner.Create(identity, [], 1, PeerCapabilities.None, suite, Now);
        Assert.Null(plain.Moniker);
        var (plainBack, _) = PeerRecordCodec.Decode(PeerRecordCodec.Encode(plain));
        Assert.Null(plainBack.Moniker);
    }

    [Fact]
    public void Moniker_IsCoveredBySignature_UnderSecureSuite()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var record = PeerRecordSigner.Create(identity, [], 1, PeerCapabilities.None, suite, Now, moniker: "Wikipedia");

        Assert.True(PeerRecordSigner.Verify(record, suite));
        Assert.False(PeerRecordSigner.Verify(record with { Moniker = "Imposter" }, suite));
    }

    [Fact]
    public void Sigil_IsDerivedFromSeal_MatchesIdentity()
    {
        var suite = CryptoSuites.Simulacrum();
        var identity = NodeIdentity.Generate(suite);
        var record = PeerRecordSigner.Create(identity, [], 1, PeerCapabilities.None, suite, Now);
        Assert.Equal(identity.Sigil, record.Sigil);
    }

    [Fact]
    public void Verify_TrueForGenuine_FalseForTampered_UnderSecureSuite()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var record = PeerRecordSigner.Create(
            identity, [new Beacon(EndpointKind.Host, "10.0.0.5", 43820)], 3, PeerCapabilities.None, suite, Now);

        Assert.True(PeerRecordSigner.Verify(record, suite));

        var tampered = record with { SequenceNumber = record.SequenceNumber + 1 };
        Assert.False(PeerRecordSigner.Verify(tampered, suite));
    }
}
