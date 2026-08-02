using System.Linq;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class MonikersTests
{
    private const char Zwsp = (char)0x200B;   // zero-width space (Cf)
    private const char Zwj = (char)0x200D;    // zero-width joiner (Cf)
    private const char Rlo = (char)0x202E;    // right-to-left override (Cf)
    private const char Bell = (char)0x0007;   // control (Cc)

    [Fact]
    public void BlankOrAllStripped_BecomesNull()
    {
        Assert.Null(Monikers.Normalize(null));
        Assert.Null(Monikers.Normalize(""));
        Assert.Null(Monikers.Normalize("   "));
        Assert.Null(Monikers.Normalize("\t\r\n"));
        Assert.Null(Monikers.Normalize(new string(new[] { Zwsp, Zwsp })));       // only zero-width
        Assert.Null(Monikers.Normalize(new string(new[] { Bell, (char)0x01 })));  // only control
    }

    [Theory]
    [InlineData("Community Relay", "Community Relay")]
    [InlineData("  Community Relay  ", "Community Relay")]        // ends trimmed
    [InlineData("Community    Relay", "Community Relay")]         // internal run collapsed
    [InlineData("Community\tRelay", "Community Relay")]           // tab -> single space
    [InlineData("Community\r\nRelay", "Community Relay")]         // newline -> single space
    public void TrimsAndCollapsesWhitespace(string input, string expected)
        => Assert.Equal(expected, Monikers.Normalize(input));

    [Fact]
    public void StripsControlFormatBidiAndZeroWidth()
    {
        // RLO bidi override + zero-width space + zero-width joiner + a control char embedded in a normal name.
        var hostile = "Wiki" + Rlo + "pedia" + Zwsp + Zwj + Bell;
        Assert.Equal("Wikipedia", Monikers.Normalize(hostile));
    }

    [Fact]
    public void ClampsToMaxLength_AndNeverTrailingSpace()
    {
        var clamped = Monikers.Normalize(new string('x', Monikers.MaxLength + 20));
        Assert.Equal(Monikers.MaxLength, clamped!.Length);

        // A space landing exactly on the clamp boundary must not leave a trailing space or a double space.
        var words = string.Join(' ', Enumerable.Repeat("ab", 40)); // well over the cap
        var result = Monikers.Normalize(words)!;
        Assert.True(result.Length <= Monikers.MaxLength);
        Assert.False(result.EndsWith(' '));
        Assert.DoesNotContain("  ", result);
    }

    [Fact]
    public void ClampCountsRunes_DoesNotSplitSurrogatePairs()
    {
        // Each emoji is one rune but two UTF-16 chars; clamping must land on a rune boundary — exactly MaxLength
        // emoji, never a lone surrogate (which prints as the replacement character U+FFFD).
        var one = char.ConvertFromUtf32(0x1F600); // grinning face
        var emoji = string.Concat(Enumerable.Repeat(one, Monikers.MaxLength + 10));
        var result = Monikers.Normalize(emoji)!;
        Assert.Equal(string.Concat(Enumerable.Repeat(one, Monikers.MaxLength)), result);
        Assert.False(result.Contains((char)0xFFFD));
    }

    [Fact]
    public void PlainNameIsIdempotent()
    {
        var once = Monikers.Normalize("Community Relay");
        Assert.Equal(once, Monikers.Normalize(once));
    }
}
