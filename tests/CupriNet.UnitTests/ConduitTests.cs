using System.Net;
using System.Security.Cryptography;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class ConduitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void ConduitFrame_Codec_RoundTrips()
    {
        var frame = new ConduitFrame { ProtocolId = 42, SchemaVersion = 3, Flags = 0x01, Payload = [9, 8, 7] };
        var decoded = ConduitCodec.Decode(ConduitCodec.Encode(frame));
        Assert.Equal(frame.ProtocolId, decoded.ProtocolId);
        Assert.Equal(frame.SchemaVersion, decoded.SchemaVersion);
        Assert.Equal(frame.Flags, decoded.Flags);
        Assert.Equal(frame.Payload, decoded.Payload);
    }

    [Fact]
    public async Task Conduit_EndToEnd_OverEncryptedSession()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);

        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        VesselSession va = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        VesselSession vb = await acceptTask;
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        var sender = new ConduitSession(va, sessionKey, suite);
        var receiver = new ConduitSession(vb, sessionKey, suite);

        await sender.SendAsync(new ConduitFrame { ProtocolId = 7, SchemaVersion = 1, Flags = 0, Payload = "state-sync"u8.ToArray() }, ct);
        var got = await receiver.ReceiveAsync(ct);

        Assert.NotNull(got);
        Assert.Equal(7u, got.ProtocolId);
        Assert.Equal("state-sync"u8.ToArray(), got.Payload);
    }
}
