using CupriNet.Alembic;
using CupriNet.Arcanum;
using Xunit;

namespace CupriNet.UnitTests;

public class ArcanumKeyTests
{
    private const string FixedSalt = "AAAAAAAAAAAAAAAAAAAAAA"; // 16 zero bytes, Base64Url
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static Watchword Fixed(string name, string salt = FixedSalt)
    {
        Assert.True(Watchword.TryParse($"{name}#{salt}", out var watchword));
        return watchword;
    }

    [Fact]
    public void Derive_IsDeterministic_ForSameWatchword()
    {
        var suite = CryptoSuites.Simulacrum();
        var w = Fixed("Dungeons&Dragons");

        var a = ArcanumKeys.Derive(w, suite);
        var b = ArcanumKeys.Derive(w, suite);

        Assert.Equal(a.GlyphKey, b.GlyphKey);
        Assert.Equal(a.VeilKey, b.VeilKey);
        Assert.Equal(a.ConcordKey, b.ConcordKey);
        Assert.True(a.Ascendant.Span.SequenceEqual(b.Ascendant.Span));
    }

    [Fact]
    public void Derive_IsDeterministic_UnderSecureSuite()
    {
        var suite = CryptoSuites.Secure(); // real Argon2id must also be deterministic
        var w = Fixed("ProjectOrion");
        Assert.Equal(ArcanumKeys.Derive(w, suite).GlyphKey, ArcanumKeys.Derive(w, suite).GlyphKey);
    }

    [Fact]
    public void Subkeys_AreDistinctFromEachOther()
    {
        var suite = CryptoSuites.Simulacrum();
        var k = ArcanumKeys.Derive(Fixed("General"), suite);

        Assert.NotEqual(k.GlyphKey, k.VeilKey);
        Assert.NotEqual(k.GlyphKey, k.ConcordKey);
        Assert.NotEqual(k.VeilKey, k.ConcordKey);
    }

    [Fact]
    public void DifferentWatchwords_ProduceDifferentKeys()
    {
        var suite = CryptoSuites.Simulacrum();
        // Same name, different (random) Obscuration => different channels.
        var a = ArcanumKeys.Derive(Watchword.Generate("General"), suite);
        var b = ArcanumKeys.Derive(Watchword.Generate("General"), suite);

        Assert.NotEqual(a.GlyphKey, b.GlyphKey);
        Assert.False(a.Ascendant.Span.SequenceEqual(b.Ascendant.Span));
    }

    [Fact]
    public void Glyph_SameEpochSame_AdjacentEpochsDiffer()
    {
        var suite = CryptoSuites.Simulacrum();
        var keys = ArcanumKeys.Derive(Fixed("Gaming"), suite);
        var epoch = Glyph.Epoch(Now);

        Assert.Equal(Glyph.ForEpoch(keys, epoch, suite), Glyph.ForEpoch(keys, epoch, suite));
        Assert.NotEqual(Glyph.ForEpoch(keys, epoch, suite), Glyph.ForEpoch(keys, epoch + 1, suite));
    }

    [Fact]
    public void Glyph_Window_IsThree_WithCurrentInTheMiddle()
    {
        var suite = CryptoSuites.Simulacrum();
        var keys = ArcanumKeys.Derive(Fixed("Gaming"), suite);

        var window = Glyph.Window(keys, Now, suite);
        Assert.Equal(3, window.Count);
        Assert.Equal(Glyph.Current(keys, Now, suite), window[1]);
    }

    [Fact]
    public void Glyph_DiffersAcrossChannels_ForSameEpoch()
    {
        var suite = CryptoSuites.Simulacrum();
        var epoch = Glyph.Epoch(Now);
        var a = ArcanumKeys.Derive(Fixed("Alpha"), suite);
        var b = ArcanumKeys.Derive(Fixed("Beta"), suite);

        // Correlation resistance: two channels never share a Glyph in the same epoch.
        Assert.NotEqual(Glyph.ForEpoch(a, epoch, suite), Glyph.ForEpoch(b, epoch, suite));
    }
}
