using CupriNet.Arcanum;
using Xunit;

namespace CupriNet.UnitTests;

public class WatchwordTests
{
    [Fact]
    public void Generate_ThenParse_RoundTrips()
    {
        var watchword = Watchword.Generate("Dungeons&Dragons");
        Assert.True(watchword.Obscuration.Length >= Watchword.MinObscurationBytes);

        var code = watchword.ToString();
        Assert.StartsWith("Dungeons&Dragons#", code);

        Assert.True(Watchword.TryParse(code, out var parsed));
        Assert.Equal(watchword.Appellation, parsed.Appellation);
        Assert.Equal(watchword.Obscuration, parsed.Obscuration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noseparator")]
    [InlineData("#onlysalt")]                 // empty name
    [InlineData("nameonly#")]                 // empty salt
    [InlineData("a#b#c")]                      // more than one separator
    [InlineData("name#short")]                 // salt decodes but < 16 bytes
    public void TryParse_RejectsMalformed(string code)
    {
        Assert.False(Watchword.TryParse(code, out _));
    }

    [Fact]
    public void Generate_RejectsNameWithSeparator()
    {
        Assert.Throws<ArgumentException>(() => Watchword.Generate("bad#name"));
    }

    [Fact]
    public void DistinctGenerations_HaveDistinctObscurations()
    {
        var a = Watchword.Generate("General");
        var b = Watchword.Generate("General");
        Assert.NotEqual(a.Obscuration, b.Obscuration);
        Assert.NotEqual(a.ToString(), b.ToString());
    }
}
