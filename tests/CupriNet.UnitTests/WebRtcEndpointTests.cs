using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Marks;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>The static WebRTC endpoint block published (signed) in an Intonation — see design/webrtc-clients.md.</summary>
public class WebRtcEndpointTests
{
    private static WebRtcEndpoint Sample()
    {
        var fingerprint = new byte[32];
        for (var i = 0; i < fingerprint.Length; i++)
            fingerprint[i] = (byte)i;
        return new WebRtcEndpoint
        {
            IceUfrag = "u5r7",
            IcePassword = new string('p', 24),
            FingerprintAlgorithm = "sha-256",
            Fingerprint = fingerprint,
            Port = 51820,
        };
    }

    private static byte[] Encode(WebRtcEndpoint endpoint)
    {
        var w = new CodexWriter();
        WebRtcCodec.Write(w, endpoint);
        return w.ToArray();
    }

    private static WebRtcEndpoint Decode(byte[] bytes)
    {
        var r = new CodexReader(bytes); // CodexReader is a ref struct — read inside a method, don't capture it
        return WebRtcCodec.Read(ref r);
    }

    [Fact]
    public void RoundTrips()
    {
        var ep = Sample();
        var back = Decode(Encode(ep));

        Assert.Equal(ep.IceUfrag, back.IceUfrag);
        Assert.Equal(ep.IcePassword, back.IcePassword);
        Assert.Equal(ep.FingerprintAlgorithm, back.FingerprintAlgorithm);
        Assert.Equal(ep.Fingerprint, back.Fingerprint);
        Assert.Equal(ep.Port, back.Port);
    }

    [Fact]
    public void Read_RejectsUnsupportedVersion()
    {
        var w = new CodexWriter();
        w.WriteByte(0x63); // version 99 — not in the catalogue
        w.WriteString("u"); w.WriteString("p"); w.WriteString("sha-256");
        w.WriteBytes(new byte[32]); w.WriteVarUInt(51820);
        var bytes = w.ToArray();
        Assert.Throws<CodexFormatException>(() => Decode(bytes));
    }

    [Fact]
    public void Read_RejectsEmptyFingerprint()
    {
        var w = new CodexWriter();
        w.WriteByte(checked((byte)CupriMarks.Supported(CupriMarks.WebRtcEndpoint).Max));
        w.WriteString("u"); w.WriteString("p"); w.WriteString("sha-256");
        w.WriteBytes([]); w.WriteVarUInt(51820);
        var bytes = w.ToArray();
        Assert.Throws<CodexFormatException>(() => Decode(bytes));
    }

    [Fact]
    public void Read_RejectsOutOfRangePort()
    {
        var w = new CodexWriter();
        w.WriteByte(checked((byte)CupriMarks.Supported(CupriMarks.WebRtcEndpoint).Max));
        w.WriteString("u"); w.WriteString("p"); w.WriteString("sha-256");
        w.WriteBytes(new byte[32]); w.WriteVarUInt(0); // port 0 is invalid
        var bytes = w.ToArray();
        Assert.Throws<CodexFormatException>(() => Decode(bytes));
    }

    [Fact]
    public void ClampsOverlongStrings()
    {
        var ep = Sample() with { IceUfrag = new string('x', WebRtcCodec.MaxStringLength + 50) };
        var back = Decode(Encode(ep));
        Assert.Equal(WebRtcCodec.MaxStringLength, back.IceUfrag.Length);
    }
}
