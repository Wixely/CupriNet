using System.Text;
using CupriNet.Abstractions;

namespace CupriNet.Core;

/// <summary>
/// Bech32 (BIP-173) encoding — a case-insensitive, checksummed, branded-prefix representation used to show a
/// node's key fingerprint (and, later, site URLs) in a form a human can read aloud and compare. Encode-only for
/// now (display); a decoder can be added when we need to parse user-entered fingerprints.
/// </summary>
public static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>Encodes <paramref name="data"/> under the human-readable prefix <paramref name="hrp"/> as <c>hrp1…checksum</c>.</summary>
    public static string Encode(string hrp, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrEmpty(hrp);
        var five = ConvertBits(data, 8, 5, pad: true);
        var checksum = CreateChecksum(hrp, five);

        var sb = new StringBuilder(hrp.Length + 1 + five.Count + checksum.Count);
        sb.Append(hrp).Append('1');
        foreach (var b in five)
            sb.Append(Charset[b]);
        foreach (var b in checksum)
            sb.Append(Charset[b]);
        return sb.ToString();
    }

    /// <summary>A node's key fingerprint as a bech32 string with the <c>cupri</c> prefix (e.g. <c>cupri1…</c>).</summary>
    public static string Fingerprint(Sigil sigil) => Encode("cupri", sigil.Span);

    private static List<int> ConvertBits(ReadOnlySpan<byte> data, int from, int to, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var maxv = (1 << to) - 1;
        var result = new List<int>((data.Length * from / to) + 1);
        foreach (var value in data)
        {
            acc = (acc << from) | value;
            bits += from;
            while (bits >= to)
            {
                bits -= to;
                result.Add((acc >> bits) & maxv);
            }
        }
        if (pad && bits > 0)
            result.Add((acc << (to - bits)) & maxv);
        return result;
    }

    private static int Polymod(IReadOnlyList<int> values)
    {
        int[] generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        var chk = 1;
        foreach (var value in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ value;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) == 1)
                    chk ^= generator[i];
        }
        return chk;
    }

    private static List<int> HrpExpand(string hrp)
    {
        var result = new List<int>((hrp.Length * 2) + 1);
        foreach (var c in hrp)
            result.Add(c >> 5);
        result.Add(0);
        foreach (var c in hrp)
            result.Add(c & 31);
        return result;
    }

    private static List<int> CreateChecksum(string hrp, List<int> data)
    {
        var values = HrpExpand(hrp);
        values.AddRange(data);
        values.AddRange([0, 0, 0, 0, 0, 0]);
        var mod = Polymod(values) ^ 1;
        var checksum = new List<int>(6);
        for (var i = 0; i < 6; i++)
            checksum.Add((mod >> (5 * (5 - i))) & 31);
        return checksum;
    }
}
