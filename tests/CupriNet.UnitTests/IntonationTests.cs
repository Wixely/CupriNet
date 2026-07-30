using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Alembic.Simulacrum;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class IntonationTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56); // fixed, deterministic

    private static ICryptoSuite Suite() => new SimulacrumSuite(InsecureConsent.IUnderstandThisProvidesNoSecurity());

    private static Intonation Sample(ICryptoSuite suite, TimeSpan? lifetime = null)
    {
        var identity = NodeIdentity.Generate(suite);
        var options = new IntonationOptions
        {
            Network = Network,
            Beacons =
            [
                new Beacon(EndpointKind.Host, "192.168.1.20", 43820),
                new Beacon(EndpointKind.Mapped, "203.0.113.7", 51000),
            ],
            Litany = [Sigil.FromSealPublicKey([1, 2, 3]), Sigil.FromSealPublicKey([4, 5, 6])],
            Lifetime = lifetime ?? TimeSpan.FromHours(2),
        };
        return IntonationMint.Intone(identity, suite, options, Now);
    }

    [Fact]
    public void Encode_Decode_RoundTrips_AllFields()
    {
        var suite = Suite();
        var original = Sample(suite);

        var (decoded, _) = IntonationCodec.Decode(IntonationCodec.Encode(original));

        Assert.Equal(original.Version, decoded.Version);
        Assert.Equal(original.Network, decoded.Network);
        Assert.Equal(original.InviterSealPublicKey, decoded.InviterSealPublicKey);
        Assert.Equal(original.Beacons, decoded.Beacons);
        Assert.Equal(original.Litany, decoded.Litany);
        Assert.Equal(original.IssuedAtUnix, decoded.IssuedAtUnix);
        Assert.Equal(original.SeveranceUnix, decoded.SeveranceUnix);
        Assert.Equal(original.Nonce, decoded.Nonce);
        Assert.Equal(original.Signature, decoded.Signature);
    }

    [Fact]
    public void Moniker_RoundTrips_WhenSet()
    {
        var suite = Suite();
        var identity = NodeIdentity.Generate(suite);
        var intonation = IntonationMint.Intone(identity, suite, new IntonationOptions
        {
            Network = Network,
            Beacons = [new Beacon(EndpointKind.Host, "192.168.1.20", 43820)],
            Moniker = "Wikipedia",
        }, Now);

        var (decoded, _) = IntonationCodec.Decode(IntonationCodec.Encode(intonation));
        Assert.Equal("Wikipedia", decoded.Moniker);
    }

    [Fact]
    public void Moniker_IsNull_WhenUnset_AndOmittedFromBody()
    {
        // A link without a Moniker must be byte-identical to the pre-Moniker format: the optional trailing field
        // is simply absent, so an older decoder reads up to the Petition flag and stops. We prove the field is
        // omitted (not written as an empty string) by decoding and confirming null.
        var suite = Suite();
        var withMoniker = Sample(suite);
        Assert.Null(withMoniker.Moniker);

        var (decoded, _) = IntonationCodec.Decode(IntonationCodec.Encode(withMoniker));
        Assert.Null(decoded.Moniker);
    }

    [Fact]
    public void Moniker_IsCoveredBySignature_UnderSecureSuite()
    {
        // The Moniker rides inside the signed body, so swapping it must break verification — a node can only
        // claim a Moniker for its own key. (Under the real suite; Simulacrum is credulous by design.)
        var secure = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(secure);
        var intonation = IntonationMint.Intone(identity, secure, new IntonationOptions
        {
            Network = Network,
            Beacons = [new Beacon(EndpointKind.Host, "192.168.1.20", 43820)],
            Moniker = "Wikipedia",
        }, Now);

        Assert.Equal(IntonationStatus.Valid,
            IntonationValidator.ValidateDocument(IntonationCodec.Encode(intonation), Network, secure, Now).Status);

        var forged = intonation with { Moniker = "TotallyLegitNews" };
        Assert.Equal(IntonationStatus.BadSignature,
            IntonationValidator.ValidateDocument(IntonationCodec.Encode(forged), Network, secure, Now).Status);
    }

    [Fact]
    public void Moniker_OverLength_IsClamped()
    {
        var suite = Suite();
        var identity = NodeIdentity.Generate(suite);
        var intonation = IntonationMint.Intone(identity, suite, new IntonationOptions
        {
            Network = Network,
            Beacons = [new Beacon(EndpointKind.Host, "192.168.1.20", 43820)],
            Moniker = new string('x', Monikers.MaxLength + 20),
        }, Now);

        var (decoded, _) = IntonationCodec.Decode(IntonationCodec.Encode(intonation));
        Assert.Equal(Monikers.MaxLength, decoded.Moniker!.Length);
    }

    [Fact]
    public void Uri_RoundTrips_ThroughPrefixedBase64Url()
    {
        var suite = Suite();
        var original = Sample(suite);

        var uri = IntonationUri.ToUri(original);
        Assert.StartsWith("cuprinet://intone/", uri);

        Assert.True(IntonationUri.TryParse(uri, out var parsed, out _));
        Assert.Equal(original.InviterSigil, parsed.InviterSigil);
        Assert.Equal(original.Beacons, parsed.Beacons);
    }

    [Fact]
    public void Mint_Then_Validate_IsValid()
    {
        var suite = Suite();
        var intonation = Sample(suite);

        var result = IntonationValidator.ValidateDocument(IntonationCodec.Encode(intonation), Network, suite, Now);
        Assert.Equal(IntonationStatus.Valid, result.Status);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AfterSeverance_ReportsSevered()
    {
        var suite = Suite();
        var intonation = Sample(suite, lifetime: TimeSpan.FromMinutes(30));

        var afterExpiry = Now.AddHours(1);
        var result = IntonationValidator.ValidateDocument(IntonationCodec.Encode(intonation), Network, suite, afterExpiry);
        Assert.Equal(IntonationStatus.Severed, result.Status);
    }

    [Fact]
    public void Validate_WrongNetwork_IsRejected()
    {
        var suite = Suite();
        var intonation = Sample(suite);

        var result = IntonationValidator.ValidateDocument(
            IntonationCodec.Encode(intonation), new Concordium("other.net"), suite, Now);
        Assert.Equal(IntonationStatus.WrongNetwork, result.Status);
    }

    [Fact]
    public void Validate_UnsupportedVersion_IsRejected()
    {
        var suite = Suite();
        // A link stamped with a version the CupriMark catalogue doesn't know (too new / superseded) is refused;
        // the version gate runs before the signature check, so this holds regardless of the (now stale) signature.
        var intonation = Sample(suite) with { Version = (byte)(IntonationCodec.CurrentVersion + 1) };

        var result = IntonationValidator.ValidateDocument(IntonationCodec.Encode(intonation), Network, suite, Now);
        Assert.Equal(IntonationStatus.UnsupportedVersion, result.Status);
    }

    [Fact]
    public void Validate_Malformed_IsRejected()
    {
        var suite = Suite();
        byte[] garbage = [0xFF, 0x00, 0x12, 0x34];
        var result = IntonationValidator.ValidateDocument(garbage, Network, suite, Now);
        Assert.Equal(IntonationStatus.Malformed, result.Status);
    }

    [Fact]
    public void UnderSimulacrum_TamperIsNotDetected_DocumentsInsecurity()
    {
        // The Simulacrum's credulous verifier accepts any signature. This test PINS that insecurity so
        // that the Phase 1b security suite (Trial by Crucible / Assay) provably flips it: with the real
        // BouncyCastle suite this same tampered body MUST yield BadSignature.
        var suite = Suite();
        var intonation = Sample(suite);
        var document = IntonationCodec.Encode(intonation);

        // Flip a byte inside the signed body region (skip the outer length prefix).
        document[5] ^= 0xFF;

        var result = IntonationValidator.ValidateDocument(document, Network, suite, Now);
        Assert.NotEqual(IntonationStatus.BadSignature, result.Status);
    }
}
