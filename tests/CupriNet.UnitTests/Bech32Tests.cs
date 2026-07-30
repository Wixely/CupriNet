using System.Text;
using CupriNet.Abstractions;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class Bech32Tests
{
    [Fact]
    public void MatchesBip173Vectors()
    {
        // Known BIP-173 valid checksums (lowercased) — proves the checksum/polymod is correct.
        Assert.Equal("a12uel5l", Bech32.Encode("a", []));
        Assert.Equal(
            "abcdef1qpzry9x8gf2tvdw0s3jn54khce6mua7lmqqqxw",
            Bech32.Encode("abcdef", [0x00, 0x44, 0x32, 0x14, 0xc7, 0x42, 0x54, 0xb6, 0x35, 0xcf, 0x84, 0x65, 0x3a, 0x56, 0xd7, 0xc6, 0x75, 0xbe, 0x77, 0xdf]));
    }

    [Fact]
    public void Fingerprint_HasCupriPrefix_IsCharsetOnly_AndDeterministic()
    {
        var sigil = Sig(0x7a);
        var fp = Bech32.Fingerprint(sigil);

        Assert.StartsWith("cupri1", fp);
        Assert.Equal(fp, Bech32.Fingerprint(sigil)); // deterministic
        Assert.NotEqual(fp, Bech32.Fingerprint(Sig(0x7b))); // different key -> different fingerprint

        // Everything after the "1" separator is from the bech32 charset (URL/DNS/read-aloud safe, case-insensitive).
        const string charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
        foreach (var c in fp["cupri1".Length..])
            Assert.Contains(c, charset);
    }

    private static Sigil Sig(byte seed)
    {
        var bytes = new byte[Sigil.Size];
        Array.Fill(bytes, seed);
        return new Sigil(bytes);
    }
}
