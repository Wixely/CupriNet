using CupriNet.Arcanum;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class DecreeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static Watchword Fixed(string name, string salt = "AAAAAAAAAAAAAAAAAAAAAA")
    {
        Assert.True(Watchword.TryParse($"{name}#{salt}", out var w));
        return w;
    }

    [Fact]
    public void Publish_RoundTrips_AndVerifies()
    {
        var suite = CryptoSuites.Secure();
        var keys = ArcanumKeys.Derive(Fixed("Dungeons&Dragons"), suite);
        var provider = NodeIdentity.Generate(suite);

        var decree = DecreeSigner.Publish(
            provider, keys, [new Beacon(EndpointKind.Host, "10.0.0.5", 43820)],
            TimeSpan.FromMinutes(20), sequenceNumber: 1, suite, Now);

        var (decoded, _) = DecreeCodec.Decode(DecreeCodec.Encode(decree));
        Assert.Equal(decree.ProviderSigil, decoded.ProviderSigil);
        Assert.Equal(decree.Glyph, decoded.Glyph);
        Assert.Equal(decree.Endpoints, decoded.Endpoints);

        Assert.True(DecreeSigner.Verify(decree, suite));

        var tampered = decree with { SequenceNumber = decree.SequenceNumber + 1 };
        Assert.False(DecreeSigner.Verify(tampered, suite));
    }

    [Fact]
    public void Matches_OnlyForOwnChannel_WithinWindow()
    {
        var suite = CryptoSuites.Simulacrum();
        var mine = ArcanumKeys.Derive(Fixed("Gaming"), suite);
        var foreign = ArcanumKeys.Derive(Fixed("Politics"), suite);
        var provider = NodeIdentity.Generate(suite);

        var decree = DecreeSigner.Publish(provider, mine, [], TimeSpan.FromMinutes(20), 1, suite, Now);

        Assert.True(DecreeValidator.Matches(decree, mine, Now, suite));      // my channel, current epoch
        Assert.False(DecreeValidator.Matches(decree, foreign, Now, suite));  // different channel's Glyph
    }

    [Fact]
    public void Matches_False_AfterExpiry()
    {
        var suite = CryptoSuites.Simulacrum();
        var keys = ArcanumKeys.Derive(Fixed("Gaming"), suite);
        var provider = NodeIdentity.Generate(suite);

        var decree = DecreeSigner.Publish(provider, keys, [], TimeSpan.FromMinutes(10), 1, suite, Now);
        Assert.False(DecreeValidator.Matches(decree, keys, Now.AddMinutes(30), suite));
    }
}
