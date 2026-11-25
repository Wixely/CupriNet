using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Concordance;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// A signed document is verified — and passed on — as the bytes it arrived as. The signature is over those bytes,
/// and re-encoding the fields this build happens to parse does not always reproduce them: a newer build may have
/// appended a field, or a name may have been normalised on the way in.
/// </summary>
public class SignedBodyTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static byte[] Document(byte[] body, byte[] signature)
    {
        var w = new CodexWriter();
        w.WriteBytes(body);
        w.WriteBytes(signature);
        return w.ToArray();
    }

    [Fact]
    public void PeerRecord_WithAFieldThisBuildDoesNotKnow_StillVerifies_AndIsPassedOnIntact()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var known = PeerRecordSigner.Create(
            identity, [new Beacon(EndpointKind.Host, "203.0.113.7", 4000)], 7, PeerCapabilities.ChannelProvider, suite, Now, "Relay");

        // What a newer build would sign: everything this build knows, then a field it does not.
        byte[] body = [.. PeerRecordCodec.EncodeBody(known), 0x03, 0xAA, 0xBB, 0xCC];
        var document = Document(body, suite.CreateSigner(identity.Seal.PrivateKey).Sign(SealDomain.Input(PeerRecordCodec.SignLabel, body)));

        var (record, _) = PeerRecordCodec.Decode(document);

        Assert.Equal("Relay", record.Moniker);
        Assert.True(PeerRecordSigner.Verify(record, suite));
        Assert.Equal(document, PeerRecordCodec.Encode(record)); // gossiped onward byte-for-byte
    }

    [Fact]
    public void PeerRecord_ChangedAfterDecoding_DoesNotVerifyOnTheOriginalsBytes()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var genuine = PeerRecordSigner.Create(
            identity, [new Beacon(EndpointKind.Host, "203.0.113.7", 4000)], 7, PeerCapabilities.ChannelProvider, suite, Now);
        var (record, _) = PeerRecordCodec.Decode(PeerRecordCodec.Encode(genuine));
        Assert.True(PeerRecordSigner.Verify(record, suite));

        var tampered = record with { Endpoints = [new Beacon(EndpointKind.Host, "198.51.100.1", 4000)] };

        Assert.False(PeerRecordSigner.Verify(tampered, suite));
        Assert.NotEqual(PeerRecordCodec.Encode(record), PeerRecordCodec.Encode(tampered));
    }

    [Fact]
    public void Intonation_WhoseMonikerIsNormalisedOnDecode_IsValidHoweverItIsChecked()
    {
        var suite = CryptoSuites.Secure();
        var identity = NodeIdentity.Generate(suite);
        var link = IntonationMint.Intone(identity, suite, new IntonationOptions
        {
            Network = Network,
            Beacons = [new Beacon(EndpointKind.Host, "203.0.113.7", 43820)],
            Litany = [],
            Lifetime = TimeSpan.FromHours(2),
        }, Now);

        // Signed by an implementation that did not tidy the name first: the body says "  Relay  ".
        var trailer = new CodexWriter();
        trailer.WriteString("  Relay  ");
        byte[] body = [.. IntonationCodec.EncodeBody(link), .. trailer.ToArray()];
        var document = Document(body, suite.CreateSigner(identity.Seal.PrivateKey).Sign(SealDomain.Input(SealDomain.Label("intonation", link.Version), body)));

        var fromBytes = IntonationValidator.ValidateDocument(document, Network, suite, Now);
        Assert.True(fromBytes.IsValid);
        Assert.Equal("Relay", fromBytes.Intonation!.Moniker);

        // The path a node takes to act on a parsed link: encode it again, then validate that.
        var reEncoded = IntonationCodec.Encode(fromBytes.Intonation);
        Assert.Equal(document, reEncoded);
        Assert.True(IntonationValidator.ValidateDocument(reEncoded, Network, suite, Now).IsValid);
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x00 })]                                                       // 0, padded
    [InlineData(new byte[] { 0xFF, 0x80, 0x00 })]                                                 // 127, padded
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02 })]       // bit 64 set
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F })]       // bits 64–69 set
    public void VarUInt_NotInCanonicalForm_IsRejected(byte[] encoded)
    {
        Assert.Throws<CodexFormatException>(() =>
        {
            var reader = new CodexReader(encoded);
            reader.ReadVarUInt();
        });
    }

    [Fact]
    public void String_ThatIsNotWellFormedUtf8_IsRejected()
    {
        Assert.Throws<CodexFormatException>(() =>
        {
            var reader = new CodexReader([0x02, 0xC3, 0x28]); // a two-byte lead followed by a non-continuation byte
            reader.ReadString();
        });
    }
}
