using CupriMark;
using CupriNet.Marks;
using Xunit;

namespace CupriNet.UnitTests;

public class CupriMarksTests
{
    [Fact]
    public void Catalogue_HasConjunctionComponent_WithAStableId()
    {
        Assert.NotNull(CupriMarks.Catalogue.Component(CupriMarks.Conjunction));
        Assert.Equal(32, CupriMarks.Catalogue.Id.Length); // SHA-256 catalogue identity
    }

    [Fact]
    public void Supported_ForConjunction_IsVersionOne()
    {
        var supported = CupriMarks.Supported(CupriMarks.Conjunction);
        Assert.Equal(1, (int)supported.Min);
        Assert.Equal(1, (int)supported.Max); // only v1 exists today
    }

    [Fact]
    public void Negotiate_WithMatchingPeer_AcceptsVersionOne()
    {
        var result = CupriMarks.Negotiate(CupriMarks.Conjunction, OrdinalRange.Create(1, 1));
        Assert.True(result.Accepted);
        Assert.Equal(1, (int)result.SelectedOrdinal);
    }

    [Fact]
    public void Negotiate_WithNewerPeer_SelectsHighestBothSpeak()
    {
        // A future peer advertising [1..5] still pairs with us on the shared version (1) — no flag-day partition.
        var result = CupriMarks.Negotiate(CupriMarks.Conjunction, OrdinalRange.Create(1, 5));
        Assert.True(result.Accepted);
        Assert.Equal(1, (int)result.SelectedOrdinal);
    }

    [Fact]
    public void Negotiate_WithNoSharedVersion_Rejects()
    {
        // A peer that dropped v1 and only speaks [2..5] shares nothing with us: rejected, not silently paired.
        var result = CupriMarks.Negotiate(CupriMarks.Conjunction, OrdinalRange.Create(2, 5));
        Assert.False(result.Accepted);
    }

    [Fact]
    public void Consecration_SupportedIsVersionOne_AndNegotiates()
    {
        var supported = CupriMarks.Supported(CupriMarks.Consecration);
        Assert.Equal(1, (int)supported.Min);
        Assert.Equal(1, (int)supported.Max);

        var shared = CupriMarks.Negotiate(CupriMarks.Consecration, OrdinalRange.Create(1, 3));
        Assert.True(shared.Accepted);
        Assert.Equal(1, (int)shared.SelectedOrdinal);

        var none = CupriMarks.Negotiate(CupriMarks.Consecration, OrdinalRange.Create(2, 5));
        Assert.False(none.Accepted);
    }

    [Fact]
    public void Decree_AcceptsSupportedVersion_RejectsUnknownOrZero()
    {
        Assert.Equal(1, (int)CupriMarks.Supported(CupriMarks.Decree).Max);
        Assert.True(CupriMarks.Accepts(CupriMarks.Decree, 1));   // current
        Assert.False(CupriMarks.Accepts(CupriMarks.Decree, 2));  // too new — not in our catalogue
        Assert.False(CupriMarks.Accepts(CupriMarks.Decree, 0));  // not a real version
    }

    [Fact]
    public void Intonation_AcceptsSupportedVersion_RejectsUnknown()
    {
        Assert.Equal(1, (int)CupriMarks.Supported(CupriMarks.Intonation).Max);
        Assert.True(CupriMarks.Accepts(CupriMarks.Intonation, 1));
        Assert.False(CupriMarks.Accepts(CupriMarks.Intonation, 2));
    }

    [Fact]
    public void UnknownComponent_Throws()
    {
        Assert.Throws<ArgumentException>(() => CupriMarks.Supported("not-a-component"));
    }
}
