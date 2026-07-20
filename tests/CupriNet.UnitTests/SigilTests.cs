using CupriNet.Abstractions;
using Xunit;

namespace CupriNet.UnitTests;

public class SigilTests
{
    [Fact]
    public void FromSealPublicKey_Is32Bytes_AndDeterministic()
    {
        byte[] pubkey = [1, 2, 3, 4, 5];
        var a = Sigil.FromSealPublicKey(pubkey);
        var b = Sigil.FromSealPublicKey(pubkey);

        Assert.Equal(Sigil.Size, a.Span.Length);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferentKeys_ProduceDifferentSigils()
    {
        var a = Sigil.FromSealPublicKey([1]);
        var b = Sigil.FromSealPublicKey([2]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToString_IsLowerHex_OfFullMark()
    {
        var sigil = Sigil.FromSealPublicKey([9, 9, 9]);
        var text = sigil.ToString();
        Assert.Equal(Sigil.Size * 2, text.Length);
        Assert.DoesNotContain(text, c => char.IsUpper(c));
    }

    [Fact]
    public void Default_IsEmpty()
    {
        Sigil sigil = default;
        Assert.True(sigil.IsEmpty);
        Assert.Equal(0, sigil.Span.Length);
        Assert.Equal("<empty>", sigil.ToString());
    }

    [Fact]
    public void Constructor_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => new Sigil([1, 2, 3]));
    }
}
