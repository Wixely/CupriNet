using CupriNet.Abstractions;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The optional Shrine-address field on an Intonation: a node may advertise the Signet of the L2 site it hosts, so
/// discovering the node discovers the site. It is a positional trailing field, so these cover every combination with
/// the Moniker / WebRTC slots that precede it — including that the older wire form is unchanged when it is unset.
/// </summary>
public class IntonationShrineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static WebRtcEndpoint Endpoint() => new()
    {
        IceUfrag = "ufrag",
        IcePassword = "password",
        FingerprintAlgorithm = "sha-256",
        Fingerprint = new byte[32],
        Port = 43820,
    };

    /// <summary>A node identity plus the suite that minted it — everything needed to sign a link.</summary>
    private sealed record Minter(Alembic.ICryptoSuite Suite, NodeIdentity Identity)
    {
        public static Minter Create()
        {
            var suite = CryptoSuites.Secure();
            return new Minter(suite, NodeIdentity.Generate(suite));
        }
    }

    private static Intonation Mint(Minter minter, Sigil? shrine, WebRtcEndpoint? webrtc, string? moniker)
        => IntonationMint.Intone(minter.Identity, minter.Suite, new IntonationOptions
        {
            Network = new Concordium("sites.example"),
            Beacons = [new Beacon(EndpointKind.Host, "203.0.113.5", 43820)],
            Lifetime = TimeSpan.FromHours(2),
            Moniker = moniker,
            WebRtc = webrtc,
            Shrine = shrine,
        }, Now);

    [Fact]
    public void Shrine_RoundTrips_AndRendersCupriAddress()
    {
        var minter = Minter.Create();
        var signet = Signet.Generate(minter.Suite);

        var link = Mint(minter, signet.Sigil, webrtc: null, moniker: null);
        var (decoded, _) = IntonationCodec.Decode(IntonationCodec.Encode(link));

        Assert.Equal(signet.Sigil, decoded.Shrine);
        Assert.Equal(signet.Address, decoded.ShrineAddress);
        Assert.StartsWith("cupri1", decoded.ShrineAddress);
        Assert.Null(decoded.Moniker);   // an empty materialised Moniker slot decodes back to null
        Assert.Null(decoded.WebRtc);    // the absent-block sentinel decodes back to null
    }

    [Fact]
    public void Shrine_RoundTrips_AlongsideWebRtcAndMoniker()
    {
        var minter = Minter.Create();
        var signet = Signet.Generate(minter.Suite);

        var link = Mint(minter, signet.Sigil, Endpoint(), "Community Site");
        var (decoded, _) = IntonationCodec.Decode(IntonationCodec.Encode(link));

        Assert.Equal(signet.Sigil, decoded.Shrine);
        Assert.Equal("Community Site", decoded.Moniker);
        Assert.NotNull(decoded.WebRtc);
        Assert.Equal(43820, decoded.WebRtc!.Port);
        Assert.Equal("ufrag", decoded.WebRtc.IceUfrag);
    }

    [Fact]
    public void Shrine_Unset_LeavesTheOlderWireFormUnchanged()
    {
        var minter = Minter.Create();

        // No Shrine ⇒ byte-identical to what a pre-Shrine build encodes for the same content.
        var withWebRtc = Mint(minter, shrine: null, Endpoint(), moniker: "Relay");
        var (decodedWebRtc, _) = IntonationCodec.Decode(IntonationCodec.Encode(withWebRtc));
        Assert.Null(decodedWebRtc.Shrine);
        Assert.Null(decodedWebRtc.ShrineAddress);
        Assert.NotNull(decodedWebRtc.WebRtc);
        Assert.Equal("Relay", decodedWebRtc.Moniker);

        // And with nothing optional set at all, the trailer is empty.
        var bare = Mint(minter, shrine: null, webrtc: null, moniker: null);
        var (decodedBare, _) = IntonationCodec.Decode(IntonationCodec.Encode(bare));
        Assert.Null(decodedBare.Shrine);
        Assert.Null(decodedBare.WebRtc);
        Assert.Null(decodedBare.Moniker);
    }

    [Fact]
    public void Shrine_IsCoveredByTheSignature()
    {
        var minter = Minter.Create();
        var signet = Signet.Generate(minter.Suite);
        var link = Mint(minter, signet.Sigil, Endpoint(), moniker: null);

        // A link that validates normally...
        var document = IntonationCodec.Encode(link);
        var ok = IntonationValidator.ValidateDocument(document, new Concordium("sites.example"), minter.Suite, Now);
        Assert.Equal(IntonationStatus.Valid, ok.Status);
        Assert.Equal(signet.Sigil, ok.Intonation!.Shrine);

        // ...fails once the advertised Shrine is swapped, because the field is inside the signed body.
        var tampered = IntonationCodec.Encode(link with { Shrine = Signet.Generate(minter.Suite).Sigil });
        var bad = IntonationValidator.ValidateDocument(tampered, new Concordium("sites.example"), minter.Suite, Now);
        Assert.Equal(IntonationStatus.BadSignature, bad.Status);
    }

    [Fact]
    public async Task Node_AdvertisesHostedShrine_OnlyWhenOptedIn()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        await using var node = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        var signet = Signet.Generate(node.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.Text("hi"));

        // Hosting alone does not advertise: the default keeps the Shrine unlinkable from the node's overlay Sigil.
        node.HostShrine(signet, handler);
        Assert.Equal(signet.Address, node.ShrineAddress);
        Assert.Null(node.Intone(TimeSpan.FromHours(1), Now).Shrine);

        // Opting in stamps it into every link the node mints.
        node.HostShrine(signet, handler, advertiseInLink: true);
        var advertised = node.Intone(TimeSpan.FromHours(1), Now);
        Assert.Equal(signet.Sigil, advertised.Shrine);
        Assert.Equal(signet.Address, advertised.ShrineAddress);
    }
}
